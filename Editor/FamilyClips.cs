using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using SA.Animation;
using SA.Animation.Content;
using SA.Motion.Numerics;
using Pose = SA.Motion.Numerics.Pose;
using Quaternion = SA.Motion.Numerics.Quaternion;

namespace SA.Editor
{
    public sealed class FamilyRigPlan
    {
        internal GameObject character;
        internal Transform[] bones = Array.Empty<Transform>();
        internal string[] paths = Array.Empty<string>();
        internal int[] parents = Array.Empty<int>();
        internal Pose[] calibration = Array.Empty<Pose>();
        internal Pose[] rest = Array.Empty<Pose>();
        internal int[] canonicalOfBone = Array.Empty<int>();
        internal int[] boneOfCanonical = Array.Empty<int>();
        internal float[] calibrationDegrees = Array.Empty<float>();
        internal readonly List<string> warnings = new List<string>();
        internal int hips;
        internal float heightRatio;
        internal bool mappedFromAvatar;
        internal bool calibratedFromAvatar;
        internal AssetId family;
        internal AssetId target;
        internal RetargetRow[] rows = Array.Empty<RetargetRow>();
        internal RetargetBinding binding;
        internal int[] canonicalParents = Array.Empty<int>();
        internal Pose[] canonicalReference = Array.Empty<Pose>();

        internal FamilyRigPlan()
        {
        }

        public GameObject Character => character;
        public int BoneCount => bones.Length;
        public IReadOnlyList<Transform> Bones => bones;
        public IReadOnlyList<string> Paths => paths;
        public IReadOnlyList<int> Parents => parents;
        public IReadOnlyList<Pose> Calibration => calibration;
        public IReadOnlyList<Pose> Rest => rest;
        public IReadOnlyList<int> CanonicalOfBone => canonicalOfBone;
        public IReadOnlyList<int> BoneOfCanonical => boneOfCanonical;
        public IReadOnlyList<float> CalibrationDegrees => calibrationDegrees;
        public IReadOnlyList<string> Warnings => warnings;
        public int Hips => hips;
        public float HeightRatio => heightRatio;
        public bool MappedFromAvatar => mappedFromAvatar;
        public bool CalibratedFromAvatar => calibratedFromAvatar;
        public AssetId Family => family;
        public AssetId Target => target;
    }

    public static class FamilyClips
    {
        public const int CanonicalBones = SharedFamilyBake.CanonicalBones;
        public const float CalibrationWarningDegrees = 5f;
        public const float DefaultSampleRate = 30f;

        const float ScaleTolerance = 1e-4f;
        const int LeftUpperLeg = 48;
        const int RightUpperLeg = 52;

        static readonly PoseMarker[] FootMarkers =
        {
            new PoseMarker(50, new Float3(0f, -0.1f, 0.08f)),
            new PoseMarker(54, new Float3(0f, -0.1f, 0.08f))
        };

        static Dictionary<string, int> conventions;

        public static bool TryPlan(GameObject character, out FamilyRigPlan plan, out string error)
        {
            plan = null;
            error = string.Empty;
            if (character == null)
            {
                error = "no character given.";
                return false;
            }
            if (!SharedFamilyBake.CheckFamilyId(out error))
                return false;
            var result = new FamilyRigPlan { character = character };
            result.canonicalParents = new int[CanonicalBones];
            result.canonicalReference = new Pose[CanonicalBones];
            if (CanonicalHumanoid.WriteReference(result.canonicalParents, result.canonicalReference) != AnimationError.None)
            {
                error = "canonical reference failed to build.";
                return false;
            }
            SharedFamilyBake.FamilyId(out var fa, out var fb, out var fc, out var fd);
            result.family = new AssetId(fa, fb, fc, fd);
            var candidates = new List<Transform>();
            Collect(character.transform, candidates);
            var animator = character.GetComponentInChildren<Animator>(true);
            var human = animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman;
            var roles = human ? AvatarRoles(animator, candidates) : NameRoles(candidates);
            if (roles.Length == 0)
            {
                error = "bone assignment failed for '" + character.name + "'.";
                return false;
            }
            SharedFamilyBaker.DropTopologyViolations(candidates, roles);
            if (!Missing(roles, out var missing))
            {
                error = "required bones have no match on '" + character.name + "' (" + missing + ").";
                return false;
            }
            result.mappedFromAvatar = human;
            if (!Prune(candidates, roles, out var skeleton, out error))
                return false;
            var count = skeleton.Count;
            result.bones = skeleton.ToArray();
            result.parents = new int[count];
            result.paths = new string[count];
            result.canonicalOfBone = new int[count];
            for (var b = 0; b < count; b++)
            {
                result.parents[b] = b == 0 ? -1 : skeleton.IndexOf(skeleton[b].parent);
                result.paths[b] = b == 0 ? string.Empty : AnimationUtility.CalculateTransformPath(skeleton[b], character.transform);
                result.canonicalOfBone[b] = roles[candidates.IndexOf(skeleton[b])];
            }
            result.canonicalOfBone[0] = 0;
            result.boneOfCanonical = new int[CanonicalBones];
            for (var c = 0; c < CanonicalBones; c++)
                result.boneOfCanonical[c] = -1;
            for (var b = 0; b < count; b++)
                if (result.canonicalOfBone[b] >= 0)
                    result.boneOfCanonical[result.canonicalOfBone[b]] = b;
            result.hips = result.boneOfCanonical[1];
            if (!Calibrate(result, human ? animator.avatar : null, out error))
                return false;
            var identityNames = new string[count];
            for (var b = 0; b < count; b++)
                identityNames[b] = result.paths[b] + "#" + skeleton[b].name;
            if (ContentIdentity.Skeleton("SA.Target." + character.name, 1, identityNames, result.parents, result.calibration, out result.target) != AnimationError.None)
            {
                error = "target identity failed to compute; check the skeleton for duplicate paths.";
                return false;
            }
            var model = new RigidPose[count];
            if (RigidMath.Model(result.parents, result.calibration, model) != AnimationError.None)
            {
                error = "calibration model failed to compose.";
                return false;
            }
            result.heightRatio = model[result.hips].Position.Y;
            if (!float.IsFinite(result.heightRatio) || result.heightRatio <= 0f)
            {
                error = "hips height " + result.heightRatio.ToString(CultureInfo.InvariantCulture) + " is not positive; the character must stand on Y = 0 with +Y up.";
                return false;
            }
            var scratch = new RigidPose[CanonicalBones + count];
            result.rows = new RetargetRow[count];
            var compile = RetargetTable.Compile(result.family, result.target, result.canonicalParents, result.canonicalReference,
                result.parents, result.calibration, result.canonicalOfBone, 1, result.hips, result.heightRatio, scratch, result.rows, out result.binding);
            if (compile != AnimationError.None)
            {
                error = "retarget table failed to compile (" + compile + ").";
                return false;
            }
            plan = result;
            return true;
        }

        public static bool TrySample(FamilyRigPlan plan, AnimationClip clip, float hz, out Pose[] canonicalSamples, out RigidPose[] planarRoot,
            out int frames, out string error) =>
            TrySample(plan, clip, hz, out canonicalSamples, out planarRoot, out _, out frames, out error);

        public static bool TrySample(FamilyRigPlan plan, AnimationClip clip, float hz, out Pose[] canonicalSamples, out RigidPose[] planarRoot,
            out RigidPose start, out int frames, out string error)
        {
            canonicalSamples = Array.Empty<Pose>();
            planarRoot = Array.Empty<RigidPose>();
            start = RigidPose.Identity;
            if (!TrySampleBones(plan, clip, hz, out var locals, out frames, out error))
                return false;
            return TryCanonical(plan, locals, frames, out canonicalSamples, out planarRoot, out start, out error);
        }

        public static bool TryEncode(FamilyRigPlan plan, Pose[] canonicalSamples, RigidPose[] planarRoot, float hz, out byte[] saclip, out string error) =>
            TryEncode(plan, canonicalSamples, planarRoot, hz, false, out saclip, out error);

        public static bool TryEncode(FamilyRigPlan plan, Pose[] canonicalSamples, RigidPose[] planarRoot, float hz, bool lossless, out byte[] saclip, out string error)
        {
            saclip = Array.Empty<byte>();
            if (!Valid(plan, out error))
                return false;
            if (!(hz > 0f) || !float.IsFinite(hz))
            {
                error = "sample rate must be positive.";
                return false;
            }
            if (canonicalSamples == null || planarRoot == null || planarRoot.Length < 2 || canonicalSamples.Length != planarRoot.Length * CanonicalBones)
            {
                error = "expected at least two frames of " + CanonicalBones + " canonical bones with one planar root each.";
                return false;
            }
            var frames = planarRoot.Length;
            return SharedFamilyBake.EncodeCanonicalSamples(canonicalSamples, frames, (frames - 1) / (double)hz, false, RootPolicy.Planar, planarRoot,
                FootMarkers, !lossless, out saclip, out error);
        }

        public static bool TryEncode(FamilyRigPlan plan, Pose[] canonicalSamples, RigidPose[] planarRoot, double duration, bool loop, bool lossless,
            out byte[] saclip, out string error)
        {
            saclip = Array.Empty<byte>();
            if (!Valid(plan, out error))
                return false;
            if (!(duration > 0.0) || double.IsInfinity(duration))
            {
                error = "duration must be positive.";
                return false;
            }
            if (canonicalSamples == null || planarRoot == null || planarRoot.Length < 2 || canonicalSamples.Length != planarRoot.Length * CanonicalBones)
            {
                error = "expected at least two frames of " + CanonicalBones + " canonical bones with one planar root each.";
                return false;
            }
            return SharedFamilyBake.EncodeCanonicalSamples(canonicalSamples, planarRoot.Length, duration, loop, RootPolicy.Planar, planarRoot,
                FootMarkers, !lossless, out saclip, out error);
        }

        public static bool TryConvert(FamilyRigPlan plan, AnimationClip clip, float hz, bool lossless, out byte[] saclip, out string error)
        {
            saclip = Array.Empty<byte>();
            if (!TrySample(plan, clip, hz, out var canonical, out var root, out _, out error))
                return false;
            return TryEncode(plan, canonical, root, EncodedDuration(clip, hz), clip.isLooping, lossless, out saclip, out error);
        }

        public static int SampleCount(AnimationClip clip, float hz)
        {
            if (clip == null || !(hz > 0f) || !float.IsFinite(hz) || !(clip.length > 0f) || !float.IsFinite(clip.length))
                return 2;
            return Math.Max(2, (int)Math.Round(clip.length * (double)hz) + 1);
        }

        public static double SampleSeconds(AnimationClip clip, int frames, int frame)
        {
            if (clip == null || frames < 2 || !(clip.length > 0f) || !float.IsFinite(clip.length))
                return 0.0;
            return clip.length * (double)Math.Max(0, Math.Min(frames - 1, frame)) / (frames - 1);
        }

        public static double EncodedDuration(AnimationClip clip, float hz)
        {
            if (clip != null && clip.length > 0f && float.IsFinite(clip.length))
                return clip.length;
            return hz > 0f && float.IsFinite(hz) ? 1.0 / hz : 1.0 / DefaultSampleRate;
        }

        public static bool TryCapture(FamilyRigPlan plan, out Pose[] canonical, out string error)
        {
            canonical = Array.Empty<Pose>();
            if (!Valid(plan, out error))
                return false;
            var count = plan.bones.Length;
            var locals = new Pose[count];
            for (var b = 0; b < count; b++)
            {
                if (plan.bones[b] == null)
                {
                    error = "bone " + plan.paths[b] + " no longer exists; plan the character again.";
                    return false;
                }
                locals[b] = b == 0 ? Pose.Identity : Read(plan.bones[b]);
            }
            return TryCanonical(plan, locals, 1, out canonical, out _, out _, out error);
        }

        public static bool TryApply(FamilyRigPlan plan, Pose[] canonical, RigidPose[] planarRoot, out Pose[] targetLocals, out string error)
        {
            targetLocals = Array.Empty<Pose>();
            if (!Valid(plan, out error))
                return false;
            if (canonical == null || canonical.Length == 0 || canonical.Length % CanonicalBones != 0)
            {
                error = "canonical poses must hold whole frames of " + CanonicalBones + " bones.";
                return false;
            }
            var frames = canonical.Length / CanonicalBones;
            if (planarRoot != null && planarRoot.Length != 0 && planarRoot.Length != frames)
            {
                error = "expected " + frames + " roots, got " + planarRoot.Length + ".";
                return false;
            }
            var count = plan.bones.Length;
            var output = new Pose[frames * count];
            var frame = new Pose[CanonicalBones];
            var canonicalModel = new RigidPose[CanonicalBones];
            var targetModel = new RigidPose[count];
            var targetFrame = new Pose[count];
            for (var f = 0; f < frames; f++)
            {
                Array.Copy(canonical, f * CanonicalBones, frame, 0, CanonicalBones);
                if (planarRoot != null && planarRoot.Length != 0)
                {
                    var hips = frame[1];
                    if (!RigidMath.TryCompose(planarRoot[f], new RigidPose(hips.Position, Quaternion.Normalize(hips.Rotation)), out var placed))
                    {
                        error = "root of frame " + f + " is invalid.";
                        return false;
                    }
                    frame[1] = new Pose { Position = placed.Position, Rotation = placed.Rotation, Scale = Float3.One };
                }
                var apply = RetargetTable.Apply(plan.family, plan.binding, plan.canonicalParents, frame, plan.rows, canonicalModel, targetModel, targetFrame);
                if (apply != AnimationError.None)
                {
                    error = "retarget apply failed at frame " + f + " (" + apply + ").";
                    return false;
                }
                CanonicalTranslation.Apply(plan.rows, plan.binding, plan.canonicalParents, frame, plan.canonicalReference, canonicalModel, targetModel, targetFrame);
                Array.Copy(targetFrame, 0, output, f * count, count);
            }
            targetLocals = output;
            return true;
        }

        internal static bool TrySampleBones(FamilyRigPlan plan, AnimationClip clip, float hz, out Pose[] locals, out int frames, out string error)
        {
            locals = Array.Empty<Pose>();
            frames = 0;
            if (!Valid(plan, out error))
                return false;
            if (clip == null)
            {
                error = "no clip given.";
                return false;
            }
            if (!(hz > 0f) || !float.IsFinite(hz))
            {
                error = "sample rate must be positive.";
                return false;
            }
            if (plan.character == null)
            {
                error = "the planned character no longer exists.";
                return false;
            }
            frames = SampleCount(clip, hz);
            var count = plan.bones.Length;
            var copy = UnityEngine.Object.Instantiate(plan.character);
            copy.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var top = copy.transform;
                top.localPosition = Vector3.zero;
                top.localRotation = UnityEngine.Quaternion.identity;
                top.localScale = Vector3.one;
                var all = copy.GetComponentsInChildren<Transform>(true);
                var restPositions = new Vector3[all.Length];
                var restRotations = new UnityEngine.Quaternion[all.Length];
                var restScales = new Vector3[all.Length];
                for (var i = 0; i < all.Length; i++)
                {
                    restPositions[i] = all[i].localPosition;
                    restRotations[i] = all[i].localRotation;
                    restScales[i] = all[i].localScale;
                }
                var mapped = new Transform[count];
                mapped[0] = top;
                for (var b = 1; b < count; b++)
                {
                    mapped[b] = top.Find(plan.paths[b]);
                    if (mapped[b] == null)
                    {
                        error = "bone '" + plan.paths[b] + "' is missing on the sampling copy.";
                        return false;
                    }
                }
                var animator = copy.GetComponentInChildren<Animator>(true);
                var driven = animator != null ? animator.gameObject : copy;
                locals = new Pose[frames * count];
                var previous = new Quaternion[count];
                for (var f = 0; f < frames; f++)
                {
                    for (var i = 0; i < all.Length; i++)
                    {
                        all[i].localPosition = restPositions[i];
                        all[i].localRotation = restRotations[i];
                        all[i].localScale = restScales[i];
                    }
                    clip.SampleAnimation(driven, (float)SampleSeconds(clip, frames, f));
                    var moved = Read(top);
                    for (var b = 0; b < count; b++)
                    {
                        var local = b == 0 ? Pose.Identity : Read(mapped[b]);
                        if (b > 0 && plan.parents[b] == 0)
                        {
                            if (!RigidMath.TryCompose(new RigidPose(moved.Position, moved.Rotation), new RigidPose(local.Position, local.Rotation), out var lifted))
                            {
                                error = "frame " + f + " has an invalid character root.";
                                return false;
                            }
                            local = new Pose { Position = lifted.Position, Rotation = Quaternion.Normalize(lifted.Rotation), Scale = Float3.One };
                        }
                        if (!RigidMath.UnitPose(local))
                        {
                            error = "bone '" + plan.paths[b] + "' is invalid at frame " + f + ".";
                            return false;
                        }
                        if (f > 0 && Quaternion.Dot(local.Rotation, previous[b]) < 0f)
                            local.Rotation = new Quaternion(-local.Rotation.X, -local.Rotation.Y, -local.Rotation.Z, -local.Rotation.W);
                        previous[b] = local.Rotation;
                        locals[f * count + b] = local;
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
            return true;
        }

        internal static bool TryCanonical(FamilyRigPlan plan, Pose[] locals, int frames, out Pose[] canonical, out RigidPose[] planarRoot, out RigidPose start, out string error)
        {
            error = string.Empty;
            canonical = Array.Empty<Pose>();
            planarRoot = Array.Empty<RigidPose>();
            start = RigidPose.Identity;
            var count = plan.bones.Length;
            var samples = new Pose[frames * CanonicalBones];
            var root = new RigidPose[frames];
            var frameLocal = new Pose[count];
            var frameModel = new RigidPose[count];
            var canonicalModel = new RigidPose[CanonicalBones];
            var legs = plan.canonicalReference[RightUpperLeg].Position - plan.canonicalReference[LeftUpperLeg].Position;
            var firstYaw = 0f;
            var yaw = 0f;
            var first = RigidPose.Identity;
            for (var f = 0; f < frames; f++)
            {
                Array.Copy(locals, f * count, frameLocal, 0, count);
                frameLocal[0] = Pose.Identity;
                var offset = f * CanonicalBones;
                var mapped = SharedFamilyBake.TargetToCanonical(plan.parents, frameLocal, frameModel, plan.boneOfCanonical, plan.rows, plan.binding,
                    plan.hips, plan.heightRatio, plan.canonicalParents, plan.canonicalReference, canonicalModel, samples, offset, out var badBone);
                if (mapped != AnimationError.None)
                {
                    error = badBone < 0 ? "frame " + f + " failed to compose." : "canonical bone " + SharedFamilyBake.CanonicalNames[badBone] + " is not a unit pose at frame " + f + ".";
                    return false;
                }
                var hips = canonicalModel[1];
                var right = Quaternion.Rotate(Quaternion.Normalize(hips.Rotation), legs);
                var forward = Float3.Cross(right, new Float3(0f, 1f, 0f));
                if (forward.IsFinite && forward.X * forward.X + forward.Z * forward.Z >= 1e-10f)
                    yaw = (float)Math.Atan2(forward.X, forward.Z);
                var turn = Yaw(yaw);
                var heading = new RigidPose(new Float3(hips.Position.X, 0f, hips.Position.Z), turn);
                var inverse = Quaternion.InverseUnit(turn);
                samples[offset + 1] = new Pose
                {
                    Position = Quaternion.Rotate(inverse, hips.Position - heading.Position),
                    Rotation = Quaternion.Normalize(Quaternion.Multiply(inverse, Quaternion.Normalize(hips.Rotation))),
                    Scale = Float3.One
                };
                if (f == 0)
                {
                    first = heading;
                    firstYaw = yaw;
                    root[f] = RigidPose.Identity;
                    continue;
                }
                var travel = Quaternion.Rotate(Yaw(-firstYaw), heading.Position - first.Position);
                root[f] = new RigidPose(new Float3(travel.X, 0f, travel.Z), Yaw(yaw - firstYaw));
            }
            canonical = samples;
            planarRoot = root;
            start = first;
            return true;
        }

        static Quaternion Yaw(float radians) => Quaternion.AxisAngle(new Float3(0f, 1f, 0f), radians);

        static bool Valid(FamilyRigPlan plan, out string error)
        {
            error = string.Empty;
            if (plan == null || plan.bones.Length == 0 || plan.rows.Length != plan.bones.Length)
            {
                error = "no valid plan; call TryPlan first.";
                return false;
            }
            return true;
        }

        static Pose Read(Transform transform)
        {
            var rotation = transform.localRotation;
            var position = transform.localPosition;
            return new Pose
            {
                Position = new Float3(position.x, position.y, position.z),
                Rotation = Quaternion.Normalize(new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w)),
                Scale = Float3.One
            };
        }

        static void Collect(Transform parent, List<Transform> into)
        {
            into.Add(parent);
            var children = new List<Transform>();
            for (var i = 0; i < parent.childCount; i++)
                children.Add(parent.GetChild(i));
            children.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            foreach (var child in children)
                Collect(child, into);
        }

        static int[] AvatarRoles(Animator animator, List<Transform> candidates)
        {
            var roles = Unassigned(candidates.Count);
            roles[0] = 0;
            var described = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var bone in animator.avatar.humanDescription.human)
                if (!string.IsNullOrEmpty(bone.humanName) && !described.ContainsKey(bone.humanName))
                    described.Add(bone.humanName, bone.boneName);
            var names = SharedFamilyBake.CanonicalNames;
            for (var c = 1; c < CanonicalBones; c++)
            {
                if (!Enum.TryParse<HumanBodyBones>(names[c], out var human))
                    continue;
                var index = -1;
                var bone = animator.GetBoneTransform(human);
                if (bone != null)
                    index = candidates.IndexOf(bone);
                if (index < 0 && described.TryGetValue(HumanTrait.BoneName[(int)human], out var boneName))
                    index = candidates.FindIndex(t => t.name == boneName);
                if (index > 0 && roles[index] < 0)
                    roles[index] = c;
            }
            return roles;
        }

        static int[] NameRoles(List<Transform> candidates)
        {
            var count = candidates.Count;
            var names = SharedFamilyBake.CanonicalNames;
            var scores = new int[CanonicalBones * count];
            for (var b = 1; b < count; b++)
            {
                var convention = Convention(candidates[b].name);
                for (var r = 1; r < CanonicalBones; r++)
                    scores[r * count + b] = convention >= 0 ? (convention == r ? 100 : 0) : SharedFamilyBake.ScoreBoneName(names[r], candidates[b].name);
            }
            scores[0] = 100;
            if (MappingAssignment.Solve(scores, CanonicalBones, count, 15, out var assignment) != AnimationError.None || assignment == null)
                return Array.Empty<int>();
            var roles = Unassigned(count);
            for (var r = 0; r < CanonicalBones; r++)
            {
                var bone = assignment.BoneOfRole[r];
                if (bone < 0 || bone >= count || roles[bone] >= 0 || scores[r * count + bone] <= 0)
                    continue;
                roles[bone] = r;
            }
            roles[0] = 0;
            return roles;
        }

        static int Convention(string name)
        {
            if (conventions == null)
            {
                var table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var names = SharedFamilyBake.CanonicalNames;
                for (var c = 1; c < CanonicalBones; c++)
                    table[names[c]] = c;
                table["Spine1"] = 3;
                table["Spine2"] = 4;
                string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Pinky" };
                for (var side = 0; side < 2; side++)
                {
                    var prefix = side == 0 ? "Left" : "Right";
                    var arm = side == 0 ? 10 : 29;
                    var leg = side == 0 ? 48 : 52;
                    table[prefix + "Arm"] = arm + 1;
                    table[prefix + "ForeArm"] = arm + 2;
                    table[prefix + "UpLeg"] = leg;
                    table[prefix + "Leg"] = leg + 1;
                    table[prefix + "ToeBase"] = leg + 3;
                    for (var finger = 0; finger < fingers.Length; finger++)
                        for (var joint = 0; joint < 3; joint++)
                            table[prefix + "Hand" + fingers[finger] + (joint + 1).ToString(CultureInfo.InvariantCulture)] = arm + 4 + finger * 3 + joint;
                }
                conventions = table;
            }
            var cut = Math.Max(name.LastIndexOf(':'), name.LastIndexOf('|'));
            var bare = cut >= 0 ? name.Substring(cut + 1) : name;
            return conventions.TryGetValue(bare, out var role) ? role : -1;
        }

        static int[] Unassigned(int count)
        {
            var roles = new int[count];
            for (var i = 0; i < count; i++)
                roles[i] = -1;
            return roles;
        }

        static bool Missing(int[] roles, out string missing)
        {
            var names = SharedFamilyBake.CanonicalNames;
            var absent = new List<string>();
            foreach (var role in SharedFamilyBake.RequiredRoles)
                if (Array.IndexOf(roles, role) < 0)
                    absent.Add(names[role]);
            missing = string.Join(", ", absent);
            return absent.Count == 0;
        }

        static bool Prune(List<Transform> candidates, int[] roles, out List<Transform> skeleton, out string error)
        {
            error = string.Empty;
            skeleton = new List<Transform>();
            var count = candidates.Count;
            var parents = new int[count];
            for (var i = 0; i < count; i++)
                parents[i] = i == 0 ? -1 : candidates.IndexOf(candidates[i].parent);
            var carries = new bool[count];
            for (var i = 1; i < count; i++)
                if (roles[i] > 0)
                    for (var t = i; t > 0 && !carries[t]; t = parents[t])
                        carries[t] = true;
            var keep = new bool[count];
            var below = new bool[count];
            keep[0] = true;
            for (var i = 1; i < count; i++)
            {
                var parent = parents[i];
                below[i] = parent > 0 && keep[parent] && (roles[parent] > 0 || below[parent]);
                if (!carries[i] && !below[i])
                    continue;
                if (!keep[parent])
                    continue;
                var scale = candidates[i].localScale;
                var unit = Math.Abs(scale.x - 1f) <= ScaleTolerance && Math.Abs(scale.y - 1f) <= ScaleTolerance && Math.Abs(scale.z - 1f) <= ScaleTolerance;
                if (!unit && carries[i])
                {
                    error = "bone '" + candidates[i].name + "' has a non-unit scale; freeze scales on the model.";
                    return false;
                }
                keep[i] = unit;
            }
            for (var i = 0; i < count; i++)
                if (keep[i])
                    skeleton.Add(candidates[i]);
            return true;
        }

        static bool Calibrate(FamilyRigPlan plan, Avatar avatar, out string error)
        {
            error = string.Empty;
            var count = plan.bones.Length;
            plan.rest = new Pose[count];
            plan.calibration = new Pose[count];
            for (var b = 0; b < count; b++)
                plan.rest[b] = b == 0 ? Pose.Identity : Read(plan.bones[b]);
            Array.Copy(plan.rest, plan.calibration, count);
            if (avatar != null)
            {
                var description = avatar.humanDescription;
                var skeleton = new Dictionary<string, SkeletonBone>(StringComparer.Ordinal);
                foreach (var bone in description.skeleton)
                    if (!string.IsNullOrEmpty(bone.name) && !skeleton.ContainsKey(bone.name))
                        skeleton.Add(bone.name, bone);
                var human = new HashSet<string>(StringComparer.Ordinal);
                foreach (var bone in description.human)
                    if (!string.IsNullOrEmpty(bone.boneName))
                        human.Add(bone.boneName);
                var found = 0;
                for (var b = 1; b < count; b++)
                {
                    if (!skeleton.TryGetValue(plan.bones[b].name, out var described))
                        continue;
                    var rotation = described.rotation;
                    var q = Quaternion.Normalize(new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w));
                    if (!RigidMath.UnitRotation(q))
                        continue;
                    plan.calibration[b].Rotation = q;
                    var position = described.position;
                    if (human.Contains(plan.bones[b].name) && float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z))
                        plan.calibration[b].Position = new Float3(position.x, position.y, position.z);
                    found++;
                }
                plan.calibratedFromAvatar = found > 0;
            }
            plan.calibrationDegrees = new float[count];
            var restModel = new RigidPose[count];
            var calibrationModel = new RigidPose[count];
            if (RigidMath.Model(plan.parents, plan.rest, restModel) != AnimationError.None
                || RigidMath.Model(plan.parents, plan.calibration, calibrationModel) != AnimationError.None)
            {
                error = "rest or calibration pose failed to compose.";
                return false;
            }
            var names = SharedFamilyBake.CanonicalNames;
            for (var b = 1; b < count; b++)
            {
                if (plan.canonicalOfBone[b] <= 0)
                    continue;
                var degrees = SharedFamilyBake.AngleBetween(restModel[b].Rotation, calibrationModel[b].Rotation) * (180f / (float)Math.PI);
                plan.calibrationDegrees[b] = degrees;
                if (degrees > CalibrationWarningDegrees)
                    plan.warnings.Add("'" + plan.bones[b].name + "' (" + names[plan.canonicalOfBone[b]] + ") rest differs from the avatar T-pose by "
                        + degrees.ToString("0.0", CultureInfo.InvariantCulture) + " deg.");
            }
            if (plan.warnings.Count > 0)
                Debug.LogWarning("SA: '" + plan.character.name + "' rest pose is not the avatar T-pose; calibrating from the avatar.\n" + string.Join("\n", plan.warnings), plan.character);
            return true;
        }
    }
}
