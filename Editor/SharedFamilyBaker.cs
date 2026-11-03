using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using SA.Animation;
using SA.Animation.Content;
using SA.Motion.Numerics;
using SA.Motion.Unity;
using Pose = SA.Motion.Numerics.Pose;
using Quaternion = SA.Motion.Numerics.Quaternion;

namespace SA.Editor
{
    public static class SharedFamilyBake
    {
        public const string FamilyName = "Humanoid";
        public const uint FamilyVersion = 1;
        public const string PublishedFamilyId = "90cb280f14f3f02fbfe0beaeda925711d6886c2aac8e814ac04516f4946cd099";
        public const int CanonicalBones = 56;

        static readonly string[] names =
        {
            "Root", "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head", "Jaw", "LeftEye", "RightEye",
            "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "LeftThumbProximal", "LeftThumbIntermediate", "LeftThumbDistal",
            "LeftIndexProximal", "LeftIndexIntermediate", "LeftIndexDistal",
            "LeftMiddleProximal", "LeftMiddleIntermediate", "LeftMiddleDistal",
            "LeftRingProximal", "LeftRingIntermediate", "LeftRingDistal",
            "LeftLittleProximal", "LeftLittleIntermediate", "LeftLittleDistal",
            "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
            "RightThumbProximal", "RightThumbIntermediate", "RightThumbDistal",
            "RightIndexProximal", "RightIndexIntermediate", "RightIndexDistal",
            "RightMiddleProximal", "RightMiddleIntermediate", "RightMiddleDistal",
            "RightRingProximal", "RightRingIntermediate", "RightRingDistal",
            "RightLittleProximal", "RightLittleIntermediate", "RightLittleDistal",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
            "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes"
        };

        static readonly int[] requiredRoles = { 1, 2, 6, 11, 12, 13, 30, 31, 32, 48, 49, 50, 52, 53, 54 };

        public static string[] CanonicalNames => (string[])names.Clone();

        public static int[] RequiredRoles => (int[])requiredRoles.Clone();

        public static bool CheckFamilyId(out string error)
        {
            error = string.Empty;
            if (!FamilyId(out var a, out var b, out var c, out var d))
            {
                error = "family identity failed to compute.";
                return false;
            }
            var hex = ToHex(new AssetId(a, b, c, d));
            if (!string.Equals(hex, PublishedFamilyId, StringComparison.OrdinalIgnoreCase))
            {
                error = "family " + hex + " does not match published " + PublishedFamilyId + ".";
                return false;
            }
            return true;
        }

        public static string ToHex(AssetId id)
        {
            var bytes = new byte[32];
            BitConverter.GetBytes(id.A).CopyTo(bytes, 0);
            BitConverter.GetBytes(id.B).CopyTo(bytes, 8);
            BitConverter.GetBytes(id.C).CopyTo(bytes, 16);
            BitConverter.GetBytes(id.D).CopyTo(bytes, 24);
            var text = new System.Text.StringBuilder(64);
            foreach (var value in bytes)
                text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        public static Pose[] CanonicalReferencePoses()
        {
            var parents = new int[CanonicalBones];
            var reference = new Pose[CanonicalBones];
            if (CanonicalHumanoid.WriteReference(parents, reference) != AnimationError.None)
                return Array.Empty<Pose>();
            return reference;
        }

        public static int[] CanonicalParentsArray()
        {
            var parents = new int[CanonicalBones];
            var reference = new Pose[CanonicalBones];
            if (CanonicalHumanoid.WriteReference(parents, reference) != AnimationError.None)
                return Array.Empty<int>();
            return parents;
        }

        public static BlobAssetReference<SharedClipData> MakeClipBlob(byte[] bytes, double duration, int frames, bool loop)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var data = ref builder.ConstructRoot<SharedClipData>();
            if (FamilyId(out var fa, out var fb, out var fc, out var fd))
                data.Family = new AssetId(fa, fb, fc, fd);
            data.Artifact = ContentIdentity.Hash(bytes);
            data.Duration = duration;
            data.SampleCount = frames;
            data.BoneCount = CanonicalBones;
            data.Loop = (byte)(loop ? 1 : 0);
            var stored = builder.Allocate(ref data.Bytes, bytes.Length);
            for (var i = 0; i < bytes.Length; i++)
                stored[i] = bytes[i];
            var reference = builder.CreateBlobAssetReference<SharedClipData>(Allocator.Persistent);
            builder.Dispose();
            return reference;
        }

        public static BlobAssetReference<SharedRetargetData> MakeTableBlob(byte[] table, int[] targetParents, Pose[] calibration, out string error)
        {
            error = string.Empty;
            var empty = default(BlobAssetReference<SharedRetargetData>);
            var canonicalParents = new int[CanonicalBones];
            var canonicalReference = new Pose[CanonicalBones];
            if (CanonicalHumanoid.WriteReference(canonicalParents, canonicalReference) != AnimationError.None)
            {
                error = "canonical reference failed to build.";
                return empty;
            }
            if (!FamilyId(out var fa, out var fb, out var fc, out var fd))
            {
                error = "family identity failed to compute.";
                return empty;
            }
            var family = new AssetId(fa, fb, fc, fd);
            if (ContentIdentity.Skeleton("SA.Target", 1, UniqueNames(targetParents.Length), targetParents, calibration, out var target) != AnimationError.None)
            {
                error = "target identity failed to compute.";
                return empty;
            }
            if (RetargetFile.Load(table, family, target, canonicalParents, canonicalReference, out _, out _) != AnimationError.None)
            {
                error = "table does not match the given target.";
                return empty;
            }
            var builder = new BlobBuilder(Allocator.Temp);
            ref var data = ref builder.ConstructRoot<SharedRetargetData>();
            data.Family = family;
            data.Target = target;
            var stored = builder.Allocate(ref data.Table, table.Length);
            for (var i = 0; i < table.Length; i++)
                stored[i] = table[i];
            var reference = builder.CreateBlobAssetReference<SharedRetargetData>(Allocator.Persistent);
            builder.Dispose();
            return reference;
        }

        public static bool FamilyId(out ulong a, out ulong b, out ulong c, out ulong d)
        {
            a = b = c = d = 0;
            var parents = new int[CanonicalBones];
            var reference = new Pose[CanonicalBones];
            if (CanonicalHumanoid.WriteReference(parents, reference) != AnimationError.None)
                return false;
            if (ContentIdentity.Skeleton(FamilyName, FamilyVersion, names, parents, reference, out var family) != AnimationError.None)
                return false;
            a = family.A;
            b = family.B;
            c = family.C;
            d = family.D;
            return true;
        }

        public static int ScoreBoneName(string canonical, string bone)
        {
            if (string.Equals(canonical, bone, StringComparison.OrdinalIgnoreCase))
                return 100;
            var canonicalSide = SideOf(canonical);
            var boneSide = SideOf(bone);
            if (canonicalSide != 0 && boneSide != 0 && canonicalSide != boneSide)
                return 0;
            var right = new HashSet<string>(CoreTokens(bone), StringComparer.OrdinalIgnoreCase);
            var shared = 0;
            foreach (var token in CoreTokens(canonical, true))
                if (right.Contains(token))
                    shared++;
            if (shared == 0)
                return 0;
            var score = shared * 10;
            if (canonicalSide != 0 && canonicalSide == boneSide)
                score += 10;
            if (bone.IndexOf(canonical, StringComparison.OrdinalIgnoreCase) >= 0 || canonical.IndexOf(bone, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 5;
            return Math.Min(score, 90);
        }

        static int SideOf(string name)
        {
            var lower = name.ToLowerInvariant();
            var left = lower.Contains("left") || lower.EndsWith("_l") || lower.EndsWith("-l") || lower.EndsWith(".l") || lower.EndsWith(" l");
            var right = lower.Contains("right") || lower.EndsWith("_r") || lower.EndsWith("-r") || lower.EndsWith(".r") || lower.EndsWith(" r") || HasNumberedMirrorSuffix(lower);
            if (left && !right)
                return 1;
            if (right && !left)
                return -1;
            return 0;
        }

        static bool HasNumberedMirrorSuffix(string lower)
        {
            if (!lower.EndsWith("_1"))
                return false;
            return lower.Length > 2 && char.IsDigit(lower[lower.Length - 3]);
        }
        static string StripNumberedMirrorSuffix(string text)
        {
            if (text != null && HasNumberedMirrorSuffix(text.ToLowerInvariant()))
                return text.Substring(0, text.Length - 2);
            return text;
        }

        static List<string> CoreTokens(string text, bool canonical = false)
        {
            text = StripNumberedMirrorSuffix(text);
            var flat = new System.Text.StringBuilder();
            foreach (var glyph in text)
                flat.Append(char.IsLetterOrDigit(glyph) ? char.ToLowerInvariant(glyph) : ' ');
            var joined = System.Text.RegularExpressions.Regex.Replace(flat.ToString(), " +", " ").Trim();
            if (joined == "spine 01")
                return new List<string> { "spine" };
            if (joined == "spine 02")
                return new List<string> { "chest" };
            if (joined == "spine 03")
                return new List<string> { "upper", "chest" };
            if (joined == "neck 01")
                return new List<string> { "neck" };
            var raw = Tokens(text);
            var finger = false;
            foreach (var token in raw)
            {
                var word = token.ToLowerInvariant();
                if (word == "thumb" || word == "index" || word == "middle" || word == "ring" || word == "pinky" || word == "little" || word == "toe" || word == "ball")
                    finger = true;
            }
            var cores = new List<string>();
            foreach (var token in raw)
            {
                var word = token.ToLowerInvariant();
                var mapped = canonical && word == "shoulder" ? "shoulder" : Alias(word, finger);
                if (string.IsNullOrEmpty(mapped))
                    continue;
                foreach (var piece in mapped.Split(' '))
                    cores.Add(piece);
            }
            return cores;
        }

        static string Alias(string word, bool finger)
        {
            switch (word)
            {
                case "upperarm": return "upper arm";
                case "lowerarm": return "lower arm";
                case "forearm": return "lower arm";
                case "upperleg": return "upper leg";
                case "lowerleg": return "lower leg";
                case "pelvis": case "hip": case "hips": return "hips";
                case "thigh": return "upper leg";
                case "calf": case "knee": case "shin": return "lower leg";
                case "ankle": case "foot": return "foot";
                case "ball": case "toe": case "toes": return "toes";
                case "clavicle": case "collar": case "collarbone": return "shoulder";
                case "shoulder": return "shoulder upper arm";
                case "elbow": return "elbow lower arm";
                case "wrist": case "hand": return "hand";
                case "neck": return "neck";
                case "head": case "skull": return "head";
                case "chest": return "chest";
                case "ribs": case "ribcage": return "spine";
                case "spine": return "spine";
                case "pinky": case "little": return "little";
                case "arm": return "arm";
                case "leg": return "leg";
                case "upper": return "upper";
                case "lower": return "lower";
                case "left": return "left";
                case "right": return "right";
                case "thumb": return "thumb";
                case "index": return "index";
                case "middle": return "middle";
                case "ring": return "ring";
                case "root": return "root";
            }
            if (word == "joint" || word == "finger")
                return "";
            if (word.Length > 0 && char.IsDigit(word[0]))
            {
                if (!finger)
                    return "";
                var trimmed = word.TrimStart('0');
                if (trimmed.Length == 0)
                    return "";
                var head = trimmed[0];
                if (head == '1' || head == 'a')
                    return "proximal";
                if (head == '2' || head == 'b')
                    return "intermediate";
                if (head == '3' || head == 'c')
                    return "distal";
                return "";
            }
            return word;
        }

        static List<string> Tokens(string text)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (var glyph in text)
            {
                if (char.IsLetterOrDigit(glyph))
                {
                    if (current.Length > 0 && char.IsUpper(glyph) && !char.IsUpper(current[current.Length - 1]))
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    current.Append(char.ToLowerInvariant(glyph));
                }
                else if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            if (current.Length > 0)
                tokens.Add(current.ToString());
            return tokens;
        }

        public static bool EncodeCanonicalSamples(Pose[] samples, int frames, double duration, bool loop, out byte[] bytes, out string error)
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;
            var parents = new int[CanonicalBones];
            var reference = new Pose[CanonicalBones];
            if (CanonicalHumanoid.WriteReference(parents, reference) != AnimationError.None)
            {
                error = "canonical reference failed to build.";
                return false;
            }
            if (!FamilyId(out var fa, out var fb, out var fc, out var fd))
            {
                error = "family identity failed to compute.";
                return false;
            }
            var family = new AssetId(fa, fb, fc, fd);
            var roots = new RigidPose[frames];
            for (var f = 0; f < frames; f++)
                roots[f] = RigidPose.Identity;
            var encode = SharedClipEncoder.Encode(family, parents, reference, samples, frames, duration, loop,
                RootPolicy.None, roots, Array.Empty<Pose>(), Array.Empty<float>(), Array.Empty<PoseMarker>(),
                true, CompressionLimits.Default, out bytes, out _);
            if (encode != AnimationError.None)
            {
                error = "encode failed (" + encode + ").";
                return false;
            }
            var verify = SharedClipReader.Load(bytes, family, reference, out var clip);
            if (verify != AnimationError.None || clip == null)
            {
                error = "encoded clip failed to reload (" + verify + ").";
                return false;
            }
            var probe = new Pose[CanonicalBones];
            var picks = new[] { 0, frames / 2, frames - 1 };
            foreach (var pick in picks)
            {
                var seconds = duration * pick / Math.Max(1, frames - 1);
                var read = clip.Sample(seconds, reference, probe);
                if (read != AnimationError.None)
                {
                    error = "verify sample failed (" + read + ").";
                    return false;
                }
                for (var bone = 0; bone < CanonicalBones; bone++)
                {
                    var expected = samples[pick * CanonicalBones + bone];
                    var angle = AngleBetween(expected.Rotation, probe[bone].Rotation);
                    var distance = Distance(expected.Position, probe[bone].Position);
                    if (!(angle <= 0.01f) || !(distance <= 0.01f))
                    {
                        error = "verify drift at frame " + pick + " bone " + bone + " (" + angle + " rad, " + distance + " m).";
                        return false;
                    }
                }
            }
            return true;
        }

        public static float AngleBetween(in Quaternion a, in Quaternion b)
        {
            var dot = Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b));
            dot = Math.Max(-1f, Math.Min(1f, dot));
            return 2f * (float)Math.Acos(Math.Abs(dot));
        }

        public static float Distance(in Float3 a, in Float3 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static bool CompileTableBytes(int[] targetParents, Pose[] calibration, int[] sourceMap,
            int sourceHips, int targetHips, float heightRatio, out byte[] table, out string error)
        {
            table = Array.Empty<byte>();
            error = string.Empty;
            var canonicalParents = new int[CanonicalBones];
            var canonicalReference = new Pose[CanonicalBones];
            if (CanonicalHumanoid.WriteReference(canonicalParents, canonicalReference) != AnimationError.None)
            {
                error = "canonical reference failed to build.";
                return false;
            }
            if (!FamilyId(out var fa, out var fb, out var fc, out var fd))
            {
                error = "family identity failed to compute.";
                return false;
            }
            var family = new AssetId(fa, fb, fc, fd);
            if (ContentIdentity.Skeleton("SA.Target", 1, UniqueNames(targetParents.Length), targetParents, calibration, out var target) != AnimationError.None)
            {
                error = "target identity failed to compute.";
                return false;
            }
            var scratch = new RigidPose[CanonicalBones + targetParents.Length];
            var rows = new RetargetRow[targetParents.Length];
            var compile = RetargetTable.Compile(family, target, canonicalParents, canonicalReference,
                targetParents, calibration, sourceMap, sourceHips, targetHips, heightRatio, scratch, rows, out var binding);
            if (compile != AnimationError.None)
            {
                error = "retarget compile failed (" + compile + ").";
                return false;
            }
            var encode = RetargetFile.Encode(binding, canonicalParents, canonicalReference, rows, out table);
            if (encode != AnimationError.None)
            {
                error = "table encode failed (" + encode + ").";
                return false;
            }
            var load = RetargetFile.Load(table, family, target, canonicalParents, canonicalReference, out var reloaded, out var reloadedRows);
            if (load != AnimationError.None)
            {
                error = "table reload failed (" + load + ").";
                return false;
            }
            if (!reloaded.Equals(binding) || reloadedRows.Length != rows.Length)
            {
                error = "table round-trip changed the binding.";
                return false;
            }
            for (var i = 0; i < rows.Length; i++)
            {
                if (reloadedRows[i].Parent != rows[i].Parent || reloadedRows[i].Source != rows[i].Source || reloadedRows[i].Mode != rows[i].Mode)
                {
                    error = "table round-trip changed row " + i + ".";
                    return false;
                }
                var angle = AngleBetween(reloadedRows[i].Correction, rows[i].Correction);
                var pre = AngleBetween(reloadedRows[i].PreRotation, rows[i].PreRotation);
                var distance = Distance(reloadedRows[i].Calibration.Position, rows[i].Calibration.Position);
                if (!(angle <= 1e-4f) || !(pre <= 1e-4f) || !(distance <= 1e-4f))
                {
                    error = "table round-trip drifted row " + i + ".";
                    return false;
                }
            }
            var canonicalModel = new RigidPose[CanonicalBones];
            var targetModel = new RigidPose[targetParents.Length];
            var reproduced = new Pose[targetParents.Length];
            var check = RetargetTable.ApplyReference(family, reloaded, canonicalParents, canonicalReference,
                reloadedRows, canonicalModel, targetModel, reproduced);
            if (check != AnimationError.None)
            {
                error = "table reference check failed (" + check + ").";
                return false;
            }
            for (var i = 0; i < calibration.Length; i++)
            {
                var angle = AngleBetween(calibration[i].Rotation, reproduced[i].Rotation);
                var distance = Distance(calibration[i].Position, reproduced[i].Position);
                if (!(angle <= 1e-3f) || !(distance <= 1e-3f))
                {
                    error = "table reference check drifted bone " + i + " (" + angle + " rad, " + distance + " m).";
                    return false;
                }
            }
            return true;
        }

        static string[] UniqueNames(int count)
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = "bone" + i.ToString(CultureInfo.InvariantCulture);
            return result;
        }
    }

    internal class SharedFamilyBaker : Baker<SharedFamilyAuthoring>
    {
        const float UnitScaleTolerance = 1e-5f;

        sealed class FamilyPlan
        {
            public AssetId Family;
            public AssetId Target;
            public int[] TargetParents = Array.Empty<int>();
            public Pose[] Calibration = Array.Empty<Pose>();
            public int[] SourceMap = Array.Empty<int>();
            public int TargetHips;
            public float HeightRatio;
            public RetargetRow[] Rows = Array.Empty<RetargetRow>();
            public RetargetBinding Binding;
            public int[] CanonicalParents = Array.Empty<int>();
            public Pose[] CanonicalReference = Array.Empty<Pose>();
        }

        public override void Bake(SharedFamilyAuthoring authoring)
        {
            var rig = authoring.Rig != null ? authoring.Rig : GetComponent<RigAuthoring>();
            if (rig == null)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': no RigAuthoring found. Fix: add RigAuthoring to the same GameObject, then rebake.", authoring);
                return;
            }
            var clips = new List<AnimationClip>();
            if (authoring.Clips.Count > 0)
            {
                foreach (var clip in authoring.Clips)
                    if (clip != null && !clips.Contains(clip))
                        clips.Add(clip);
            }
            else
            {
                foreach (var clip in AnimationDiscovery.Clips(rig))
                    if (clip != null && !clips.Contains(clip))
                        clips.Add(clip);
            }
            if (clips.Count == 0)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': no animation clips to bake. Fix: assign Clips on '" + authoring.name + "', then rebake.", authoring);
                return;
            }
            foreach (var clip in clips)
                DependsOn(clip);
            var animator = rig.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                DependsOn(animator);
                DependsOn(animator.avatar);
            }
            var skins = new List<SkinAuthoring>();
            foreach (var skin in rig.GetComponentsInChildren<SkinAuthoring>())
                if (skin.Rig == rig || skin.Rig == null)
                {
                    DependsOn(skin);
                    var filter = skin.GetComponent<MeshFilter>();
                    DependsOn(filter != null ? filter.sharedMesh : null);
                    skins.Add(skin);
                }
            if (rig.Engine != Engine.Cpu)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': RigAuthoring Engine must be Cpu for timeline rigs. Fix: set Engine to Cpu on '" + rig.name + "', then rebake.", authoring);
                return;
            }
            if (!(rig.SampleRate > 0f) || !math.isfinite(rig.SampleRate))
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': SampleRate must be greater than 0. Fix: set SampleRate on '" + rig.name + "', then rebake.", authoring);
                return;
            }
            var root = rig.transform;
            var errors = new List<string>();
            var skeleton = RigBuilder.Skeleton(root, clips, skins, errors, rig.ExtraBones);
            var skeletonSet = new HashSet<Transform>(skeleton);
            foreach (var skin in skins)
            {
                var filter = skin.GetComponent<MeshFilter>();
                RigBuilder.ValidateSkin(skin, filter != null ? filter.sharedMesh : null, skeletonSet, errors);
            }
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': " + error + " Fix the rig, then rebake.", authoring);
                return;
            }
            if (!PlanFamily(root, authoring, rig, animator, skeleton, out var plan))
                return;
            var tableBytes = EncodePlan(plan);
            if (tableBytes.Length == 0)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': retarget table failed to encode. Fix: update SA.Animation.Content, then rebake.", authoring);
                return;
            }
            var builder = new BlobBuilder(Allocator.Temp);
            ref var tableData = ref builder.ConstructRoot<SharedRetargetData>();
            tableData.Family = plan.Family;
            tableData.Target = plan.Target;
            var stored = builder.Allocate(ref tableData.Table, tableBytes.Length);
            for (var i = 0; i < tableBytes.Length; i++)
                stored[i] = tableBytes[i];
            var table = builder.CreateBlobAssetReference<SharedRetargetData>(Allocator.Persistent);
            builder.Dispose();
            AddBlobAsset(ref table, out _);
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent<SharedTimeline>(entity);
            AddComponent(entity, new SharedRig { NameHash = RigBuilder.Hash(authoring.name) });
            AddComponent(entity, new SharedRetarget { Value = table });
            AddComponent(entity, new SharedClock
            {
                Seconds = 0.0,
                Tick = 0,
                Continuity = 0,
                Transition = authoring.TransitionSeconds,
                Seek = 0,
                Paused = 0
            });
            var clipAssets = AddBuffer<SharedClipAsset>(entity);
            var contributions = AddBuffer<SharedContribution>(entity);
            for (var i = 0; i < clips.Count; i++)
            {
                if (!BakeClip(root, skeleton, clips[i], rig.SampleRate, plan, out var clipBlob))
                    return;
                clipAssets.Add(new SharedClipAsset { Value = clipBlob });
                contributions.Add(new SharedContribution
                {
                    Clip = i,
                    Weight = clips[i] == authoring.Autoplay ? 1f : 0f,
                    Layer = 0,
                    Mask = ushort.MaxValue,
                    Speed = 1f,
                    Offset = 0f,
                    Sample = 0.0
                });
            }
        }

        static byte[] EncodePlan(FamilyPlan plan)
        {
            if (RetargetFile.Encode(plan.Binding, plan.CanonicalParents, plan.CanonicalReference, plan.Rows, out var bytes) != AnimationError.None)
                return Array.Empty<byte>();
            if (RetargetFile.Load(bytes, plan.Family, plan.Target, plan.CanonicalParents, plan.CanonicalReference, out var reloaded, out _) != AnimationError.None)
                return Array.Empty<byte>();
            return reloaded.Equals(plan.Binding) ? bytes : Array.Empty<byte>();
        }

        bool PlanFamily(Transform root, SharedFamilyAuthoring authoring, RigAuthoring rig, Animator animator,
            List<Transform> skeleton, out FamilyPlan plan)
        {
            plan = new FamilyPlan();
            plan.CanonicalParents = new int[SharedFamilyBake.CanonicalBones];
            plan.CanonicalReference = new Pose[SharedFamilyBake.CanonicalBones];
            if (CanonicalHumanoid.WriteReference(plan.CanonicalParents, plan.CanonicalReference) != AnimationError.None)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': canonical reference failed to build. Fix: update SA.Animation, then rebake.", authoring);
                return false;
            }
            if (!SharedFamilyBake.FamilyId(out var fa, out var fb, out var fc, out var fd))
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': family identity failed to compute. Fix: update SA.Animation.Content, then rebake.", authoring);
                return false;
            }
            plan.Family = new AssetId(fa, fb, fc, fd);
            if (!string.Equals(SharedFamilyBake.ToHex(plan.Family), SharedFamilyBake.PublishedFamilyId, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': family " + SharedFamilyBake.ToHex(plan.Family) + " does not match published " + SharedFamilyBake.PublishedFamilyId + ". Fix: update the canonical bone names, then rebake.", authoring);
                return false;
            }
            var count = skeleton.Count;
            plan.TargetParents = new int[count];
            for (var b = 0; b < count; b++)
                plan.TargetParents[b] = skeleton.IndexOf(skeleton[b].parent);
            if (count == 0 || plan.TargetParents[0] != -1)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': skeleton has no single root bone. Fix: put RigAuthoring on a common parent of every bone, then rebake.", authoring);
                return false;
            }
            for (var b = 1; b < count; b++)
                if (plan.TargetParents[b] < 0)
                {
                    Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': bone '" + skeleton[b].name + "' is detached from the skeleton root. Fix: parent every bone below '" + skeleton[0].name + "', then rebake.", authoring);
                    return false;
                }
            plan.Calibration = new Pose[count];
            for (var b = 0; b < count; b++)
            {
                var rest = RigBuilder.Local(skeleton[b]);
                if (!IsUnitScale(SnapScale(rest.Scale)))
                {
                    Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': bone '" + skeleton[b].name + "' has a non-unit rest scale. Fix: freeze scales on the model, then rebake.", authoring);
                    return false;
                }
                plan.Calibration[b] = new Pose { Position = Conversions.ToMotion(rest.Position), Rotation = Conversions.ToMotion(rest.Rotation), Scale = Float3.One };
            }
            var identityNames = new string[count];
            for (var b = 0; b < count; b++)
                identityNames[b] = AnimationUtility.CalculateTransformPath(skeleton[b], root) + "#" + skeleton[b].name;
            if (ContentIdentity.Skeleton("SA.Target." + rig.name, 1, identityNames, plan.TargetParents, plan.Calibration, out plan.Target) != AnimationError.None)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': target identity failed to compute. Fix: check the skeleton for duplicate paths, then rebake.", authoring);
                return false;
            }
            plan.SourceMap = SolveMap(skeleton);
            if (plan.SourceMap.Length == 0)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': bone assignment failed. Fix: rename rig bones toward humanoid names, then rebake.", authoring);
                return false;
            }
            var canonical = SharedFamilyBake.CanonicalNames;
            var missing = new List<string>();
            for (var r = 0; r < SharedFamilyBake.CanonicalBones; r++)
            {
                var bone = BoneOfRole(plan.SourceMap, skeleton.Count, r);
                if (bone < 0 && IsRequired(r))
                    missing.Add(canonical[r]);
            }
            if (missing.Count > 0)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': required bones have no match (" + string.Join(", ", missing) + "). Fix: rename rig bones toward humanoid names, then rebake.", authoring);
                return false;
            }
            if (plan.SourceMap[0] != 0)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': skeleton root '" + skeleton[0].name + "' did not map to Root. Fix: rename the root bone, then rebake.", authoring);
                return false;
            }
            plan.TargetHips = FindHips(animator, skeleton, plan.SourceMap);
            if (plan.TargetHips < 0 || plan.SourceMap[plan.TargetHips] != 1)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': hips bone did not map to Hips. Fix: check the Animator humanoid Hips mapping, then rebake.", authoring);
                return false;
            }
            var calibrationModel = new RigidPose[count];
            if (RigidMath.Model(plan.TargetParents, plan.Calibration, calibrationModel) != AnimationError.None)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': calibration model failed to compose. Fix: check rest rotations on the rig, then rebake.", authoring);
                return false;
            }
            plan.HeightRatio = calibrationModel[plan.TargetHips].Position.Y;
            if (!float.IsFinite(plan.HeightRatio) || plan.HeightRatio <= 0f)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': hips height is not positive. Fix: check the hips rest height, then rebake.", authoring);
                return false;
            }
            var scratch = new RigidPose[SharedFamilyBake.CanonicalBones + count];
            plan.Rows = new RetargetRow[count];
            if (RetargetTable.Compile(plan.Family, plan.Target, plan.CanonicalParents, plan.CanonicalReference,
                plan.TargetParents, plan.Calibration, plan.SourceMap, 1, plan.TargetHips, plan.HeightRatio,
                scratch, plan.Rows, out plan.Binding) != AnimationError.None)
            {
                Debug.LogError("SA: " + authoring.name + " at '" + AssetPath(authoring) + "': retarget table failed to compile. Fix: check the skeleton chain matches the humanoid hierarchy, then rebake.", authoring);
                return false;
            }
            return true;
        }

        static int[] SolveMap(List<Transform> skeleton)
        {
            var count = skeleton.Count;
            var canonical = SharedFamilyBake.CanonicalNames;
            var scores = new int[SharedFamilyBake.CanonicalBones * count];
            for (var r = 0; r < SharedFamilyBake.CanonicalBones; r++)
                for (var b = 0; b < count; b++)
                    scores[r * count + b] = SharedFamilyBake.ScoreBoneName(canonical[r], skeleton[b].name);
            for (var b = 1; b < count; b++)
                scores[b] = 0;
            for (var r = 1; r < SharedFamilyBake.CanonicalBones; r++)
                scores[r * count] = 0;
            scores[0] = 100;
            if (MappingAssignment.Solve(scores, SharedFamilyBake.CanonicalBones, count, 15, out var assignment) != AnimationError.None || assignment == null)
                return Array.Empty<int>();
            var sourceMap = new int[count];
            for (var b = 0; b < count; b++)
                sourceMap[b] = -1;
            for (var r = 0; r < SharedFamilyBake.CanonicalBones; r++)
            {
                var bone = assignment.BoneOfRole[r];
                if (bone < 0 || bone >= count || sourceMap[bone] >= 0)
                    continue;
                sourceMap[bone] = r;
            }
            DropTopologyViolations(skeleton, sourceMap);
            return sourceMap;
        }

        static void DropTopologyViolations(List<Transform> skeleton, int[] sourceMap)
        {
            var count = skeleton.Count;
            var targetParents = new int[count];
            for (var b = 0; b < count; b++)
                targetParents[b] = skeleton.IndexOf(skeleton[b].parent);
            var canonicalParents = SharedFamilyBake.CanonicalParentsArray();
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var b = 1; b < count; b++)
                {
                    var source = sourceMap[b];
                    if (source <= 0)
                        continue;
                    var ancestor = targetParents[b];
                    while (ancestor > 0 && sourceMap[ancestor] < 0)
                        ancestor = targetParents[ancestor];
                    var required = sourceMap[ancestor];
                    if (required < 0)
                        continue;
                    var parent = canonicalParents[source];
                    while (parent > 0 && parent != required)
                        parent = canonicalParents[parent];
                    if (parent != required)
                    {
                        sourceMap[b] = -1;
                        changed = true;
                    }
                }
            }
        }

        static int BoneOfRole(int[] sourceMap, int bones, int role)
        {
            for (var b = 0; b < bones; b++)
                if (sourceMap[b] == role)
                    return b;
            return -1;
        }

        bool BakeClip(Transform root, List<Transform> skeleton, AnimationClip clip, float sampleRate,
            FamilyPlan plan, out BlobAssetReference<SharedClipData> clipBlob)
        {
            clipBlob = default;
            if (!SampleRigClip(root, skeleton, clip, sampleRate, out var rigFrames, out var frames, out var rootMoves))
            {
                Debug.LogError("SA: clip '" + clip.name + "' at '" + AssetDatabase.GetAssetPath(clip) + "': rig sampling failed. Fix: check the clip import settings, then rebake.", clip);
                return false;
            }
            var count = skeleton.Count;
            var targetForCanonical = new int[SharedFamilyBake.CanonicalBones];
            for (var c = 0; c < SharedFamilyBake.CanonicalBones; c++)
                targetForCanonical[c] = -1;
            for (var b = 0; b < count; b++)
                if (plan.SourceMap[b] >= 0)
                    targetForCanonical[plan.SourceMap[b]] = b;
            if (rootMoves)
                Debug.LogWarning("SA: clip '" + clip.name + "' at '" + AssetDatabase.GetAssetPath(clip) + "': root travel is dropped, the family clip plays in place. Fix: this is expected for treadmill playback; move the character with a motion driver instead.", clip);
            var samples = new Pose[frames * SharedFamilyBake.CanonicalBones];
            var canonicalModel = new RigidPose[SharedFamilyBake.CanonicalBones];
            var frameModel = new RigidPose[count];
            var frameLocal = new Pose[count];
            for (var f = 0; f < frames; f++)
            {
                for (var b = 0; b < count; b++)
                    frameLocal[b] = rigFrames[f * count + b];
                if (RigidMath.Model(plan.TargetParents, frameLocal, frameModel) != AnimationError.None)
                {
                    Debug.LogError("SA: clip '" + clip.name + "' at '" + AssetDatabase.GetAssetPath(clip) + "': frame " + f + " failed to compose. Fix: check the clip for invalid bones, then rebake.", clip);
                    return false;
                }
                samples[f * SharedFamilyBake.CanonicalBones] = Pose.Identity;
                canonicalModel[0] = RigidPose.Identity;
                for (var c = 1; c < SharedFamilyBake.CanonicalBones; c++)
                {
                    var t = targetForCanonical[c];
                    Pose local;
                    if (t < 0)
                    {
                        local = plan.CanonicalReference[c];
                        canonicalModel[c] = Compose(ParentModel(canonicalModel, plan.CanonicalParents[c]), local);
                    }
                    else
                    {
                        ref readonly var row = ref plan.Rows[t];
                        var correction = Quaternion.InverseUnit(Quaternion.Normalize(row.Correction));
                        Quaternion rotation;
                        if (row.Mode == RetargetRowMode.FastLocal)
                        {
                            var pre = Quaternion.InverseUnit(Quaternion.Normalize(row.PreRotation));
                            rotation = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Multiply(pre, frameLocal[t].Rotation), correction));
                        }
                        else
                        {
                            var parentModel = frameModel[plan.TargetParents[t]];
                            rotation = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Multiply(Quaternion.InverseUnit(Quaternion.Normalize(parentModel.Rotation)), frameLocal[t].Rotation), correction));
                        }
                        if (c == 1)
                        {
                            var desired = plan.Binding.SourceHipReference + (frameModel[plan.TargetHips].Position - plan.Binding.TargetHipReference) * (1f / plan.HeightRatio);
                            var parentModel = ParentModel(canonicalModel, plan.CanonicalParents[c]);
                            var offset = desired - parentModel.Position;
                            var inverse = Quaternion.InverseUnit(Quaternion.Normalize(parentModel.Rotation));
                            local = new Pose { Position = Quaternion.Rotate(inverse, offset), Rotation = rotation, Scale = Float3.One };
                            canonicalModel[c] = new RigidPose(desired, rotation);
                        }
                        else
                        {
                            local = new Pose { Position = plan.CanonicalReference[c].Position, Rotation = rotation, Scale = Float3.One };
                            canonicalModel[c] = Compose(ParentModel(canonicalModel, plan.CanonicalParents[c]), local);
                        }
                    }
                    if (!RigidMath.UnitPose(local))
                    {
                        Debug.LogError("SA: clip '" + clip.name + "' at '" + AssetDatabase.GetAssetPath(clip) + "': canonical bone " + c + " is not a unit pose. Fix: check the clip for scaled bones, then rebake.", clip);
                        return false;
                    }
                    samples[f * SharedFamilyBake.CanonicalBones + c] = local;
                }
            }
            if (!SharedFamilyBake.EncodeCanonicalSamples(samples, frames, clip.length, clip.isLooping, out var bytes, out _))
            {
                Debug.LogError("SA: clip '" + clip.name + "' at '" + AssetDatabase.GetAssetPath(clip) + "': family encode failed. Fix: check the clip for scaled or invalid bones, then rebake.", clip);
                return false;
            }
            var builder = new BlobBuilder(Allocator.Temp);
            ref var data = ref builder.ConstructRoot<SharedClipData>();
            data.Family = plan.Family;
            data.Artifact = ContentIdentity.Hash(bytes);
            data.Duration = clip.length;
            data.SampleCount = frames;
            data.BoneCount = SharedFamilyBake.CanonicalBones;
            data.Loop = (byte)(clip.isLooping ? 1 : 0);
            var stored = builder.Allocate(ref data.Bytes, bytes.Length);
            for (var i = 0; i < bytes.Length; i++)
                stored[i] = bytes[i];
            clipBlob = builder.CreateBlobAssetReference<SharedClipData>(Allocator.Persistent);
            builder.Dispose();
            AddBlobAsset(ref clipBlob, out _);
            return true;
        }

        static RigidPose ParentModel(RigidPose[] canonicalModel, int parent)
        {
            return parent < 0 ? RigidPose.Identity : canonicalModel[parent];
        }

        static RigidPose Compose(in RigidPose parent, in Pose local)
        {
            var rotation = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Normalize(parent.Rotation), Quaternion.Normalize(local.Rotation)));
            return new RigidPose(parent.Position + Quaternion.Rotate(Quaternion.Normalize(parent.Rotation), local.Position), rotation);
        }

        bool SampleRigClip(Transform root, List<Transform> skeleton, AnimationClip clip, float sampleRate, out Pose[] frames, out int frameCount, out bool rootMoves)
        {
            frames = Array.Empty<Pose>();
            frameCount = 0;
            rootMoves = false;
            var rate = clip.frameRate > 60f ? 60f : clip.frameRate;
            if (rate <= 0f)
                return false;
            var count = skeleton.Count;
            frameCount = Math.Max(2, Mathf.CeilToInt(clip.length * rate) + 1);
            var copy = UnityEngine.Object.Instantiate(root.gameObject);
            copy.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var all = copy.GetComponentsInChildren<Transform>(true);
                var rest = new BonePose[all.Length];
                for (var i = 0; i < all.Length; i++)
                    rest[i] = RigBuilder.Local(all[i]);
                var mapped = new Transform[count];
                for (var b = 0; b < count; b++)
                    mapped[b] = copy.transform.Find(AnimationUtility.CalculateTransformPath(skeleton[b], root));
                var animationRoot = copy.GetComponentInChildren<Animator>(true);
                var target = animationRoot != null ? animationRoot.gameObject : copy;
                frames = new Pose[frameCount * count];
                var previous = new quaternion[count];
                for (var b = 0; b < count; b++)
                    previous[b] = quaternion.identity;
                var rootBase = float3.zero;
                for (var f = 0; f < frameCount; f++)
                {
                    for (var i = 0; i < all.Length; i++)
                    {
                        all[i].localPosition = rest[i].Position;
                        all[i].localRotation = rest[i].Rotation;
                        all[i].localScale = rest[i].Scale;
                    }
                    clip.SampleAnimation(target, clip.length * f / (frameCount - 1));
                    for (var b = 0; b < count; b++)
                    {
                        var local = mapped[b] != null ? RigBuilder.Local(mapped[b]) : RigBuilder.Local(skeleton[b]);
                        if (f > 0 && math.dot(local.Rotation.value, previous[b].value) < 0f)
                            local.Rotation = new quaternion(-local.Rotation.value);
                        previous[b] = local.Rotation;
                        if (!IsUnitScale(SnapScale(local.Scale)))
                            return false;
                        frames[f * count + b] = new Pose
                        {
                            Position = Conversions.ToMotion(local.Position),
                            Rotation = Conversions.ToMotion(local.Rotation),
                            Scale = Float3.One
                        };
                        if (b == 0)
                        {
                            var position = new float3(local.Position.x, local.Position.y, local.Position.z);
                            if (f == 0)
                                rootBase = position;
                            else if (math.distancesq(position, rootBase) > 1e-12f)
                                rootMoves = true;
                        }
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
            return true;
        }

        static float3 SnapScale(float3 scale)
        {
            return new float3(SnapUnit(scale.x), SnapUnit(scale.y), SnapUnit(scale.z));
        }

        static float SnapUnit(float value)
        {
            return Math.Abs(value - 1f) <= UnitScaleTolerance ? 1f : value;
        }

        static bool IsUnitScale(float3 scale)
        {
            return scale.x == 1f && scale.y == 1f && scale.z == 1f;
        }

        static bool IsRequired(int role)
        {
            foreach (var required in SharedFamilyBake.RequiredRoles)
                if (required == role)
                    return true;
            return false;
        }

        static int FindHips(Animator animator, List<Transform> skeleton, int[] sourceMap)
        {
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null)
                {
                    var index = skeleton.IndexOf(hips);
                    if (index >= 0)
                        return index;
                }
            }
            for (var b = 0; b < skeleton.Count; b++)
                if (sourceMap[b] == 1)
                    return b;
            return -1;
        }

        static string AssetPath(UnityEngine.Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? asset.name : path;
        }
    }
}
