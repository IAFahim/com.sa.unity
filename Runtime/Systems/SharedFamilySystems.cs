using System;
using System.Collections.Generic;
using Unity.Collections;
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
    public partial class SharedFamilySystem : SystemBase
    {
        public const int CanonicalBones = 56;
        const int MaxRigBones = 128;

        sealed class RigState
        {
            public int BoneCount;
            public int[] RigParents = Array.Empty<int>();
            public PoseFrameState Frame;
            public BoneFrameState[] Bones = Array.Empty<BoneFrameState>();
            public BoneFrameState[] BoneScratch = Array.Empty<BoneFrameState>();
            public ClipContribution[] PreviousContributions = new ClipContribution[SharedFamilyControl.MaxContributions];
            public LayerInput[] PreviousLayers = new LayerInput[SharedFamilyControl.MaxContributions];
            public Pose[] Committed = Array.Empty<Pose>();
            public Pose[] PoseScratch = Array.Empty<Pose>();
            public BlobAssetReference<SharedRetargetData> TableRef;
            public RetargetBinding Binding;
            public RetargetRow[] Rows = Array.Empty<RetargetRow>();
        }

        EntityQuery rigs;
        readonly Dictionary<Entity, RigState> states = new Dictionary<Entity, RigState>();
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
                ComponentType.Exclude<ShapeDriven>());
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
                if (!EntityManager.Exists(pair.Key) || !EntityManager.HasComponent<SharedTimeline>(pair.Key))
                {
                    dead = pair.Key;
                    break;
                }
            if (dead != Entity.Null)
                states.Remove(dead);
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
                WarnOnce("bones" + entity.Index, "SA: timeline rig has " + boneCount + " bones, expected 1.." + MaxRigBones + ".");
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
                WarnOnce("table" + entity.Index, "SA: timeline rig is missing its retarget table.");
                return;
            }
            if (!states.TryGetValue(entity, out var state) || state.BoneCount != boneCount)
            {
                state = new RigState { BoneCount = boneCount };
                state.RigParents = new int[boneCount];
                for (var b = 0; b < boneCount; b++)
                    state.RigParents[b] = rig.Bones[b].Parent;
                state.Bones = new BoneFrameState[boneCount];
                state.BoneScratch = new BoneFrameState[boneCount];
                state.Committed = new Pose[boneCount];
                state.PoseScratch = new Pose[boneCount];
                states[entity] = state;
            }
            if (!state.TableRef.Equals(tableReference) || state.Rows.Length == 0)
            {
                ref var tableBytes = ref tableReference.Value.Table;
                var error = RetargetFile.Load(
                    new ReadOnlySpan<byte>(tableBytes.GetUnsafePtr(), tableBytes.Length),
                    tableReference.Value.Family, tableReference.Value.Target,
                    canonicalParents.AsSpan(), familyReference.AsSpan(),
                    out state.Binding, out state.Rows);
                if (error != AnimationError.None)
                {
                    WarnOnce("tableload" + entity.Index, "SA: timeline rig table failed to load (" + error + ").");
                    return;
                }
                state.TableRef = tableReference;
                state.Frame = default;
            }
            var clips = EntityManager.GetBuffer<SharedClipAsset>(entity);
            var contributions = EntityManager.GetBuffer<SharedContribution>(entity);
            var bones = EntityManager.GetBuffer<BoneEntity>(entity);
            if (bones.Length < boneCount)
            {
                WarnOnce("boneentities" + entity.Index, "SA: timeline rig has " + bones.Length + " bone entities for " + boneCount + " bones.");
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
                        WarnOnce("clip" + blob.Artifact.GetHashCode(), "SA: timeline clip failed to load (" + load + ").");
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
                    WarnOnce("family" + entity.Index, "SA: timeline rig mixes clip families in one tick.");
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
                    WarnOnce("sample" + entity.Index, "SA: timeline clip sample failed (" + error + ").");
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
                WarnOnce("blend" + entity.Index, "SA: timeline blend failed (" + blend + ").");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            var destination = retargeted.GetSubArray(0, boneCount).AsSpan();
            if (family == state.Binding.Target && boneCount == CanonicalBones)
            {
                var identity = IdentityRetarget.Apply(family, state.Binding.Target,
                    new ReadOnlySpan<int>(state.RigParents), blended.AsSpan(),
                    targetModel.GetSubArray(0, boneCount).AsSpan(), destination);
                if (identity != AnimationError.None)
                {
                    WarnOnce("identity" + entity.Index, "SA: timeline identity retarget failed (" + identity + ").");
                    EntityManager.SetComponentData(entity, clock);
                    return;
                }
            }
            else
            {
                var remap = RetargetTable.Apply(family, state.Binding,
                    canonicalParents.AsSpan(), blended.AsSpan(), state.Rows,
                    canonicalModel.AsSpan(), targetModel.GetSubArray(0, boneCount).AsSpan(), destination);
                if (remap != AnimationError.None)
                {
                    WarnOnce("remap" + entity.Index, "SA: timeline retarget failed (" + remap + ").");
                    EntityManager.SetComponentData(entity, clock);
                    return;
                }
                CanonicalTranslation.Apply(state.Rows, state.Binding, canonicalParents.AsSpan(), blended.AsSpan(), familyReference.AsSpan(),
                    canonicalModel.AsSpan(), targetModel.GetSubArray(0, boneCount).AsSpan(), destination);
            }
            if (!SameMembers(ordered.Slice(0, active), state, active))
                clock.Continuity++;
            var frame = new AnimationFrame(clock.Tick, 0, clock.Continuity, clock.Seconds, advance, clock.Transition,
                seek ? FrameMode.Seek : FrameMode.Advance, 0.1f);
            var smooth = TimelinePose.Evaluate(frame, ordered.Slice(0, active), layers.Slice(0, layerCount), true, destination,
                zeroVelocity.GetSubArray(0, boneCount).AsSpan(), ref state.Frame, state.Bones, state.BoneScratch,
                state.PreviousContributions, state.PreviousLayers, state.Committed, state.PoseScratch, out var action);
            LastActive = active;
            LastAction = action.ToString();
            LastSmooth = smooth.ToString();
            if (smooth != AnimationError.None)
            {
                WarnOnce("smooth" + entity.Index, "SA: timeline smoothing failed (" + smooth + ").");
                EntityManager.SetComponentData(entity, clock);
                return;
            }
            for (var b = 0; b < boneCount; b++)
            {
                var boneEntity = bones[b].Value;
                if (!EntityManager.HasComponent<LocalTransform>(boneEntity))
                    continue;
                var pose = state.Committed[b];
                var uniform = pose.Scale.X;
                EntityManager.SetComponentData(boneEntity, LocalTransform.FromPositionRotationScale(
                    Conversions.ToUnity(pose.Position), Conversions.ToUnity(pose.Rotation), uniform));
                if (EntityManager.HasComponent<PostTransformMatrix>(boneEntity))
                    EntityManager.SetComponentData(boneEntity, new PostTransformMatrix
                    {
                        Value = float4x4.Scale(uniform != 0f ? Conversions.ToUnity(pose.Scale) / uniform : 1f)
                    });
            }
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

        static bool SameMembers(ReadOnlySpan<ClipContribution> contributions, RigState state, int active)
        {
            if (state.Frame.ContributionCount != active)
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

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(SkinningSystem))]
    [UpdateBefore(typeof(global::Unity.Rendering.EntitiesGraphicsSystem))]
    public partial class SharedFamilyPaletteSystem : SystemBase
    {
        const int MaxBones = 512;

        EntityQuery skinQuery;
        NativeArray<RigidPose> model;
        NativeArray<Affine> affineModel;
        NativeArray<Affine> bindMatrices;
        NativeArray<Affine> palette;
        NativeArray<float4x4> upload;
        NativeArray<Pose> localPose;
        NativeArray<int> parents;

        protected override void OnCreate()
        {
            skinQuery = GetEntityQuery(ComponentType.ReadOnly<Skin>(), ComponentType.ReadOnly<SkinBinding>());
            model = new NativeArray<RigidPose>(MaxBones, Allocator.Persistent);
            affineModel = new NativeArray<Affine>(MaxBones, Allocator.Persistent);
            bindMatrices = new NativeArray<Affine>(MaxBones, Allocator.Persistent);
            palette = new NativeArray<Affine>(MaxBones, Allocator.Persistent);
            upload = new NativeArray<float4x4>(MaxBones, Allocator.Persistent);
            localPose = new NativeArray<Pose>(MaxBones, Allocator.Persistent);
            parents = new NativeArray<int>(MaxBones, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            model.Dispose();
            affineModel.Dispose();
            bindMatrices.Dispose();
            palette.Dispose();
            upload.Dispose();
            localPose.Dispose();
            parents.Dispose();
        }

        protected override void OnUpdate()
        {
            var skinning = World.GetExistingSystemManaged<SkinningSystem>();
            if (skinning == null)
                return;
            var matrices = skinning.SkinMatrixBuffer;
            if (matrices == null)
                return;
            var skins = skinQuery.ToEntityArray(Allocator.Temp);
            foreach (var skinEntity in skins)
            {
                if (!RouteSkin(skinEntity, matrices))
                    continue;
            }
            skins.Dispose();
        }

        unsafe bool RouteSkin(Entity skinEntity, UnityEngine.GraphicsBuffer matrices)
        {
            var skin = EntityManager.GetComponentData<Skin>(skinEntity);
            if (!skin.Value.IsCreated || !EntityManager.HasComponent<SharedTimeline>(skin.Rig))
                return true;
            if (EntityManager.HasComponent<ShapeDriven>(skin.Rig))
                return true;
            if (!EntityManager.HasBuffer<BoneEntity>(skin.Rig))
                return true;
            ref var blob = ref skin.Value.Value;
            var boneCount = blob.Bones.Length;
            if (boneCount <= 0 || boneCount > MaxBones)
                return true;
            var rigBones = EntityManager.GetBuffer<BoneEntity>(skin.Rig);
            if (rigBones.Length <= 0 || rigBones.Length > MaxBones)
                return true;
            var rigReference = EntityManager.GetComponentData<Rig>(skin.Rig);
            if (!rigReference.Value.IsCreated || rigReference.Value.Value.Bones.Length != rigBones.Length)
                return true;
            for (var b = 0; b < rigBones.Length; b++)
            {
                if (!EntityManager.HasComponent<LocalTransform>(rigBones[b].Value))
                    return true;
                var transform = EntityManager.GetComponentData<LocalTransform>(rigBones[b].Value);
                localPose[b] = new Pose
                {
                    Position = Conversions.ToMotion(transform.Position),
                    Rotation = Conversions.ToMotion(transform.Rotation),
                    Scale = new Float3(transform.Scale, transform.Scale, transform.Scale)
                };
                parents[b] = rigReference.Value.Value.Bones[b].Parent;
            }
            if (RigidMath.Model(parents.GetSubArray(0, rigBones.Length).AsSpan(), localPose.GetSubArray(0, rigBones.Length).AsSpan(), model.GetSubArray(0, rigBones.Length).AsSpan()) != AnimationError.None)
                return false;
            for (var b = 0; b < rigBones.Length; b++)
                affineModel[b] = ToAffine(model[b]);
            for (var b = 0; b < boneCount; b++)
                bindMatrices[b] = ToAffine(blob.Bindposes[b]);
            var paletteToRig = new ReadOnlySpan<int>(blob.Bones.GetUnsafePtr(), boneCount);
            var build = SkinPalette.Build(affineModel.GetSubArray(0, rigBones.Length).AsSpan(), paletteToRig,
                bindMatrices.GetSubArray(0, boneCount).AsSpan(), ToAffine(blob.MeshFromRig), palette.GetSubArray(0, boneCount).AsSpan());
            if (build != AnimationError.None)
                return false;
            for (var b = 0; b < boneCount; b++)
            {
                var joint = palette[b];
                upload[b] = new float4x4(new float4(Conversions.ToUnity(joint.C0), 0f), new float4(Conversions.ToUnity(joint.C1), 0f), new float4(Conversions.ToUnity(joint.C2), 0f), new float4(Conversions.ToUnity(joint.C3), 1f));
            }
            var binding = EntityManager.GetComponentData<SkinBinding>(skinEntity);
            matrices.SetData(upload, 0, (int)binding.MatrixBase, boneCount);
            return true;
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
