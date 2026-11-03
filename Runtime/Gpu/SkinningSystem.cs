using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace SA
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateBefore(typeof(EntitiesGraphicsSystem))]
    public partial class SkinningSystem : SystemBase
    {
        static readonly ProfilerMarker RegisterMarker = new ProfilerMarker("SA.Register");
        static readonly ProfilerMarker LayoutMarker = new ProfilerMarker("SA.Layout");
        static readonly ProfilerMarker FillMarker = new ProfilerMarker("SA.Fill");
        static readonly ProfilerMarker UploadMarker = new ProfilerMarker("SA.Upload");

        EntityQuery rigs;
        EntityQuery skins;
        EntityQuery changedRigs;
        EntityQuery changedSkins;
        EntityQuery changedAssets;
        GpuPipeline pipeline;
        NativeArray<float4x4> identityMatrix;

        public GraphicsBuffer SkinMatrixBuffer => pipeline?.SkinMatrices.Buffer;
        internal GpuRegistry Registry => pipeline?.Registry;

        protected override void OnCreate()
        {
            rigs = GetEntityQuery(ComponentType.ReadOnly<Rig>(), ComponentType.ReadOnly<ClipState>(), ComponentType.ReadOnly<ClipAsset>(), ComponentType.ReadOnly<MaskAsset>(), ComponentType.ReadWrite<PoseSlot>(), ComponentType.ReadOnly<LocalToWorld>());
            skins = GetEntityQuery(ComponentType.ReadOnly<Skin>(), ComponentType.ReadWrite<SkinBinding>());
            changedRigs = GetEntityQuery(ComponentType.ReadOnly<Rig>());
            changedRigs.SetChangedVersionFilter(ComponentType.ReadOnly<Rig>());
            changedAssets = GetEntityQuery(ComponentType.ReadOnly<ClipAsset>(), ComponentType.ReadOnly<MaskAsset>());
            changedAssets.SetChangedVersionFilter(new[] { ComponentType.ReadOnly<ClipAsset>(), ComponentType.ReadOnly<MaskAsset>() });
            changedSkins = GetEntityQuery(ComponentType.ReadOnly<Skin>());
            changedSkins.SetChangedVersionFilter(ComponentType.ReadOnly<Skin>());
            RequireForUpdate(skins);
            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogWarning("SA: compute shaders are unsupported on this device; skinned meshes render in bind pose.");
                Enabled = false;
                return;
            }
            pipeline = new GpuPipeline();
            identityMatrix = new NativeArray<float4x4>(1, Allocator.Persistent);
            identityMatrix[0] = GpuFrame.Identity;
        }

        protected override void OnDestroy()
        {
            if (identityMatrix.IsCreated)
                identityMatrix.Dispose();
            pipeline?.Dispose();
        }

        protected override void OnUpdate()
        {
            using (RegisterMarker.Auto())
                Register();

            var rigCounts = new NativeArray<int4>(rigs.CalculateChunkCountWithoutFiltering(), Allocator.TempJob);
            var skinCounts = new NativeArray<int2>(skins.CalculateChunkCountWithoutFiltering(), Allocator.TempJob);
            var totals = new NativeArray<int>(6, Allocator.TempJob);
            using (LayoutMarker.Auto())
            {
                var counted = new RigCountJob
                {
                    Rigs = GetComponentTypeHandle<Rig>(true),
                    States = GetBufferTypeHandle<ClipState>(true),
                    Clips = GetBufferTypeHandle<ClipAsset>(true),
                    Masks = GetBufferTypeHandle<MaskAsset>(true),
                    Cpu = GetComponentTypeHandle<CpuEngine>(true),
                    Counts = rigCounts
                }.ScheduleParallel(rigs, Dependency);
                counted = new SkinCountJob { Skins = GetComponentTypeHandle<Skin>(true), Counts = skinCounts }.ScheduleParallel(skins, counted);
                new LayoutJob { RigCounts = rigCounts, SkinCounts = skinCounts, Totals = totals }.Schedule(counted).Complete();
            }

            var instances = new NativeArray<GpuInstance>(totals[0], Allocator.TempJob);
            var motions = new NativeArray<GpuMotion>(totals[2], Allocator.TempJob);
            var cpuPoses = new NativeArray<float4x4>(totals[3], Allocator.TempJob);
            var skinJobs = new NativeArray<GpuSkinJob>(totals[4], Allocator.TempJob);
            using (FillMarker.Auto())
            {
                var filled = new RigFillJob
                {
                    Rigs = GetComponentTypeHandle<Rig>(true),
                    States = GetBufferTypeHandle<ClipState>(true),
                    Clips = GetBufferTypeHandle<ClipAsset>(true),
                    Masks = GetBufferTypeHandle<MaskAsset>(true),
                    Cpu = GetComponentTypeHandle<CpuEngine>(true),
                    Worlds = GetComponentTypeHandle<LocalToWorld>(true),
                    BoneEntities = GetBufferTypeHandle<BoneEntity>(true),
                    Slots = GetComponentTypeHandle<PoseSlot>(),
                    BoneWorlds = GetComponentLookup<LocalToWorld>(true),
                    Records = pipeline.Registry.Rigs,
                    ClipSamples = pipeline.Registry.ClipSamples,
                    MaskBases = pipeline.Registry.MaskBases,
                    Bases = rigCounts,
                    GpuPoseCount = totals[1],
                    Instances = instances,
                    Motions = motions,
                    CpuPoses = cpuPoses
                }.ScheduleParallel(rigs, default);
                filled = new SkinFillJob
                {
                    Skins = GetComponentTypeHandle<Skin>(true),
                    Bindings = GetComponentTypeHandle<SkinBinding>(),
                    Slots = GetComponentLookup<PoseSlot>(true),
                    Shaped = GetComponentLookup<ShapeDriven>(true),
                    Records = pipeline.Registry.Skins,
                    Bases = skinCounts,
                    Jobs = skinJobs
                }.ScheduleParallel(skins, filled);
                filled.Complete();
            }

            using (UploadMarker.Auto())
                pipeline.Execute(instances, motions, cpuPoses, totals[1], skinJobs, totals[5]);
            if (identityMatrix.IsCreated && pipeline != null && pipeline.SkinMatrices.Buffer != null && pipeline.SkinMatrices.Buffer.count > 0)
                pipeline.SkinMatrices.Buffer.SetData(identityMatrix, 0, 0, 1);
            instances.Dispose();
            motions.Dispose();
            cpuPoses.Dispose();
            skinJobs.Dispose();
            rigCounts.Dispose();
            skinCounts.Dispose();
            totals.Dispose();
        }

        void Register()
        {
            using (var changed = changedRigs.ToComponentDataArray<Rig>(Allocator.Temp))
                foreach (var rig in changed)
                    if (rig.Value.IsCreated)
                        pipeline.Registry.Add(rig.Value);
            using (var changed = changedAssets.ToEntityArray(Allocator.Temp))
                foreach (var entity in changed)
                {
                    foreach (var clip in EntityManager.GetBuffer<ClipAsset>(entity, true))
                        if (clip.Value.IsCreated)
                            pipeline.Registry.Add(clip.Value);
                    foreach (var mask in EntityManager.GetBuffer<MaskAsset>(entity, true))
                        if (mask.Value.IsCreated)
                            pipeline.Registry.Add(mask.Value);
                }
            using (var changed = changedSkins.ToComponentDataArray<Skin>(Allocator.Temp))
                foreach (var skin in changed)
                    if (skin.Value.IsCreated)
                        pipeline.Registry.Add(skin.Value);
        }

        static bool OnCpu(in ArchetypeChunk chunk, ref ComponentTypeHandle<CpuEngine> handle, int index) =>
            chunk.Has(ref handle) && chunk.IsComponentEnabled(ref handle, index);

        [BurstCompile]
        struct RigCountJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<Rig> Rigs;
            [ReadOnly] public BufferTypeHandle<ClipState> States;
            [ReadOnly] public BufferTypeHandle<ClipAsset> Clips;
            [ReadOnly] public BufferTypeHandle<MaskAsset> Masks;
            [ReadOnly] public ComponentTypeHandle<CpuEngine> Cpu;
            [NativeDisableParallelForRestriction] public NativeArray<int4> Counts;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var rigs = chunk.GetNativeArray(ref Rigs);
                var states = chunk.GetBufferAccessor(ref States);
                var clips = chunk.GetBufferAccessor(ref Clips);
                var masks = chunk.GetBufferAccessor(ref Masks);
                var counts = int4.zero;
                for (var i = 0; i < chunk.Count; i++)
                {
                    var reference = rigs[i].Value;
                    if (!reference.IsCreated)
                        continue;
                    ref var rig = ref reference.Value;
                    if (OnCpu(chunk, ref Cpu, i))
                    {
                        counts.w += rig.Bones.Length;
                        continue;
                    }
                    counts.x += 1;
                    counts.y += rig.Bones.Length;
                    var list = states[i];
                    for (var s = 0; s < list.Length; s++)
                        if (Evaluation.Contributes(ref rig, clips[i].AsNativeArray().AsReadOnlySpan(), masks[i].AsNativeArray().AsReadOnlySpan(), list[s]))
                            counts.z++;
                }
                Counts[unfilteredChunkIndex] = counts;
            }
        }

        [BurstCompile]
        struct SkinCountJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<Skin> Skins;
            [NativeDisableParallelForRestriction] public NativeArray<int2> Counts;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var skins = chunk.GetNativeArray(ref Skins);
                var bones = 0;
                for (var i = 0; i < chunk.Count; i++)
                {
                    var reference = skins[i].Value;
                    if (reference.IsCreated)
                        bones += reference.Value.Bones.Length;
                }
                Counts[unfilteredChunkIndex] = new int2(chunk.Count, bones);
            }
        }

        [BurstCompile]
        struct LayoutJob : IJob
        {
            public NativeArray<int4> RigCounts;
            public NativeArray<int2> SkinCounts;
            public NativeArray<int> Totals;

            public void Execute()
            {
                var rigSum = int4.zero;
                for (var i = 0; i < RigCounts.Length; i++)
                {
                    var count = RigCounts[i];
                    RigCounts[i] = rigSum;
                    rigSum += count;
                }
                var skinSum = new int2(0, 1);
                for (var i = 0; i < SkinCounts.Length; i++)
                {
                    var count = SkinCounts[i];
                    SkinCounts[i] = skinSum;
                    skinSum += count;
                }
                Totals[0] = rigSum.x;
                Totals[1] = rigSum.y;
                Totals[2] = rigSum.z;
                Totals[3] = rigSum.w;
                Totals[4] = skinSum.x;
                Totals[5] = skinSum.y;
            }
        }

        [BurstCompile]
        struct RigFillJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<Rig> Rigs;
            [ReadOnly] public BufferTypeHandle<ClipState> States;
            [ReadOnly] public BufferTypeHandle<ClipAsset> Clips;
            [ReadOnly] public BufferTypeHandle<MaskAsset> Masks;
            [ReadOnly] public ComponentTypeHandle<CpuEngine> Cpu;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> Worlds;
            [ReadOnly] public BufferTypeHandle<BoneEntity> BoneEntities;
            public ComponentTypeHandle<PoseSlot> Slots;
            [ReadOnly] public ComponentLookup<LocalToWorld> BoneWorlds;
            [ReadOnly] public NativeHashMap<Hash128, RigRecord> Records;
            [ReadOnly] public NativeHashMap<Hash128, int> ClipSamples;
            [ReadOnly] public NativeHashMap<Hash128, int> MaskBases;
            [ReadOnly] public NativeArray<int4> Bases;
            public int GpuPoseCount;
            [NativeDisableParallelForRestriction] public NativeArray<GpuInstance> Instances;
            [NativeDisableParallelForRestriction] public NativeArray<GpuMotion> Motions;
            [NativeDisableParallelForRestriction] public NativeArray<float4x4> CpuPoses;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var rigs = chunk.GetNativeArray(ref Rigs);
                var states = chunk.GetBufferAccessor(ref States);
                var clips = chunk.GetBufferAccessor(ref Clips);
                var masks = chunk.GetBufferAccessor(ref Masks);
                var worlds = chunk.GetNativeArray(ref Worlds);
                var slots = chunk.GetNativeArray(ref Slots);
                var hasBones = chunk.Has(ref BoneEntities);
                var boneEntities = hasBones ? chunk.GetBufferAccessor(ref BoneEntities) : default;
                var at = Bases[unfilteredChunkIndex];
                for (var i = 0; i < chunk.Count; i++)
                {
                    var reference = rigs[i].Value;
                    var slot = -1;
                    if (reference.IsCreated)
                    {
                        ref var rig = ref reference.Value;
                        var boneCount = rig.Bones.Length;
                        if (OnCpu(chunk, ref Cpu, i))
                        {
                            slot = GpuPoseCount + at.w;
                            var rigFromWorld = math.inverse(worlds[i].Value);
                            for (var b = 0; b < boneCount; b++)
                            {
                                var bone = hasBones && b < boneEntities[i].Length ? boneEntities[i][b].Value : Entity.Null;
                                CpuPoses[at.w + b] = BoneWorlds.TryGetComponent(bone, out var world)
                                    ? GpuFrame.Pack(PoseAlgebra.Affine(math.mul(rigFromWorld, world.Value)))
                                    : GpuFrame.Identity;
                            }
                            at.w += boneCount;
                        }
                        else if (Records.TryGetValue(rig.Id, out var record))
                        {
                            slot = at.y;
                            var motionBase = at.z;
                            var list = states[i];
                            for (var s = 0; s < list.Length; s++)
                                if (Evaluation.Contributes(ref rig, clips[i].AsNativeArray().AsReadOnlySpan(), masks[i].AsNativeArray().AsReadOnlySpan(), list[s]))
                                {
                                    var state = list[s];
                                    ref var clip = ref clips[i][state.Clip.Index].Value.Value;
                                    var mask = masks[i][state.Mask.Index].Value;
                                    Motions[at.z++] = GpuFrame.Motion(ref clip, state, ClipSamples[clip.Id], MaskBases[mask.Value.Id]);
                                }
                            Instances[at.x++] = new GpuInstance
                            {
                                BoneBase = (uint)record.BoneBase,
                                BoneCount = (uint)boneCount,
                                PoseBase = (uint)slot,
                                MotionBase = (uint)motionBase,
                                MotionCount = (uint)(at.z - motionBase)
                            };
                            at.y += boneCount;
                        }
                    }
                    slots[i] = new PoseSlot { Base = slot };
                }
            }
        }

        [BurstCompile]
        unsafe struct SkinFillJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<Skin> Skins;
            public ComponentTypeHandle<SkinBinding> Bindings;
            [ReadOnly] public ComponentLookup<PoseSlot> Slots;
            [ReadOnly] public ComponentLookup<ShapeDriven> Shaped;
            [ReadOnly] public NativeHashMap<Hash128, int2> Records;
            [ReadOnly] public NativeArray<int2> Bases;
            [NativeDisableParallelForRestriction] public NativeArray<GpuSkinJob> Jobs;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var skins = chunk.GetNativeArray(ref Skins);
                var current = (SkinBinding*)chunk.GetComponentDataPtrRO(ref Bindings);
                var next = new NativeArray<SkinBinding>(chunk.Count, Allocator.Temp);
                var at = Bases[unfilteredChunkIndex];
                var changed = false;
                for (var i = 0; i < chunk.Count; i++)
                {
                    var skin = skins[i];
                    var job = new GpuSkinJob { Skin = GpuFrame.NoSkin };
                    var binding = new SkinBinding();
                    if (skin.Value.IsCreated)
                    {
                        ref var blob = ref skin.Value.Value;
                        if (Records.TryGetValue(blob.Id, out var record) && Slots.TryGetComponent(skin.Rig, out var slot) && slot.Base >= 0)
                        {
                            job = new GpuSkinJob { Skin = (uint)record.x, PoseBase = (uint)slot.Base, OutputBase = (uint)at.y };
                            binding = new SkinBinding { MatrixBase = (uint)at.y, InfluenceBase = (uint)record.y };
                            if (Shaped.HasComponent(skin.Rig))
                                job.Skin = GpuFrame.NoSkin;
                        }
                        at.y += blob.Bones.Length;
                    }
                    Jobs[at.x + i] = job;
                    next[i] = binding;
                    changed |= current[i].MatrixBase != binding.MatrixBase || current[i].InfluenceBase != binding.InfluenceBase;
                }
                if (changed)
                    chunk.GetNativeArray(ref Bindings).CopyFrom(next);
            }
        }
    }
}
