using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SA.Editor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace SA.Tests
{
    public class BakeAndGpuTests
    {
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in created)
                if (item != null)
                    Object.DestroyImmediate(item);
            created.Clear();
        }

        Transform Node(string name, Transform parent, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject(name);
            if (parent == null)
                created.Add(go);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            return go.transform;
        }

        AnimationClip TestClip()
        {
            var clip = new AnimationClip { name = "Wave" };
            created.Add(clip);
            clip.SetCurve("Hips/Arm", typeof(Transform), "localEulerAnglesRaw.z", AnimationCurve.Linear(0f, 0f, 1f, 90f));
            clip.SetCurve("Hips", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Linear(0f, 1f, 1f, 2f));
            return clip;
        }

        [Test]
        public void SkeletonWithoutSkinsIncludesAllDescendantsAndOrdersParentsFirst()
        {
            var root = Node("Root", null, Vector3.zero, Quaternion.identity);
            var hips = Node("Hips", root, Vector3.up, Quaternion.Euler(10, 20, 30));
            Node("Arm", hips, Vector3.right, Quaternion.Euler(0, 45, 0));
            Node("Prop", root, Vector3.forward, Quaternion.identity);
            var errors = new List<string>();
            var skeleton = RigBuilder.Skeleton(root, new[] { TestClip() }, new SkinAuthoring[0], errors);
            Assert.IsEmpty(errors);
            Assert.AreEqual(new[] { "Hips", "Arm", "Prop" }, skeleton.ConvertAll(t => t.name).ToArray());
        }

        [Test]
        public void BakedFramesEqualUnitySampleAnimationIncludingEulerCurves()
        {
            var root = Node("Root", null, new Vector3(5, 0, 0), Quaternion.Euler(0, 90, 0));
            var hips = Node("Hips", root, Vector3.up, Quaternion.Euler(10, 20, 30));
            var arm = Node("Arm", hips, Vector3.right, Quaternion.Euler(0, 45, 0));
            var clip = TestClip();
            var errors = new List<string>();
            var skeleton = RigBuilder.Skeleton(root, new[] { clip }, new SkinAuthoring[0], errors);
            var rigId = RigBuilder.RigId(root, skeleton);
            var rig = RigBuilder.BuildRig(skeleton, rigId);
            var sampled = RigBuilder.BuildClip(root, skeleton, clip, 30f, rigId, RigBuilder.ClipKey(root, clip, 30f, rigId));
            try
            {
                ref var baked = ref sampled.Value;
                Assert.AreEqual(31, baked.FrameCount);
                Assert.AreEqual(2, rig.Value.Bones.Length);
                foreach (var frame in new[] { 0, 7, 30 })
                {
                    clip.SampleAnimation(root.gameObject, frame / 30f);
                    Assert.Less(TestRigs.Distance(baked.Samples[frame * 2 + 0], RigBuilder.Local(hips)), 1e-5f);
                    Assert.Less(TestRigs.Distance(baked.Samples[frame * 2 + 1], RigBuilder.Local(arm)), 1e-5f);
                }
                Assert.Less(TestRigs.Distance(baked.Reference[1], baked.Samples[1]), 1e-6f);
            }
            finally
            {
                sampled.Dispose();
                rig.Dispose();
            }
        }

        [Test]
        public void ARootObjectBouncedByTheClipLiftsTheBakedTopBones()
        {
            var root = Node("Root", null, new Vector3(5, 0, 0), Quaternion.Euler(0, 90, 0));
            var hips = Node("Hips", root, Vector3.up, Quaternion.identity);
            Node("Arm", hips, Vector3.right, Quaternion.identity);
            var clip = new AnimationClip { name = "Bounce" };
            created.Add(clip);
            clip.SetCurve("", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Linear(0f, 0f, 1f, 0.5f));
            clip.SetCurve("Hips/Arm", typeof(Transform), "localEulerAnglesRaw.z", AnimationCurve.Linear(0f, 0f, 1f, 10f));
            var errors = new List<string>();
            var skeleton = RigBuilder.Skeleton(root, new[] { clip }, new SkinAuthoring[0], errors);
            var rigId = RigBuilder.RigId(root, skeleton);
            var sampled = RigBuilder.BuildClip(root, skeleton, clip, 30f, rigId, RigBuilder.ClipKey(root, clip, 30f, rigId));
            try
            {
                ref var baked = ref sampled.Value;
                Assert.AreEqual(1f, baked.Samples[0].Position.y, 1e-5f);
                Assert.AreEqual(1.25f, baked.Samples[15 * 2].Position.y, 1e-4f);
                Assert.AreEqual(1.5f, baked.Samples[30 * 2].Position.y, 1e-5f);
                Assert.AreEqual(1f, baked.Samples[30 * 2 + 1].Position.x, 1e-5f);
            }
            finally
            {
                sampled.Dispose();
            }
        }

        [Test]
        public void InfluencesPackToFourWeightsThatSumExactly()
        {
            var packed = RigBuilder.Pack(new[]
            {
                new BoneWeight1 { boneIndex = 7, weight = 0.5f },
                new BoneWeight1 { boneIndex = 3, weight = 0.3f },
                new BoneWeight1 { boneIndex = 1, weight = 0.15f },
                new BoneWeight1 { boneIndex = 9, weight = 0.04f },
                new BoneWeight1 { boneIndex = 2, weight = 0.01f }
            });
            Assert.AreEqual(7u, packed.x & 0xFFFF);
            Assert.AreEqual(9u, packed.y >> 16);
            Assert.AreEqual(65535u, (packed.z & 0xFFFF) + (packed.z >> 16) + (packed.w & 0xFFFF) + (packed.w >> 16));
        }

        [Test]
        public void MaskWeightInheritsFromNearestListedAncestor()
        {
            var root = Node("Root", null, Vector3.zero, Quaternion.identity);
            var hips = Node("Hips", root, Vector3.up, Quaternion.identity);
            var arm = Node("Arm", hips, Vector3.right, Quaternion.identity);
            var active = new Dictionary<string, bool> { { "Hips", false } };
            Assert.AreEqual(0f, RigBuilder.MaskWeight(root, arm, active));
            active["Hips/Arm"] = true;
            Assert.AreEqual(1f, RigBuilder.MaskWeight(root, arm, active));
            Assert.AreEqual(1f, RigBuilder.MaskWeight(root, hips, new Dictionary<string, bool>()));
        }

        [Test]
        public void GpuMatchesCpuEvaluationAndSkinning()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("compute shaders unavailable");
            var rig = TestRigs.Build(5, 0.8f);
            var skinBuilder = new BlobBuilder(Allocator.Temp);
            ref var skinRoot = ref skinBuilder.ConstructRoot<SkinBlob>();
            skinRoot.Id = new Hash128(9, 9, 9, 9);
            skinRoot.MeshFromRig = PoseAlgebra.Affine(new BonePose { Position = new float3(0.2f, 0f, 0f), Rotation = quaternion.RotateZ(0.3f), Scale = 1f });
            var skinBones = skinBuilder.Allocate(ref skinRoot.Bones, 2);
            skinBones[0] = 1;
            skinBones[1] = 0;
            var bindposes = skinBuilder.Allocate(ref skinRoot.Bindposes, 2);
            bindposes[0] = PoseAlgebra.Affine(TestRigs.Pose(-1f, quaternion.RotateX(0.5f)));
            bindposes[1] = PoseAlgebra.Affine(GpuFrame.Identity);
            skinBuilder.Allocate(ref skinRoot.Influences, 1);
            var skin = skinBuilder.CreateBlobAssetReference<SkinBlob>(Allocator.Persistent);
            skinBuilder.Dispose();
            var world = new World("SA.Parity");
            var pipeline = new GpuPipeline();
            try
            {
                var states = world.EntityManager.AddBuffer<ClipState>(world.EntityManager.CreateEntity());
                Playback.Blend(ref states, new[] { TestRigs.Clip(rig, 0), TestRigs.Clip(rig, 1) }, new[] { 0.35f, 0.65f }, 0f, PlayOptions.Default);
                Playback.CrossFade(ref states, TestRigs.Clip(rig, 1), 1f, PlayOptions.Default.WithLayer(1).WithMask(TestRigs.Mask(rig, 1)).WithWrap(Wrap.PingPong));
                Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default.WithLayer(2).WithMode(ClipMode.Additive).WithWrap(Wrap.Clamp));
                Playback.Step(ref states, rig.Clips, 0.43f);
                rig.Register(pipeline.Registry);
                pipeline.Registry.Add(skin);
                var record = pipeline.Registry.Rigs[rig.Value.Id];
                var motions = new NativeList<GpuMotion>(Allocator.Temp);
                for (var i = 0; i < states.Length; i++)
                    if (Evaluation.Contributes(ref rig.Value, rig.Clips, rig.Masks, states[i]))
                        motions.Add(GpuFrame.Motion(ref rig.Clips[states[i].Clip.Index].Value.Value, states[i], pipeline.Registry.ClipSamples[rig.Clips[states[i].Clip.Index].Value.Value.Id], pipeline.Registry.MaskBases[rig.Masks[states[i].Mask.Index].Value.Value.Id]));
                var instances = new NativeArray<GpuInstance>(1, Allocator.Temp);
                instances[0] = new GpuInstance { BoneBase = (uint)record.BoneBase, BoneCount = 2, PoseBase = 0, MotionBase = 0, MotionCount = (uint)motions.Length };
                var jobs = new NativeArray<GpuSkinJob>(1, Allocator.Temp);
                jobs[0] = new GpuSkinJob { Skin = 0, PoseBase = 0, OutputBase = 1 };
                pipeline.Execute(instances, motions.AsArray(), new NativeArray<float4x4>(0, Allocator.Temp), 2, jobs, 3);
                var poses = new float4x4[2];
                var matrices = new float4x4[3];
                pipeline.Poses.Buffer.GetData(poses, 0, 0, 2);
                pipeline.SkinMatrices.Buffer.GetData(matrices, 0, 0, 3);

                var view = states.AsNativeArray().AsReadOnlySpan();
                var model0 = PoseAlgebra.Affine(Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, view, 0));
                var model1 = PoseAlgebra.Mul(model0, PoseAlgebra.Affine(Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, view, 1)));
                AssertNear(model0, poses[0]);
                AssertNear(model1, poses[1]);
                AssertNear(PoseAlgebra.Mul(PoseAlgebra.Mul(skin.Value.MeshFromRig, model1), skin.Value.Bindposes[0]), matrices[1]);
                AssertNear(PoseAlgebra.Mul(skin.Value.MeshFromRig, model0), matrices[2]);
            }
            finally
            {
                pipeline.Dispose();
                world.Dispose();
                rig.Dispose();
                skin.Dispose();
            }
        }

        static void AssertNear(float3x4 expected, float4x4 actual)
        {
            var packed = GpuFrame.Pack(expected);
            var error = math.cmax(math.abs(packed.c0 - actual.c0)) + math.cmax(math.abs(packed.c1 - actual.c1)) + math.cmax(math.abs(packed.c2 - actual.c2)) + math.cmax(math.abs(packed.c3 - actual.c3));
            Assert.Less(error, 1e-4f, $"expected {expected} got {actual}");
        }

        [Test]
        public void PackageSourceHasNoCommentsReflectionLegacyInputOrRukhanka()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.sa.unity");
            Assert.IsNotNull(package);
            var root = package.resolvedPath;
            var offenders = new List<string>();
            foreach (var pattern in new[] { "*.cs", "*.hlsl", "*.compute", "*.shader" })
                foreach (var file in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
                {
                    var text = File.ReadAllText(file);
                    var code = Regex.Replace(text, "\"(\\\\.|[^\"\\\\])*\"", "\"\"");
                    if (code.Contains("//") || code.Contains("/*") || code.Contains("#region"))
                        offenders.Add($"{file}: comment");
                    if (!file.EndsWith("BakeAndGpuTests.cs") && Regex.IsMatch(text, "rukhanka", RegexOptions.IgnoreCase))
                        offenders.Add($"{file}: rukhanka");
                    if (file.EndsWith(".cs") && (code.Contains("System.Reflection") || code.Contains("Input.Get")))
                        offenders.Add($"{file}: reflection or legacy input");
                }
            Assert.IsEmpty(offenders, string.Join("\n", offenders));
            Assert.LessOrEqual(File.ReadAllLines(Path.Combine(root, "Runtime/Core/Evaluation.cs")).Length, 120);
        }
    }
}
