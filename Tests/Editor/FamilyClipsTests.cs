using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SA.Animation;
using SA.Animation.Content;
using SA.Editor;
using SA.Motion.Numerics;
using Pose = SA.Motion.Numerics.Pose;
using Quaternion = SA.Motion.Numerics.Quaternion;

namespace SA.Tests
{
    public class FamilyClipsTests
    {
        const string Prefix = "mixamorig:";
        const float Hz = 30f;
        const string SyntyCharacter = "PolygonSyntyCharacter.fbx";
        const string NeophyteCharacter = "Neophyte_RIG.fbx";
        const string SyntyJump = "A_Jump_Walking_RootMotionVertical_Masc.fbx";
        const string SyntyJumpInPlace = "A_Jump_Walking_Masc.fbx";
        const string SyntyJumpMoving = "A_Jump_Walking_RootMotion_Masc.fbx";

        static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Pinky" };

        struct Bone
        {
            public string Name;
            public string Parent;
            public Vector3 World;
        }

        struct RoundTrip
        {
            public float Degrees;
            public float Metres;
            public int Frames;
            public string Worst;
        }

        static Dictionary<string, int> MixamoRoles()
        {
            var roles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Hips", 1 }, { "Spine", 2 }, { "Spine1", 3 }, { "Spine2", 4 }, { "Neck", 5 }, { "Head", 6 },
                { "LeftShoulder", 10 }, { "LeftArm", 11 }, { "LeftForeArm", 12 }, { "LeftHand", 13 },
                { "RightShoulder", 29 }, { "RightArm", 30 }, { "RightForeArm", 31 }, { "RightHand", 32 },
                { "LeftUpLeg", 48 }, { "LeftLeg", 49 }, { "LeftFoot", 50 }, { "LeftToeBase", 51 },
                { "RightUpLeg", 52 }, { "RightLeg", 53 }, { "RightFoot", 54 }, { "RightToeBase", 55 }
            };
            for (var f = 0; f < Fingers.Length; f++)
                for (var j = 0; j < 3; j++)
                {
                    roles["LeftHand" + Fingers[f] + (j + 1)] = 14 + f * 3 + j;
                    roles["RightHand" + Fingers[f] + (j + 1)] = 33 + f * 3 + j;
                }
            return roles;
        }

        static List<Bone> MixamoBones()
        {
            var bones = new List<Bone>();
            void Add(string name, string parent, float x, float y, float z) => bones.Add(new Bone { Name = name, Parent = parent, World = new Vector3(x, y, z) });
            Add("Hips", null, 0f, 1f, 0f);
            Add("Spine", "Hips", 0f, 1.1f, 0f);
            Add("Spine1", "Spine", 0f, 1.25f, 0f);
            Add("Spine2", "Spine1", 0f, 1.4f, 0f);
            Add("Neck", "Spine2", 0f, 1.55f, 0f);
            Add("Head", "Neck", 0f, 1.65f, 0f);
            Add("HeadTop_End", "Head", 0f, 1.85f, 0f);
            for (var side = 0; side < 2; side++)
            {
                var s = side == 0 ? "Left" : "Right";
                var x = side == 0 ? -1f : 1f;
                Add(s + "Shoulder", "Spine2", 0.06f * x, 1.48f, 0f);
                Add(s + "Arm", s + "Shoulder", 0.18f * x, 1.48f, 0f);
                Add(s + "ForeArm", s + "Arm", 0.45f * x, 1.48f, 0f);
                Add(s + "Hand", s + "ForeArm", 0.7f * x, 1.48f, 0f);
                for (var f = 0; f < Fingers.Length; f++)
                    for (var j = 1; j <= 4; j++)
                        Add(s + "Hand" + Fingers[f] + j, j == 1 ? s + "Hand" : s + "Hand" + Fingers[f] + (j - 1),
                            (0.72f + 0.03f * j) * x, f == 0 ? 1.46f : 1.48f, 0.04f - 0.02f * f);
                Add(s + "UpLeg", "Hips", 0.09f * x, 0.95f, 0f);
                Add(s + "Leg", s + "UpLeg", 0.09f * x, 0.52f, 0.01f);
                Add(s + "Foot", s + "Leg", 0.09f * x, 0.09f, 0f);
                Add(s + "ToeBase", s + "Foot", 0.09f * x, 0.02f, 0.12f);
                Add(s + "Toe_End", s + "ToeBase", 0.09f * x, 0.02f, 0.2f);
            }
            return bones;
        }

        static GameObject MixamoRig(out Dictionary<string, Transform> lookup)
        {
            var root = new GameObject("W1MixamoRig");
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);
            lookup = new Dictionary<string, Transform>();
            var index = 0;
            foreach (var bone in MixamoBones())
            {
                var go = new GameObject(Prefix + bone.Name);
                go.transform.SetParent(bone.Parent == null ? root.transform : lookup[bone.Parent], false);
                go.transform.position = bone.World;
                go.transform.rotation = UnityEngine.Quaternion.Euler(37f * index % 90f - 45f, 53f * index % 120f - 60f, 71f * index % 80f - 40f);
                lookup[bone.Name] = go.transform;
                index++;
            }
            return root;
        }

        static Avatar BuildAvatar(GameObject root) => BuildAvatar(root, 0.5f);

        static Avatar BuildAvatar(GameObject root, float twist)
        {
            var human = new List<HumanBone>();
            var names = SharedFamilyBake.CanonicalNames;
            foreach (var pair in MixamoRoles())
            {
                var role = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), names[pair.Value]);
                human.Add(new HumanBone { boneName = Prefix + pair.Key, humanName = HumanTrait.BoneName[(int)role], limit = new HumanLimit { useDefaultValues = true } });
            }
            var skeleton = new List<SkeletonBone>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                skeleton.Add(new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
            var description = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = twist,
                lowerArmTwist = twist,
                upperLegTwist = twist,
                lowerLegTwist = twist,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false
            };
            var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            avatar.name = "W1MixamoAvatar";
            return avatar;
        }

        static AnimationClip SyntheticClip(Dictionary<string, Transform> lookup, Transform root)
        {
            var clip = new AnimationClip { name = "W1Synthetic", frameRate = Hz };
            var roles = MixamoRoles();
            var index = 0;
            foreach (var pair in lookup)
            {
                index++;
                if (!roles.ContainsKey(pair.Key))
                    continue;
                var t = pair.Value;
                var path = AnimationUtility.CalculateTransformPath(t, root);
                var axis = new Vector3(Mathf.Sin(index * 1.3f), Mathf.Cos(index * 0.7f), Mathf.Sin(index * 2.1f)).normalized;
                var curves = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
                var px = new AnimationCurve();
                var py = new AnimationCurve();
                var pz = new AnimationCurve();
                var previous = t.localRotation;
                for (var f = 0; f <= 30; f++)
                {
                    var time = f / Hz;
                    var swing = 25f * Mathf.Sin(2f * Mathf.PI * time + index);
                    var rotation = t.localRotation * UnityEngine.Quaternion.AngleAxis(swing, axis);
                    if (pair.Key == "Hips")
                        rotation = UnityEngine.Quaternion.AngleAxis(60f * time, Vector3.up) * rotation;
                    if (UnityEngine.Quaternion.Dot(rotation, previous) < 0f)
                        rotation = new UnityEngine.Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                    previous = rotation;
                    curves[0].AddKey(time, rotation.x);
                    curves[1].AddKey(time, rotation.y);
                    curves[2].AddKey(time, rotation.z);
                    curves[3].AddKey(time, rotation.w);
                    if (pair.Key == "Hips")
                    {
                        var p = t.localPosition + new Vector3(0.3f * time, 0.08f * Mathf.Sin(2f * Mathf.PI * time), 1.2f * time);
                        px.AddKey(time, p.x);
                        py.AddKey(time, p.y);
                        pz.AddKey(time, p.z);
                    }
                }
                clip.SetCurve(path, typeof(Transform), "localRotation.x", curves[0]);
                clip.SetCurve(path, typeof(Transform), "localRotation.y", curves[1]);
                clip.SetCurve(path, typeof(Transform), "localRotation.z", curves[2]);
                clip.SetCurve(path, typeof(Transform), "localRotation.w", curves[3]);
                if (pair.Key == "Hips")
                {
                    clip.SetCurve(path, typeof(Transform), "localPosition.x", px);
                    clip.SetCurve(path, typeof(Transform), "localPosition.y", py);
                    clip.SetCurve(path, typeof(Transform), "localPosition.z", pz);
                }
            }
            clip.EnsureQuaternionContinuity();
            return clip;
        }

        static float Degrees(Quaternion a, Quaternion b)
        {
            var x = (double)a.X;
            var y = (double)a.Y;
            var z = (double)a.Z;
            var w = (double)a.W;
            var la = Math.Sqrt(x * x + y * y + z * z + w * w);
            var lb = Math.Sqrt((double)b.X * b.X + (double)b.Y * b.Y + (double)b.Z * b.Z + (double)b.W * b.W);
            var rw = (w * b.W + x * b.X + y * b.Y + z * b.Z) / (la * lb);
            var rx = (w * b.X - x * b.W - y * b.Z + z * b.Y) / (la * lb);
            var ry = (w * b.Y + x * b.Z - y * b.W - z * b.X) / (la * lb);
            var rz = (w * b.Z - x * b.Y + y * b.X - z * b.W) / (la * lb);
            return (float)(2.0 * Math.Atan2(Math.Sqrt(rx * rx + ry * ry + rz * rz), Math.Abs(rw)) * 180.0 / Math.PI);
        }

        static bool Body(int canonical) => canonical > 0 && canonical < 7 || canonical >= 10 && canonical <= 13 || canonical >= 29 && canonical <= 32 || canonical >= 48;

        static RoundTrip Measure(FamilyRigPlan plan, AnimationClip clip, bool encode, bool lossless)
        {
            Assert.IsTrue(FamilyClips.TrySampleBones(plan, clip, Hz, out var direct, out var frames, out var error), error);
            Assert.IsTrue(FamilyClips.TrySampleFixed(plan, clip, Hz, out var canonical, out var root, out var start, out var sampled, out error), error);
            Assert.AreEqual(frames, sampled);
            Assert.AreEqual(frames, root.Length);
            if (encode)
            {
                Assert.IsTrue(FamilyClips.TryEncode(plan, canonical, root, Hz, lossless, out var bytes, out error), error);
                Assert.AreEqual(AnimationError.None, SharedClipReader.Load(bytes, plan.Family, SharedFamilyBake.CanonicalReferencePoses(), out var loaded), "reload");
                Assert.AreEqual(RootPolicy.Planar, loaded.RootPolicy);
                Assert.AreEqual(frames, loaded.SampleCount);
                var reference = SharedFamilyBake.CanonicalReferencePoses();
                var probe = new Pose[FamilyClips.CanonicalBones];
                for (var f = 0; f < frames; f++)
                {
                    Assert.AreEqual(AnimationError.None, loaded.Sample(loaded.Duration * f / (frames - 1), reference, probe));
                    Array.Copy(probe, 0, canonical, f * FamilyClips.CanonicalBones, FamilyClips.CanonicalBones);
                    root[f] = loaded.Root[f];
                }
            }
            var placed = new RigidPose[frames];
            for (var f = 0; f < frames; f++)
                Assert.IsTrue(RigidMath.TryCompose(start, root[f], out placed[f]));
            Assert.IsTrue(FamilyClips.TryApply(plan, canonical, placed, out var applied, out error), error);
            var count = plan.BoneCount;
            var parents = new int[count];
            for (var b = 0; b < count; b++)
                parents[b] = plan.Parents[b];
            var directModel = new RigidPose[count];
            var appliedModel = new RigidPose[count];
            var result = new RoundTrip { Frames = frames, Worst = string.Empty };
            var extremities = new[] { plan.BoneOfCanonical[13], plan.BoneOfCanonical[32], plan.BoneOfCanonical[50], plan.BoneOfCanonical[54] };
            for (var f = 0; f < frames; f++)
            {
                Assert.AreEqual(AnimationError.None, RigidMath.Model(parents, new ReadOnlySpan<Pose>(direct, f * count, count), directModel));
                Assert.AreEqual(AnimationError.None, RigidMath.Model(parents, new ReadOnlySpan<Pose>(applied, f * count, count), appliedModel));
                for (var b = 1; b < count; b++)
                {
                    if (!Body(plan.CanonicalOfBone[b]))
                        continue;
                    var degrees = Degrees(directModel[b].Rotation, appliedModel[b].Rotation);
                    if (degrees > result.Degrees)
                    {
                        result.Degrees = degrees;
                        result.Worst = plan.Bones[b].name + " frame " + f;
                    }
                }
                foreach (var b in extremities)
                    result.Metres = Math.Max(result.Metres, SharedFamilyBake.Distance(directModel[b].Position, appliedModel[b].Position));
            }
            return result;
        }

        static void Report(string name, string text)
        {
            var folder = Path.Combine(Application.dataPath, "..", "Temp", "sa", "w1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".txt"), text);
            TestContext.WriteLine(text);
        }

        static string Describe(string label, RoundTrip trip) =>
            label + ": frames " + trip.Frames + ", max world rotation " + trip.Degrees.ToString("0.00000", CultureInfo.InvariantCulture)
            + " deg (" + trip.Worst + "), max hand/foot " + (trip.Metres * 100f).ToString("0.0000", CultureInfo.InvariantCulture) + " cm\n";

        static void AssertMixamoMap(FamilyRigPlan plan, out int differences, StringBuilder log)
        {
            var expected = MixamoRoles();
            var names = SharedFamilyBake.CanonicalNames;
            differences = 0;
            for (var b = 1; b < plan.BoneCount; b++)
            {
                var name = plan.Bones[b].name;
                var cut = name.LastIndexOf(':');
                var bare = cut >= 0 ? name.Substring(cut + 1) : name;
                var want = expected.TryGetValue(bare, out var role) ? role : -1;
                var got = plan.CanonicalOfBone[b];
                if (want == got || want < 0 && got < 0)
                    continue;
                differences++;
                log.Append("map difference: ").Append(name).Append(" expected ").Append(want < 0 ? "none" : names[want]).Append(" got ").Append(got < 0 ? "none" : names[got]).Append('\n');
            }
            foreach (var pair in expected)
                if (plan.BoneOfCanonical[pair.Value] < 0)
                {
                    differences++;
                    log.Append("unmapped: ").Append(pair.Key).Append('\n');
                }
        }

        static void AssertRequired(FamilyRigPlan plan)
        {
            foreach (var role in SharedFamilyBake.RequiredRoles)
                Assert.GreaterOrEqual(plan.BoneOfCanonical[role], 0, "required " + SharedFamilyBake.CanonicalNames[role]);
        }

        [Test]
        public void SyntheticMixamoNamePathMapsAndRoundTrips()
        {
            var rig = MixamoRig(out var lookup);
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                Assert.IsFalse(plan.MappedFromAvatar);
                Assert.IsFalse(plan.CalibratedFromAvatar);
                AssertRequired(plan);
                var log = new StringBuilder();
                AssertMixamoMap(plan, out var differences, log);
                Assert.AreEqual(0, differences, log.ToString());
                Assert.AreEqual(lookup["LeftArm"], plan.Bones[plan.BoneOfCanonical[11]]);
                Assert.AreEqual(lookup["LeftForeArm"], plan.Bones[plan.BoneOfCanonical[12]]);
                Assert.AreEqual(lookup["LeftUpLeg"], plan.Bones[plan.BoneOfCanonical[48]]);
                Assert.AreEqual(lookup["LeftLeg"], plan.Bones[plan.BoneOfCanonical[49]]);
                foreach (var bone in plan.Bones)
                    Assert.AreNotEqual("Body", bone.name);
                var clip = SyntheticClip(lookup, rig.transform);
                var raw = Measure(plan, clip, false, true);
                var lossless = Measure(plan, clip, true, true);
                var packed = Measure(plan, clip, true, false);
                Report("synthetic-name", Describe("I1 sample->apply", raw) + Describe("I1 lossless .saclip", lossless) + Describe("I1 packed .saclip", packed));
                Assert.LessOrEqual(raw.Degrees, 0.01f, raw.Worst);
                Assert.LessOrEqual(lossless.Degrees, 0.01f, lossless.Worst);
                Assert.LessOrEqual(lossless.Metres, 0.0001f);
                Assert.LessOrEqual(packed.Degrees, 1f, packed.Worst);
                Assert.LessOrEqual(packed.Metres, 0.01f);
                UnityEngine.Object.DestroyImmediate(clip);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void SyntheticRootIsPlanarAndStartsAtIdentity()
        {
            var rig = MixamoRig(out var lookup);
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                var clip = SyntheticClip(lookup, rig.transform);
                Assert.IsTrue(FamilyClips.TrySampleFixed(plan, clip, Hz, out var canonical, out var root, out _, out var frames, out error), error);
                Assert.AreEqual(31, frames);
                Assert.AreEqual(0f, root[0].Position.X);
                Assert.AreEqual(1f, Math.Abs(root[0].Rotation.W));
                var last = root[frames - 1];
                Assert.AreEqual(0f, last.Position.Y);
                Assert.AreEqual(0f, last.Rotation.X);
                Assert.AreEqual(0f, last.Rotation.Z);
                Assert.Greater(Math.Sqrt(last.Position.X * last.Position.X + last.Position.Z * last.Position.Z), 0.5);
                Assert.AreEqual(60f, 2f * Math.Atan2(last.Rotation.Y, last.Rotation.W) * 180.0 / Math.PI, 0.5);
                for (var f = 0; f < frames; f++)
                    Assert.AreEqual(Pose.Identity.Rotation.W, canonical[f * FamilyClips.CanonicalBones].Rotation.W);
                Assert.IsTrue(FamilyClips.TryCapture(plan, out var pose, out error), error);
                Assert.AreEqual(FamilyClips.CanonicalBones, pose.Length);
                Assert.Less(Math.Abs(pose[1].Position.X) + Math.Abs(pose[1].Position.Z), 1e-4f);
                UnityEngine.Object.DestroyImmediate(clip);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void SyntheticAvatarPathMapsAndCalibratesFromTPose()
        {
            var rig = MixamoRig(out var lookup);
            Avatar avatar = null;
            try
            {
                avatar = BuildAvatar(rig);
                Assert.IsTrue(avatar.isValid && avatar.isHuman, "avatar build failed");
                var animator = rig.AddComponent<Animator>();
                animator.avatar = avatar;
                lookup["LeftArm"].Rotate(Vector3.forward, 40f, Space.World);
                lookup["RightArm"].Rotate(Vector3.forward, -40f, Space.World);
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                Assert.IsTrue(plan.MappedFromAvatar);
                Assert.IsTrue(plan.CalibratedFromAvatar);
                AssertRequired(plan);
                var log = new StringBuilder();
                AssertMixamoMap(plan, out var differences, log);
                Assert.AreEqual(0, differences, log.ToString());
                var leftArm = plan.BoneOfCanonical[11];
                Assert.AreEqual(40f, plan.CalibrationDegrees[leftArm], 0.1f);
                Assert.AreEqual(0f, plan.CalibrationDegrees[plan.BoneOfCanonical[2]], 0.01f);
                Assert.Greater(plan.Warnings.Count, 0);
                var clip = SyntheticClip(lookup, rig.transform);
                var raw = Measure(plan, clip, false, true);
                var lossless = Measure(plan, clip, true, true);
                Report("synthetic-avatar", Describe("I1 sample->apply", raw) + Describe("I1 lossless .saclip", lossless)
                    + "left arm rest vs avatar T-pose " + plan.CalibrationDegrees[leftArm].ToString("0.000", CultureInfo.InvariantCulture) + " deg\n");
                Assert.LessOrEqual(raw.Degrees, 0.01f, raw.Worst);
                Assert.LessOrEqual(lossless.Degrees, 0.01f, lossless.Worst);
                Assert.LessOrEqual(lossless.Metres, 0.0001f);
                UnityEngine.Object.DestroyImmediate(clip);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig);
                if (avatar != null)
                    UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void EncodeRejectsWrongShapes()
        {
            var rig = MixamoRig(out _);
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                Assert.IsFalse(FamilyClips.TryEncode(plan, new Pose[FamilyClips.CanonicalBones], new RigidPose[1], Hz, out _, out error));
                Assert.IsFalse(FamilyClips.TryApply(plan, new Pose[3], null, out _, out error));
                Assert.IsFalse(FamilyClips.TryPlan(null, out _, out error));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        static AnimationClip Take(string path, string name)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is AnimationClip clip && clip.name == name)
                    return clip;
            return null;
        }

        static string FindModel(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var found = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets(stem + " t:Model"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                    found.Add(path);
            }
            found.Sort(StringComparer.Ordinal);
            return found.Count > 0 ? found[0] : null;
        }

        static string RequireModel(string fileName)
        {
            var path = FindModel(fileName);
            if (path == null)
                Assert.Ignore(fileName + " is not in this project (searched every model in Assets and Packages); import the Synty Animation Base Locomotion pack or the Neophyte character to run this test.");
            return path;
        }

        static AnimationClip RequireTake(string fileName, string take)
        {
            var path = RequireModel(fileName);
            var clip = Take(path, take);
            if (clip == null)
                Assert.Ignore(path + " has no take named " + take + ".");
            return clip;
        }

        static GameObject Spawn(string fileName)
        {
            var path = RequireModel(fileName);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
                Assert.Ignore("asset missing: " + path);
            var instance = UnityEngine.Object.Instantiate(asset);
            instance.name = asset.name;
            return instance;
        }

        static string PlanSummary(FamilyRigPlan plan)
        {
            var text = new StringBuilder();
            var names = SharedFamilyBake.CanonicalNames;
            text.Append("mapped from avatar ").Append(plan.MappedFromAvatar).Append(", calibrated from avatar ").Append(plan.CalibratedFromAvatar)
                .Append(", bones ").Append(plan.BoneCount).Append(", height ratio ").Append(plan.HeightRatio.ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
            var mapped = 0;
            var worst = 0f;
            var worstName = string.Empty;
            for (var b = 1; b < plan.BoneCount; b++)
            {
                if (plan.CanonicalOfBone[b] <= 0)
                    continue;
                mapped++;
                text.Append("  ").Append(names[plan.CanonicalOfBone[b]]).Append(" <- ").Append(plan.Bones[b].name)
                    .Append(" (rest vs T-pose ").Append(plan.CalibrationDegrees[b].ToString("0.00", CultureInfo.InvariantCulture)).Append(" deg)\n");
                if (plan.CalibrationDegrees[b] > worst)
                {
                    worst = plan.CalibrationDegrees[b];
                    worstName = plan.Bones[b].name;
                }
            }
            var required = 0;
            foreach (var role in SharedFamilyBake.RequiredRoles)
                if (plan.BoneOfCanonical[role] >= 0)
                    required++;
            text.Append("I3 required roles ").Append(required).Append("/15, mapped roles ").Append(mapped)
                .Append(", max rest vs T-pose ").Append(worst.ToString("0.00", CultureInfo.InvariantCulture)).Append(" deg (").Append(worstName).Append(")\n");
            foreach (var warning in plan.Warnings)
                text.Append("warning: ").Append(warning).Append('\n');
            return text.ToString();
        }

        [Test, Category("RealAssets")]
        public void SyntyJumpRoundTrip()
        {
            var clip = RequireTake(SyntyJump, "A_Jump_Walking_RootMotionVertical_Masc_Full");
            var character = Spawn(SyntyCharacter);
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(character, out var plan, out var error), error);
                AssertRequired(plan);
                var raw = Measure(plan, clip, false, true);
                var packed = Measure(plan, clip, true, false);
                Assert.IsTrue(FamilyClips.TrySampleFixed(plan, clip, Hz, out _, out var root, out _, out var frames, out error), error);
                var last = root[frames - 1];
                var report = PlanSummary(plan) + Describe("I1 sample->apply", raw) + Describe("I1 packed .saclip", packed)
                    + "planar root travel " + Math.Sqrt(last.Position.X * last.Position.X + last.Position.Z * last.Position.Z).ToString("0.000", CultureInfo.InvariantCulture)
                    + " m, turn " + (2.0 * Math.Atan2(last.Rotation.Y, last.Rotation.W) * 180.0 / Math.PI).ToString("0.00", CultureInfo.InvariantCulture) + " deg\n"
                    + RootMotionVariants(plan);
                Report("synty", report);
                Assert.LessOrEqual(packed.Degrees, 1f, packed.Worst);
                Assert.LessOrEqual(packed.Metres, 0.01f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        static string RootMotionVariants(FamilyRigPlan plan)
        {
            var inPlacePath = FindModel(SyntyJumpInPlace);
            var movingPath = FindModel(SyntyJumpMoving);
            var inPlace = inPlacePath != null ? Take(inPlacePath, "A_Jump_Walking_Masc") : null;
            var moving = movingPath != null ? Take(movingPath, "A_Jump_Walking_RootMotion_Masc") : null;
            if (inPlace == null || moving == null)
                return "R17 variants missing\n";
            Assert.IsTrue(FamilyClips.TrySampleFixed(plan, inPlace, Hz, out var a, out var rootA, out _, out var framesA, out var error), error);
            Assert.IsTrue(FamilyClips.TrySampleFixed(plan, moving, Hz, out var b, out var rootB, out _, out var framesB, out error), error);
            var frames = Math.Min(framesA, framesB);
            var degrees = 0f;
            var metres = 0f;
            for (var i = 0; i < frames * FamilyClips.CanonicalBones; i++)
            {
                degrees = Math.Max(degrees, Degrees(a[i].Rotation, b[i].Rotation));
                metres = Math.Max(metres, SharedFamilyBake.Distance(a[i].Position, b[i].Position));
            }
            var detail = new StringBuilder();
            foreach (var f in new[] { 0, frames / 2, frames - 1 })
            {
                var ha = a[f * FamilyClips.CanonicalBones + 1].Position;
                var hb = b[f * FamilyClips.CanonicalBones + 1].Position;
                detail.Append("  frame ").Append(f).Append(" hips in-place (").Append(ha.X.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(ha.Y.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ").Append(ha.Z.ToString("0.000", CultureInfo.InvariantCulture))
                    .Append(") root-motion (").Append(hb.X.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(hb.Y.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ").Append(hb.Z.ToString("0.000", CultureInfo.InvariantCulture))
                    .Append(") roots (").Append(rootA[f].Position.Z.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(rootB[f].Position.Z.ToString("0.000", CultureInfo.InvariantCulture)).Append(")\n");
            }
            var travelA = rootA[framesA - 1].Position;
            var travelB = rootB[framesB - 1].Position;
            return "R17 in-place vs root-motion body (heading-local canonical): max " + degrees.ToString("0.000", CultureInfo.InvariantCulture) + " deg, "
                + (metres * 100f).ToString("0.000", CultureInfo.InvariantCulture) + " cm; root travel in-place "
                + Math.Sqrt(travelA.X * travelA.X + travelA.Z * travelA.Z).ToString("0.000", CultureInfo.InvariantCulture) + " m vs root-motion "
                + Math.Sqrt(travelB.X * travelB.X + travelB.Z * travelB.Z).ToString("0.000", CultureInfo.InvariantCulture) + " m\n" + detail;
        }

        [Test, Category("RealAssets")]
        public void NeophyteMixamoMapAndRoundTrip()
        {
            var clip = RequireTake(SyntyJump, "A_Jump_Walking_RootMotionVertical_Masc_Full");
            var character = Spawn(NeophyteCharacter);
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(character, out var plan, out var error), error);
                AssertRequired(plan);
                var log = new StringBuilder();
                AssertMixamoMap(plan, out var differences, log);
                var raw = Measure(plan, clip, false, true);
                var packed = Measure(plan, clip, true, false);
                Report("neophyte", PlanSummary(plan) + "I3 map differences vs RealCorpusSweep table: " + differences + "\n" + log
                    + Describe("I1 sample->apply", raw) + Describe("I1 packed .saclip", packed));
                Assert.AreEqual(0, differences, log.ToString());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        static void AddCurves(AnimationClip clip, string path, Func<float, Vector3> position, Func<float, UnityEngine.Quaternion> rotation, float seconds)
        {
            var curves = new AnimationCurve[7];
            for (var i = 0; i < curves.Length; i++)
                curves[i] = new AnimationCurve();
            var previous = UnityEngine.Quaternion.identity;
            for (var f = 0; f <= Mathf.RoundToInt(seconds * Hz); f++)
            {
                var time = f / Hz;
                if (position != null)
                {
                    var p = position(time);
                    curves[0].AddKey(time, p.x);
                    curves[1].AddKey(time, p.y);
                    curves[2].AddKey(time, p.z);
                }
                if (rotation != null)
                {
                    var q = rotation(time);
                    if (UnityEngine.Quaternion.Dot(q, previous) < 0f)
                        q = new UnityEngine.Quaternion(-q.x, -q.y, -q.z, -q.w);
                    previous = q;
                    curves[3].AddKey(time, q.x);
                    curves[4].AddKey(time, q.y);
                    curves[5].AddKey(time, q.z);
                    curves[6].AddKey(time, q.w);
                }
            }
            string[] names = { "localPosition.x", "localPosition.y", "localPosition.z", "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w" };
            for (var i = 0; i < names.Length; i++)
                if (curves[i].length > 0)
                    clip.SetCurve(path, typeof(Transform), names[i], curves[i]);
        }

        static GameObject MovingParentRig(out Dictionary<string, Transform> lookup, out Transform armature)
        {
            var rig = MixamoRig(out lookup);
            armature = new GameObject("Armature").transform;
            armature.SetParent(rig.transform, false);
            lookup["Hips"].SetParent(armature, true);
            return rig;
        }

        static void AssertSweepPasses(FamilyRigPlan plan, AnimationClip clip, bool loop)
        {
            var row = new ClipSweepRow();
            Assert.IsTrue(FamilyClipSweep.TryMeasure(plan, clip, Hz, 1f, ClipSweepLimits.Default, row, out var error), error);
            Report("sweep-" + clip.name, FamilyClipSweep.Header + "\n" + FamilyClipSweep.ToCsv(row) + "\n");
            Assert.AreEqual(FamilyClipSweep.Pass, row.Status, row.Reason);
            Assert.AreEqual(loop, row.EncodedLoop);
            Assert.AreEqual(clip.length, row.EncodedDuration, 1e-6);
            Assert.LessOrEqual(row.Degrees, 0.1f, row.WorstBone);
            Assert.LessOrEqual(row.Centimetres, 0.1f);
        }

        [Test]
        public void SampleTimesSpanTheExactClipLength()
        {
            var clip = new AnimationClip { frameRate = Hz };
            clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1.05f, 1f));
            try
            {
                var frames = FamilyClips.SampleCount(clip, Hz);
                Assert.AreEqual((int)Math.Round(clip.length * (double)Hz) + 1, frames);
                Assert.AreEqual(0.0, FamilyClips.SampleSeconds(clip, frames, 0));
                Assert.AreEqual(clip.length, FamilyClips.SampleSeconds(clip, frames, frames - 1), 1e-6);
                Assert.AreEqual(clip.length, FamilyClips.EncodedDuration(clip, Hz), 1e-6);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void AdaptiveSamplingRaisesTheRateOnlyWhereInBetweensMissUnity()
        {
            var rig = MixamoRig(out var lookup);
            AnimationClip slow = null;
            AnimationClip fast = null;
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                slow = SyntheticClip(lookup, rig.transform);
                fast = SyntheticClip(lookup, rig.transform);
                fast.name = "W1Fast";
                var arm = lookup["LeftForeArm"];
                var rest = arm.localRotation;
                AddCurves(fast, AnimationUtility.CalculateTransformPath(arm, rig.transform), null,
                    t => rest * UnityEngine.Quaternion.AngleAxis(80f * Mathf.Sin(2f * Mathf.PI * 4f * t), Vector3.forward), 1f);
                foreach (var binding in AnimationUtility.GetCurveBindings(fast))
                {
                    var curve = AnimationUtility.GetEditorCurve(fast, binding);
                    for (var k = 0; k < curve.length; k++)
                    {
                        AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Auto);
                        AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Auto);
                    }
                    AnimationUtility.SetEditorCurve(fast, binding, curve);
                }
                Assert.IsTrue(FamilyClips.TrySampleAdaptive(plan, slow, Hz, FamilyClips.InBetweenDegrees, FamilyClips.MaxRateMultiplier,
                    out _, out _, out _, out var slowFrames, out var slowReport, out error), error);
                Assert.IsTrue(FamilyClips.TrySampleAdaptive(plan, fast, Hz, FamilyClips.InBetweenDegrees, FamilyClips.MaxRateMultiplier,
                    out var canonical, out var root, out _, out var fastFrames, out var fastReport, out error), error);
                Report("adaptive", "slow x" + slowReport.Multiplier + " " + slowFrames + " frames, est " + slowReport.InBetweenDegrees + " deg; fast x"
                    + fastReport.Multiplier + " " + fastFrames + " frames, est " + fastReport.InBetweenDegrees + " deg\n");
                Assert.AreEqual(1, slowReport.Multiplier);
                Assert.AreEqual(31, slowFrames);
                Assert.GreaterOrEqual(fastReport.Multiplier, 2);
                Assert.AreEqual(fastReport.Multiplier * 30 + 1, fastFrames);
                Assert.IsTrue(FamilyClips.TryEncode(plan, canonical, root, Hz, out var bytes, out error), error);
                Assert.AreEqual(AnimationError.None, SharedClipReader.Load(bytes, plan.Family, SharedFamilyBake.CanonicalReferencePoses(), out var loaded));
                Assert.AreEqual(fast.length, loaded.Duration, 1e-6, "TryEncode(hz) keeps the clip length after adaptive sampling");
                var fixedRow = new ClipSweepRow();
                Assert.IsTrue(FamilyClipSweep.TryMeasure(plan, fast, Hz, 1, 1f, ClipSweepLimits.Default, fixedRow, out error), error);
                var adaptiveRow = new ClipSweepRow();
                Assert.IsTrue(FamilyClipSweep.TryMeasure(plan, fast, Hz, 1f, ClipSweepLimits.Default, adaptiveRow, out error), error);
                Report("adaptive-sweep", FamilyClipSweep.Header + "\n" + FamilyClipSweep.ToCsv(fixedRow) + "\n" + FamilyClipSweep.ToCsv(adaptiveRow) + "\n");
                Assert.AreEqual(FamilyClipSweep.Pass, fixedRow.Status, fixedRow.Reason);
                Assert.AreEqual(FamilyClipSweep.Pass, adaptiveRow.Status, adaptiveRow.Reason);
                Assert.Greater(fixedRow.MidDegrees, FamilyClips.InBetweenDegrees);
                Assert.LessOrEqual(adaptiveRow.MidDegrees, FamilyClips.InBetweenDegrees + 0.1f, adaptiveRow.MidWorst);
            }
            finally
            {
                if (slow != null)
                    UnityEngine.Object.DestroyImmediate(slow);
                if (fast != null)
                    UnityEngine.Object.DestroyImmediate(fast);
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void LoopingClipWhoseEndDiffersFromItsStartEncodes()
        {
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            var samples = new Pose[9 * FamilyClips.CanonicalBones];
            for (var f = 0; f < 9; f++)
                for (var c = 0; c < FamilyClips.CanonicalBones; c++)
                {
                    var pose = reference[c];
                    if (c == 11)
                        pose.Rotation = Quaternion.Normalize(Quaternion.Multiply(pose.Rotation, Quaternion.AxisAngle(new Float3(0f, 0f, 1f), 0.15f * f)));
                    samples[f * FamilyClips.CanonicalBones + c] = pose;
                }
            Assert.IsTrue(SharedFamilyBake.EncodeCanonicalSamples(samples, 9, 1.0, true, out var bytes, out var error), error);
            Assert.Greater(bytes.Length, 0);
        }

        [Test]
        public void TranslationOffsetsRoundTripThroughTheCanonicalFrame()
        {
            var targetParent = Quaternion.Normalize(new Quaternion(0.2f, -0.4f, 0.1f, 0.9f));
            var canonicalParent = Quaternion.Normalize(new Quaternion(-0.3f, 0.1f, 0.5f, 0.8f));
            var offset = new Float3(0.03f, -0.02f, 0.05f);
            var canonical = CanonicalTranslation.ToCanonical(targetParent, canonicalParent, offset, 1.7f);
            var back = CanonicalTranslation.ToTarget(targetParent, canonicalParent, canonical, 1.7f);
            Assert.Less(SharedFamilyBake.Distance(offset, back), 1e-6f);
            Assert.AreEqual(0f, CanonicalTranslation.ToTarget(targetParent, canonicalParent, offset, 0f).LengthSquared);
        }

        [Test]
        public void HipsUnderARotatingUnmappedParentMatchUnity()
        {
            var rig = MovingParentRig(out var lookup, out var armature);
            Avatar avatar = null;
            AnimationClip clip = null;
            try
            {
                avatar = BuildAvatar(rig);
                rig.AddComponent<Animator>().avatar = avatar;
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                clip = SyntheticClip(lookup, rig.transform);
                clip.name = "W1MovingParent";
                AddCurves(clip, "Armature", t => new Vector3(0.4f * t, 0f, 0.9f * t), t => UnityEngine.Quaternion.AngleAxis(120f * t, Vector3.up), 1f);
                AssertSweepPasses(plan, clip, false);
            }
            finally
            {
                if (clip != null)
                    UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(rig);
                if (avatar != null)
                    UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void BoneTranslationAndLoopFlagMatchUnity()
        {
            var rig = MixamoRig(out var lookup);
            Avatar avatar = null;
            AnimationClip clip = null;
            try
            {
                avatar = BuildAvatar(rig);
                rig.AddComponent<Animator>().avatar = avatar;
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                clip = SyntheticClip(lookup, rig.transform);
                clip.name = "W1Translation";
                var spine = lookup["Spine"];
                var rest = spine.localPosition;
                AddCurves(clip, AnimationUtility.CalculateTransformPath(spine, rig.transform), t => rest + new Vector3(0.03f * Mathf.Sin(6f * t), 0.02f * t, 0f), null, 1f);
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                AssertSweepPasses(plan, clip, true);
            }
            finally
            {
                if (clip != null)
                    UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(rig);
                if (avatar != null)
                    UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void TimelineSystemPlaysTheSaclipLikeTheDecoderAndUnity()
        {
            var rig = MovingParentRig(out var lookup, out _);
            Avatar avatar = null;
            AnimationClip clip = null;
            try
            {
                avatar = BuildAvatar(rig);
                rig.AddComponent<Animator>().avatar = avatar;
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                clip = SyntheticClip(lookup, rig.transform);
                clip.name = "W1Runtime";
                AddCurves(clip, "Armature", t => new Vector3(0.2f * t, 0f, 1.1f * t), t => UnityEngine.Quaternion.AngleAxis(-70f * t, Vector3.up), 1f);
                Assert.IsTrue(FamilyClipRuntimeCheck.TryMeasure(plan, clip, Hz, 10, FamilyClipRuntimeCheck.AttachLikeRuntime, out var result, out error), error);
                Report("runtime-synthetic", "samples " + result.Samples + ", truth " + result.TruthDegrees.ToString("0.0000", CultureInfo.InvariantCulture) + " deg "
                    + result.TruthCentimetres.ToString("0.0000", CultureInfo.InvariantCulture) + " cm (" + result.Worst + "), decoder "
                    + result.DecoderDegrees.ToString("0.000000", CultureInfo.InvariantCulture) + " deg " + result.DecoderCentimetres.ToString("0.000000", CultureInfo.InvariantCulture) + " cm\n");
                Assert.AreEqual(20, result.Samples);
                Assert.LessOrEqual(result.TruthDegrees, 1f, result.Worst);
                Assert.LessOrEqual(result.TruthCentimetres, 1f);
                Assert.LessOrEqual(result.DecoderDegrees, 0.01f);
                Assert.LessOrEqual(result.DecoderCentimetres, 0.01f);
            }
            finally
            {
                if (clip != null)
                    UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(rig);
                if (avatar != null)
                    UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        static AnimationClip MuscleClip(string name, float seconds, params (string muscle, Func<float, float> value)[] muscles)
        {
            var clip = new AnimationClip { name = name, frameRate = Hz };
            foreach (var (muscle, value) in muscles)
            {
                var curve = new AnimationCurve();
                for (var f = 0; f <= Mathf.RoundToInt(seconds * Hz); f++)
                    curve.AddKey(f / Hz, value(f / Hz));
                for (var k = 0; k < curve.length; k++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
                }
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), muscle), curve);
            }
            return clip;
        }

        static GameObject HumanoidRig(float twist, out Avatar avatar)
        {
            var rig = MixamoRig(out _);
            avatar = BuildAvatar(rig, twist);
            rig.AddComponent<Animator>().avatar = avatar;
            return rig;
        }

        static float WorstCanonicalDegrees(Pose[] a, Pose[] b, int canonical)
        {
            var worst = 0f;
            for (var i = canonical; i < a.Length && i < b.Length; i += FamilyClips.CanonicalBones)
                worst = Math.Max(worst, Degrees(a[i].Rotation, b[i].Rotation));
            return worst;
        }

        [Test]
        public void PerBoneTwistPlanSamplesLikeAnAvatarWhoseTwistSettingsAreOne()
        {
            var half = HumanoidRig(0.5f, out var halfAvatar);
            var one = HumanoidRig(1f, out var oneAvatar);
            var clip = MuscleClip("W1Twist", 1f,
                ("Left Arm Twist In-Out", t => 0.8f * Mathf.Sin(2f * Mathf.PI * t)),
                ("Left Forearm Twist In-Out", t => -0.7f * Mathf.Sin(2f * Mathf.PI * t)),
                ("Left Upper Leg Twist In-Out", t => 0.6f * Mathf.Sin(2f * Mathf.PI * t)),
                ("Left Lower Leg Twist In-Out", t => -0.5f * Mathf.Sin(2f * Mathf.PI * t)),
                ("Left Arm Down-Up", t => 0.4f * Mathf.Cos(2f * Mathf.PI * t)),
                ("Left Forearm Stretch", t => 0.3f * t));
            FamilyRigPlan perBone = null;
            try
            {
                Assert.IsTrue(clip.humanMotion);
                Assert.IsTrue(FamilyClips.TryPlan(half, HumanoidTwist.PerBone, out perBone, out var error), error);
                Assert.IsTrue(FamilyClips.TryPlan(one, out var reference, out error), error);
                Assert.IsTrue(FamilyClips.TryPlan(half, out var avatarTwist, out error), error);
                Assert.AreEqual(HumanoidTwist.PerBone, perBone.Twist);
                Assert.IsNotNull(perBone.SamplingAvatar);
                Assert.IsNull(avatarTwist.SamplingAvatar);
                Assert.IsTrue(FamilyClips.TrySampleFixed(perBone, clip, Hz, out var p, out _, out _, out _, out error), error);
                Assert.IsTrue(FamilyClips.TrySampleFixed(reference, clip, Hz, out var r, out _, out _, out _, out error), error);
                Assert.IsTrue(FamilyClips.TrySampleFixed(avatarTwist, clip, Hz, out var a, out _, out _, out _, out error), error);
                var text = new StringBuilder();
                var worstMatch = 0f;
                for (var c = 1; c < FamilyClips.CanonicalBones; c++)
                    worstMatch = Math.Max(worstMatch, WorstCanonicalDegrees(p, r, c));
                var upperArm = WorstCanonicalDegrees(p, a, 11);
                var lowerArm = WorstCanonicalDegrees(p, a, 12);
                var upperLeg = WorstCanonicalDegrees(p, a, 48);
                var lowerLeg = WorstCanonicalDegrees(p, a, 49);
                text.Append("per-bone vs avatar twist 1: ").Append(worstMatch.ToString("0.0000", CultureInfo.InvariantCulture))
                    .Append(" deg; per-bone vs avatar twist 0.5: upper arm ").Append(upperArm.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(", lower arm ").Append(lowerArm.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(", upper leg ").Append(upperLeg.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(", lower leg ").Append(lowerLeg.ToString("0.0", CultureInfo.InvariantCulture)).Append(" deg\n");
                var row = new ClipSweepRow();
                Assert.IsTrue(FamilyClipSweep.TryMeasure(perBone, clip, Hz, 1f, ClipSweepLimits.Default, row, out error), error);
                text.Append(FamilyClipSweep.Header).Append('\n').Append(FamilyClipSweep.ToCsv(row)).Append('\n');
                Report("per-bone-twist", text.ToString());
                Assert.LessOrEqual(worstMatch, 0.01f);
                Assert.Greater(upperArm, 5f);
                Assert.Greater(lowerArm, 5f);
                Assert.Greater(upperLeg, 5f);
                Assert.Greater(lowerLeg, 5f);
                Assert.AreEqual(FamilyClipSweep.Pass, row.Status, row.Reason);
                var built = perBone.SamplingAvatar;
                perBone.Dispose();
                Assert.IsNull(perBone.SamplingAvatar);
                Assert.IsTrue(built == null);
            }
            finally
            {
                perBone?.Dispose();
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(half);
                UnityEngine.Object.DestroyImmediate(one);
                UnityEngine.Object.DestroyImmediate(halfAvatar);
                UnityEngine.Object.DestroyImmediate(oneAvatar);
            }
        }

        [Test]
        public void JumpRuleExcludesOnlyTurnsNoSampleRateFollows()
        {
            var rig = HumanoidRig(0.5f, out var avatar);
            var fast = MuscleClip("W1FastInRange", 1f,
                ("Left Arm Twist In-Out", t => 0.95f * Mathf.Sin(2f * Mathf.PI * 3f * t)),
                ("Spine Twist Left-Right", t => 0.9f * Mathf.Sin(2f * Mathf.PI * 2f * t)));
            var beyond = MuscleClip("W1BeyondLimit", 1f,
                ("Left Arm Twist In-Out", t => 3f * t),
                ("Spine Twist Left-Right", t => 0.3f * Mathf.Sin(2f * Mathf.PI * t)));
            FamilyRigPlan perBone = null;
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(rig, out var plan, out var error), error);
                Assert.IsTrue(FamilyClips.TryPlan(rig, HumanoidTwist.PerBone, out perBone, out error), error);
                var fastRow = new ClipSweepRow();
                var beyondRow = new ClipSweepRow();
                var perBoneRow = new ClipSweepRow();
                Assert.IsTrue(FamilyClipSweep.TryMeasure(plan, fast, Hz, 1f, ClipSweepLimits.Default, fastRow, out error), error);
                Assert.IsTrue(FamilyClipSweep.TryMeasure(plan, beyond, Hz, 1f, ClipSweepLimits.Default, beyondRow, out error), error);
                Assert.IsTrue(FamilyClipSweep.TryMeasure(perBone, beyond, Hz, 1f, ClipSweepLimits.Default, perBoneRow, out error), error);
                Report("jump-rule", FamilyClipSweep.Header + "\n" + FamilyClipSweep.ToCsv(fastRow) + "\n" + FamilyClipSweep.ToCsv(beyondRow) + "\n" + FamilyClipSweep.ToCsv(perBoneRow) + "\n");
                Assert.AreEqual(FamilyClipSweep.Pass, fastRow.Status, fastRow.Reason);
                Assert.AreEqual(FamilyClipSweep.Pass, beyondRow.Status, beyondRow.Reason);
                Assert.AreEqual(FamilyClipSweep.Pass, perBoneRow.Status, perBoneRow.Reason);
                Assert.AreEqual(0, fastRow.JumpIntervals, fastRow.JumpEvidence);
                Assert.LessOrEqual(fastRow.MidDegrees, FamilyClips.InBetweenDegrees + 0.1f, fastRow.MidWorst);
                Assert.GreaterOrEqual(beyondRow.JumpIntervals, 1);
                Assert.Greater(beyondRow.MidJumpDegrees, FamilyClips.JumpDegrees);
                Assert.LessOrEqual(beyondRow.MidDegrees, FamilyClips.InBetweenDegrees + 0.1f, beyondRow.MidWorst);
                StringAssert.StartsWith(FamilyClipSweep.BeyondLimit + ":" + beyondRow.JumpIntervals + "@", beyondRow.JumpKinds);
                StringAssert.Contains("Left Arm Twist In-Out", beyondRow.JumpEvidence);
                Assert.AreEqual(0, perBoneRow.JumpIntervals, perBoneRow.JumpEvidence);
                var source = FamilyClipSweep.SourceOf(beyond, 0.6, 0.7, (int)HumanBodyBones.LeftUpperArm);
                Assert.AreEqual(FamilyClipSweep.BeyondLimit, source.Kind);
                Assert.AreEqual("Left Arm Twist In-Out", source.Extreme);
                Assert.AreEqual(2.1f, source.ExtremeValue, 1e-3f);
                Assert.AreEqual(FamilyClipSweep.InRange, FamilyClipSweep.SourceOf(fast, 0.1, 0.1 + 1.0 / Hz, (int)HumanBodyBones.LeftUpperArm).Kind);
            }
            finally
            {
                perBone?.Dispose();
                UnityEngine.Object.DestroyImmediate(fast);
                UnityEngine.Object.DestroyImmediate(beyond);
                UnityEngine.Object.DestroyImmediate(rig);
                UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        static AnimationClip KeyedMuscleClip(string name, params (string muscle, (float time, float value)[] keys)[] muscles)
        {
            var clip = new AnimationClip { name = name, frameRate = Hz };
            foreach (var (muscle, keys) in muscles)
            {
                var curve = new AnimationCurve();
                foreach (var (time, value) in keys)
                    curve.AddKey(time, value);
                for (var k = 0; k < curve.length; k++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
                }
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), muscle), curve);
            }
            return clip;
        }

        [Test]
        public void JumpRuleCoversATurnThatCrossesAFrameBoundary()
        {
            var rig = HumanoidRig(0.5f, out var avatar);
            var clips = new List<AnimationClip>();
            FamilyRigPlan plan = null;
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(rig, out plan, out var error), error);
                var report = new StringBuilder(FamilyClipSweep.Header + "\n");
                var framesWithANeighbour = 0;
                for (var eighth = 0; eighth < 8; eighth++)
                {
                    var spinStart = (10f + eighth / 8f) / Hz;
                    var clip = KeyedMuscleClip("W1HeadSpin" + eighth,
                        ("Head Nod Down-Up", new[] { (0f, -4.44f), (1f, -4.44f) }),
                        ("Head Tilt Left-Right", new[] { (0f, 8.5f), (spinStart, 8.5f), (spinStart + 1f / Hz, 4.05f), (spinStart + 2f / Hz, -0.04f), (1f, -0.04f) }));
                    clips.Add(clip);
                    var row = new ClipSweepRow { Take = clip.name };
                    Assert.IsTrue(FamilyClipSweep.TryMeasure(plan, clip, Hz, 1f, ClipSweepLimits.Default, row, out error), error);
                    report.Append(FamilyClipSweep.ToCsv(row)).Append('\n');
                    Assert.AreEqual(FamilyClipSweep.Pass, row.Status, row.Reason);
                    Assert.GreaterOrEqual(row.JumpIntervals, 1, clip.name);
                    Assert.Greater(row.MidJumpDegrees, FamilyClips.InBetweenDegrees, clip.name);
                    Assert.LessOrEqual(row.MidDegrees, FamilyClips.InBetweenDegrees + 0.1f, clip.name + " " + row.MidWorst);
                    StringAssert.StartsWith(FamilyClipSweep.BeyondLimit + ":" + row.JumpIntervals + "@", row.JumpKinds, clip.name);
                    StringAssert.Contains("Head Tilt Left-Right", row.JumpEvidence, clip.name);
                    if (row.JumpIntervals > 1)
                        framesWithANeighbour++;
                }
                Report("jump-boundary", report.ToString());
                Assert.Greater(framesWithANeighbour, 0, report.ToString());
            }
            finally
            {
                plan?.Dispose();
                foreach (var clip in clips)
                    UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(rig);
                UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        [Test, Explicit, Category("RealAssets")]
        public void CorpusSweepPassesEveryHumanoidClip()
        {
            var folders = new List<string>();
            foreach (var folder in AssetDatabase.GetSubFolders("Assets"))
                if (Path.GetFileName(folder).StartsWith("zz_corpus_", StringComparison.Ordinal))
                    folders.Add(folder);
            if (folders.Count == 0)
                Assert.Ignore("no Assets/zz_corpus_* folders");
            var characters = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("PolygonSyntyCharacter t:Model", folders.ToArray()))
                characters.Add(AssetDatabase.GUIDToAssetPath(guid));
            if (characters.Count == 0)
                Assert.Ignore("Synty character missing");
            var csv = Path.Combine(Application.dataPath, "..", "Temp", "sa", "sweep", "test-sweep.csv");
            var report = FamilyClipSweep.Run(characters.GetRange(0, 1).ToArray(), null, folders.ToArray(), csv, 0, int.MaxValue, false);
            var failed = 0;
            var first = string.Empty;
            foreach (var line in File.ReadAllLines(csv))
                if (line.Contains("," + FamilyClipSweep.Fail + ","))
                {
                    failed++;
                    if (first.Length == 0)
                        first = line;
                }
            Report("corpus", report + "failed " + failed + "\n" + first + "\n");
            Assert.AreEqual(0, failed, first);
        }
    }
}
