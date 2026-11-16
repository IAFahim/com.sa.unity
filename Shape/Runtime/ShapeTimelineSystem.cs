using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using SA;
using SA.Animation;
using SA.Motion.Inertialization;
using SA.Motion.Numerics;
using SA.Motion.Unity;
using SA.Shape;
using SharedMode = SA.Animation.ClipMode;

namespace SA.Shape.Unity
{
    [UpdateInGroup(typeof(AnimationSystemGroup))]
    [global::Unity.Scripting.LifecycleManagement.AutoStaticsCleanup]
    public partial class ShapeTimelineSystem : SystemBase
    {
        public const int CanonicalBones = 56;
        internal const int MaxRigBones = 128;
        internal const int BoneCountFault = 1;
        internal const int DefinitionFault = 2;
        internal const int ChannelFault = 3;
        internal const int RowsFault = 4;
        internal const int StepFault = 5;
        internal const int PhysicalFault = 6;
        internal const int GeometryFault = 7;
        internal const int MorphFault = 8;
        internal const int BoneEntityFault = 9;
        internal const int FamilyFault = 10;
        internal const int SampleFault = 11;
        internal const int BlendFault = 12;
        internal const int RemapFault = 13;
        internal const int SmoothFault = 14;

        EntityQuery rigs;
        EntityQuery fresh;
        EntityQuery changed;
        EntityQuery stale;
        EntityQuery tracked;
        EntityQuery ready;
        readonly HashSet<long> warned = new HashSet<long>();
        NativeArray<Pose> familyReference;
        NativeArray<int> canonicalParents;
        NativeQueue<SharedFault> faults;
        ComponentTypeSet stateTypes;
        ComponentTypeSet ownTypes;
        SharedFamilySystem shared;
        Action<string> announce;
        JobHandle pending;
        uint stamp;

        static ShapeTimelineSystem latest;
        static string lastWarning = string.Empty;
        static int lastActive;
        static string lastAction = string.Empty;
        static string lastSmooth = string.Empty;
        static int trackedRigCount;

        public static string LastWarning
        {
            get
            {
                ReadLatest();
                return lastWarning ?? string.Empty;
            }
        }

        public static int LastEntityCount { get; private set; }

        public static float LastStep { get; private set; }

        public static int LastActive
        {
            get
            {
                ReadLatest();
                return lastActive;
            }
        }

        public static string LastAction
        {
            get
            {
                ReadLatest();
                return lastAction ?? string.Empty;
            }
        }

        public static string LastSmooth
        {
            get
            {
                ReadLatest();
                return lastSmooth ?? string.Empty;
            }
        }

        public static int TrackedRigCount
        {
            get
            {
                ReadLatest();
                return trackedRigCount;
            }
        }

        protected override void OnCreate()
        {
            rigs = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SharedTimeline, Rig, SharedClipAsset, SharedContribution>()
                .WithAll<SharedClock, SharedRetarget, BoneEntity>()
                .WithAll<ShapeDriven, ShapeDefinitionRef, ShapeChannelState, ShapeState>()
                .Build(this);
            fresh = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SharedTimeline, Rig, SharedClipAsset, SharedContribution>()
                .WithAll<SharedClock, SharedRetarget, BoneEntity>()
                .WithAll<ShapeDriven, ShapeDefinitionRef, ShapeChannelState, ShapeState>()
                .WithNone<ShapePoseState>()
                .Build(this);
            changed = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SharedTimeline, Rig, SharedClipAsset, SharedRetarget>()
                .WithAll<ShapeDriven>()
                .WithAllRW<SharedTableRef>()
                .WithAllRW<SharedClipLayoutRef>()
                .Build(this);
            changed.SetChangedVersionFilter(new[] { ComponentType.ReadOnly<SharedClipAsset>(), ComponentType.ReadOnly<SharedRetarget>() });
            stale = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ShapePoseState>()
                .WithNone<ShapeDriven>()
                .Build(this);
            tracked = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ShapeDriven, ShapePoseState>()
                .Build(this);
            ready = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SharedTimeline, ShapeDriven, SharedRetarget>()
                .WithAll<Rig, SharedTableRef, SharedClipAsset, SharedClipLayoutRef, BoneEntity, ShapeDefinitionRef>()
                .WithAllRW<SharedContribution, SharedClock>()
                .WithAllRW<ShapeChannelState, ShapeState>()
                .WithAllRW<ShapePoseState, ShapeBoneFrame>()
                .WithAllRW<ShapeNeutralPose, ShapeDisplayedPose>()
                .WithAllRW<ShapeBoneOffset, ShapeScalar>()
                .WithAllRW<ShapePreviousContribution, ShapePreviousLayer>()
                .Build(this);
            ownTypes = new ComponentTypeSet(new[]
            {
                ComponentType.ReadWrite<ShapePoseState>(),
                ComponentType.ReadWrite<ShapeBoneFrame>(),
                ComponentType.ReadWrite<ShapeNeutralPose>(),
                ComponentType.ReadWrite<ShapeDisplayedPose>(),
                ComponentType.ReadWrite<ShapeBoneOffset>(),
                ComponentType.ReadWrite<ShapeScalar>(),
                ComponentType.ReadWrite<ShapePreviousContribution>(),
                ComponentType.ReadWrite<ShapePreviousLayer>()
            });
            stateTypes = new ComponentTypeSet(new[]
            {
                ComponentType.ReadWrite<ShapePoseState>(),
                ComponentType.ReadWrite<ShapeBoneFrame>(),
                ComponentType.ReadWrite<ShapeNeutralPose>(),
                ComponentType.ReadWrite<ShapeDisplayedPose>(),
                ComponentType.ReadWrite<ShapeBoneOffset>(),
                ComponentType.ReadWrite<ShapeScalar>(),
                ComponentType.ReadWrite<ShapePreviousContribution>(),
                ComponentType.ReadWrite<ShapePreviousLayer>(),
                ComponentType.ReadWrite<SharedTableRef>(),
                ComponentType.ReadWrite<SharedClipLayoutRef>()
            });
            familyReference = new NativeArray<Pose>(CanonicalBones, Allocator.Persistent);
            canonicalParents = new NativeArray<int>(CanonicalBones, Allocator.Persistent);
            faults = new NativeQueue<SharedFault>(Allocator.Persistent);
            CanonicalHumanoid.WriteReference(canonicalParents.AsSpan(), familyReference.AsSpan());
            shared = World.GetOrCreateSystemManaged<SharedFamilySystem>();
            announce = Announce;
        }

        protected override void OnDestroy()
        {
            pending.Complete();
            CompleteDependency();
            if (ReferenceEquals(latest, this))
                latest = null;
            familyReference.Dispose();
            canonicalParents.Dispose();
            faults.Dispose();
            warned.Clear();
        }

        protected override void OnUpdate()
        {
            pending.Complete();
            CompleteDependency();
            Report();
            if (!stale.IsEmptyIgnoreFilter)
                EntityManager.RemoveComponent(stale, ownTypes);
            if (!fresh.IsEmptyIgnoreFilter)
                EntityManager.AddComponent(fresh, stateTypes);
            if (!changed.IsEmpty)
            {
                var entities = changed.ToEntityArray(Allocator.Temp);
                shared.ResolveLayouts(entities, "shape", announce);
                entities.Dispose();
            }
            latest = this;
            LastStep = SystemAPI.Time.DeltaTime;
            LastEntityCount = rigs.CalculateEntityCount();
            stamp++;
            pending = new ShapeRigJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                Stamp = stamp,
                EntityType = GetEntityTypeHandle(),
                RigType = GetComponentTypeHandle<Rig>(true),
                TableType = GetComponentTypeHandle<SharedTableRef>(true),
                ClipType = GetBufferTypeHandle<SharedClipAsset>(true),
                LayoutType = GetBufferTypeHandle<SharedClipLayoutRef>(true),
                ContributionType = GetBufferTypeHandle<SharedContribution>(),
                BoneType = GetBufferTypeHandle<BoneEntity>(true),
                ClockType = GetComponentTypeHandle<SharedClock>(),
                DefinitionType = GetComponentTypeHandle<ShapeDefinitionRef>(true),
                ChannelType = GetBufferTypeHandle<ShapeChannelState>(),
                ShapeStateType = GetComponentTypeHandle<ShapeState>(),
                PoseStateType = GetComponentTypeHandle<ShapePoseState>(),
                BoneFrameType = GetBufferTypeHandle<ShapeBoneFrame>(),
                NeutralType = GetBufferTypeHandle<ShapeNeutralPose>(),
                DisplayedType = GetBufferTypeHandle<ShapeDisplayedPose>(),
                OffsetType = GetBufferTypeHandle<ShapeBoneOffset>(),
                ScalarType = GetBufferTypeHandle<ShapeScalar>(),
                PreviousContributionType = GetBufferTypeHandle<ShapePreviousContribution>(),
                PreviousLayerType = GetBufferTypeHandle<ShapePreviousLayer>(),
                FamilyReference = familyReference,
                CanonicalParents = canonicalParents,
                Transforms = GetComponentLookup<LocalTransform>(),
                PostTransforms = GetComponentLookup<PostTransformMatrix>(),
                Physicals = GetComponentLookup<ShapePhysical>(),
                Contacts = GetComponentLookup<ShapeContactState>(),
                Deformations = GetBufferLookup<ShapeSkinDeformation>(),
                MorphWeights = GetBufferLookup<ShapeMorphWeight>(),
                Faults = faults.AsParallelWriter()
            }.ScheduleParallel(ready, Dependency);
            Dependency = pending;
        }

        static void ReadLatest()
        {
            var system = latest;
            if (system == null || system.World == null || !system.World.IsCreated)
                return;
            system.ReadLast();
        }

        void ReadLast()
        {
            pending.Complete();
            Report();
            var count = 0;
            var found = false;
            var entities = tracked.ToEntityArray(Allocator.Temp);
            for (var i = entities.Length - 1; i >= 0; i--)
            {
                var state = EntityManager.GetComponentData<ShapePoseState>(entities[i]);
                if (state.BoneCount > 0)
                    count++;
                if (found || state.Stamp != stamp)
                    continue;
                found = true;
                lastActive = state.Active;
                lastAction = state.Action.ToString();
                lastSmooth = state.Smooth.ToString();
            }
            entities.Dispose();
            trackedRigCount = count;
        }

        void Announce(string message)
        {
            lastWarning = message;
            UnityEngine.Debug.LogWarning(message);
        }

        void Warn(int kind, long subject, string message)
        {
            if (warned.Add(((long)kind << 40) ^ subject))
                Announce(message);
        }

        void Report()
        {
            while (faults.TryDequeue(out var fault))
            {
                var kind = fault.Kind;
                var error = ((AnimationError)fault.First).ToString();
                var message = kind == BoneCountFault ? "SA: shape rig has " + fault.First + " bones, expected 1.." + MaxRigBones + "."
                    : kind == DefinitionFault ? "SA: shape rig is missing its shape definition."
                    : kind == ChannelFault ? "SA: shape rig has " + fault.First + " channel states for " + fault.Second + " definition channels."
                    : kind == RowsFault ? "SA: shape rig table has " + fault.First + " rows for " + fault.Second + " bones."
                    : kind == StepFault ? "SA: shape parameter step failed (" + error + ")."
                    : kind == PhysicalFault ? "SA: shape rig holds its physical snapshot while live-physical; customize kinematic, then rebuild."
                    : kind == GeometryFault ? "SA: shape geometry failed (" + error + "). Check that the definition targets this rig."
                    : kind == MorphFault ? "SA: shape morph evaluation failed. Check that the definition targets this rig."
                    : kind == BoneEntityFault ? "SA: shape rig has " + fault.First + " bone entities for " + fault.Second + " bones."
                    : kind == FamilyFault ? "SA: shape rig mixes clip families in one tick."
                    : kind == SampleFault ? "SA: shape clip sample failed (" + error + ")."
                    : kind == BlendFault ? "SA: shape blend failed (" + error + ")."
                    : kind == RemapFault ? "SA: shape retarget failed (" + error + ")."
                    : "SA: shape timeline smoothing failed (" + error + ").";
                Warn(kind, fault.Rig.Index, message);
            }
        }
    }
}
