using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA;
using SA.Animation;
using SA.Motion.Mechanics;
using SA.Motion.Numerics;
using SA.Motion.Unity;

namespace SA.Shape.Unity
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(SharedFamilyPaletteSystem))]
    [UpdateBefore(typeof(global::Unity.Rendering.EntitiesGraphicsSystem))]
    public partial class ShapeSkinPaletteSystem : SystemBase
    {
        internal const int MaxBones = 512;

        PaletteStaging staging;
        EntityQuery skinQuery;

        internal int StagedMatrices { get; private set; }

        internal int StagingCapacity => staging != null && staging.Matrices.IsCreated ? staging.Matrices.Length : 0;

        internal int UploadCalls { get; private set; }

        internal int DirectUploadLimit
        {
            get => staging.DirectLimit;
            set => staging.DirectLimit = value;
        }

        protected override void OnCreate()
        {
            skinQuery = GetEntityQuery(ComponentType.ReadOnly<Skin>(), ComponentType.ReadOnly<SkinBinding>());
            staging = new PaletteStaging();
        }

        protected override void OnDestroy()
        {
            CompleteDependency();
            staging.Dispose();
        }

        protected override void OnUpdate()
        {
            StagedMatrices = 0;
            UploadCalls = 0;
            var skinning = World.GetExistingSystemManaged<SkinningSystem>();
            if (skinning == null)
                return;
            var matrices = skinning.SkinMatrixBuffer;
            if (matrices == null)
                return;
            var count = skinQuery.CalculateEntityCount();
            if (count == 0)
                return;
            staging.Begin(count);
            var rigs = GetComponentLookup<Rig>(true);
            var bones = GetBufferLookup<BoneEntity>(true);
            var deformations = GetBufferLookup<ShapeSkinDeformation>(true);
            var skinType = GetComponentTypeHandle<Skin>(true);
            var chunkBase = skinQuery.CalculateBaseEntityIndexArrayAsync(Allocator.TempJob, Dependency, out var indexed);
            var measured = new ShapeMeasureJob
            {
                Shaped = GetComponentLookup<ShapeDriven>(true),
                Rigs = rigs,
                Bones = bones,
                Deformations = deformations,
                Limit = matrices.count,
                Ranges = staging.Ranges.AsParallelWriter(),
                SkinType = skinType,
                BindingType = GetComponentTypeHandle<SkinBinding>(true),
                ChunkBase = chunkBase
            }.ScheduleParallel(skinQuery, indexed);
            measured.Complete();
            StagedMatrices = staging.Arrange();
            if (StagedMatrices > 0)
            {
                var built = new ShapePaletteJob
                {
                    Rigs = rigs,
                    Bones = bones,
                    Deformations = deformations,
                    Transforms = GetComponentLookup<LocalTransform>(true),
                    Offsets = staging.Offsets,
                    Built = staging.Built,
                    Staging = staging.Matrices,
                    SkinType = skinType,
                    ChunkBase = chunkBase
                }.ScheduleParallel(skinQuery, Dependency);
                built.Complete();
                UploadCalls = staging.Upload(skinning.Pipeline);
            }
            chunkBase.Dispose();
        }
    }
}
