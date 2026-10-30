using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Hash128 = Unity.Entities.Hash128;

namespace SA
{
    internal struct GpuBone
    {
        public int Parent;
        public float3 RestPosition;
        public float4 RestRotation;
        public float3 RestScale;
    }

    internal struct GpuInstance
    {
        public uint BoneBase, BoneCount, PoseBase, MotionBase, MotionCount;
    }

    internal struct GpuMotion
    {
        public uint SampleBase, FrameCount, Layer, Mode, MaskBase;
        public float Frame, Weight;
    }

    internal struct GpuSkin
    {
        public uint SkinBoneBase, SkinBoneCount;
        public float3x4 MeshFromRig;
    }

    internal struct GpuSkinBone
    {
        public uint Bone;
        public float3x4 Bindpose;
    }

    internal struct GpuSkinJob
    {
        public uint Skin, PoseBase, OutputBase;
    }

    internal struct RigRecord
    {
        public int BoneBase;
    }

    internal static class GpuFrame
    {
        public const uint NoSkin = uint.MaxValue;

        public static float3x4 Identity => new float3x4(new float3(1f, 0f, 0f), new float3(0f, 1f, 0f), new float3(0f, 0f, 1f), float3.zero);

        public static GpuMotion Motion(ref Clip clip, in ClipState state, int sampleBase, int maskBase)
        {
            return new GpuMotion
            {
                SampleBase = (uint)sampleBase,
                FrameCount = (uint)clip.FrameCount,
                Layer = state.Layer,
                Mode = (uint)state.Mode,
                MaskBase = (uint)maskBase,
                Frame = Sampling.Frame(state.Phase, state.Wrap, clip.FrameCount),
                Weight = state.Weight * state.Fade
            };
        }
    }

    internal sealed class GpuArray<T> : IDisposable where T : unmanaged
    {
        public GraphicsBuffer Buffer { get; private set; } = Create(1);
        int uploaded;

        static GraphicsBuffer Create(int count) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(1, count), UnsafeUtility.SizeOf<T>());

        public void Reserve(int count)
        {
            if (count <= Buffer.count)
                return;
            var capacity = math.max(count, Buffer.count * 3 / 2);
            Buffer.Dispose();
            Buffer = Create(capacity);
            uploaded = 0;
        }

        public void Sync(CommandBuffer commands, NativeList<T> source)
        {
            Reserve(source.Length);
            if (source.Length > uploaded)
                commands.SetBufferData(Buffer, source.AsArray(), uploaded, uploaded, source.Length - uploaded);
            uploaded = source.Length;
        }

        public void Write(CommandBuffer commands, NativeArray<T> source, int start, int count)
        {
            if (count > 0)
                commands.SetBufferData(Buffer, source, 0, start, count);
        }

        public void Dispose() => Buffer.Dispose();
    }

    internal sealed class GpuRegistry : IDisposable
    {
        public NativeHashMap<Hash128, RigRecord> Rigs = new NativeHashMap<Hash128, RigRecord>(16, Allocator.Persistent);
        public NativeHashMap<Hash128, int2> Skins = new NativeHashMap<Hash128, int2>(16, Allocator.Persistent);
        public NativeList<GpuBone> Bones = new NativeList<GpuBone>(Allocator.Persistent);
        public NativeList<BonePose> Samples = new NativeList<BonePose>(Allocator.Persistent);
        public NativeHashMap<Hash128, int> ClipSamples = new NativeHashMap<Hash128, int>(16, Allocator.Persistent);
        public NativeHashMap<Hash128, int> MaskBases = new NativeHashMap<Hash128, int>(16, Allocator.Persistent);
        public NativeList<float> Masks = new NativeList<float>(Allocator.Persistent);
        public NativeList<GpuSkin> SkinTable = new NativeList<GpuSkin>(Allocator.Persistent);
        public NativeList<GpuSkinBone> SkinBones = new NativeList<GpuSkinBone>(Allocator.Persistent);
        public NativeList<uint4> Influences = new NativeList<uint4>(Allocator.Persistent);
        public readonly GpuArray<GpuBone> BoneBuffer = new GpuArray<GpuBone>();
        public readonly GpuArray<BonePose> SampleBuffer = new GpuArray<BonePose>();
        public readonly GpuArray<float> MaskBuffer = new GpuArray<float>();
        public readonly GpuArray<GpuSkin> SkinBuffer = new GpuArray<GpuSkin>();
        public readonly GpuArray<GpuSkinBone> SkinBoneBuffer = new GpuArray<GpuSkinBone>();
        public readonly GpuArray<uint4> InfluenceBuffer = new GpuArray<uint4>();

        public void Add(BlobAssetReference<RigBlob> reference)
        {
            ref var rig = ref reference.Value;
            if (Rigs.ContainsKey(rig.Id))
                return;
            var boneCount = rig.Bones.Length;
            Rigs.Add(rig.Id, new RigRecord { BoneBase = Bones.Length });
            for (var b = 0; b < boneCount; b++)
            {
                var rest = rig.Bones[b].Rest;
                Bones.Add(new GpuBone { Parent = rig.Bones[b].Parent, RestPosition = rest.Position, RestRotation = rest.Rotation.value, RestScale = rest.Scale });
            }
        }

        public void Add(BlobAssetReference<Clip> reference)
        {
            ref var clip = ref reference.Value;
            if (ClipSamples.ContainsKey(clip.Id))
                return;
            ClipSamples.Add(clip.Id, Samples.Length);
            for (var s = 0; s < clip.Samples.Length; s++)
                Samples.Add(clip.Samples[s]);
            for (var b = 0; b < clip.Reference.Length; b++)
                Samples.Add(clip.Reference[b]);
        }

        public void Add(BlobAssetReference<BoneMask> reference)
        {
            ref var mask = ref reference.Value;
            if (MaskBases.ContainsKey(mask.Id))
                return;
            MaskBases.Add(mask.Id, Masks.Length);
            for (var b = 0; b < mask.Weights.Length; b++)
                Masks.Add(mask.Weights[b]);
        }

        public void Add(BlobAssetReference<SkinBlob> reference)
        {
            ref var skin = ref reference.Value;
            if (Skins.ContainsKey(skin.Id))
                return;
            Skins.Add(skin.Id, new int2(SkinTable.Length, Influences.Length));
            SkinTable.Add(new GpuSkin { SkinBoneBase = (uint)SkinBones.Length, SkinBoneCount = (uint)skin.Bones.Length, MeshFromRig = skin.MeshFromRig });
            for (var k = 0; k < skin.Bones.Length; k++)
                SkinBones.Add(new GpuSkinBone { Bone = (uint)skin.Bones[k], Bindpose = skin.Bindposes[k] });
            for (var v = 0; v < skin.Influences.Length; v++)
                Influences.Add(skin.Influences[v]);
        }

        public void Sync(CommandBuffer commands)
        {
            BoneBuffer.Sync(commands, Bones);
            SampleBuffer.Sync(commands, Samples);
            MaskBuffer.Sync(commands, Masks);
            SkinBuffer.Sync(commands, SkinTable);
            SkinBoneBuffer.Sync(commands, SkinBones);
            InfluenceBuffer.Sync(commands, Influences);
        }

        public void Dispose()
        {
            Rigs.Dispose();
            Skins.Dispose();
            Bones.Dispose();
            Samples.Dispose();
            ClipSamples.Dispose();
            MaskBases.Dispose();
            Masks.Dispose();
            SkinTable.Dispose();
            SkinBones.Dispose();
            Influences.Dispose();
            BoneBuffer.Dispose();
            SampleBuffer.Dispose();
            MaskBuffer.Dispose();
            SkinBuffer.Dispose();
            SkinBoneBuffer.Dispose();
            InfluenceBuffer.Dispose();
        }
    }

    internal sealed class GpuPipeline : IDisposable
    {
        static readonly int BonesId = Shader.PropertyToID("_Bones");
        static readonly int SamplesId = Shader.PropertyToID("_Samples");
        static readonly int MasksId = Shader.PropertyToID("_Masks");
        static readonly int InstancesId = Shader.PropertyToID("_Instances");
        static readonly int MotionsId = Shader.PropertyToID("_Motions");
        static readonly int SkinsId = Shader.PropertyToID("_Skins");
        static readonly int SkinBonesId = Shader.PropertyToID("_SkinBones");
        static readonly int SkinJobsId = Shader.PropertyToID("_SkinJobs");
        static readonly int PosesId = Shader.PropertyToID("_Poses");
        static readonly int SkinMatricesId = Shader.PropertyToID("_SkinMatrices");
        static readonly int InstanceCountId = Shader.PropertyToID("_InstanceCount");
        static readonly int SkinJobCountId = Shader.PropertyToID("_SkinJobCount");
        static readonly int GlobalSkinMatricesId = Shader.PropertyToID("_SaSkinMatrices");
        static readonly int GlobalInfluencesId = Shader.PropertyToID("_SaInfluences");

        public readonly GpuRegistry Registry = new GpuRegistry();
        public readonly GpuArray<GpuInstance> Instances = new GpuArray<GpuInstance>();
        public readonly GpuArray<GpuMotion> Motions = new GpuArray<GpuMotion>();
        public readonly GpuArray<float3x4> Poses = new GpuArray<float3x4>();
        public readonly GpuArray<GpuSkinJob> SkinJobs = new GpuArray<GpuSkinJob>();
        public readonly GpuArray<float3x4> SkinMatrices = new GpuArray<float3x4>();
        readonly CommandBuffer commands = new CommandBuffer { name = "SA" };
        readonly ComputeShader shader = Resources.Load<ComputeShader>("SA/Animation");
        readonly int evaluateKernel;
        readonly int skinKernel;

        public GpuPipeline()
        {
            evaluateKernel = shader.FindKernel("EvaluatePose");
            skinKernel = shader.FindKernel("Skin");
        }

        public void Execute(NativeArray<GpuInstance> instances, NativeArray<GpuMotion> motions, NativeArray<float3x4> cpuPoses, int gpuPoseCount, NativeArray<GpuSkinJob> skinJobs, int skinMatrixCount)
        {
            commands.Clear();
            Registry.Sync(commands);
            Instances.Reserve(instances.Length);
            Motions.Reserve(motions.Length);
            Poses.Reserve(gpuPoseCount + cpuPoses.Length);
            SkinJobs.Reserve(skinJobs.Length);
            SkinMatrices.Reserve(skinMatrixCount);
            Instances.Write(commands, instances, 0, instances.Length);
            Motions.Write(commands, motions, 0, motions.Length);
            Poses.Write(commands, cpuPoses, gpuPoseCount, cpuPoses.Length);
            SkinJobs.Write(commands, skinJobs, 0, skinJobs.Length);
            commands.BeginSample("SA.GPU");
            if (instances.Length > 0)
            {
                Bind(evaluateKernel, BonesId, Registry.BoneBuffer.Buffer);
                Bind(evaluateKernel, SamplesId, Registry.SampleBuffer.Buffer);
                Bind(evaluateKernel, MasksId, Registry.MaskBuffer.Buffer);
                Bind(evaluateKernel, InstancesId, Instances.Buffer);
                Bind(evaluateKernel, MotionsId, Motions.Buffer);
                Bind(evaluateKernel, PosesId, Poses.Buffer);
                commands.SetComputeIntParam(shader, InstanceCountId, instances.Length);
                commands.DispatchCompute(shader, evaluateKernel, (instances.Length + 63) / 64, 1, 1);
            }
            if (skinJobs.Length > 0)
            {
                Bind(skinKernel, SkinsId, Registry.SkinBuffer.Buffer);
                Bind(skinKernel, SkinBonesId, Registry.SkinBoneBuffer.Buffer);
                Bind(skinKernel, SkinJobsId, SkinJobs.Buffer);
                Bind(skinKernel, PosesId, Poses.Buffer);
                Bind(skinKernel, SkinMatricesId, SkinMatrices.Buffer);
                commands.SetComputeIntParam(shader, SkinJobCountId, skinJobs.Length);
                commands.DispatchCompute(shader, skinKernel, (skinJobs.Length + 63) / 64, 1, 1);
            }
            commands.EndSample("SA.GPU");
            commands.SetGlobalBuffer(GlobalSkinMatricesId, SkinMatrices.Buffer);
            commands.SetGlobalBuffer(GlobalInfluencesId, Registry.InfluenceBuffer.Buffer);
            Graphics.ExecuteCommandBuffer(commands);
        }

        void Bind(int kernel, int id, GraphicsBuffer buffer) => commands.SetComputeBufferParam(shader, kernel, id, buffer);

        public void Dispose()
        {
            Registry.Dispose();
            Instances.Dispose();
            Motions.Dispose();
            Poses.Dispose();
            SkinJobs.Dispose();
            SkinMatrices.Dispose();
            commands.Release();
        }
    }
}
