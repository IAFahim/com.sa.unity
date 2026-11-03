using System;
using Unity.Collections;
using Unity.Entities;
using SA;
using SA.Animation;
using SA.Motion.Numerics;

namespace SA.Shape.Unity
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial class ShapePhysicalSyncSystem : SystemBase
    {
        public static string LastDiagnostic { get; private set; } = string.Empty;
        public static int LastRebuilt { get; private set; }
        public static int LastRefused { get; private set; }
        public static int LastStale { get; private set; }

        const int MaxBones = 128;

        EntityQuery physicalQuery;
        NativeArray<int> parents;
        NativeArray<ShapePhysicalRest> restSnapshot;
        NativeArray<Affine> affines;
        NativeArray<float> previous;
        NativeArray<ShapePhysicalBody> nextBodies;
        NativeArray<ShapePhysicalJoint> nextJoints;

        protected override void OnCreate()
        {
            physicalQuery = GetEntityQuery(
                ComponentType.ReadOnly<ShapeDriven>(),
                ComponentType.ReadWrite<ShapePhysical>(),
                ComponentType.ReadOnly<ShapeState>(),
                ComponentType.ReadWrite<ShapeChannelState>(),
                ComponentType.ReadOnly<ShapeSkinDeformation>(),
                ComponentType.ReadOnly<ShapePhysicalRest>(),
                ComponentType.ReadWrite<ShapePhysicalBody>(),
                ComponentType.ReadWrite<ShapePhysicalJoint>(),
                ComponentType.ReadOnly<ShapeContactState>(),
                ComponentType.ReadOnly<Rig>());
            parents = new NativeArray<int>(MaxBones, Allocator.Persistent);
            restSnapshot = new NativeArray<ShapePhysicalRest>(MaxBones, Allocator.Persistent);
            affines = new NativeArray<Affine>(MaxBones, Allocator.Persistent);
            previous = new NativeArray<float>(MaxBones, Allocator.Persistent);
            nextBodies = new NativeArray<ShapePhysicalBody>(MaxBones, Allocator.Persistent);
            nextJoints = new NativeArray<ShapePhysicalJoint>(MaxBones, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            parents.Dispose();
            restSnapshot.Dispose();
            affines.Dispose();
            previous.Dispose();
            nextBodies.Dispose();
            nextJoints.Dispose();
        }

        protected override void OnUpdate()
        {
            LastRebuilt = 0;
            LastRefused = 0;
            LastStale = 0;
            using var entities = physicalQuery.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
                SyncEntity(entity);
        }

        void SyncEntity(Entity entity)
        {
            var physical = EntityManager.GetComponentData<ShapePhysical>(entity);
            var shapeState = EntityManager.GetComponentData<ShapeState>(entity);
            var channels = EntityManager.GetBuffer<ShapeChannelState>(entity);
            var rest = EntityManager.GetBuffer<ShapePhysicalRest>(entity);
            var bodies = EntityManager.GetBuffer<ShapePhysicalBody>(entity);
            var boneCount = rest.Length;
            if (boneCount == 0)
                return;
            if (physical.LivePhysical != 0)
            {
                var dirty = bodies.Length != boneCount || shapeState.Revision != physical.AppliedRevision || ShapePhysicalSync.HasPendingChange(channels);
                if (!dirty)
                    return;
                ShapePhysicalSync.RevertRequests(channels);
                physical.PendingRebuild = 0;
                if (bodies.Length != boneCount)
                    physical.Diagnostic = ShapePhysicalSync.LivePhysicalUnbuilt;
                else
                    physical.Diagnostic = ShapePhysicalSync.LivePhysicalRefusal;
                EntityManager.SetComponentData(entity, physical);
                LastDiagnostic = physical.Diagnostic.ToString();
                LastRefused++;
                return;
            }
            var deformations = EntityManager.GetBuffer<ShapeSkinDeformation>(entity);
            var joints = EntityManager.GetBuffer<ShapePhysicalJoint>(entity);
            var contact = EntityManager.GetComponentData<ShapeContactState>(entity);
            if (bodies.Length == boneCount && joints.Length == boneCount && physical.PendingRebuild == 0)
            {
                if (shapeState.Revision != physical.AppliedRevision)
                {
                    physical.Diagnostic = ShapePhysicalSync.StaleSnapshot(physical.AppliedRevision, shapeState.Revision);
                    EntityManager.SetComponentData(entity, physical);
                    LastDiagnostic = physical.Diagnostic.ToString();
                    LastStale++;
                    return;
                }
                if (physical.Diagnostic.Length > 0)
                {
                    physical.Diagnostic = default;
                    EntityManager.SetComponentData(entity, physical);
                }
                return;
            }
            if (deformations.Length != boneCount || contact.Revision != shapeState.Revision)
            {
                physical.Diagnostic = ShapePhysicalSync.WaitingForGeometry(shapeState.Revision);
                EntityManager.SetComponentData(entity, physical);
                LastDiagnostic = physical.Diagnostic.ToString();
                return;
            }
            var rigReference = EntityManager.GetComponentData<Rig>(entity);
            ref var rig = ref rigReference.Value.Value;
            if (rig.Bones.Length != boneCount || boneCount > MaxBones)
            {
                physical.PendingRebuild = 0;
                physical.Diagnostic = ShapePhysicalSync.RebuildFailed(AnimationError.InvalidTable);
                EntityManager.SetComponentData(entity, physical);
                LastDiagnostic = physical.Diagnostic.ToString();
                return;
            }
            var range = parents.GetSubArray(0, boneCount);
            for (var b = 0; b < boneCount; b++)
                range[b] = rig.Bones[b].Parent;
            var restRange = restSnapshot.GetSubArray(0, boneCount);
            for (var b = 0; b < boneCount; b++)
                restRange[b] = rest[b];
            var affineRange = affines.GetSubArray(0, boneCount);
            for (var b = 0; b < boneCount; b++)
                affineRange[b] = deformations[b].Value;
            var massRange = previous.GetSubArray(0, boneCount);
            var hasPrevious = bodies.Length == boneCount;
            for (var b = 0; b < boneCount; b++)
                massRange[b] = hasPrevious ? bodies[b].Mass : 0f;
            var bodyRange = nextBodies.GetSubArray(0, boneCount);
            var jointRange = nextJoints.GetSubArray(0, boneCount);
            var error = ShapePhysicalSync.RefitRig(
                range.AsSpan(),
                restRange.AsSpan(),
                affineRange.AsSpan(),
                massRange.AsSpan(),
                ShapePhysicalControl.PolicyOrDefault(physical.MassPolicy),
                bodyRange.AsSpan(),
                jointRange.AsSpan());
            if (error != AnimationError.None)
            {
                physical.PendingRebuild = 0;
                physical.Diagnostic = ShapePhysicalSync.RebuildFailed(error);
                EntityManager.SetComponentData(entity, physical);
                LastDiagnostic = physical.Diagnostic.ToString();
                return;
            }
            if (bodies.Length != boneCount)
                bodies.ResizeUninitialized(boneCount);
            if (joints.Length != boneCount)
                joints.ResizeUninitialized(boneCount);
            for (var b = 0; b < boneCount; b++)
            {
                bodies[b] = nextBodies[b];
                joints[b] = nextJoints[b];
            }
            physical.AppliedRevision = shapeState.Revision;
            physical.PendingRebuild = 0;
            physical.Diagnostic = default;
            EntityManager.SetComponentData(entity, physical);
            LastRebuilt++;
        }

    }
}
