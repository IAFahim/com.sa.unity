using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA.Animation;
using SA.Motion.Inertialization;
using SA.Motion.Mechanics;
using SA.Motion.Numerics;
using SA.Motion.Unity;
using SA.Shape;
using SharedMode = SA.Animation.ClipMode;

namespace SA.Shape.Unity
{
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
    internal unsafe struct ShapeRigJob : IJobChunk
    {
        const int CanonicalBones = ShapeTimelineSystem.CanonicalBones;
        const int MaxRigBones = ShapeTimelineSystem.MaxRigBones;
        const int BoneCountFault = ShapeTimelineSystem.BoneCountFault;
        const int DefinitionFault = ShapeTimelineSystem.DefinitionFault;
        const int ChannelFault = ShapeTimelineSystem.ChannelFault;
        const int RowsFault = ShapeTimelineSystem.RowsFault;
        const int StepFault = ShapeTimelineSystem.StepFault;
        const int PhysicalFault = ShapeTimelineSystem.PhysicalFault;
        const int GeometryFault = ShapeTimelineSystem.GeometryFault;
        const int MorphFault = ShapeTimelineSystem.MorphFault;
        const int BoneEntityFault = ShapeTimelineSystem.BoneEntityFault;
        const int FamilyFault = ShapeTimelineSystem.FamilyFault;
        const int SampleFault = ShapeTimelineSystem.SampleFault;
        const int BlendFault = ShapeTimelineSystem.BlendFault;
        const int RemapFault = ShapeTimelineSystem.RemapFault;
        const int SmoothFault = ShapeTimelineSystem.SmoothFault;
        public float DeltaTime;
        public uint Stamp;
        [ReadOnly] public EntityTypeHandle EntityType;
        [ReadOnly] public ComponentTypeHandle<Rig> RigType;
        [ReadOnly] public ComponentTypeHandle<SharedTableRef> TableType;
        [ReadOnly] public BufferTypeHandle<SharedClipAsset> ClipType;
        [ReadOnly] public BufferTypeHandle<SharedClipLayoutRef> LayoutType;
        public BufferTypeHandle<SharedContribution> ContributionType;
        [ReadOnly] public BufferTypeHandle<BoneEntity> BoneType;
        public ComponentTypeHandle<SharedClock> ClockType;
        [ReadOnly] public ComponentTypeHandle<ShapeDefinitionRef> DefinitionType;
        public BufferTypeHandle<ShapeChannelState> ChannelType;
        public ComponentTypeHandle<ShapeState> ShapeStateType;
        public ComponentTypeHandle<ShapePoseState> PoseStateType;
        public BufferTypeHandle<ShapeBoneFrame> BoneFrameType;
        public BufferTypeHandle<ShapeNeutralPose> NeutralType;
        public BufferTypeHandle<ShapeDisplayedPose> DisplayedType;
        public BufferTypeHandle<ShapeBoneOffset> OffsetType;
        public BufferTypeHandle<ShapeScalar> ScalarType;
        public BufferTypeHandle<ShapePreviousContribution> PreviousContributionType;
        public BufferTypeHandle<ShapePreviousLayer> PreviousLayerType;
        [ReadOnly] public NativeArray<Pose> FamilyReference;
        [ReadOnly] public NativeArray<int> CanonicalParents;
        [NativeDisableParallelForRestriction] public ComponentLookup<LocalTransform> Transforms;
        [NativeDisableParallelForRestriction] public ComponentLookup<PostTransformMatrix> PostTransforms;
        [NativeDisableParallelForRestriction] public ComponentLookup<ShapePhysical> Physicals;
        [NativeDisableParallelForRestriction] public ComponentLookup<ShapeContactState> Contacts;
        [NativeDisableParallelForRestriction] public BufferLookup<ShapeSkinDeformation> Deformations;
        [NativeDisableParallelForRestriction] public BufferLookup<ShapeMorphWeight> MorphWeights;
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

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(EntityType);
            var rigs = chunk.GetNativeArray(ref RigType);
            var tables = chunk.GetNativeArray(ref TableType);
            var clips = chunk.GetBufferAccessor(ref ClipType);
            var layouts = chunk.GetBufferAccessor(ref LayoutType);
            var contributions = chunk.GetBufferAccessor(ref ContributionType);
            var bones = chunk.GetBufferAccessor(ref BoneType);
            var clocks = chunk.GetNativeArray(ref ClockType);
            var definitions = chunk.GetNativeArray(ref DefinitionType);
            var channels = chunk.GetBufferAccessor(ref ChannelType);
            var shapeStates = chunk.GetNativeArray(ref ShapeStateType);
            var poseStates = chunk.GetNativeArray(ref PoseStateType);
            var boneFrames = chunk.GetBufferAccessor(ref BoneFrameType);
            var neutralPoses = chunk.GetBufferAccessor(ref NeutralType);
            var displayedPoses = chunk.GetBufferAccessor(ref DisplayedType);
            var boneOffsets = chunk.GetBufferAccessor(ref OffsetType);
            var scalars = chunk.GetBufferAccessor(ref ScalarType);
            var previousContributions = chunk.GetBufferAccessor(ref PreviousContributionType);
            var previousLayers = chunk.GetBufferAccessor(ref PreviousLayerType);
            for (var i = 0; i < chunk.Count; i++)
            {
                var clock = clocks[i];
                var shapeState = shapeStates[i];
                var poseState = poseStates[i];
                var rigContributions = contributions[i];
                var rigChannels = channels[i];
                var rigBoneFrames = boneFrames[i];
                var rigNeutral = neutralPoses[i];
                var rigDisplayed = displayedPoses[i];
                var rigOffsets = boneOffsets[i];
                var rigScalars = scalars[i];
                var rigPreviousContributions = previousContributions[i];
                var rigPreviousLayers = previousLayers[i];
                Evaluate(entities[i], rigs[i], tables[i], clips[i], layouts[i], ref rigContributions, bones[i], ref clock, definitions[i],
                    ref rigChannels, ref shapeState, ref poseState, ref rigBoneFrames, ref rigNeutral, ref rigDisplayed, ref rigOffsets,
                    ref rigScalars, ref rigPreviousContributions, ref rigPreviousLayers);
                clocks[i] = clock;
                shapeStates[i] = shapeState;
                poseStates[i] = poseState;
            }
        }

        void Evaluate(Entity entity, in Rig rigReference, in SharedTableRef table, in DynamicBuffer<SharedClipAsset> clips,
            in DynamicBuffer<SharedClipLayoutRef> layouts, ref DynamicBuffer<SharedContribution> contributions, in DynamicBuffer<BoneEntity> bones,
            ref SharedClock clockData, in ShapeDefinitionRef definitionReference, ref DynamicBuffer<ShapeChannelState> channels,
            ref ShapeState shapeStateData, ref ShapePoseState state, ref DynamicBuffer<ShapeBoneFrame> boneFrames,
            ref DynamicBuffer<ShapeNeutralPose> neutralPoses, ref DynamicBuffer<ShapeDisplayedPose> displayedPoses,
            ref DynamicBuffer<ShapeBoneOffset> boneOffsets, ref DynamicBuffer<ShapeScalar> scalars,
            ref DynamicBuffer<ShapePreviousContribution> previousContributions, ref DynamicBuffer<ShapePreviousLayer> previousLayers)
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
            if (table.Source == 0)
                return;
            if (!definitionReference.Value.IsCreated)
            {
                Fault(entity, DefinitionFault, 0, 0);
                clockData = clock;
                return;
            }
            ref var definitionData = ref definitionReference.Value.Value;
            var channelCount = definitionData.Channels.Length;
            if (channels.Length != channelCount)
            {
                Fault(entity, ChannelFault, channels.Length, channelCount);
                clockData = clock;
                return;
            }
            if (state.BoneCount != boneCount || state.ChannelCount != channelCount)
            {
                state = default;
                state.BoneCount = boneCount;
                state.ChannelCount = channelCount;
                Clear(boneFrames, boneCount);
                Clear(neutralPoses, boneCount);
                Clear(displayedPoses, boneCount);
                Clear(boneOffsets, boneCount);
                Clear(scalars, boneCount + channelCount);
                Clear(previousContributions, SharedFamilyControl.MaxContributions);
                Clear(previousLayers, SharedFamilyControl.MaxContributions);
                for (var i = 0; i < channelCount; i++)
                    scalars[boneCount + i] = new ShapeScalar { Value = channels[i].Value };
            }
            var scalarSpan = scalars.Reinterpret<float>().AsNativeArray().AsSpan();
            var motionScales = scalarSpan.Slice(0, boneCount);
            var committedValues = scalarSpan.Slice(boneCount, channelCount);
            var offsets = boneOffsets.Reinterpret<Float3>().AsNativeArray().AsSpan();
            if (state.HasGeometry != 0 && (state.BuiltDefinition != definitionData.Definition || state.BuiltTarget != definitionData.Target))
            {
                state.Frame = default;
                state.HasGeometry = 0;
                for (var i = 0; i < channelCount; i++)
                    committedValues[i] = channels[i].Value;
            }
            if (!table.Value.IsCreated)
            {
                clockData = clock;
                return;
            }
            if (state.Table != table.Source)
            {
                state.Table = table.Source;
                state.Frame = default;
                state.HasGeometry = 0;
            }
            ref var tableLayout = ref table.Value.Value;
            var binding = tableLayout.Binding;
            var rows = View(ref tableLayout.Rows);
            if (rows.Length != boneCount)
            {
                Fault(entity, RowsFault, rows.Length, boneCount);
                clockData = clock;
                return;
            }
            var shapeState = shapeStateData;
            var definitionChannels = View(ref definitionData.Channels);
            var requestedValues = new NativeArray<float>(channelCount, Allocator.Temp);
            var steppedValues = new NativeArray<float>(channelCount, Allocator.Temp);
            var currentParameters = new NativeArray<ShapeParameter>(channelCount, Allocator.Temp);
            var nextParameters = new NativeArray<ShapeParameter>(channelCount, Allocator.Temp);
            for (var i = 0; i < channelCount; i++)
            {
                requestedValues[i] = channels[i].Requested;
                currentParameters[i] = new ShapeParameter(channels[i].Value, channels[i].Velocity);
            }
            var stepError = ShapeParameters.Step(definitionChannels, requestedValues.AsReadOnlySpan(), currentParameters.AsReadOnlySpan(), advance, nextParameters.AsSpan());
            if (stepError != AnimationError.None)
            {
                Fault(entity, StepFault, (int)stepError, 0);
                clockData = clock;
                return;
            }
            for (var i = 0; i < channelCount; i++)
                steppedValues[i] = nextParameters[i].Value;
            var shapeChanged = ShapeRevision.HasEffectiveChange(committedValues, steppedValues.AsReadOnlySpan());
            var candidate = shapeState.Revision + (shapeChanged ? 1UL : 0UL);
            if (shapeChanged && Physicals.HasComponent(entity))
            {
                var physical = Physicals[entity];
                if (physical.LivePhysical != 0)
                {
                    for (var i = 0; i < channels.Length; i++)
                    {
                        var channel = channels[i];
                        channel.Requested = channel.Value;
                        channels[i] = channel;
                    }
                    physical.PendingRebuild = 0;
                    physical.Diagnostic = ShapePhysicalSync.LivePhysicalRefusal;
                    Physicals[entity] = physical;
                    Fault(entity, PhysicalFault, 0, 0);
                    clockData = clock;
                    return;
                }
            }
            if (state.HasGeometry == 0 || shapeChanged)
            {
                var profile = new ShapeProfile(definitionData.Definition, definitionData.Target,
                    definitionData.SupportA, definitionData.SupportB, definitionData.Grounding,
                    definitionData.MinimumLength, definitionData.MaximumLength,
                    definitionData.MinimumSkinScale, definitionData.MaximumSkinScale);
                var definition = new ShapeDefinition(profile, definitionChannels, View(ref definitionData.Offsets),
                    View(ref definitionData.Skin), View(ref definitionData.Frames));
                var lengthScales = new NativeArray<float>(boneCount, Allocator.Temp);
                var skinScales = new NativeArray<Float3>(boneCount, Allocator.Temp);
                var deformations = new NativeArray<Affine>(boneCount, Allocator.Temp);
                var rigModels = new NativeArray<RigidPose>(boneCount, Allocator.Temp);
                var workspace = new ShapeWorkspace(offsets, lengthScales.AsSpan(), motionScales, skinScales.AsSpan(), deformations.AsSpan(), rigModels.AsSpan());
                var geometryError = ShapeRig.Evaluate(definition, binding, rows, steppedValues.AsReadOnlySpan(), candidate, workspace, out state.Geometry);
                if (geometryError != AnimationError.None)
                {
                    Fault(entity, GeometryFault, (int)geometryError, 0);
                    clockData = clock;
                    return;
                }
                state.HasGeometry = 1;
                state.BuiltDefinition = definitionData.Definition;
                state.BuiltTarget = definitionData.Target;
                if (shapeChanged)
                {
                    for (var i = 0; i < channelCount; i++)
                        committedValues[i] = steppedValues[i];
                    shapeState.Revision = candidate;
                }
                if (Deformations.HasBuffer(entity))
                {
                    var published = Deformations[entity];
                    if (published.Length != boneCount)
                        published.ResizeUninitialized(boneCount);
                    for (var b = 0; b < boneCount; b++)
                        published[b] = new ShapeSkinDeformation { Value = deformations[b] };
                }
                var morphCount = definitionData.Morphs.Length;
                if (morphCount > 0 && MorphWeights.HasBuffer(entity))
                {
                    var weights = new NativeArray<float>(morphCount, Allocator.Temp);
                    var weightBuffer = MorphWeights[entity];
                    if (weightBuffer.Length != morphCount)
                        weightBuffer.ResizeUninitialized(morphCount);
                    if (ShapeMorphs.Evaluate(definitionChannels, steppedValues.AsReadOnlySpan(), View(ref definitionData.Morphs),
                        View(ref definitionData.MorphEffects), weights.AsSpan()) == AnimationError.None)
                    {
                        for (var m = 0; m < morphCount; m++)
                            weightBuffer[m] = new ShapeMorphWeight { Value = weights[m] };
                    }
                    else
                    {
                        Fault(entity, MorphFault, 0, 0);
                    }
                }
                var supportA = definitionData.SupportA;
                var supportB = definitionData.SupportB;
                if (Contacts.HasComponent(entity) && (uint)supportA.Bone < (uint)boneCount && (uint)supportB.Bone < (uint)boneCount)
                {
                    ShapeContactUpload.MarkerPoint(rigModels[supportA.Bone], deformations[supportA.Bone], supportA.Offset, out var soleA);
                    ShapeContactUpload.MarkerPoint(rigModels[supportB.Bone], deformations[supportB.Bone], supportB.Offset, out var soleB);
                    Contacts[entity] = new ShapeContactState
                    {
                        SoleA = soleA,
                        SoleB = soleB,
                        HipHeight = state.Geometry.HipHeight,
                        GroundY = state.Geometry.GroundY,
                        Revision = candidate
                    };
                }
            }
            if (bones.Length < boneCount)
            {
                Fault(entity, BoneEntityFault, bones.Length, boneCount);
                clockData = clock;
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
            var remap = family == binding.Target && boneCount == CanonicalBones
                ? RetargetTable.ApplyReferenceGeometry(family, binding, state.Geometry.Retarget, canonical, blended.AsSpan(), rows, offsets,
                    canonicalModel.AsSpan(), targetModel.AsSpan(), destination)
                : RetargetTable.ApplyGeometry(family, binding, state.Geometry.Retarget, canonical, blended.AsSpan(), rows, offsets,
                    canonicalModel.AsSpan(), targetModel.AsSpan(), destination);
            if (remap != AnimationError.None)
            {
                Fault(entity, RemapFault, (int)remap, 0);
                clockData = clock;
                return;
            }
            var previous = previousContributions.Reinterpret<ClipContribution>().AsNativeArray().AsSpan();
            if (!SameMembers(ordered.Slice(0, active), previous, state.Frame.Motion.ContributionCount))
                clock.Continuity++;
            var frame = new AnimationFrame(clock.Tick, 0, clock.Continuity, clock.Seconds, advance, clock.Transition,
                seek ? FrameMode.Seek : FrameMode.Advance, 0.1f);
            var boneScratch = new NativeArray<BoneFrameState>(boneCount, Allocator.Temp);
            var destinationScratch = new NativeArray<Pose>(boneCount, Allocator.Temp);
            var neutralScratch = new NativeArray<Pose>(boneCount, Allocator.Temp);
            var displayScratch = new NativeArray<Pose>(boneCount, Allocator.Temp);
            var poseModels = new NativeArray<RigidPose>(boneCount, Allocator.Temp);
            var zeroVelocity = new NativeArray<PoseVelocity>(boneCount, Allocator.Temp);
            var displayed = displayedPoses.Reinterpret<Pose>().AsNativeArray();
            var workspacePose = new ShapePoseWorkspace(destinationScratch.AsSpan(), neutralPoses.Reinterpret<Pose>().AsNativeArray().AsSpan(),
                neutralScratch.AsSpan(), displayed.AsSpan(), displayScratch.AsSpan(), poseModels.AsSpan());
            var smooth = ShapeTimelinePose.Evaluate(frame, ordered.Slice(0, active), layers.Slice(0, layerCount), true, destination,
                zeroVelocity.AsSpan(), state.Geometry, binding, rows, offsets, motionScales,
                ref state.Frame, boneFrames.Reinterpret<BoneFrameState>().AsNativeArray().AsSpan(), boneScratch.AsSpan(),
                previous, previousLayers.Reinterpret<LayerInput>().AsNativeArray().AsSpan(), workspacePose, out var action);
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
            for (var b = 0; b < boneCount; b++)
            {
                var boneEntity = bones[b].Value;
                if (!Transforms.HasComponent(boneEntity))
                    continue;
                var pose = displayed[b];
                var uniform = pose.Scale.X;
                Transforms[boneEntity] = LocalTransform.FromPositionRotationScale(
                    Conversions.ToUnity(pose.Position), Conversions.ToUnity(pose.Rotation), uniform);
                if (PostTransforms.HasComponent(boneEntity))
                    PostTransforms[boneEntity] = new PostTransformMatrix
                    {
                        Value = float4x4.Scale(uniform != 0f ? Conversions.ToUnity(pose.Scale) / uniform : 1f)
                    };
            }
            for (var i = 0; i < channelCount; i++)
                channels[i] = new ShapeChannelState { Id = channels[i].Id, Requested = channels[i].Requested, Value = nextParameters[i].Value, Velocity = nextParameters[i].Velocity };
            shapeStateData = shapeState;
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

    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
    internal struct ShapeMeasureJob : IJobChunk
    {
        const int MaxBones = ShapeSkinPaletteSystem.MaxBones;
        [ReadOnly] public ComponentLookup<ShapeDriven> Shaped;
        [ReadOnly] public ComponentLookup<Rig> Rigs;
        [ReadOnly] public BufferLookup<BoneEntity> Bones;
        [ReadOnly] public BufferLookup<ShapeSkinDeformation> Deformations;
        public int Limit;
        public NativeList<PaletteRange>.ParallelWriter Ranges;
        [ReadOnly] public ComponentTypeHandle<Skin> SkinType;
        [ReadOnly] public ComponentTypeHandle<SkinBinding> BindingType;
        [ReadOnly] public NativeArray<int> ChunkBase;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var skins = chunk.GetNativeArray(ref SkinType);
            var bindings = chunk.GetNativeArray(ref BindingType);
            var first = ChunkBase[unfilteredChunkIndex];
            for (var i = 0; i < chunk.Count; i++)
                Measure(first + i, skins[i], bindings[i]);
        }

        void Measure(int index, in Skin skin, in SkinBinding binding)
        {
            if (!skin.Value.IsCreated || !Shaped.HasComponent(skin.Rig))
                return;
            if (!Bones.HasBuffer(skin.Rig) || !Deformations.HasBuffer(skin.Rig) || !Rigs.HasComponent(skin.Rig))
                return;
            var boneCount = skin.Value.Value.Bones.Length;
            if (boneCount <= 0 || boneCount > MaxBones)
                return;
            var rigCount = Bones[skin.Rig].Length;
            if (rigCount <= 0 || rigCount > MaxBones || Deformations[skin.Rig].Length != rigCount)
                return;
            var rigReference = Rigs[skin.Rig];
            if (!rigReference.Value.IsCreated || rigReference.Value.Value.Bones.Length != rigCount)
                return;
            var start = (int)binding.MatrixBase;
            if (start <= 0 || start > Limit - boneCount)
                return;
            Ranges.AddNoResize(new PaletteRange { Start = start, Count = boneCount, Index = index });
        }
    }

    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
    internal unsafe struct ShapePaletteJob : IJobChunk
    {
        [ReadOnly] public ComponentLookup<Rig> Rigs;
        [ReadOnly] public BufferLookup<BoneEntity> Bones;
        [ReadOnly] public BufferLookup<ShapeSkinDeformation> Deformations;
        [ReadOnly] public ComponentLookup<LocalTransform> Transforms;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Built;
        [NativeDisableParallelForRestriction] public NativeArray<float4x4> Staging;
        [ReadOnly] public ComponentTypeHandle<Skin> SkinType;
        [ReadOnly] public NativeArray<int> ChunkBase;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var skins = chunk.GetNativeArray(ref SkinType);
            var first = ChunkBase[unfilteredChunkIndex];
            for (var i = 0; i < chunk.Count; i++)
                Build(first + i, skins[i]);
        }

        void Build(int index, in Skin skin)
        {
            var offset = Offsets[index];
            if (offset < 0)
                return;
            ref var blob = ref skin.Value.Value;
            var boneCount = blob.Bones.Length;
            var rigBones = Bones[skin.Rig];
            var rigCount = rigBones.Length;
            var rigReference = Rigs[skin.Rig];
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
                    Scale = Float3.One
                };
                parents[b] = rig.Bones[b].Parent;
            }
            var model = new NativeArray<RigidPose>(rigCount, Allocator.Temp);
            if (RigidMath.Model(parents.AsReadOnlySpan(), localPose.AsReadOnlySpan(), model.AsSpan()) != AnimationError.None)
                return;
            var bindMatrices = new NativeArray<Affine>(boneCount, Allocator.Temp);
            for (var b = 0; b < boneCount; b++)
                bindMatrices[b] = ToAffine(blob.Bindposes[b]);
            var affineModel = new NativeArray<Affine>(rigCount, Allocator.Temp);
            var palette = new NativeArray<Affine>(boneCount, Allocator.Temp);
            var normals = new NativeArray<Float3x3>(boneCount, Allocator.Temp);
            var paletteToRig = new ReadOnlySpan<int>(blob.Bones.GetUnsafePtr(), boneCount);
            var build = ShapeSkinUpload.BuildPalette(ToAffine(blob.MeshFromRig), model.AsReadOnlySpan(),
                Deformations[skin.Rig].Reinterpret<Affine>().AsNativeArray().AsReadOnlySpan(), paletteToRig, bindMatrices.AsReadOnlySpan(),
                affineModel.AsSpan(), palette.AsSpan(), normals.AsSpan());
            if (build != AnimationError.None)
                return;
            for (var b = 0; b < boneCount; b++)
            {
                var joint = palette[b];
                Staging[offset + b] = new float4x4(
                    new float4(Conversions.ToUnity(joint.C0), 0f), new float4(Conversions.ToUnity(joint.C1), 0f),
                    new float4(Conversions.ToUnity(joint.C2), 0f), new float4(Conversions.ToUnity(joint.C3), 1f));
            }
            Built[index] = 1;
        }

        static Affine ToAffine(in float3x4 matrix)
        {
            return new Affine(
                Conversions.ToMotion(matrix.c0), Conversions.ToMotion(matrix.c1),
                Conversions.ToMotion(matrix.c2), Conversions.ToMotion(matrix.c3));
        }
    }
}
