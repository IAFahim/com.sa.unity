using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA.Animation;
using SA.Animation.Content;
using SA.Motion.Inertialization;
using SA.Motion.Numerics;
using SA.Motion.Unity;
using SharedMode = SA.Animation.ClipMode;

namespace SA
{
    public static class SharedFamilyControl
    {
        public const int MaxContributions = 8;

        public static void PlaySingle(ref DynamicBuffer<SharedContribution> contributions, int clip)
        {
            for (var i = 0; i < contributions.Length; i++)
            {
                var contribution = contributions[i];
                contribution.Weight = contribution.Clip == clip ? 1f : 0f;
                contributions[i] = contribution;
            }
        }

        public static void Seek(ref SharedClock clock, double seconds)
        {
            clock.Seconds = seconds;
            clock.Seek = 1;
        }

        public static void SetPaused(ref SharedClock clock, bool paused)
        {
            clock.Paused = paused ? (byte)1 : (byte)0;
        }
    }

    [UpdateInGroup(typeof(AnimationSystemGroup))]
    [UpdateAfter(typeof(CpuPoseSystem))]
    [Unity.Scripting.LifecycleManagement.AutoStaticsCleanup]
    public partial class SharedFamilySystem : SystemBase
    {
        public const int CanonicalBones = 56;
        const int MaxRigBones = 128;
        const int BoneCountFault = 1;
        const int BoneEntityFault = 2;
        const int FamilyFault = 3;
        const int SampleFault = 4;
        const int BlendFault = 5;
        const int IdentityFault = 6;
        const int RemapFault = 7;
        const int SmoothFault = 8;
        const int TableMissingFault = 9;
        const int TableLoadFault = 10;
        const int ClipLoadFault = 11;

        EntityQuery rigs;
        EntityQuery fresh;
        EntityQuery changed;
        EntityQuery tracked;
        readonly Dictionary<AssetId, BlobAssetReference<SharedClipLayout>> clipLayouts = new Dictionary<AssetId, BlobAssetReference<SharedClipLayout>>();
        readonly Dictionary<uint4, BlobAssetReference<SharedTableLayout>> tableLayouts = new Dictionary<uint4, BlobAssetReference<SharedTableLayout>>();
        readonly Dictionary<uint4, AnimationError> tableErrors = new Dictionary<uint4, AnimationError>();
        readonly HashSet<long> warned = new HashSet<long>();
        NativeArray<Pose> familyReference;
        NativeArray<int> canonicalParents;
        NativeQueue<SharedFault> faults;
        ComponentTypeSet stateTypes;
        uint stamp;

        static SharedFamilySystem latest;
        static int lastActive;
        static string lastAction = string.Empty;
        static string lastSmooth = string.Empty;

        public static string LastWarning { get; private set; } = string.Empty;

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

        protected override void OnCreate()
        {
            rigs = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SharedTimeline, Rig, SharedClipAsset, SharedContribution>()
                .WithAll<SharedClock, SharedRetarget, BoneEntity>()
                .WithNone<ShapeDriven>()
                .Build(this);
            fresh = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SharedTimeline, Rig, SharedClipAsset, SharedContribution>()
                .WithAll<SharedClock, SharedRetarget, BoneEntity>()
                .WithNone<ShapeDriven, SharedPoseState>()
                .Build(this);
            changed = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SharedTimeline, Rig, SharedClipAsset, SharedRetarget>()
                .WithAllRW<SharedTableRef>()
                .WithAllRW<SharedClipLayoutRef>()
                .WithNone<ShapeDriven>()
                .Build(this);
            changed.SetChangedVersionFilter(new[] { ComponentType.ReadOnly<SharedClipAsset>(), ComponentType.ReadOnly<SharedRetarget>() });
            tracked = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SharedTimeline, SharedPoseState>()
                .WithNone<ShapeDriven>()
                .Build(this);
            stateTypes = new ComponentTypeSet(new[]
            {
                ComponentType.ReadWrite<SharedPoseState>(),
                ComponentType.ReadWrite<SharedTableRef>(),
                ComponentType.ReadWrite<SharedClipLayoutRef>(),
                ComponentType.ReadWrite<SharedBoneState>(),
                ComponentType.ReadWrite<SharedCommittedPose>(),
                ComponentType.ReadWrite<SharedPreviousContribution>(),
                ComponentType.ReadWrite<SharedPreviousLayer>()
            });
            familyReference = new NativeArray<Pose>(CanonicalBones, Allocator.Persistent);
            canonicalParents = new NativeArray<int>(CanonicalBones, Allocator.Persistent);
            faults = new NativeQueue<SharedFault>(Allocator.Persistent);
            CanonicalHumanoid.WriteReference(canonicalParents.AsSpan(), familyReference.AsSpan());
        }

        protected override void OnDestroy()
        {
            CompleteDependency();
            if (ReferenceEquals(latest, this))
                latest = null;
            familyReference.Dispose();
            canonicalParents.Dispose();
            faults.Dispose();
            foreach (var layout in clipLayouts.Values)
                if (layout.IsCreated)
                    layout.Dispose();
            foreach (var layout in tableLayouts.Values)
                if (layout.IsCreated)
                    layout.Dispose();
            clipLayouts.Clear();
            tableLayouts.Clear();
            tableErrors.Clear();
            warned.Clear();
        }

        protected override void OnUpdate()
        {
            CompleteDependency();
            Report();
            if (!fresh.IsEmptyIgnoreFilter)
                EntityManager.AddComponent(fresh, stateTypes);
            Resolve();
            latest = this;
            LastStep = SystemAPI.Time.DeltaTime;
            LastEntityCount = rigs.CalculateEntityCount();
            stamp++;
            Dependency = new RigJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                Stamp = stamp,
                FamilyReference = familyReference,
                CanonicalParents = canonicalParents,
                Transforms = SystemAPI.GetComponentLookup<LocalTransform>(),
                PostTransforms = SystemAPI.GetComponentLookup<PostTransformMatrix>(),
                Faults = faults.AsParallelWriter()
            }.ScheduleParallel(Dependency);
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
            var entities = tracked.ToEntityArray(Allocator.Temp);
            for (var i = entities.Length - 1; i >= 0; i--)
            {
                var state = EntityManager.GetComponentData<SharedPoseState>(entities[i]);
                if (state.Stamp != stamp)
                    continue;
                lastActive = state.Active;
                lastAction = state.Action.ToString();
                lastSmooth = state.Smooth.ToString();
                break;
            }
            entities.Dispose();
        }

        void Warn(int kind, long subject, string message)
        {
            if (!warned.Add(((long)kind << 40) ^ subject))
                return;
            LastWarning = message;
            UnityEngine.Debug.LogWarning(message);
        }

        void Report()
        {
            while (faults.TryDequeue(out var fault))
            {
                var kind = fault.Kind;
                var error = ((AnimationError)fault.First).ToString();
                var message = kind == BoneCountFault ? "SA: timeline rig has " + fault.First + " bones, expected 1.." + MaxRigBones + "."
                    : kind == BoneEntityFault ? "SA: timeline rig has " + fault.First + " bone entities for " + fault.Second + " bones."
                    : kind == FamilyFault ? "SA: timeline rig mixes clip families in one tick."
                    : kind == SampleFault ? "SA: timeline clip sample failed (" + error + ")."
                    : kind == BlendFault ? "SA: timeline blend failed (" + error + ")."
                    : kind == IdentityFault ? "SA: timeline identity retarget failed (" + error + ")."
                    : kind == RemapFault ? "SA: timeline retarget failed (" + error + ")."
                    : "SA: timeline smoothing failed (" + error + ").";
                Warn(kind, fault.Rig.Index, message);
            }
        }

        unsafe void Resolve()
        {
            if (changed.IsEmpty)
                return;
            var entities = changed.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var retarget = EntityManager.GetComponentData<SharedRetarget>(entity).Value;
                EntityManager.SetComponentData(entity, new SharedTableRef
                {
                    Value = TableLayout(entity, retarget),
                    Source = retarget.IsCreated ? (long)retarget.GetUnsafePtr() : 0
                });
                var clips = EntityManager.GetBuffer<SharedClipAsset>(entity, true);
                var layouts = EntityManager.GetBuffer<SharedClipLayoutRef>(entity);
                layouts.ResizeUninitialized(clips.Length);
                for (var i = 0; i < clips.Length; i++)
                    layouts[i] = new SharedClipLayoutRef { Value = ClipLayout(clips[i].Value) };
            }
            entities.Dispose();
        }

        unsafe BlobAssetReference<SharedTableLayout> TableLayout(Entity entity, BlobAssetReference<SharedRetargetData> retarget)
        {
            if (!retarget.IsCreated)
            {
                Warn(TableMissingFault, entity.Index, "SA: timeline rig is missing its retarget table.");
                return default;
            }
            ref var data = ref retarget.Value;
            var family = data.Family;
            var target = data.Target;
            var key = xxHash3.Hash128(data.Table.GetUnsafePtr(), data.Table.Length)
                ^ new uint4((uint)family.A, (uint)(family.B >> 32), (uint)target.A, (uint)(target.B >> 32));
            if (tableLayouts.TryGetValue(key, out var layout))
            {
                if (!layout.IsCreated)
                    Warn(TableLoadFault, entity.Index, "SA: timeline rig table failed to load (" + tableErrors[key] + ").");
                return layout;
            }
            var error = RetargetFile.Load(new ReadOnlySpan<byte>(data.Table.GetUnsafePtr(), data.Table.Length), family, target,
                canonicalParents.AsSpan(), familyReference.AsSpan(), out var binding, out var rows);
            if (error != AnimationError.None)
            {
                tableLayouts.Add(key, default);
                tableErrors.Add(key, error);
                Warn(TableLoadFault, entity.Index, "SA: timeline rig table failed to load (" + error + ").");
                return default;
            }
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<SharedTableLayout>();
            root.Binding = binding;
            var stored = builder.Allocate(ref root.Rows, rows.Length);
            for (var i = 0; i < rows.Length; i++)
                stored[i] = rows[i];
            layout = builder.CreateBlobAssetReference<SharedTableLayout>(Allocator.Persistent);
            builder.Dispose();
            tableLayouts.Add(key, layout);
            return layout;
        }

        unsafe BlobAssetReference<SharedClipLayout> ClipLayout(BlobAssetReference<SharedClipData> clip)
        {
            if (!clip.IsCreated)
                return default;
            ref var data = ref clip.Value;
            if (data.Bytes.Length == 0 || data.BoneCount != CanonicalBones)
                return default;
            var artifact = data.Artifact;
            if (clipLayouts.TryGetValue(artifact, out var layout))
                return layout;
            var load = SharedClipReader.Load(new ReadOnlySpan<byte>(data.Bytes.GetUnsafePtr(), data.Bytes.Length), data.Family, familyReference.AsSpan(), out var shared);
            var blocks = 0;
            var bytes = 0;
            if (load == AnimationError.None)
                load = shared == null ? AnimationError.InvalidFormat : SharedClipStorage.Measure(shared, out blocks, out bytes);
            if (load != AnimationError.None)
            {
                clipLayouts.Add(artifact, default);
                Warn(ClipLoadFault, artifact.GetHashCode(), "SA: timeline clip failed to load (" + load + ").");
                return default;
            }
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<SharedClipLayout>();
            root.Duration = shared.Duration;
            root.SampleCount = shared.SampleCount;
            root.Loop = (byte)(shared.Loop ? 1 : 0);
            var tracks = builder.Allocate(ref root.Tracks, shared.Tracks.Length);
            for (var i = 0; i < shared.Tracks.Length; i++)
                tracks[i] = shared.Tracks[i];
            var segments = builder.Allocate(ref root.Segments, shared.Segments.Length);
            var blockArray = builder.Allocate(ref root.Blocks, blocks);
            var byteArray = builder.Allocate(ref root.Bytes, bytes);
            var write = SharedClipStorage.Write(shared,
                new Span<SegmentRange>(segments.GetUnsafePtr(), segments.Length),
                new Span<TrackBlock>(blockArray.GetUnsafePtr(), blockArray.Length),
                new Span<byte>(byteArray.GetUnsafePtr(), byteArray.Length));
            if (write != AnimationError.None)
            {
                builder.Dispose();
                clipLayouts.Add(artifact, default);
                Warn(ClipLoadFault, artifact.GetHashCode(), "SA: timeline clip failed to load (" + write + ").");
                return default;
            }
            layout = builder.CreateBlobAssetReference<SharedClipLayout>(Allocator.Persistent);
            builder.Dispose();
            clipLayouts.Add(artifact, layout);
            return layout;
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [WithAll(typeof(SharedTimeline))]
        [WithNone(typeof(ShapeDriven))]
        unsafe partial struct RigJob : IJobEntity
        {
            public float DeltaTime;
            public uint Stamp;
            [ReadOnly] public NativeArray<Pose> FamilyReference;
            [ReadOnly] public NativeArray<int> CanonicalParents;
            [NativeDisableParallelForRestriction] public ComponentLookup<LocalTransform> Transforms;
            [NativeDisableParallelForRestriction] public ComponentLookup<PostTransformMatrix> PostTransforms;
            public NativeQueue<SharedFault>.ParallelWriter Faults;

            [BurstDiscard]
            static void MarkManaged(ref byte native) => native = 0;

            static ReadOnlySpan<T> View<T>(ref BlobArray<T> array) where T : unmanaged =>
                new ReadOnlySpan<T>(array.GetUnsafePtr(), array.Length);

            static void Clear<T>(DynamicBuffer<T> buffer, int length) where T : unmanaged, IBufferElementData
            {
                buffer.ResizeUninitialized(length);
                UnsafeUtility.MemClear(buffer.GetUnsafePtr(), (long)length * UnsafeUtility.SizeOf<T>());
            }

            void Fault(Entity rig, int kind, int first, int second) =>
                Faults.Enqueue(new SharedFault { Rig = rig, Kind = kind, First = first, Second = second });

            void Execute(Entity entity, in Rig rigReference, in SharedTableRef table, in DynamicBuffer<SharedClipAsset> clips,
                in DynamicBuffer<SharedClipLayoutRef> layouts, ref DynamicBuffer<SharedContribution> contributions, in DynamicBuffer<BoneEntity> bones,
                ref SharedClock clockData, ref SharedPoseState state, ref DynamicBuffer<SharedBoneState> boneStates,
                ref DynamicBuffer<SharedCommittedPose> committed, ref DynamicBuffer<SharedPreviousContribution> previousContributions,
                ref DynamicBuffer<SharedPreviousLayer> previousLayers)
            {
                if (!rigReference.Value.IsCreated)
                    return;
                ref var rig = ref rigReference.Value.Value;
                var boneCount = rig.Bones.Length;
                if (boneCount <= 0 || boneCount > MaxRigBones)
                {
                    Fault(entity, BoneCountFault, boneCount, 0);
                    return;
                }
                var clock = clockData;
                var advance = clock.Paused == 0 ? DeltaTime : 0f;
                if (advance < 0f)
                    advance = 0f;
                if (advance > 0f)
                {
                    clock.Seconds += advance;
                    clock.Tick++;
                }
                var seek = clock.Seek != 0;
                clock.Seek = 0;
                if (!table.Value.IsCreated)
                    return;
                if (state.BoneCount != boneCount)
                {
                    state = default;
                    state.BoneCount = boneCount;
                    Clear(boneStates, boneCount);
                    Clear(committed, boneCount);
                    Clear(previousContributions, SharedFamilyControl.MaxContributions);
                    Clear(previousLayers, SharedFamilyControl.MaxContributions);
                }
                if (state.Table != table.Source)
                {
                    state.Table = table.Source;
                    state.Frame = default;
                }
                if (bones.Length < boneCount)
                {
                    Fault(entity, BoneEntityFault, bones.Length, boneCount);
                    return;
                }
                Span<ClipContribution> ordered = stackalloc ClipContribution[SharedFamilyControl.MaxContributions];
                Span<int> clipFor = stackalloc int[SharedFamilyControl.MaxContributions];
                var active = 0;
                for (var i = 0; i < contributions.Length && active < SharedFamilyControl.MaxContributions; i++)
                {
                    var element = contributions[i];
                    if (element.Weight <= 0f || element.Clip < 0 || element.Clip >= clips.Length || !clips[element.Clip].Value.IsCreated)
                        continue;
                    ref var blob = ref clips[element.Clip].Value.Value;
                    if (blob.Bytes.Length == 0 || blob.BoneCount != CanonicalBones)
                        continue;
                    if (element.Clip >= layouts.Length || !layouts[element.Clip].Value.IsCreated)
                        continue;
                    var sample = seek ? clock.Seconds + element.Offset : element.Sample + advance * element.Speed;
                    sample = WrapSample(sample, blob.Duration, blob.Loop != 0);
                    element.Sample = sample;
                    contributions[i] = element;
                    ordered[active] = new ClipContribution(new ClipHandle((uint)element.Clip, 1), sample, element.Weight, element.Layer, element.Mask);
                    clipFor[active] = element.Clip;
                    active++;
                }
                if (active == 0)
                {
                    clockData = clock;
                    return;
                }
                SortByLayer(ordered.Slice(0, active), clipFor.Slice(0, active));
                Span<LayerInput> layers = stackalloc LayerInput[SharedFamilyControl.MaxContributions];
                var layerCount = BuildLayers(ordered.Slice(0, active), layers);
                var family = clips[clipFor[0]].Value.Value.Family;
                for (var i = 1; i < active; i++)
                {
                    if (clips[clipFor[i]].Value.Value.Family != family)
                    {
                        Fault(entity, FamilyFault, 0, 0);
                        clockData = clock;
                        return;
                    }
                }
                var reference = FamilyReference.AsReadOnlySpan();
                var canonical = CanonicalParents.AsReadOnlySpan();
                var decoded = new NativeArray<Pose>(active * CanonicalBones, Allocator.Temp);
                for (var i = 0; i < active; i++)
                {
                    ref var layout = ref layouts[clipFor[i]].Value.Value;
                    var error = SegmentSampling.Sample(layout.Duration, layout.SampleCount, layout.Loop != 0, ordered[i].SampleSeconds,
                        View(ref layout.Tracks), View(ref layout.Segments), View(ref layout.Blocks), View(ref layout.Bytes),
                        reference, decoded.AsSpan().Slice(i * CanonicalBones, CanonicalBones));
                    if (error != AnimationError.None)
                    {
                        Fault(entity, SampleFault, (int)error, 0);
                        clockData = clock;
                        return;
                    }
                }
                var blended = new NativeArray<Pose>(CanonicalBones, Allocator.Temp);
                var blend = CanonicalBlend.Evaluate(ordered.Slice(0, active), layers.Slice(0, layerCount), decoded.AsSpan(),
                    default(ReadOnlySpan<Pose>), default(ReadOnlySpan<float>), reference, blended.AsSpan());
                if (blend != AnimationError.None)
                {
                    Fault(entity, BlendFault, (int)blend, 0);
                    clockData = clock;
                    return;
                }
                var retargeted = new NativeArray<Pose>(boneCount, Allocator.Temp);
                var destination = retargeted.AsSpan();
                var canonicalModel = new NativeArray<RigidPose>(CanonicalBones, Allocator.Temp);
                var targetModel = new NativeArray<RigidPose>(boneCount, Allocator.Temp);
                ref var tableLayout = ref table.Value.Value;
                var binding = tableLayout.Binding;
                if (family == binding.Target && boneCount == CanonicalBones)
                {
                    Span<int> rigParents = stackalloc int[CanonicalBones];
                    for (var b = 0; b < boneCount; b++)
                        rigParents[b] = rig.Bones[b].Parent;
                    var identity = IdentityRetarget.Apply(family, binding.Target, rigParents, blended.AsSpan(), targetModel.AsSpan(), destination);
                    if (identity != AnimationError.None)
                    {
                        Fault(entity, IdentityFault, (int)identity, 0);
                        clockData = clock;
                        return;
                    }
                }
                else
                {
                    var rows = View(ref tableLayout.Rows);
                    var remap = RetargetTable.Apply(family, binding, canonical, blended.AsSpan(), rows, canonicalModel.AsSpan(), targetModel.AsSpan(), destination);
                    if (remap != AnimationError.None)
                    {
                        Fault(entity, RemapFault, (int)remap, 0);
                        clockData = clock;
                        return;
                    }
                    CanonicalTranslation.Apply(rows, binding, canonical, blended.AsSpan(), reference, canonicalModel.AsSpan(), targetModel.AsSpan(), destination);
                }
                var previous = previousContributions.Reinterpret<ClipContribution>().AsNativeArray().AsSpan();
                if (!SameMembers(ordered.Slice(0, active), previous, state.Frame.ContributionCount))
                    clock.Continuity++;
                var frame = new AnimationFrame(clock.Tick, 0, clock.Continuity, clock.Seconds, advance, clock.Transition,
                    seek ? FrameMode.Seek : FrameMode.Advance, 0.1f);
                var boneScratch = new NativeArray<BoneFrameState>(boneCount, Allocator.Temp);
                var poseScratch = new NativeArray<Pose>(boneCount, Allocator.Temp);
                var zeroVelocity = new NativeArray<PoseVelocity>(boneCount, Allocator.Temp);
                var smooth = TimelinePose.Evaluate(frame, ordered.Slice(0, active), layers.Slice(0, layerCount), true, destination,
                    zeroVelocity.AsSpan(), ref state.Frame, boneStates.Reinterpret<BoneFrameState>().AsNativeArray().AsSpan(), boneScratch.AsSpan(),
                    previous, previousLayers.Reinterpret<LayerInput>().AsNativeArray().AsSpan(),
                    committed.Reinterpret<Pose>().AsNativeArray().AsSpan(), poseScratch.AsSpan(), out var action);
                state.Active = active;
                state.Action = action;
                state.Smooth = smooth;
                state.Stamp = Stamp;
                state.Native = 1;
                MarkManaged(ref state.Native);
                if (smooth != AnimationError.None)
                {
                    Fault(entity, SmoothFault, (int)smooth, 0);
                    clockData = clock;
                    return;
                }
                var poses = committed.Reinterpret<Pose>().AsNativeArray();
                for (var b = 0; b < boneCount; b++)
                {
                    var boneEntity = bones[b].Value;
                    if (!Transforms.HasComponent(boneEntity))
                        continue;
                    var pose = poses[b];
                    var uniform = pose.Scale.X;
                    Transforms[boneEntity] = LocalTransform.FromPositionRotationScale(
                        Conversions.ToUnity(pose.Position), Conversions.ToUnity(pose.Rotation), uniform);
                    if (PostTransforms.HasComponent(boneEntity))
                        PostTransforms[boneEntity] = new PostTransformMatrix
                        {
                            Value = float4x4.Scale(uniform != 0f ? Conversions.ToUnity(pose.Scale) / uniform : 1f)
                        };
                }
                clockData = clock;
            }

            static double WrapSample(double sample, double duration, bool loop)
            {
                if (!(duration > 0.0))
                    return 0.0;
                if (loop)
                {
                    sample %= duration;
                    if (sample < 0.0)
                        sample += duration;
                    return sample;
                }
                if (sample < 0.0)
                    return 0.0;
                return sample > duration ? duration : sample;
            }

            static void SortByLayer(Span<ClipContribution> contributions, Span<int> clipFor)
            {
                for (var i = 1; i < contributions.Length; i++)
                {
                    var contribution = contributions[i];
                    var clip = clipFor[i];
                    var j = i - 1;
                    while (j >= 0 && contributions[j].Layer > contribution.Layer)
                    {
                        contributions[j + 1] = contributions[j];
                        clipFor[j + 1] = clipFor[j];
                        j--;
                    }
                    contributions[j + 1] = contribution;
                    clipFor[j + 1] = clip;
                }
            }

            static int BuildLayers(ReadOnlySpan<ClipContribution> contributions, Span<LayerInput> layers)
            {
                var count = 0;
                var i = 0;
                while (i < contributions.Length)
                {
                    var layer = contributions[i].Layer;
                    var first = i;
                    while (i < contributions.Length && contributions[i].Layer == layer)
                        i++;
                    layers[count++] = new LayerInput(layer, SharedMode.Override, first, i - first, 1f);
                }
                return count;
            }

            static bool SameMembers(ReadOnlySpan<ClipContribution> contributions, ReadOnlySpan<ClipContribution> previous, int previousCount)
            {
                if (previousCount != contributions.Length)
                    return false;
                for (var i = 0; i < contributions.Length; i++)
                {
                    var before = previous[i];
                    if (before.Clip.Slot != contributions[i].Clip.Slot || before.Layer != contributions[i].Layer || before.MaskIndex != contributions[i].MaskIndex)
                        return false;
                    if (math.abs(before.Weight - contributions[i].Weight) > 1e-6f)
                        return false;
                }
                return true;
            }
        }
    }

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(SkinningSystem))]
    [UpdateBefore(typeof(global::Unity.Rendering.EntitiesGraphicsSystem))]
    public partial class SharedFamilyPaletteSystem : SystemBase
    {
        const int MaxBones = 512;

        NativeArray<float4x4> staging;
        NativeList<int2> ranges;
        EntityQuery skins;

        struct RangeOrder : IComparer<int2>
        {
            public int Compare(int2 a, int2 b) => a.x.CompareTo(b.x);
        }

        protected override void OnCreate()
        {
            skins = GetEntityQuery(ComponentType.ReadOnly<Skin>(), ComponentType.ReadOnly<SkinBinding>());
            ranges = new NativeList<int2>(64, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            CompleteDependency();
            if (staging.IsCreated)
                staging.Dispose();
            ranges.Dispose();
        }

        protected override void OnUpdate()
        {
            var skinning = World.GetExistingSystemManaged<SkinningSystem>();
            if (skinning == null)
                return;
            var matrices = skinning.SkinMatrixBuffer;
            if (matrices == null)
                return;
            if (!staging.IsCreated || staging.Length < matrices.count)
            {
                if (staging.IsCreated)
                    staging.Dispose();
                staging = new NativeArray<float4x4>(matrices.count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            ranges.Clear();
            var count = skins.CalculateEntityCount();
            if (count == 0)
                return;
            if (ranges.Capacity < count)
                ranges.Capacity = count;
            Dependency = new PaletteJob
            {
                Timelines = SystemAPI.GetComponentLookup<SharedTimeline>(true),
                Shaped = SystemAPI.GetComponentLookup<ShapeDriven>(true),
                Rigs = SystemAPI.GetComponentLookup<Rig>(true),
                Bones = SystemAPI.GetBufferLookup<BoneEntity>(true),
                Transforms = SystemAPI.GetComponentLookup<LocalTransform>(true),
                Staging = staging,
                Limit = matrices.count,
                Ranges = ranges.AsParallelWriter()
            }.ScheduleParallel(skins, Dependency);
            CompleteDependency();
            if (ranges.Length == 0)
                return;
            ranges.Sort(new RangeOrder());
            var start = ranges[0].x;
            var end = start + ranges[0].y;
            for (var i = 1; i < ranges.Length; i++)
            {
                var range = ranges[i];
                if (range.x == end)
                {
                    end += range.y;
                    continue;
                }
                matrices.SetData(staging, start, start, end - start);
                start = range.x;
                end = start + range.y;
            }
            matrices.SetData(staging, start, start, end - start);
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        unsafe partial struct PaletteJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<SharedTimeline> Timelines;
            [ReadOnly] public ComponentLookup<ShapeDriven> Shaped;
            [ReadOnly] public ComponentLookup<Rig> Rigs;
            [ReadOnly] public BufferLookup<BoneEntity> Bones;
            [ReadOnly] public ComponentLookup<LocalTransform> Transforms;
            [NativeDisableParallelForRestriction] public NativeArray<float4x4> Staging;
            public int Limit;
            public NativeList<int2>.ParallelWriter Ranges;

            void Execute(in Skin skin, in SkinBinding binding)
            {
                if (!skin.Value.IsCreated || !Timelines.HasComponent(skin.Rig))
                    return;
                if (Shaped.HasComponent(skin.Rig))
                    return;
                if (!Bones.HasBuffer(skin.Rig) || !Rigs.HasComponent(skin.Rig))
                    return;
                ref var blob = ref skin.Value.Value;
                var boneCount = blob.Bones.Length;
                if (boneCount <= 0 || boneCount > MaxBones)
                    return;
                var rigBones = Bones[skin.Rig];
                var rigCount = rigBones.Length;
                if (rigCount <= 0 || rigCount > MaxBones)
                    return;
                var rigReference = Rigs[skin.Rig];
                if (!rigReference.Value.IsCreated || rigReference.Value.Value.Bones.Length != rigCount)
                    return;
                var start = (int)binding.MatrixBase;
                if (start <= 0 || start > Limit - boneCount)
                    return;
                ref var rig = ref rigReference.Value.Value;
                var localPose = new NativeArray<Pose>(rigCount, Allocator.Temp);
                var parents = new NativeArray<int>(rigCount, Allocator.Temp);
                for (var b = 0; b < rigCount; b++)
                {
                    if (!Transforms.TryGetComponent(rigBones[b].Value, out var transform))
                        return;
                    localPose[b] = new Pose
                    {
                        Position = Conversions.ToMotion(transform.Position),
                        Rotation = Conversions.ToMotion(transform.Rotation),
                        Scale = new Float3(transform.Scale, transform.Scale, transform.Scale)
                    };
                    parents[b] = rig.Bones[b].Parent;
                }
                var model = new NativeArray<RigidPose>(rigCount, Allocator.Temp);
                if (RigidMath.Model(parents.AsReadOnlySpan(), localPose.AsReadOnlySpan(), model.AsSpan()) != AnimationError.None)
                    return;
                var affineModel = new NativeArray<Affine>(rigCount, Allocator.Temp);
                for (var b = 0; b < rigCount; b++)
                    affineModel[b] = ToAffine(model[b]);
                var bindMatrices = new NativeArray<Affine>(boneCount, Allocator.Temp);
                for (var b = 0; b < boneCount; b++)
                    bindMatrices[b] = ToAffine(blob.Bindposes[b]);
                var palette = new NativeArray<Affine>(boneCount, Allocator.Temp);
                var paletteToRig = new ReadOnlySpan<int>(blob.Bones.GetUnsafePtr(), boneCount);
                var build = SkinPalette.Build(affineModel.AsReadOnlySpan(), paletteToRig, bindMatrices.AsReadOnlySpan(), ToAffine(blob.MeshFromRig), palette.AsSpan());
                if (build != AnimationError.None)
                    return;
                for (var b = 0; b < boneCount; b++)
                {
                    var joint = palette[b];
                    Staging[start + b] = new float4x4(
                        new float4(Conversions.ToUnity(joint.C0), 0f), new float4(Conversions.ToUnity(joint.C1), 0f),
                        new float4(Conversions.ToUnity(joint.C2), 0f), new float4(Conversions.ToUnity(joint.C3), 1f));
                }
                Ranges.AddNoResize(new int2(start, boneCount));
            }

            static Affine ToAffine(in RigidPose pose)
            {
                var x = Quaternion.Rotate(pose.Rotation, new Float3(1f, 0f, 0f));
                var y = Quaternion.Rotate(pose.Rotation, new Float3(0f, 1f, 0f));
                var z = Quaternion.Rotate(pose.Rotation, new Float3(0f, 0f, 1f));
                return new Affine(x, y, z, pose.Position);
            }

            static Affine ToAffine(in float3x4 matrix)
            {
                return new Affine(
                    Conversions.ToMotion(matrix.c0), Conversions.ToMotion(matrix.c1),
                    Conversions.ToMotion(matrix.c2), Conversions.ToMotion(matrix.c3));
            }
        }
    }
}
