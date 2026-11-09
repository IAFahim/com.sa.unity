using System;
using System.Globalization;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using SA.Animation;
using SA.Animation.Content;
using SA.Motion.Numerics;
using SA.Motion.Unity;
using Pose = SA.Motion.Numerics.Pose;
using Quaternion = SA.Motion.Numerics.Quaternion;

namespace SA.Editor
{
    public delegate bool SharedClipAttach(EntityManager manager, Entity entity, byte[] bytes, bool loop, out int clipIndex, out string error);

    public sealed class ClipRuntimeResult
    {
        public int Samples;
        public float TruthDegrees;
        public float TruthCentimetres;
        public float DecoderDegrees;
        public float DecoderCentimetres;
        public string Worst = string.Empty;
    }

    public static class FamilyClipRuntimeCheck
    {
        static readonly int[] TrackedPositions = { 1, 6, 13, 32, 50, 54 };

        public static bool AttachLikeRuntime(EntityManager manager, Entity entity, byte[] bytes, bool loop, out int clipIndex, out string error)
        {
            clipIndex = -1;
            error = string.Empty;
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            if (!SharedFamilyBake.FamilyId(out var fa, out var fb, out var fc, out var fd)
                || SharedClipReader.Load(bytes, new AssetId(fa, fb, fc, fd), reference, out var loaded) != AnimationError.None || loaded == null)
            {
                error = "clip rejected.";
                return false;
            }
            var blob = SharedFamilyBake.MakeClipBlob(bytes, loaded.Duration, loaded.SampleCount, loop);
            manager.CompleteAllTrackedJobs();
            var clips = manager.GetBuffer<SharedClipAsset>(entity);
            clipIndex = clips.Length;
            clips.Add(new SharedClipAsset { Value = blob });
            var contributions = manager.GetBuffer<SharedContribution>(entity);
            contributions.Add(new SharedContribution { Clip = clipIndex, Weight = 1f, Mask = ushort.MaxValue, Speed = 1f });
            SharedFamilyControl.PlaySingle(ref contributions, clipIndex);
            return true;
        }

        public static bool TryMeasure(FamilyRigPlan plan, AnimationClip clip, float hz, int samples, SharedClipAttach attach, out ClipRuntimeResult result, out string error)
        {
            result = new ClipRuntimeResult();
            error = string.Empty;
            if (plan == null || clip == null || attach == null || samples < 1)
            {
                error = "plan, clip, attach and at least one sample are required.";
                return false;
            }
            if (!FamilyClips.TrySample(plan, clip, hz, out var canonical, out var root, out var start, out var frames, out error))
                return false;
            if (!FamilyClips.TryEncode(plan, canonical, root, FamilyClips.EncodedDuration(clip, hz), clip.isLooping, false, out var bytes, out error))
                return false;
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            if (SharedClipReader.Load(bytes, plan.Family, reference, out var loaded) != AnimationError.None || loaded == null)
            {
                error = "encoded clip failed to reload.";
                return false;
            }
            if (RetargetFile.Encode(plan.binding, plan.canonicalParents, plan.canonicalReference, plan.rows, out var tableBytes) != AnimationError.None
                || RetargetFile.Load(tableBytes, plan.family, plan.target, plan.canonicalParents, plan.canonicalReference, out _, out _) != AnimationError.None)
            {
                error = "retarget table failed to encode or reload.";
                return false;
            }
            var table = TableBlob(plan, tableBytes);
            var placeholder = SharedFamilyBake.MakeClipBlob(bytes, loaded.Duration, loaded.SampleCount, clip.isLooping);
            var world = new World("SA.ClipSweep.Runtime");
            var copy = UnityEngine.Object.Instantiate(plan.character);
            copy.hideFlags = HideFlags.HideAndDontSave;
            BlobAssetReference<RigBlob> rigBlob = default;
            try
            {
                var entity = Build(world.EntityManager, plan, table, placeholder, out rigBlob);
                if (!attach(world.EntityManager, entity, bytes, clip.isLooping, out var clipIndex, out error))
                    return false;
                var system = world.GetOrCreateSystemManaged<SharedFamilySystem>();
                return Compare(plan, clip, copy, world, entity, system, loaded, start, frames, samples, result, out error);
            }
            finally
            {
                var clips = world.EntityManager.CreateEntityQuery(typeof(SharedClipAsset)).ToEntityArray(Allocator.Temp);
                foreach (var e in clips)
                {
                    var buffer = world.EntityManager.GetBuffer<SharedClipAsset>(e);
                    for (var i = 0; i < buffer.Length; i++)
                        if (buffer[i].Value.IsCreated && !buffer[i].Value.Equals(placeholder))
                            buffer[i].Value.Dispose();
                }
                clips.Dispose();
                world.Dispose();
                if (placeholder.IsCreated)
                    placeholder.Dispose();
                if (table.IsCreated)
                    table.Dispose();
                if (rigBlob.IsCreated)
                    rigBlob.Dispose();
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        static BlobAssetReference<SharedRetargetData> TableBlob(FamilyRigPlan plan, byte[] tableBytes)
        {
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var data = ref builder.ConstructRoot<SharedRetargetData>();
            data.Family = plan.family;
            data.Target = plan.target;
            var stored = builder.Allocate(ref data.Table, tableBytes.Length);
            for (var i = 0; i < tableBytes.Length; i++)
                stored[i] = tableBytes[i];
            return builder.CreateBlobAssetReference<SharedRetargetData>(Allocator.Persistent);
        }

        static Entity Build(EntityManager manager, FamilyRigPlan plan, BlobAssetReference<SharedRetargetData> table, BlobAssetReference<SharedClipData> placeholder,
            out BlobAssetReference<RigBlob> rigBlob)
        {
            var count = plan.bones.Length;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var rig = ref builder.ConstructRoot<RigBlob>();
                rig.Id = new Unity.Entities.Hash128(3, 1, 4, 1);
                var bones = builder.Allocate(ref rig.Bones, count);
                for (var b = 0; b < count; b++)
                    bones[b] = new Bone
                    {
                        Parent = plan.parents[b],
                        Rest = new BonePose { Position = Conversions.ToUnity(plan.calibration[b].Position), Rotation = Conversions.ToUnity(plan.calibration[b].Rotation), Scale = 1f },
                        NameHash = (uint)(b + 1)
                    };
                rigBlob = builder.CreateBlobAssetReference<RigBlob>(Allocator.Persistent);
            }
            var entity = manager.CreateEntity(typeof(Rig), typeof(SharedTimeline), typeof(SharedClock), typeof(SharedRig), typeof(SharedRetarget));
            manager.SetComponentData(entity, new Rig { Value = rigBlob });
            manager.SetComponentData(entity, new SharedRig { NameHash = 1 });
            manager.SetComponentData(entity, new SharedRetarget { Value = table });
            manager.SetComponentData(entity, new SharedClock { Transition = 0.2f });
            manager.AddBuffer<SharedClipAsset>(entity).Add(new SharedClipAsset { Value = placeholder });
            manager.AddBuffer<SharedContribution>(entity).Add(new SharedContribution { Clip = 0, Weight = 0f, Mask = ushort.MaxValue, Speed = 1f });
            manager.AddBuffer<BoneEntity>(entity);
            for (var b = 0; b < count; b++)
            {
                var bone = manager.CreateEntity(typeof(LocalTransform));
                manager.SetComponentData(bone, LocalTransform.FromPositionRotationScale(Conversions.ToUnity(plan.calibration[b].Position), Conversions.ToUnity(plan.calibration[b].Rotation), 1f));
                manager.GetBuffer<BoneEntity>(entity).Add(new BoneEntity { Value = bone });
            }
            return entity;
        }

        public static RigidPose EntityMotion(FamilyRigPlan plan, in RigidPose heading)
        {
            var ratio = plan.binding.HeightRatio;
            var pivot = plan.binding.TargetHipReference - plan.binding.SourceHipReference * ratio;
            var rotation = Quaternion.Normalize(heading.Rotation);
            return new RigidPose(heading.Position * ratio + pivot - Quaternion.Rotate(rotation, pivot), rotation);
        }

        static bool Compare(FamilyRigPlan plan, AnimationClip clip, GameObject copy, World world, Entity entity, SharedFamilySystem system, SharedClip loaded,
            RigidPose start, int frames, int samples, ClipRuntimeResult result, out string error)
        {
            error = string.Empty;
            var count = plan.bones.Length;
            var top = copy.transform;
            top.localPosition = Vector3.zero;
            top.localRotation = UnityEngine.Quaternion.identity;
            top.localScale = Vector3.one;
            var all = copy.GetComponentsInChildren<Transform>(true);
            var restPositions = new Vector3[all.Length];
            var restRotations = new UnityEngine.Quaternion[all.Length];
            for (var i = 0; i < all.Length; i++)
            {
                restPositions[i] = all[i].localPosition;
                restRotations[i] = all[i].localRotation;
            }
            var mapped = new Transform[count];
            mapped[0] = top;
            for (var b = 1; b < count; b++)
                mapped[b] = top.Find(plan.paths[b]);
            var animator = copy.GetComponentInChildren<Animator>(true);
            var driven = animator != null ? animator.gameObject : copy;
            var manager = world.EntityManager;
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            var canonicalFrame = new Pose[FamilyClips.CanonicalBones];
            var local = new Pose[count];
            var model = new RigidPose[count];
            var decoderModel = new RigidPose[count];
            var placed = new RigidPose[1];
            var elapsed = 0.0;
            var duration = loaded.Duration;
            for (var k = 0; k < 2 * samples; k++)
            {
                var onFrame = k < samples;
                var index = onFrame ? k : k - samples;
                var frame = samples == 1 ? 0 : (int)Math.Round(index * (frames - 1) / (double)(samples - 1));
                var seconds = onFrame ? duration * frame / (frames - 1) : duration * (index + 0.37) / samples;
                if (loaded.Loop && seconds >= duration)
                    seconds = duration * (1.0 - 1e-9);
                elapsed += 1.0;
                world.SetTime(new TimeData(elapsed, 0f));
                var clock = manager.GetComponentData<SharedClock>(entity);
                SharedFamilyControl.Seek(ref clock, seconds);
                manager.SetComponentData(entity, clock);
                system.Update();
                var bones = manager.GetBuffer<BoneEntity>(entity);
                for (var b = 0; b < count; b++)
                {
                    var transform = manager.GetComponentData<LocalTransform>(bones[b].Value);
                    local[b] = new Pose { Position = Conversions.ToMotion(transform.Position), Rotation = Quaternion.Normalize(Conversions.ToMotion(transform.Rotation)), Scale = Float3.One };
                }
                if (RigidMath.Model(plan.parents, local, model) != AnimationError.None)
                {
                    error = "runtime pose failed to compose at " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s.";
                    return false;
                }
                if (!FamilyClipSweep.RootAt(loaded, seconds, out var rootPose) || !RigidMath.TryCompose(start, rootPose, out var heading))
                {
                    error = "root invalid at " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s.";
                    return false;
                }
                var motion = EntityMotion(plan, heading);
                for (var b = 0; b < count; b++)
                    if (RigidMath.TryCompose(motion, model[b], out var moved))
                        model[b] = moved;
                if (loaded.Sample(seconds, reference, canonicalFrame) != AnimationError.None)
                {
                    error = "decode failed.";
                    return false;
                }
                placed[0] = heading;
                if (!FamilyClips.TryApply(plan, canonicalFrame, placed, out var applied, out error))
                    return false;
                if (RigidMath.Model(plan.parents, applied, decoderModel) != AnimationError.None)
                {
                    error = "decoder pose failed to compose.";
                    return false;
                }
                for (var i = 0; i < all.Length; i++)
                {
                    all[i].localPosition = restPositions[i];
                    all[i].localRotation = restRotations[i];
                }
                clip.SampleAnimation(driven, (float)Math.Min(clip.length, seconds));
                for (var b = 1; b < count; b++)
                {
                    var c = plan.canonicalOfBone[b];
                    if (c <= 0)
                        continue;
                    var rotation = mapped[b].rotation;
                    var truth = Quaternion.Normalize(new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w));
                    var decoder = FamilyClipSweep.Degrees(decoderModel[b].Rotation, model[b].Rotation);
                    result.DecoderDegrees = Math.Max(result.DecoderDegrees, float.IsFinite(decoder) ? decoder : float.PositiveInfinity);
                    if (!onFrame)
                        continue;
                    var degrees = FamilyClipSweep.Degrees(truth, model[b].Rotation);
                    if (!float.IsFinite(degrees))
                        degrees = float.PositiveInfinity;
                    if (degrees > result.TruthDegrees)
                    {
                        result.TruthDegrees = degrees;
                        result.Worst = SharedFamilyBake.CanonicalNames[c] + "@" + seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
                    }
                }
                foreach (var c in TrackedPositions)
                {
                    var b = plan.boneOfCanonical[c];
                    if (b < 0)
                        continue;
                    var p = mapped[b].position;
                    var truth = new Float3(p.x, p.y, p.z);
                    result.DecoderCentimetres = Math.Max(result.DecoderCentimetres, SharedFamilyBake.Distance(decoderModel[b].Position, model[b].Position) * 100f);
                    if (onFrame)
                        result.TruthCentimetres = Math.Max(result.TruthCentimetres, SharedFamilyBake.Distance(truth, model[b].Position) * 100f);
                }
                result.Samples++;
            }
            return true;
        }
    }
}
