using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SA.Editor;
using Hash128 = Unity.Entities.Hash128;

namespace SA.Tests
{
    public class PoseOnlyBakeTests
    {
        const string Folder = "Assets/SA.BakeProbe";
        const string WithBones = "SaBakeEntities";
        const string PoseOnly = "SaBakePoseOnly";
        const string Attached = "SaBakeAttached";
        const string Cache = "Assets/SceneDependencyCache";

        bool single;
        bool hadCache;
        readonly HashSet<string> cached = new HashSet<string>();

        static IEnumerable<string> CacheEntries()
        {
            if (!AssetDatabase.IsValidFolder(Cache))
                yield break;
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { Cache }))
                yield return AssetDatabase.GUIDToAssetPath(guid);
        }

        [SetUp]
        public void SetUp()
        {
            hadCache = AssetDatabase.IsValidFolder(Cache);
            cached.Clear();
            foreach (var entry in CacheEntries())
                cached.Add(entry);
            Remove();
        }

        [TearDown]
        public void TearDown()
        {
            if (single)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            single = false;
            Remove();
            foreach (var entry in new List<string>(CacheEntries()))
                if (!cached.Contains(entry))
                    AssetDatabase.DeleteAsset(entry);
            if (!hadCache && AssetDatabase.IsValidFolder(Cache) && AssetDatabase.FindAssets(string.Empty, new[] { Cache }).Length == 0)
                AssetDatabase.DeleteAsset(Cache);
            LogAssert.ignoreFailingMessages = false;
        }

        static void Remove()
        {
            if (AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.DeleteAsset(Folder);
        }

        Scene NewScene()
        {
            for (var i = 0; !Application.isBatchMode && i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    Assert.Ignore("An open scene has unsaved changes. This test creates and closes scenes, so in a windowed Editor it will not run over unsaved work.");
            var active = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path))
                return EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            single = true;
            return EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        void Close(Scene scene)
        {
            if (single)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                single = false;
            }
            else
                EditorSceneManager.CloseScene(scene, true);
        }

        static Mesh SkinMesh(Transform holder, Transform[] bones)
        {
            var vertices = new Vector3[bones.Length];
            var weights = new BoneWeight[bones.Length];
            var bindposes = new Matrix4x4[bones.Length];
            for (var i = 0; i < bones.Length; i++)
            {
                vertices[i] = holder.InverseTransformPoint(bones[i].position);
                weights[i] = new BoneWeight { boneIndex0 = i, weight0 = 1f };
                bindposes[i] = bones[i].worldToLocalMatrix * holder.localToWorldMatrix;
            }
            var triangles = new int[(bones.Length - 2) * 3];
            for (var i = 0; i < bones.Length - 2; i++)
            {
                triangles[i * 3] = i;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = i + 2;
            }
            var mesh = new Mesh { name = "SaBakeMesh", vertices = vertices, triangles = triangles, boneWeights = weights, bindposes = bindposes };
            mesh.RecalculateBounds();
            return mesh;
        }

        static GameObject Character(string name, SharedBones mode, ref Mesh mesh, Material material, ref AnimationClip clip, bool attachment)
        {
            var root = FamilyClipsTests.MixamoRig(out var lookup, name, true);
            var armature = root.transform.Find("Armature");
            var bones = new List<Transform> { armature };
            foreach (var bone in armature.GetComponentsInChildren<Transform>(true))
                if (bone != armature)
                    bones.Add(bone);
            var holder = new GameObject("Skin");
            holder.transform.SetParent(root.transform, false);
            if (mesh == null)
            {
                mesh = SkinMesh(holder.transform, bones.ToArray());
                AssetDatabase.CreateAsset(mesh, Folder + "/Mesh.asset");
            }
            if (clip == null)
            {
                clip = FamilyClipsTests.SyntheticClip(lookup, root.transform);
                AssetDatabase.CreateAsset(clip, Folder + "/Clip.anim");
            }
            var renderer = holder.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = bones.ToArray();
            renderer.rootBone = armature;
            renderer.sharedMaterial = material;
            var rig = root.AddComponent<RigAuthoring>();
            rig.DiscoverClips = false;
            rig.Engine = Engine.Cpu;
            Assert.AreEqual(1, SkinnedMeshConversion.Convert(root), "the skinned mesh was not converted");
            var family = root.AddComponent<SharedFamilyAuthoring>();
            family.Rig = rig;
            family.Clips = new List<AnimationClip> { clip };
            family.Autoplay = clip;
            family.Bones = mode;
            if (attachment)
            {
                var prop = GameObject.CreatePrimitive(PrimitiveType.Cube);
                prop.name = "Prop";
                UnityEngine.Object.DestroyImmediate(prop.GetComponent<Collider>());
                prop.transform.SetParent(lookup["RightHand"], false);
            }
            return root;
        }

        const double LoadSeconds = 600.0;

        static World Load(string scenePath, out Entity sceneEntity)
        {
            var world = new World("SA.BakeProbe", WorldFlags.Game);
            var kept = new List<Type>();
            foreach (var type in DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default))
            {
                var space = type.Namespace ?? string.Empty;
                if (space.StartsWith("Unity.Entities") || space.StartsWith("Unity.Scenes"))
                    kept.Add(type);
            }
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, kept.ToArray());
            var guid = new Hash128(AssetDatabase.AssetPathToGUID(scenePath));
            sceneEntity = SceneSystem.LoadSceneAsync(world.Unmanaged, guid, new SceneSystem.LoadParameters { Flags = SceneLoadFlags.BlockOnStreamIn });
            return world;
        }

        static Entity Find(EntityManager manager, string name)
        {
            var hash = RigBuilder.Hash(name);
            using (var query = manager.CreateEntityQuery(typeof(SharedRig)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                    if (manager.GetComponentData<SharedRig>(entity).NameHash == hash)
                        return entity;
            return Entity.Null;
        }

        static Entity SkinOf(EntityManager manager, Entity rig)
        {
            using (var query = manager.CreateEntityQuery(typeof(Skin), typeof(SkinBinding)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                    if (manager.GetComponentData<Skin>(entity).Rig == rig)
                        return entity;
            return Entity.Null;
        }

        static int Below(EntityManager manager, Entity rig)
        {
            var count = 0;
            using (var query = manager.CreateEntityQuery(typeof(Parent)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                {
                    var at = entity;
                    for (var depth = 0; depth < 64 && manager.HasComponent<Parent>(at); depth++)
                        at = manager.GetComponentData<Parent>(at).Value;
                    if (at == rig)
                        count++;
                }
            return count;
        }

        static float4x4[] Palette(SkinningSystem skinning, EntityManager manager, Entity skin)
        {
            var binding = manager.GetComponentData<SkinBinding>(skin);
            var count = manager.GetComponentData<Skin>(skin).Value.Value.Bones.Length;
            Assert.Greater(binding.MatrixBase, 0u, "the skin has no place in the matrix buffer");
            var matrices = new float4x4[count];
            skinning.SkinMatrixBuffer.GetData(matrices, 0, (int)binding.MatrixBase, count);
            return matrices;
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator ACharacterBakedWithoutBoneEntitiesPlaysLikeOneBakedWithThem()
        {
            TestRigs.RequireGpu();
            LogAssert.ignoreFailingMessages = true;
            var scene = NewScene();
            AssetDatabase.CreateFolder("Assets", "SA.BakeProbe");
            var shader = Shader.Find("SA/Skinned Lit");
            Assert.IsNotNull(shader, "the SA skinned shader was not found");
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, Folder + "/Skin.mat");
            Mesh mesh = null;
            AnimationClip clip = null;
            SceneManager.MoveGameObjectToScene(Character(WithBones, SharedBones.Entities, ref mesh, material, ref clip, false), scene);
            SceneManager.MoveGameObjectToScene(Character(PoseOnly, SharedBones.PoseOnly, ref mesh, material, ref clip, false), scene);
            SceneManager.MoveGameObjectToScene(Character(Attached, SharedBones.PoseOnly, ref mesh, material, ref clip, true), scene);
            var path = Folder + "/Sub.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, path), "the sub scene was not saved");
            Close(scene);
            AssetDatabase.Refresh();
            var world = Load(path, out var sceneEntity);
            try
            {
                var waited = Stopwatch.StartNew();
                while (!SceneSystem.IsSceneLoaded(world.Unmanaged, sceneEntity))
                {
                    if (waited.Elapsed.TotalSeconds > LoadSeconds)
                        Assert.Fail("the baked sub scene did not load within " + LoadSeconds + " s; the Editor's import of it stalled");
                    world.Update();
                    yield return null;
                }
                world.Update();
                world.EntityManager.CompleteAllTrackedJobs();
                Check(world);
            }
            finally
            {
                world.Dispose();
            }
        }

        static void Check(World world)
        {
            {
                var manager = world.EntityManager;
                var withBones = Find(manager, WithBones);
                var poseOnly = Find(manager, PoseOnly);
                var attached = Find(manager, Attached);
                Assert.AreNotEqual(Entity.Null, withBones, "the character with bone entities was not baked");
                Assert.AreNotEqual(Entity.Null, poseOnly, "the character without bone entities was not baked");
                Assert.IsTrue(manager.HasComponent<Rig>(withBones) && manager.HasComponent<Rig>(poseOnly));
                Assert.IsTrue(attached == Entity.Null || !manager.HasComponent<Rig>(attached), "a renderer under a bone must stop a PoseOnly rig from baking");
                var boneCount = manager.GetComponentData<Rig>(withBones).Value.Value.Bones.Length;
                Assert.AreEqual(boneCount, manager.GetComponentData<Rig>(poseOnly).Value.Value.Bones.Length);
                Assert.IsTrue(manager.HasBuffer<BoneEntity>(withBones));
                Assert.AreEqual(boneCount, manager.GetBuffer<BoneEntity>(withBones).Length);
                Assert.IsFalse(manager.HasBuffer<BoneEntity>(poseOnly), "a PoseOnly rig must not carry bone entities");
                Assert.IsTrue(manager.HasComponent<CpuEngine>(poseOnly) && manager.HasComponent<SharedTimeline>(poseOnly));
                Assert.IsTrue(manager.HasComponent<SharedCulled>(poseOnly) && !SharedFamilyControl.IsCulled(manager, poseOnly), "a baked rig carries the culling switch, turned off");
                using (var driven = manager.CreateEntityQuery(typeof(PoseDriven)))
                    Assert.AreEqual(boneCount, driven.CalculateEntityCount(), "only the character with bone entities may keep its bones");
                var belowWithBones = Below(manager, withBones);
                var belowPoseOnly = Below(manager, poseOnly);
                TestContext.WriteLine("PoseOnlyBakeEntities below the rig: " + belowWithBones + " with bone entities, " + belowPoseOnly + " without");
                Assert.GreaterOrEqual(belowWithBones, boneCount, "the character with bone entities lost its bones");
                Assert.Less(belowPoseOnly, 4, "the PoseOnly character still carries bone entities");
                var skinWithBones = SkinOf(manager, withBones);
                var skinPoseOnly = SkinOf(manager, poseOnly);
                Assert.AreNotEqual(Entity.Null, skinWithBones, "the skin of the character with bone entities was not baked");
                Assert.AreNotEqual(Entity.Null, skinPoseOnly, "the skin of the character without bone entities was not baked");
                var pose = world.GetOrCreateSystemManaged<SharedFamilySystem>();
                var skinning = world.GetOrCreateSystemManaged<SkinningSystem>();
                var palette = world.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
                var elapsed = 0.0;
                float4x4[] first = null;
                var moved = 0f;
                for (var step = 0; step < 12; step++)
                {
                    var seconds = step == 0 ? 0f : 1f / 60f;
                    elapsed += seconds;
                    world.SetTime(new Unity.Core.TimeData(elapsed, seconds));
                    pose.Update();
                    manager.CompleteAllTrackedJobs();
                    skinning.Update();
                    palette.Update();
                    manager.CompleteAllTrackedJobs();
                    var a = Palette(skinning, manager, skinWithBones);
                    var b = Palette(skinning, manager, skinPoseOnly);
                    Assert.AreEqual(a.Length, b.Length);
                    for (var k = 0; k < a.Length; k++)
                        Assert.IsTrue(a[k].Equals(b[k]), "step " + step + " palette bone " + k + " differs between the two characters");
                    if (first == null)
                        first = a;
                    for (var k = 0; k < a.Length; k++)
                        moved = math.max(moved, math.cmax(math.abs(a[k].c3 - first[k].c3)));
                }
                Assert.Greater(moved, 0.001f, "the baked clip did not move the skin");
                Assert.AreEqual(1, manager.GetComponentData<SharedPoseState>(poseOnly).Native, "the rig job fell back to managed code");
                var local = new NativeArray<BonePose>(boneCount, Allocator.Temp);
                Assert.IsTrue(SharedFamilyPose.TryCopyLocal(manager, poseOnly, local));
                var entities = manager.GetBuffer<BoneEntity>(withBones).ToNativeArray(Allocator.Temp);
                for (var b = 0; b < boneCount; b++)
                {
                    var transform = manager.GetComponentData<LocalTransform>(entities[b].Value);
                    Assert.IsTrue(transform.Position.Equals(local[b].Position) && transform.Rotation.value.Equals(local[b].Rotation.value),
                        "bone " + b + " of the PoseOnly character is not where the bone entity of the other character is");
                }
                Assert.AreEqual(boneCount, skinning.CpuPoseCount, "only the character with bone entities sends bone matrices to the GPU");
                TestContext.WriteLine("PoseOnlyBake bones " + boneCount + " moved " + moved.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
