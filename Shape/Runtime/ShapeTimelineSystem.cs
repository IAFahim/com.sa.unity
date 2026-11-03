using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA;
using SA.Animation;
using SA.Animation.Content;
using SA.Motion.Inertialization;
using SA.Motion.Numerics;
using SA.Motion.Unity;
using SA.Shape;
using SharedMode = SA.Animation.ClipMode;

namespace SA.Shape.Unity
{
    [UpdateInGroup(typeof(AnimationSystemGroup))]
    public partial class ShapeTimelineSystem : SystemBase
    {
        public const int CanonicalBones = 56;
        const int MaxRigBones = 128;

        sealed class ShapeRigState
        {
            public int BoneCount;
            public int ChannelCount;
            public int[] RigParents = Array.Empty<int>();
            public ShapeFrameState Frame;
            public BoneFrameState[] Bones = Array.Empty<BoneFrameState>();
            public BoneFrameState[] BoneScratch = Array.Empty<BoneFrameState>();
            public ClipContribution[] PreviousContributions = new ClipContribution[SharedFamilyControl.MaxContributions];
            public LayerInput[] PreviousLayers = new LayerInput[SharedFamilyControl.MaxContributions];
            public Pose[] Destination = Array.Empty<Pose>();
            public Pose[] NeutralCommitted = Array.Empty<Pose>();
            public Pose[] NeutralScratch = Array.Empty<Pose>();
            public Pose[] Displayed = Array.Empty<Pose>();
            public Pose[] DisplayScratch = Array.Empty<Pose>();
            public RigidPose[] PoseModels = Array.Empty<RigidPose>();
            public Float3[] Offsets = Array.Empty<Float3>();
            public float[] LengthScales = Array.Empty<float>();
            public float[] MotionScales = Array.Empty<float>();
            public Float3[] SkinScales = Array.Empty<Float3>();
            public Affine[] Deformations = Array.Empty<Affine>();
            public RigidPose[] RigModels = Array.Empty<RigidPose>();
            public float[] RequestedValues = Array.Empty<float>();
            public float[] SteppedValues = Array.Empty<float>();
            public float[] CommittedValues = Array.Empty<float>();
            public float[] MorphWeights = Array.Empty<float>();
            public ShapeParameter[] CurrentParameters = Array.Empty<ShapeParameter>();
            public ShapeParameter[] NextParameters = Array.Empty<ShapeParameter>();
            public ShapeGeometry Geometry;
            public bool HasGeometry;
            public AssetId BuiltDefinition;
            public AssetId BuiltTarget;
            public BlobAssetReference<SharedRetargetData> TableRef;
            public RetargetBinding Binding;
            public RetargetRow[] Rows = Array.Empty<RetargetRow>();
        }

        EntityQuery rigs;
        readonly Dictionary<Entity, ShapeRigState> states = new Dictionary<Entity, ShapeRigState>();
        readonly Dictionary<AssetId, SharedClip> cache = new Dictionary<AssetId, SharedClip>();
        readonly List<SharedClip> pinned = new List<SharedClip>();
        readonly HashSet<string> warned = new HashSet<string>();

        NativeArray<Pose> decoded;
        NativeArray<Pose> blended;
        NativeArray<Pose> retargeted;
        NativeArray<RigidPose> canonicalModel;
        NativeArray<RigidPose> targetModel;
        NativeArray<Pose> familyReference;
        NativeArray<int> canonicalParents;
        NativeArray<PoseVelocity> zeroVelocity;
        NativeArray<Pose> emptyPose;
        NativeArray<float> emptyMask;

        protected override void OnCreate()
        {
            rigs = GetEntityQuery(
                ComponentType.ReadOnly<SharedTimeline>(),
                ComponentType.ReadOnly<Rig>(),
                ComponentType.ReadOnly<SharedClipAsset>(),
                ComponentType.ReadOnly<SharedContribution>(),
                ComponentType.ReadWrite<SharedClock>(),
                ComponentType.ReadOnly<SharedRetarget>(),
                ComponentType.ReadOnly<BoneEntity>(),
                ComponentType.ReadOnly<ShapeDriven>(),
                ComponentType.ReadOnly<ShapeDefinitionRef>(),
                ComponentType.ReadOnly<ShapeChannelState>(),
                ComponentType.ReadWrite<ShapeState>());
            decoded = new NativeArray<Pose>(SharedFamilyControl.MaxContributions * CanonicalBones, Allocator.Persistent);
            blended = new NativeArray<Pose>(CanonicalBones, Allocator.Persistent);
            retargeted = new NativeArray<Pose>(MaxRigBones, Allocator.Persistent);
            canonicalModel = new NativeArray<RigidPose>(CanonicalBones, Allocator.Persistent);
            targetModel = new NativeArray<RigidPose>(MaxRigBones, Allocator.Persistent);
            familyReference = new NativeArray<Pose>(CanonicalBones, Allocator.Persistent);
            canonicalParents = new NativeArray<int>(CanonicalBones, Allocator.Persistent);
            zeroVelocity = new NativeArray<PoseVelocity>(MaxRigBones, Allocator.Persistent);
            emptyPose = new NativeArray<Pose>(0, Allocator.Persistent);
            emptyMask = new NativeArray<float>(0, Allocator.Persistent);
            CanonicalHumanoid.WriteReference(canonicalParents.AsSpan(), familyReference.AsSpan());
        }

        protected override void OnDestroy()
        {
            decoded.Dispose();
            blended.Dispose();
            retargeted.Dispose();
            canonicalModel.Dispose();
            targetModel.Dispose();
            familyReference.Dispose();
            canonicalParents.Dispose();
            zeroVelocity.Dispose();
            emptyPose.Dispose();
            emptyMask.Dispose();
            states.Clear();
            cache.Clear();
            pinned.Clear();
        }

        public static string LastWarning { get; private set; } = string.Empty;

        void WarnOnce(string key, string message)
        {
            if (warned.Add(key))
            {
                LastWarning = message;
                UnityEngine.Debug.LogWarning(message);
            }
        }

        public static int LastEntityCount { get; private set; }

        public static float LastStep { get; private set; }

        public static int LastActive { get; private set; }

        public static string LastAction { get; private set; } = string.Empty;

        public static string LastSmooth { get; private set; } = string.Empty;

        public static int TrackedRigCount { get; private set; }

        protected override void OnUpdate()
        {
            var step = SystemAPI.Time.DeltaTime;
            LastStep = step;
            pinned.Clear();
            var entities = rigs.ToEntityArray(Allocator.Temp);
            LastEntityCount = entities.Length;
            foreach (var entity in entities)
                UpdateRig(entity, step);
            entities.Dispose();
            Entity dead = Entity.Null;
            foreach (var pair in states)
                if (!EntityManager.Exists(pair.Key) || !EntityManager.HasComponent<ShapeDriven>(pair.Key))
                {
                    dead = pair.Key;
                    break;
                }
            if (dead != Entity.Null)
                states.Remove(dead);
            TrackedRigCount = states.Count;
        }

        unsafe void UpdateRig(Entity entity, float deltaTime)
        {
            var rigReference = EntityManager.GetComponentData<Rig>(entity);
            if (!rigReference.Value.IsCreated)
                return;
            ref var rig = ref rigReference.Value.Value;
            var boneCount = rig.Bones.Length;
            if (boneCount <= 0 || boneCount > MaxRigBones)
            {
                WarnOnce("bones" + entity.Index, "SA: shape rig has " + boneCount + " bones, expected 1.." + MaxRigBones + ".");
                return;
            }
            var clock = EntityManager.GetComponentData<SharedClock>(entity);
            var advance = clock.Paused == 0 ? deltaTime : 0f;
            if (advance < 0f)
                advance = 0f;
            if (advance > 0f)
            {
                clock.Seconds += advance;
                clock.Tick++;
            }
            var seek = clock.Seek != 0;
            clock.Seek = 0;
            var tableReference = EntityManager.GetComponentData<SharedRetarget>(entity).Value;
            if (!tableReference.IsCreated)
            {
                WarnOnce("table" + entity.Index, "SA: shape rig is missing its retarget table.");
                return;
            }
            var definitionReference = EntityManager.GetComponentData<ShapeDefinitionRef>(entity).Value;
            if (!definitionReference.IsCreated)
            {
                WarnOnce("definition" + entity.Index, "SA: shape rig is missing its shape definition.");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            ref var definitionData = ref definitionReference.Value;
            var channelCount = definitionData.Channels.Length;
            var channels = EntityManager.GetBuffer<ShapeChannelState>(entity);
            if (channels.Length != channelCount)
            {
                WarnOnce("channels" + entity.Index, "SA: shape rig has " + channels.Length + " channel states for " + channelCount + " definition channels.");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            if (!states.TryGetValue(entity, out var state) || state.BoneCount != boneCount || state.ChannelCount != channelCount)
            {
                state = new ShapeRigState { BoneCount = boneCount, ChannelCount = channelCount };
                state.RigParents = new int[boneCount];
                for (var b = 0; b < boneCount; b++)
                    state.RigParents[b] = rig.Bones[b].Parent;
                state.Bones = new BoneFrameState[boneCount];
                state.BoneScratch = new BoneFrameState[boneCount];
                state.Destination = new Pose[boneCount];
                state.NeutralCommitted = new Pose[boneCount];
                state.NeutralScratch = new Pose[boneCount];
                state.Displayed = new Pose[boneCount];
                state.DisplayScratch = new Pose[boneCount];
                state.PoseModels = new RigidPose[boneCount];
                state.Offsets = new Float3[boneCount];
                state.LengthScales = new float[boneCount];
                state.MotionScales = new float[boneCount];
                state.SkinScales = new Float3[boneCount];
                state.Deformations = new Affine[boneCount];
                state.RigModels = new RigidPose[boneCount];
                state.RequestedValues = new float[channelCount];
                state.SteppedValues = new float[channelCount];
                state.CommittedValues = new float[channelCount];
                state.CurrentParameters = new ShapeParameter[channelCount];
                state.NextParameters = new ShapeParameter[channelCount];
                for (var i = 0; i < channelCount; i++)
                {
                    state.CommittedValues[i] = channels[i].Value;
                    state.CurrentParameters[i] = new ShapeParameter(channels[i].Value, channels[i].Velocity);
                }
                states[entity] = state;
            }
            if (state.HasGeometry && (state.BuiltDefinition != definitionData.Definition || state.BuiltTarget != definitionData.Target))
            {
                state.Frame = default;
                state.HasGeometry = false;
                for (var i = 0; i < channelCount; i++)
                    state.CommittedValues[i] = channels[i].Value;
            }
            if (!state.TableRef.Equals(tableReference) || state.Rows.Length == 0)
            {
                ref var tableBytes = ref tableReference.Value.Table;
                var tableError = RetargetFile.Load(
                    new ReadOnlySpan<byte>(tableBytes.GetUnsafePtr(), tableBytes.Length),
                    tableReference.Value.Family, tableReference.Value.Target,
                    canonicalParents.AsSpan(), familyReference.AsSpan(),
                    out state.Binding, out state.Rows);
                if (tableError != AnimationError.None)
                {
                    WarnOnce("tableload" + entity.Index, "SA: shape rig table failed to load (" + tableError + ").");
                    EntityManager.SetComponentData(entity, clock);
                    return;
                }
                state.TableRef = tableReference;
                state.Frame = default;
                state.HasGeometry = false;
            }
            if (state.Rows.Length != boneCount)
            {
                WarnOnce("rows" + entity.Index, "SA: shape rig table has " + state.Rows.Length + " rows for " + boneCount + " bones.");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            var shapeState = EntityManager.GetComponentData<ShapeState>(entity);
            var definitionChannels = new ReadOnlySpan<ShapeChannel>(definitionData.Channels.GetUnsafePtr(), channelCount);
            var definitionOffsets = new ReadOnlySpan<OffsetEffect>(definitionData.Offsets.GetUnsafePtr(), definitionData.Offsets.Length);
            var definitionSkin = new ReadOnlySpan<SkinEffect>(definitionData.Skin.GetUnsafePtr(), definitionData.Skin.Length);
            var definitionFrames = new ReadOnlySpan<SkinFrame>(definitionData.Frames.GetUnsafePtr(), definitionData.Frames.Length);
            for (var i = 0; i < channelCount; i++)
            {
                state.RequestedValues[i] = channels[i].Requested;
                state.CurrentParameters[i] = new ShapeParameter(channels[i].Value, channels[i].Velocity);
            }
            var stepError = ShapeParameters.Step(definitionChannels, state.RequestedValues, state.CurrentParameters, advance, state.NextParameters);
            if (stepError != AnimationError.None)
            {
                WarnOnce("step" + entity.Index, "SA: shape parameter step failed (" + stepError + ").");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            for (var i = 0; i < channelCount; i++)
                state.SteppedValues[i] = state.NextParameters[i].Value;
            var changed = ShapeRevision.HasEffectiveChange(state.CommittedValues, state.SteppedValues);
            var candidate = shapeState.Revision + (changed ? 1UL : 0UL);
            if (changed && EntityManager.HasComponent<ShapePhysical>(entity))
            {
                var physical = EntityManager.GetComponentData<ShapePhysical>(entity);
                if (physical.LivePhysical != 0)
                {
                    ShapePhysicalSync.RevertRequests(channels);
                    physical.PendingRebuild = 0;
                    physical.Diagnostic = ShapePhysicalSync.LivePhysicalRefusal;
                    EntityManager.SetComponentData(entity, physical);
                    WarnOnce("physicallock" + entity.Index, "SA: shape rig holds its physical snapshot while live-physical; customize kinematic, then rebuild.");
                    EntityManager.SetComponentData(entity, clock);
                    return;
                }
            }
            if (!state.HasGeometry || changed)
            {
                var profile = new ShapeProfile(definitionData.Definition, definitionData.Target,
                    definitionData.SupportA, definitionData.SupportB, definitionData.Grounding,
                    definitionData.MinimumLength, definitionData.MaximumLength,
                    definitionData.MinimumSkinScale, definitionData.MaximumSkinScale);
                var definition = new ShapeDefinition(profile, definitionChannels, definitionOffsets, definitionSkin, definitionFrames);
                var workspace = new ShapeWorkspace(state.Offsets, state.LengthScales, state.MotionScales,
                    state.SkinScales, state.Deformations, state.RigModels);
                var geometryError = ShapeRig.Evaluate(definition, state.Binding, state.Rows, state.SteppedValues, candidate, workspace, out state.Geometry);
                if (geometryError != AnimationError.None)
                {
                    WarnOnce("geometry" + entity.Index, "SA: shape geometry failed (" + geometryError + "). Check that the definition targets this rig.");
                    EntityManager.SetComponentData(entity, clock);
                    return;
                }
                state.HasGeometry = true;
                state.BuiltDefinition = definitionData.Definition;
                state.BuiltTarget = definitionData.Target;
                if (changed)
                {
                    for (var i = 0; i < channelCount; i++)
                        state.CommittedValues[i] = state.SteppedValues[i];
                    shapeState.Revision = candidate;
                }
                if (EntityManager.HasBuffer<ShapeSkinDeformation>(entity))
                {
                    var published = EntityManager.GetBuffer<ShapeSkinDeformation>(entity);
                    if (published.Length != boneCount)
                        published.ResizeUninitialized(boneCount);
                    for (var b = 0; b < boneCount; b++)
                        published[b] = new ShapeSkinDeformation { Value = state.Deformations[b] };
                }
                var morphCount = definitionData.Morphs.Length;
                if (morphCount > 0 && EntityManager.HasBuffer<ShapeMorphWeight>(entity))
                {
                    var morphEffects = definitionData.MorphEffects.Length == 0
                        ? ReadOnlySpan<MorphEffect>.Empty
                        : new ReadOnlySpan<MorphEffect>(definitionData.MorphEffects.GetUnsafePtr(), definitionData.MorphEffects.Length);
                    var morphs = new ReadOnlySpan<ShapeMorph>(definitionData.Morphs.GetUnsafePtr(), morphCount);
                    if (state.MorphWeights.Length != morphCount)
                        state.MorphWeights = new float[morphCount];
                    var weightBuffer = EntityManager.GetBuffer<ShapeMorphWeight>(entity);
                    if (weightBuffer.Length != morphCount)
                        weightBuffer.ResizeUninitialized(morphCount);
                    if (ShapeMorphs.Evaluate(definitionChannels, state.SteppedValues, morphs, morphEffects, state.MorphWeights) == AnimationError.None)
                    {
                        for (var m = 0; m < morphCount; m++)
                            weightBuffer[m] = new ShapeMorphWeight { Value = state.MorphWeights[m] };
                    }
                    else
                    {
                        WarnOnce("morph" + entity.Index, "SA: shape morph evaluation failed. Check that the definition targets this rig.");
                    }
                }
                if (EntityManager.HasComponent<ShapeContactState>(entity))
                {
                    ShapeContactUpload.MarkerPoint(state.RigModels[definitionData.SupportA.Bone], state.Deformations[definitionData.SupportA.Bone], definitionData.SupportA.Offset, out var soleA);
                    ShapeContactUpload.MarkerPoint(state.RigModels[definitionData.SupportB.Bone], state.Deformations[definitionData.SupportB.Bone], definitionData.SupportB.Offset, out var soleB);
                    EntityManager.SetComponentData(entity, new ShapeContactState
                    {
                        SoleA = soleA,
                        SoleB = soleB,
                        HipHeight = state.Geometry.HipHeight,
                        GroundY = state.Geometry.GroundY,
                        Revision = candidate
                    });
                }
            }
            var clips = EntityManager.GetBuffer<SharedClipAsset>(entity);
            var contributions = EntityManager.GetBuffer<SharedContribution>(entity);
            var bones = EntityManager.GetBuffer<BoneEntity>(entity);
            if (bones.Length < boneCount)
            {
                WarnOnce("boneentities" + entity.Index, "SA: shape rig has " + bones.Length + " bone entities for " + boneCount + " bones.");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            Span<ClipContribution> ordered = stackalloc ClipContribution[SharedFamilyControl.MaxContributions];
            Span<int> clipFor = stackalloc int[SharedFamilyControl.MaxContributions];
            var active = 0;
            for (var i = 0; i < contributions.Length && active < SharedFamilyControl.MaxContributions; i++)
            {
                var element = contributions[i];
                if (element.Weight <= 0f || element.Clip < 0 || element.Clip >= clips.Length)
                    continue;
                ref var blob = ref clips[element.Clip].Value.Value;
                if (blob.Bytes.Length == 0 || blob.BoneCount != CanonicalBones)
                    continue;
                if (!cache.TryGetValue(blob.Artifact, out var shared))
                {
                    var load = SharedClipReader.Load(
                        new ReadOnlySpan<byte>(blob.Bytes.GetUnsafePtr(), blob.Bytes.Length),
                        blob.Family, familyReference.AsSpan(), out shared);
                    if (load != AnimationError.None || shared == null)
                    {
                        WarnOnce("clip" + blob.Artifact.GetHashCode(), "SA: shape clip failed to load (" + load + ").");
                        continue;
                    }
                    cache.Add(blob.Artifact, shared);
                }
                if (!pinned.Contains(shared))
                    pinned.Add(shared);
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
                EntityManager.SetComponentData(entity, clock);
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
                    WarnOnce("family" + entity.Index, "SA: shape rig mixes clip families in one tick.");
                    EntityManager.SetComponentData(entity, clock);
                    return;
                }
            }
            for (var i = 0; i < active; i++)
            {
                var shared = cache[clips[clipFor[i]].Value.Value.Artifact];
                var slice = decoded.GetSubArray(i * CanonicalBones, CanonicalBones).AsSpan();
                var error = shared.Sample(ordered[i].SampleSeconds, familyReference.AsSpan(), slice);
                if (error != AnimationError.None)
                {
                    WarnOnce("sample" + entity.Index, "SA: shape clip sample failed (" + error + ").");
                    EntityManager.SetComponentData(entity, clock);
                    return;
                }
            }
            var blend = CanonicalBlend.Evaluate(
                ordered.Slice(0, active), layers.Slice(0, layerCount), decoded.GetSubArray(0, active * CanonicalBones).AsSpan(),
                emptyPose.AsSpan(), emptyMask.AsSpan(),
                familyReference.AsSpan(), blended.AsSpan());
            if (blend != AnimationError.None)
            {
                WarnOnce("blend" + entity.Index, "SA: shape blend failed (" + blend + ").");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            var destination = retargeted.GetSubArray(0, boneCount).AsSpan();
            AnimationError remap;
            if (family == state.Binding.Target && boneCount == CanonicalBones)
                remap = RetargetTable.ApplyReferenceGeometry(family, state.Binding, state.Geometry.Retarget,
                    canonicalParents.AsSpan(), blended.AsSpan(), state.Rows, state.Offsets,
                    canonicalModel.AsSpan(), targetModel.GetSubArray(0, boneCount).AsSpan(), destination);
            else
                remap = RetargetTable.ApplyGeometry(family, state.Binding, state.Geometry.Retarget,
                    canonicalParents.AsSpan(), blended.AsSpan(), state.Rows, state.Offsets,
                    canonicalModel.AsSpan(), targetModel.GetSubArray(0, boneCount).AsSpan(), destination);
            if (remap != AnimationError.None)
            {
                WarnOnce("remap" + entity.Index, "SA: shape retarget failed (" + remap + ").");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            if (!SameMembers(ordered.Slice(0, active), state, active))
                clock.Continuity++;
            var frame = new AnimationFrame(clock.Tick, 0, clock.Continuity, clock.Seconds, advance, clock.Transition,
                seek ? FrameMode.Seek : FrameMode.Advance, 0.1f);
            var workspacePose = new ShapePoseWorkspace(state.Destination, state.NeutralCommitted, state.NeutralScratch,
                state.Displayed, state.DisplayScratch, state.PoseModels);
            var smooth = ShapeTimelinePose.Evaluate(frame, ordered.Slice(0, active), layers.Slice(0, layerCount), true, destination,
                zeroVelocity.GetSubArray(0, boneCount).AsSpan(), state.Geometry, state.Binding, state.Rows, state.Offsets, state.MotionScales,
                ref state.Frame, state.Bones, state.BoneScratch,
                state.PreviousContributions, state.PreviousLayers, workspacePose, out var action);
            LastActive = active;
            LastAction = action.ToString();
            LastSmooth = smooth.ToString();
            if (smooth != AnimationError.None)
            {
                WarnOnce("smooth" + entity.Index, "SA: shape timeline smoothing failed (" + smooth + ").");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            for (var b = 0; b < boneCount; b++)
            {
                var boneEntity = bones[b].Value;
                if (!EntityManager.HasComponent<LocalTransform>(boneEntity))
                    continue;
                var pose = state.Displayed[b];
                var uniform = pose.Scale.X;
                EntityManager.SetComponentData(boneEntity, LocalTransform.FromPositionRotationScale(
                    Conversions.ToUnity(pose.Position), Conversions.ToUnity(pose.Rotation), uniform));
                if (EntityManager.HasComponent<PostTransformMatrix>(boneEntity))
                    EntityManager.SetComponentData(boneEntity, new PostTransformMatrix
                    {
                        Value = float4x4.Scale(uniform != 0f ? Conversions.ToUnity(pose.Scale) / uniform : 1f)
                    });
            }
            for (var i = 0; i < channelCount; i++)
                channels[i] = new ShapeChannelState { Id = channels[i].Id, Requested = channels[i].Requested, Value = state.NextParameters[i].Value, Velocity = state.NextParameters[i].Velocity };
            EntityManager.SetComponentData(entity, shapeState);
            EntityManager.SetComponentData(entity, clock);
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

        static bool SameMembers(ReadOnlySpan<ClipContribution> contributions, ShapeRigState state, int active)
        {
            if (state.Frame.Motion.ContributionCount != active)
                return false;
            for (var i = 0; i < active; i++)
            {
                var previous = state.PreviousContributions[i];
                if (previous.Clip.Slot != contributions[i].Clip.Slot || previous.Layer != contributions[i].Layer || previous.MaskIndex != contributions[i].MaskIndex)
                    return false;
                if (Math.Abs(previous.Weight - contributions[i].Weight) > 1e-6f)
                    return false;
            }
            return true;
        }
    }
}
