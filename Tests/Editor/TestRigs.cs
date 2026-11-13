using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SA.Tests
{
    internal sealed class TestRig : IDisposable
    {
        public BlobAssetReference<RigBlob> Skeleton;
        public ClipAsset[] Clips;
        public MaskAsset[] Masks;
        public ref RigBlob Value => ref Skeleton.Value;

        public void Attach(EntityManager manager, Entity entity)
        {
            var clips = manager.AddBuffer<ClipAsset>(entity);
            clips.AddRange(new NativeArray<ClipAsset>(Clips, Allocator.Temp));
            var masks = manager.AddBuffer<MaskAsset>(entity);
            masks.AddRange(new NativeArray<MaskAsset>(Masks, Allocator.Temp));
        }

        public void Register(GpuRegistry registry)
        {
            registry.Add(Skeleton);
            foreach (var clip in Clips)
                registry.Add(clip.Value);
            foreach (var mask in Masks)
                registry.Add(mask.Value);
        }

        public void Dispose()
        {
            foreach (var clip in Clips)
                clip.Value.Dispose();
            foreach (var mask in Masks)
                mask.Value.Dispose();
            Skeleton.Dispose();
        }
    }

    internal static class TestRigs
    {
        public static readonly quaternion RestTilt = quaternion.Euler(0.4f, -0.7f, 1.1f);

        public const string OpenGlReadback = "OpenGLCore: compute results read back in this Editor came back stale in measured batchmode runs (Unity picks OpenGLCore for -batchmode on Linux). Run the Editor on Vulkan (Player Settings, Linux, Graphics APIs) to test the GPU path.";

        public static void RequireGpu()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("compute shaders unavailable");
            RequireReliableReadback();
        }

        public static void RequireReliableReadback()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore)
                Assert.Ignore(OpenGlReadback);
        }

        public static BonePose Pose(float x, quaternion rotation) => new BonePose { Position = new float3(x, 1f, 0f), Rotation = rotation, Scale = 1f };

        public static TestRig Build(int frames = 3, float duration = 1f)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var rig = ref builder.ConstructRoot<RigBlob>();
            rig.Id = new Hash128(1, 2, 3, 4);
            var bones = builder.Allocate(ref rig.Bones, 2);
            bones[0] = new Bone { Parent = -1, Rest = BonePose.Identity, NameHash = 1 };
            bones[1] = new Bone { Parent = 0, Rest = Pose(0f, RestTilt), NameHash = 2 };
            var result = new TestRig { Skeleton = builder.CreateBlobAssetReference<RigBlob>(Allocator.Persistent), Clips = new ClipAsset[2], Masks = new MaskAsset[2] };
            builder.Dispose();
            for (var c = 0; c < 2; c++)
            {
                using var clipBuilder = new BlobBuilder(Allocator.Temp);
                ref var clip = ref clipBuilder.ConstructRoot<Clip>();
                clip.Id = new Hash128((uint)(10 + c), (uint)frames, math.asuint(duration), 1);
                clip.RigId = result.Value.Id;
                clip.NameHash = (uint)(10 + c);
                clip.Duration = duration * (c + 1);
                clip.FrameCount = frames;
                var samples = clipBuilder.Allocate(ref clip.Samples, frames * 2);
                for (var f = 0; f < frames; f++)
                {
                    var turn = math.mul(RestTilt, quaternion.RotateY(0.3f * f * (c + 1)));
                    samples[f * 2] = new BonePose { Position = new float3(0f, 0f, f), Rotation = quaternion.RotateX(0.1f * f), Scale = 1f };
                    samples[f * 2 + 1] = Pose(2f * f * (c + 1), turn);
                }
                var reference = clipBuilder.Allocate(ref clip.Reference, 2);
                reference[0] = samples[0];
                reference[1] = samples[1];
                result.Clips[c] = new ClipAsset { Value = clipBuilder.CreateBlobAssetReference<Clip>(Allocator.Persistent) };
            }
            for (var m = 0; m < 2; m++)
            {
                using var maskBuilder = new BlobBuilder(Allocator.Temp);
                ref var mask = ref maskBuilder.ConstructRoot<BoneMask>();
                mask.Id = new Hash128((uint)(90 + m), 1, 1, 1);
                mask.RigId = result.Value.Id;
                mask.NameHash = m == 1 ? 99u : 0u;
                var weights = maskBuilder.Allocate(ref mask.Weights, 2);
                weights[0] = m == 0 ? 1f : 0f;
                weights[1] = 1f;
                result.Masks[m] = new MaskAsset { Value = maskBuilder.CreateBlobAssetReference<BoneMask>(Allocator.Persistent) };
            }
            return result;
        }

        public static ClipId Clip(TestRig rig, int index) => Names.ClipAt(rig.Clips, index);

        public static MaskId Mask(TestRig rig, int index) => Names.MaskAt(rig.Masks, index);

        public static float Distance(in BonePose a, in BonePose b) =>
            math.max(math.distance(a.Position, b.Position), math.max(math.distance(a.Scale, b.Scale), 1f - math.abs(math.dot(a.Rotation.value, b.Rotation.value))));

        public static ReadOnlySpan<ClipState> View(DynamicBuffer<ClipState> states) => states.AsNativeArray().AsReadOnlySpan();
    }
}
