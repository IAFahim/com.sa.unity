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
        const string SyntyCharacter = "Assets/Synty/AnimationBaseLocomotion/Meshes/PolygonSyntyCharacter.fbx";
        const string SyntyClips = "Assets/Synty/AnimationBaseLocomotion/Animations/Polygon/Masculine/InAir/";
        const string NeophyteCharacter = "Assets/zz_w1_test/Neophyte_RIG.fbx";

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

        static Avatar BuildAvatar(GameObject root)
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
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
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
            Assert.IsTrue(FamilyClips.TrySample(plan, clip, Hz, out var canonical, out var root, out var start, out var sampled, out error), error);
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
                    Assert.AreEqual(AnimationError.None, loaded.Sample(f / (double)Hz, reference, probe));
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
                Assert.IsTrue(FamilyClips.TrySample(plan, clip, Hz, out var canonical, out var root, out var frames, out error), error);
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

        static GameObject Spawn(string path)
        {
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
            var clip = Take(SyntyClips + "A_Jump_Walking_RootMotionVertical_Masc.fbx", "A_Jump_Walking_RootMotionVertical_Masc_Full");
            if (clip == null)
                Assert.Ignore("Synty jump clip missing");
            var character = Spawn(SyntyCharacter);
            try
            {
                Assert.IsTrue(FamilyClips.TryPlan(character, out var plan, out var error), error);
                AssertRequired(plan);
                var raw = Measure(plan, clip, false, true);
                var packed = Measure(plan, clip, true, false);
                Assert.IsTrue(FamilyClips.TrySample(plan, clip, Hz, out var canonical, out var root, out var frames, out error), error);
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
            var inPlace = Take(SyntyClips + "A_Jump_Walking_Masc.fbx", "A_Jump_Walking_Masc");
            var moving = Take(SyntyClips + "A_Jump_Walking_RootMotion_Masc.fbx", "A_Jump_Walking_RootMotion_Masc");
            if (inPlace == null || moving == null)
                return "R17 variants missing\n";
            Assert.IsTrue(FamilyClips.TrySample(plan, inPlace, Hz, out var a, out var rootA, out var framesA, out var error), error);
            Assert.IsTrue(FamilyClips.TrySample(plan, moving, Hz, out var b, out var rootB, out var framesB, out error), error);
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
            var clip = Take(SyntyClips + "A_Jump_Walking_RootMotionVertical_Masc.fbx", "A_Jump_Walking_RootMotionVertical_Masc_Full");
            if (clip == null)
                Assert.Ignore("Synty jump clip missing");
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
    }
}
