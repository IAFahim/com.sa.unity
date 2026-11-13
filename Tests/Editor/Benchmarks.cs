using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace SA.Tests
{
    public class Benchmarks
    {
        const int Bones = 49;
        const int Frames = 31;

        static TestRig BuildKyleSizedRig()
        {
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var rig = ref builder.ConstructRoot<RigBlob>();
            rig.Id = new Hash128(7, 7, 7, 7);
            var bones = builder.Allocate(ref rig.Bones, Bones);
            for (var b = 0; b < Bones; b++)
                bones[b] = new Bone { Parent = b - 1, Rest = BonePose.Identity, NameHash = (uint)b };
            var result = new TestRig { Skeleton = builder.CreateBlobAssetReference<RigBlob>(Allocator.Persistent), Clips = new ClipAsset[5], Masks = new MaskAsset[2] };
            for (var c = 0; c < result.Clips.Length; c++)
            {
                using var clipBuilder = new BlobBuilder(Allocator.Temp);
                ref var clip = ref clipBuilder.ConstructRoot<Clip>();
                clip.Id = new Hash128((uint)c, 7, 7, 7);
                clip.RigId = result.Value.Id;
                clip.Duration = 1f + 0.1f * c;
                clip.FrameCount = Frames;
                var samples = clipBuilder.Allocate(ref clip.Samples, Frames * Bones);
                for (var f = 0; f < Frames; f++)
                    for (var b = 0; b < Bones; b++)
                        samples[f * Bones + b] = new BonePose { Position = new float3(0f, 0.1f, 0f), Rotation = quaternion.RotateZ(0.02f * f * (c + 1)), Scale = 1f };
                var reference = clipBuilder.Allocate(ref clip.Reference, Bones);
                for (var b = 0; b < Bones; b++)
                    reference[b] = samples[b];
                result.Clips[c] = new ClipAsset { Value = clipBuilder.CreateBlobAssetReference<Clip>(Allocator.Persistent) };
            }
            for (var m = 0; m < result.Masks.Length; m++)
            {
                using var maskBuilder = new BlobBuilder(Allocator.Temp);
                ref var mask = ref maskBuilder.ConstructRoot<BoneMask>();
                mask.Id = new Hash128((uint)m, 8, 7, 7);
                mask.RigId = result.Value.Id;
                var weights = maskBuilder.Allocate(ref mask.Weights, Bones);
                for (var b = 0; b < Bones; b++)
                    weights[b] = m == 0 || b > 24 ? 1f : 0f;
                result.Masks[m] = new MaskAsset { Value = maskBuilder.CreateBlobAssetReference<BoneMask>(Allocator.Persistent) };
            }
            return result;
        }

        static BlobAssetReference<SkinBlob> BuildSkin()
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var skin = ref builder.ConstructRoot<SkinBlob>();
            skin.Id = new Hash128(8, 8, 8, 8);
            skin.MeshFromRig = PoseAlgebra.Affine(GpuFrame.Identity);
            var bones = builder.Allocate(ref skin.Bones, Bones);
            var bindposes = builder.Allocate(ref skin.Bindposes, Bones);
            for (var b = 0; b < Bones; b++)
            {
                bones[b] = b;
                bindposes[b] = PoseAlgebra.Affine(GpuFrame.Identity);
            }
            builder.Allocate(ref skin.Influences, 1);
            var result = builder.CreateBlobAssetReference<SkinBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }

        [TestCase(10000)]
        [TestCase(50000)]
        [Explicit]
        public void FullSaFramePerCharacterCount(int count)
        {
            TestRigs.RequireGpu();
            var rig = BuildKyleSizedRig();
            var skin = BuildSkin();
            using var world = new World("SA.Benchmark");
            var manager = world.EntityManager;
            var prototype = manager.CreateEntity(typeof(Rig), typeof(TimeScale), typeof(PoseSlot), typeof(Unity.Transforms.LocalToWorld));
            manager.SetComponentData(prototype, new Rig { Value = rig.Skeleton });
            manager.SetComponentData(prototype, new TimeScale { Value = 1f });
            rig.Attach(manager, prototype);
            var states = manager.AddBuffer<ClipState>(prototype);
            var clips = new[] { ClipId.None, ClipId.None, ClipId.None, ClipId.None };
            for (var i = 0; i < clips.Length; i++)
                clips[i] = Names.ClipAt(rig.Clips, i);
            Playback.Blend(ref states, clips, new[] { 0.1f, 0.4f, 0.4f, 0.1f }, 0f, PlayOptions.Default);
            Playback.CrossFade(ref states, Names.ClipAt(rig.Clips, 4), 0f, PlayOptions.Default.WithLayer(1).WithMode(ClipMode.Additive).WithMask(Names.MaskAt(rig.Masks, 1)));
            var rigs = manager.Instantiate(prototype, count, Allocator.Temp);
            var skinPrototype = manager.CreateEntity(typeof(Skin), typeof(SkinBinding));
            var skinEntities = manager.Instantiate(skinPrototype, count, Allocator.Temp);
            for (var i = 0; i < count; i++)
                manager.SetComponentData(skinEntities[i], new Skin { Value = skin, Rig = rigs[i] });

            var playback = world.GetOrCreateSystem<PlaybackSystem>();
            var skinning = world.GetOrCreateSystemManaged<SkinningSystem>();
            var elapsed = 0.0;
            var cpuMs = new List<float>();
            var totalMs = new List<float>();
            var timer = new Stopwatch();
            for (var frame = 0; frame < 260; frame++)
            {
                elapsed += 1.0 / 60.0;
                world.SetTime(new TimeData(elapsed, 1f / 60f));
                timer.Restart();
                playback.Update(world.Unmanaged);
                manager.CompleteAllTrackedJobs();
                skinning.Update();
                var cpu = (float)timer.Elapsed.TotalMilliseconds;
                var readback = new float4x4[1];
                skinning.SkinMatrixBuffer.GetData(readback, 0, 1, 1);
                var total = (float)timer.Elapsed.TotalMilliseconds;
                if (frame >= 60)
                {
                    cpuMs.Add(cpu);
                    totalMs.Add(total);
                }
            }
            cpuMs.Sort();
            totalMs.Sort();
            float Average(List<float> v) { var s = 0f; foreach (var x in v) s += x; return s / v.Count; }
            var json = $"{{\n  \"Characters\": {count},\n  \"Bones\": {Bones},\n  \"CpuSubmitMsAvg\": {Average(cpuMs):F3},\n  \"CpuSubmitMsP95\": {cpuMs[(int)(cpuMs.Count * 0.95f)]:F3},\n  \"CpuPlusGpuMsAvg\": {Average(totalMs):F3},\n  \"CpuPlusGpuMsP95\": {totalMs[(int)(totalMs.Count * 0.95f)]:F3},\n  \"Gpu\": \"{SystemInfo.graphicsDeviceName}\",\n  \"Api\": \"{SystemInfo.graphicsDeviceType}\",\n  \"Cpu\": \"{SystemInfo.processorType}\"\n}}";
            Directory.CreateDirectory("Temp/sa");
            File.WriteAllText($"Temp/sa/micro_{count}.json", json);
            rig.Dispose();
            skin.Dispose();
        }
    }
}
