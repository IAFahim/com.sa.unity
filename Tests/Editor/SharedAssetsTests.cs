using System;
using System.Collections.Generic;
using NUnit.Framework;
using SA.Editor;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;
using Object = UnityEngine.Object;

namespace SA.Tests
{
    public class SharedAssetsTests
    {
        readonly List<Object> created = new List<Object>();
        readonly List<string> assetPaths = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var path in assetPaths)
                AssetDatabase.DeleteAsset(path);
            assetPaths.Clear();
            foreach (var item in created)
                if (item != null)
                    Object.DestroyImmediate(item);
            created.Clear();
        }

        Transform Character(string name)
        {
            var root = new GameObject(name);
            created.Add(root);
            var hips = new GameObject("Hips").transform;
            hips.SetParent(root.transform, false);
            hips.localPosition = Vector3.up;
            var arm = new GameObject("Arm").transform;
            arm.SetParent(hips, false);
            arm.localPosition = Vector3.right;
            return root.transform;
        }

        AnimationClip Animation(string name, float amount)
        {
            var clip = new AnimationClip { name = name };
            created.Add(clip);
            clip.SetCurve("Hips/Arm", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0f, 1f, 1f, amount));
            return clip;
        }

        AvatarMask Mask(string name)
        {
            var mask = new AvatarMask { name = name };
            created.Add(mask);
            mask.transformCount = 2;
            mask.SetTransformPath(0, "Hips");
            mask.SetTransformActive(0, name == "Lower");
            mask.SetTransformPath(1, "Hips/Arm");
            mask.SetTransformActive(1, name == "Upper");
            return mask;
        }

        static List<Transform> Skeleton(Transform root, AnimationClip[] clips)
        {
            var errors = new List<string>();
            var result = RigBuilder.Skeleton(root, clips, Array.Empty<SkinAuthoring>(), errors);
            Assert.IsEmpty(errors);
            return result;
        }

        static BlobAssetReference<Clip> CachedClip(BlobAssetStore store, Transform root, List<Transform> skeleton, AnimationClip clip, float rate = 30f)
        {
            var rigId = RigBuilder.RigId(root, skeleton);
            var id = RigBuilder.ClipKey(root, clip, rate, rigId);
            if (!store.TryGet(id, out BlobAssetReference<Clip> asset))
            {
                asset = RigBuilder.BuildClip(root, skeleton, clip, rate, rigId, id);
                store.TryAdd(id, ref asset);
            }
            return asset;
        }

        static BlobAssetReference<BoneMask> CachedMask(BlobAssetStore store, Transform root, List<Transform> skeleton, AvatarMask mask)
        {
            var rigId = RigBuilder.RigId(root, skeleton);
            var id = RigBuilder.MaskKey(mask, rigId);
            if (!store.TryGet(id, out BlobAssetReference<BoneMask> asset))
            {
                asset = RigBuilder.BuildMask(root, skeleton, mask, rigId, id);
                store.TryAdd(id, ref asset);
            }
            return asset;
        }

        [Test]
        public void PartialOverlapAndReorderedMasksShareExactAssetsAndGpuRanges()
        {
            var a = Character("Soldier");
            var b = Character("Guard");
            b.position = new Vector3(10f, 0f, 0f);
            var idle = Animation("Idle", 1f);
            var walk = Animation("Walk", 2f);
            var run = Animation("Run", 3f);
            var attack = Animation("Attack", 4f);
            var aClips = new[] { idle, walk, run };
            var bClips = new[] { run, attack, idle, walk };
            var aBones = Skeleton(a, aClips);
            var bBones = Skeleton(b, bClips);
            var rigId = RigBuilder.RigId(a, aBones);
            Assert.AreEqual(rigId, RigBuilder.RigId(b, bBones));
            using var store = new BlobAssetStore(128);
            var rig = RigBuilder.BuildRig(aBones, rigId);
            store.TryAdd(rigId, ref rig);
            Assert.IsTrue(store.TryGet(rigId, out BlobAssetReference<RigBlob> sameRig));
            Assert.AreEqual(rig, sameRig);
            var first = new ClipAsset[aClips.Length];
            var second = new ClipAsset[bClips.Length];
            for (var i = 0; i < first.Length; i++)
                first[i] = new ClipAsset { Value = CachedClip(store, a, aBones, aClips[i]) };
            for (var i = 0; i < second.Length; i++)
                second[i] = new ClipAsset { Value = CachedClip(store, b, bBones, bClips[i]) };
            Assert.AreEqual(first[0].Value, second[2].Value);
            Assert.AreEqual(first[1].Value, second[3].Value);
            Assert.AreEqual(first[2].Value, second[0].Value);
            var upper = Mask("Upper");
            var lower = Mask("Lower");
            var aMasks = new[] { CachedMask(store, a, aBones, null), CachedMask(store, a, aBones, upper), CachedMask(store, a, aBones, lower) };
            var bMasks = new[] { CachedMask(store, b, bBones, null), CachedMask(store, b, bBones, lower), CachedMask(store, b, bBones, upper) };
            Assert.AreEqual(aMasks[0], bMasks[0]);
            Assert.AreEqual(aMasks[1], bMasks[2]);
            Assert.AreEqual(aMasks[2], bMasks[1]);
            Assert.AreEqual(0f, aMasks[1].Value.Weights[0]);
            Assert.AreEqual(1f, aMasks[1].Value.Weights[1]);
            Assert.AreEqual(1f, aMasks[2].Value.Weights[0]);
            Assert.AreEqual(0f, aMasks[2].Value.Weights[1]);
            Assert.AreEqual(first[0].Value, CachedClip(store, a, aBones, idle));
            if (!SystemInfo.supportsComputeShaders)
                return;
            using var registry = new GpuRegistry();
            registry.Add(rig);
            registry.Add(sameRig);
            foreach (var clip in first)
                registry.Add(clip.Value);
            var idleBase = registry.ClipSamples[first[0].Value.Value.Id];
            foreach (var clip in second)
                registry.Add(clip.Value);
            foreach (var mask in aMasks)
                registry.Add(mask);
            foreach (var mask in bMasks)
                registry.Add(mask);
            Assert.AreEqual(1, registry.Rigs.Count);
            Assert.AreEqual(4, registry.ClipSamples.Count);
            Assert.AreEqual(3, registry.MaskBases.Count);
            Assert.AreEqual(2, registry.Bones.Length);
            Assert.AreEqual(4 * (31 * 2 + 2), registry.Samples.Length);
            Assert.AreEqual(3 * 2, registry.Masks.Length);
            Assert.AreEqual(idleBase, registry.ClipSamples[second[2].Value.Value.Id]);
        }

        [Test]
        public void SkinLayoutIsStableAndDiscoversUnskinnedSkeletonDescendants()
        {
            var root = Character("Rig");
            var skin = root.gameObject.AddComponent<SkinAuthoring>();
            skin.Bones = new[] { root.Find("Hips/Arm") };
            var extra = new GameObject("Socket").transform;
            extra.SetParent(root.Find("Hips"), false);
            var idle = Animation("Idle", 1f);
            var added = Animation("Added", 2f);
            var errors = new List<string>();
            var before = RigBuilder.Skeleton(root, new[] { idle }, new[] { skin }, errors);
            var after = RigBuilder.Skeleton(root, new[] { added, idle }, new[] { skin }, errors);
            Assert.IsEmpty(errors);
            Assert.AreEqual(RigBuilder.RigId(root, before), RigBuilder.RigId(root, after));
            added.SetCurve("Hips/Socket", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Linear(0, 0, 1, 1));
            RigBuilder.Skeleton(root, new[] { added }, new[] { skin }, errors);
            Assert.IsEmpty(errors);
            Assert.Contains(extra, before);
            errors.Clear();
            var extended = RigBuilder.Skeleton(root, new[] { added }, new[] { skin }, errors, new[] { extra });
            Assert.IsEmpty(errors);
            Assert.Contains(extra, extended);
            RigBuilder.Skeleton(root, new[] { idle }, new[] { skin }, errors, new[] { root });
            Assert.IsTrue(errors.Exists(e => e.Contains("not below")));
        }

        [Test]
        public void IncompatibleRestHierarchySamplingAndAdditiveReferenceHaveDifferentKeys()
        {
            var root = Character("Rig");
            var clip = Animation("Walk", 2f);
            var bones = Skeleton(root, new[] { clip });
            var rigId = RigBuilder.RigId(root, bones);
            var original = RigBuilder.ClipKey(root, clip, 30f, rigId);
            Assert.AreNotEqual(original, RigBuilder.ClipKey(root, clip, 60f, rigId));
            root.Find("Hips/Arm").localPosition = Vector3.left;
            var changedRest = RigBuilder.RigId(root, bones);
            Assert.AreNotEqual(rigId, changedRest);
            Assert.AreNotEqual(original, RigBuilder.ClipKey(root, clip, 30f, changedRest));
            root.Find("Hips/Arm").name = "Hand";
            Assert.AreNotEqual(changedRest, RigBuilder.RigId(root, bones));
            var reference = Animation("Reference", 4f);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.hasAdditiveReferencePose = true;
            settings.additiveReferencePoseClip = reference;
            settings.additiveReferencePoseTime = 0.25f;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            var additive = RigBuilder.ClipKey(root, clip, 30f, rigId);
            Assert.AreNotEqual(original, additive);
            settings.additiveReferencePoseTime = 0.75f;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            var later = RigBuilder.ClipKey(root, clip, 30f, rigId);
            Assert.AreNotEqual(additive, later);
            reference.SetCurve("Hips/Arm", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 1, 1, 9));
            Assert.AreNotEqual(later, RigBuilder.ClipKey(root, clip, 30f, rigId));
            var genericAvatar = AvatarBuilder.BuildGenericAvatar(root.gameObject, "");
            created.Add(genericAvatar);
            root.gameObject.AddComponent<Animator>().avatar = genericAvatar;
            Assert.AreNotEqual(later, RigBuilder.ClipKey(root, clip, 30f, rigId));
        }

        [Test]
        public void SameFileAndNameStillDistinguishClipSubAssets()
        {
            var root = Character("Rig");
            var first = Animation("Same", 2f);
            var second = Animation("Same", 3f);
            var path = "Assets/SA_SharingTests_" + Guid.NewGuid().ToString("N") + ".asset";
            assetPaths.Add(path);
            AssetDatabase.CreateAsset(first, path);
            AssetDatabase.AddObjectToAsset(second, path);
            AssetDatabase.SaveAssets();
            var bones = Skeleton(root, new[] { first });
            var rigId = RigBuilder.RigId(root, bones);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(first, out string guidA, out long localA);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(second, out string guidB, out long localB);
            Assert.AreEqual(guidA, guidB);
            Assert.AreNotEqual(localA, localB);
            Assert.AreNotEqual(RigBuilder.ClipKey(root, first, 30f, rigId), RigBuilder.ClipKey(root, second, 30f, rigId));
            var beforeEdit = RigBuilder.ClipKey(root, first, 30f, rigId);
            first.SetCurve("Hips/Arm", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 1, 1, 7));
            Assert.AreNotEqual(beforeEdit, RigBuilder.ClipKey(root, first, 30f, rigId));
            var beforeLengthChange = RigBuilder.ClipKey(root, first, 30f, rigId);
            var oldLength = first.length;
            AnimationUtility.SetObjectReferenceCurve(first, EditorCurveBinding.PPtrCurve("Visual", typeof(SpriteRenderer), "m_Sprite"), new[]
            {
                new ObjectReferenceKeyframe { time = 0f },
                new ObjectReferenceKeyframe { time = 2f }
            });
            Assert.Greater(first.length, oldLength);
            Assert.AreNotEqual(beforeLengthChange, RigBuilder.ClipKey(root, first, 30f, rigId));
        }

        [Test]
        public void DirtyPersistentMaskChangesItsOwnKeyWithoutInvalidatingClips()
        {
            var root = Character("Rig");
            var clip = Animation("Idle", 1f);
            var bones = Skeleton(root, new[] { clip });
            var rigId = RigBuilder.RigId(root, bones);
            var mask = Mask("Upper");
            var path = "Assets/SA_SharingTests_" + Guid.NewGuid().ToString("N") + ".mask";
            assetPaths.Add(path);
            AssetDatabase.CreateAsset(mask, path);
            AssetDatabase.SaveAssets();
            var clipBefore = RigBuilder.ClipKey(root, clip, 30f, rigId);
            var maskBefore = RigBuilder.MaskKey(mask, rigId);
            mask.SetTransformActive(1, false);
            Assert.AreNotEqual(maskBefore, RigBuilder.MaskKey(mask, rigId));
            Assert.AreEqual(clipBefore, RigBuilder.ClipKey(root, clip, 30f, rigId));
        }

        [Test]
        public unsafe void WorldSerializationPreservesSharedClipAndMaskReferences()
        {
            using var rig = TestRigs.Build();
            using var source = new World("SA.Serialize.Source");
            var manager = source.EntityManager;
            var a = manager.CreateEntity(typeof(Rig));
            manager.SetComponentData(a, new Rig { Value = rig.Skeleton });
            rig.Attach(manager, a);
            var b = manager.Instantiate(a);
            Assert.AreEqual(manager.GetBuffer<ClipAsset>(a)[0].Value, manager.GetBuffer<ClipAsset>(b)[0].Value);
            using var writer = new MemoryBinaryWriter(manager, "SA.SharedAssets");
            SerializeUtility.SerializeWorld(manager, writer);
            using var destination = new World("SA.Serialize.Destination");
            var loaded = destination.EntityManager;
            using (var reader = new MemoryBinaryReader(writer.Data, writer.Length))
            {
                SerializeUtility.DeserializeWorld(loaded.BeginExclusiveEntityTransaction(), reader);
                loaded.EndExclusiveEntityTransaction();
            }
            using var query = loaded.CreateEntityQuery(typeof(Rig), typeof(ClipAsset), typeof(MaskAsset));
            using var entities = query.ToEntityArray(Allocator.Temp);
            Assert.AreEqual(2, entities.Length);
            var clipsA = loaded.GetBuffer<ClipAsset>(entities[0]);
            var clipsB = loaded.GetBuffer<ClipAsset>(entities[1]);
            var masksA = loaded.GetBuffer<MaskAsset>(entities[0]);
            var masksB = loaded.GetBuffer<MaskAsset>(entities[1]);
            Assert.AreEqual(clipsA[0].Value, clipsB[0].Value);
            Assert.AreEqual(masksA[1].Value, masksB[1].Value);
            Assert.AreNotEqual(rig.Clips[0].Value, clipsA[0].Value);
            Assert.AreEqual(rig.Clips[0].Value.Value.Id, clipsA[0].Value.Value.Id);
            Assert.AreEqual(2f, clipsA[0].Value.Value.Samples[3].Position.x, 1e-6f);
            Assert.AreEqual(0f, masksA[1].Value.Value.Weights[0]);
        }

        [Test]
        public void ReorderedSharedClipsHaveIndependentCpuAndGpuPlayback()
        {
            using var rig = TestRigs.Build();
            using var world = new World("SA.Independent");
            var manager = world.EntityManager;
            var a = manager.CreateEntity(typeof(ClipState));
            var b = manager.CreateEntity(typeof(ClipState));
            var first = manager.GetBuffer<ClipState>(a);
            var second = manager.GetBuffer<ClipState>(b);
            var reordered = new[] { rig.Clips[1], rig.Clips[0] };
            Playback.Play(ref first, Names.Clip(rig.Clips, 10u), PlayOptions.Default);
            Playback.Play(ref second, Names.Clip(reordered, 10u), PlayOptions.Default);
            Playback.Step(ref first, rig.Clips, 0.25f);
            Playback.Step(ref second, reordered, 0.75f);
            Assert.AreEqual(0.25f, first[0].Phase, 1e-6f);
            Assert.AreEqual(0.75f, second[0].Phase, 1e-6f);
            var aPose = Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(first), 1);
            var bPose = Evaluation.Bone(ref rig.Value, reordered, rig.Masks, TestRigs.View(second), 1);
            Assert.AreEqual(1f, aPose.Position.x, 1e-5f);
            Assert.AreEqual(3f, bPose.Position.x, 1e-5f);
            if (!SystemInfo.supportsComputeShaders)
                return;
            using var pipeline = new GpuPipeline();
            rig.Register(pipeline.Registry);
            var sampleBase = pipeline.Registry.ClipSamples[rig.Clips[0].Value.Value.Id];
            var maskBase = pipeline.Registry.MaskBases[rig.Masks[0].Value.Value.Id];
            using var instances = new NativeArray<GpuInstance>(new[]
            {
                new GpuInstance { BoneBase = 0, BoneCount = 2, PoseBase = 0, MotionBase = 0, MotionCount = 1 },
                new GpuInstance { BoneBase = 0, BoneCount = 2, PoseBase = 2, MotionBase = 1, MotionCount = 1 }
            }, Allocator.Temp);
            using var motions = new NativeArray<GpuMotion>(new[]
            {
                GpuFrame.Motion(ref rig.Clips[0].Value.Value, first[0], sampleBase, maskBase),
                GpuFrame.Motion(ref reordered[1].Value.Value, second[0], sampleBase, maskBase)
            }, Allocator.Temp);
            using var cpuPoses = new NativeArray<float4x4>(0, Allocator.Temp);
            using var skinJobs = new NativeArray<GpuSkinJob>(0, Allocator.Temp);
            Assert.AreEqual(motions[0].SampleBase, motions[1].SampleBase);
            pipeline.Execute(instances, motions, cpuPoses, 4, skinJobs, 1);
            var result = new float4x4[4];
            pipeline.Poses.Buffer.GetData(result);
            var aRoot = PoseAlgebra.Affine(Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(first), 0));
            var bRoot = PoseAlgebra.Affine(Evaluation.Bone(ref rig.Value, reordered, rig.Masks, TestRigs.View(second), 0));
            AssertMatrix(aRoot, result[0]);
            AssertMatrix(PoseAlgebra.Mul(aRoot, PoseAlgebra.Affine(aPose)), result[1]);
            AssertMatrix(bRoot, result[2]);
            AssertMatrix(PoseAlgebra.Mul(bRoot, PoseAlgebra.Affine(bPose)), result[3]);
        }

        [Test]
        public void SkinningSystemRegistersSharedAndChangedAssetTables()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("compute shaders unavailable");
            using var rig = TestRigs.Build();
            using var replacement = TestRigs.Build(5);
            using var skinBuilder = new BlobBuilder(Allocator.Temp);
            ref var skinRoot = ref skinBuilder.ConstructRoot<SkinBlob>();
            skinRoot.Id = new Hash128(60, 1, 1, 1);
            skinRoot.MeshFromRig = PoseAlgebra.Affine(GpuFrame.Identity);
            skinBuilder.Allocate(ref skinRoot.Bones, 1)[0] = 1;
            skinBuilder.Allocate(ref skinRoot.Bindposes, 1)[0] = PoseAlgebra.Affine(GpuFrame.Identity);
            skinBuilder.Allocate(ref skinRoot.Influences, 0);
            using var skin = skinBuilder.CreateBlobAssetReference<SkinBlob>(Allocator.Persistent);
            using var maskBuilder = new BlobBuilder(Allocator.Temp);
            ref var maskRoot = ref maskBuilder.ConstructRoot<BoneMask>();
            maskRoot.Id = new Hash128(61, 1, 1, 1);
            maskRoot.RigId = rig.Value.Id;
            var weights = maskBuilder.Allocate(ref maskRoot.Weights, 2);
            weights[0] = 1f;
            weights[1] = 0f;
            using var mask = maskBuilder.CreateBlobAssetReference<BoneMask>(Allocator.Persistent);
            using var world = new World("SA.SystemSharing");
            var manager = world.EntityManager;
            var a = manager.CreateEntity(typeof(Rig), typeof(PoseSlot), typeof(Unity.Transforms.LocalToWorld), typeof(ClipState));
            manager.SetComponentData(a, new Rig { Value = rig.Skeleton });
            rig.Attach(manager, a);
            var b = manager.Instantiate(a);
            var bClips = manager.GetBuffer<ClipAsset>(b);
            bClips[0] = rig.Clips[1];
            bClips[1] = rig.Clips[0];
            var aSkin = manager.CreateEntity(typeof(Skin), typeof(SkinBinding));
            var bSkin = manager.CreateEntity(typeof(Skin), typeof(SkinBinding));
            manager.SetComponentData(aSkin, new Skin { Value = skin, Rig = a });
            manager.SetComponentData(bSkin, new Skin { Value = skin, Rig = b });
            manager.GetBuffer<ClipState>(a).Add(new ClipState { Clip = new ClipId(0), Phase = 0.25f, Weight = 1f, Fade = 1f });
            manager.GetBuffer<ClipState>(b).Add(new ClipState { Clip = new ClipId(1), Phase = 0.75f, Weight = 1f, Fade = 1f });
            var system = world.GetOrCreateSystemManaged<SkinningSystem>();
            system.Update();
            AssertSystemSkin(manager, system, a, aSkin);
            AssertSystemSkin(manager, system, b, bSkin);
            Assert.AreEqual(2, system.Registry.ClipSamples.Count);
            var bStates = manager.GetBuffer<ClipState>(b);
            var playing = bStates[0];
            playing.Phase = 0.5f;
            bStates[0] = playing;
            system.Update();
            AssertSystemSkin(manager, system, a, aSkin);
            AssertSystemSkin(manager, system, b, bSkin);
            Assert.AreEqual(2, system.Registry.ClipSamples.Count);
            bClips = manager.GetBuffer<ClipAsset>(b);
            bClips[1] = replacement.Clips[0];
            system.Update();
            AssertSystemSkin(manager, system, a, aSkin);
            AssertSystemSkin(manager, system, b, bSkin);
            Assert.AreEqual(3, system.Registry.ClipSamples.Count);
            var bMasks = manager.GetBuffer<MaskAsset>(b);
            bMasks[0] = new MaskAsset { Value = mask };
            system.Update();
            AssertSystemSkin(manager, system, a, aSkin);
            AssertSystemSkin(manager, system, b, bSkin);
            Assert.AreEqual(3, system.Registry.MaskBases.Count);
        }

        static void AssertSystemSkin(EntityManager manager, SkinningSystem system, Entity entity, Entity skin)
        {
            var rig = manager.GetComponentData<Rig>(entity).Value;
            var clips = manager.GetBuffer<ClipAsset>(entity, true).AsNativeArray().AsReadOnlySpan();
            var masks = manager.GetBuffer<MaskAsset>(entity, true).AsNativeArray().AsReadOnlySpan();
            var states = manager.GetBuffer<ClipState>(entity, true).AsNativeArray().AsReadOnlySpan();
            var root = PoseAlgebra.Affine(Evaluation.Bone(ref rig.Value, clips, masks, states, 0));
            var expected = PoseAlgebra.Mul(root, PoseAlgebra.Affine(Evaluation.Bone(ref rig.Value, clips, masks, states, 1)));
            var result = new float4x4[1];
            var binding = manager.GetComponentData<SkinBinding>(skin);
            Assert.Greater(binding.MatrixBase, 0u);
            system.SkinMatrixBuffer.GetData(result, 0, (int)binding.MatrixBase, 1);
            AssertMatrix(expected, result[0]);
        }

        static void AssertMatrix(float3x4 expected, float4x4 actual)
        {
            var packed = GpuFrame.Pack(expected);
            Assert.Less(math.cmax(math.abs(packed.c0 - actual.c0)) + math.cmax(math.abs(packed.c1 - actual.c1)) + math.cmax(math.abs(packed.c2 - actual.c2)) + math.cmax(math.abs(packed.c3 - actual.c3)), 1e-4f);
        }
    }
}
