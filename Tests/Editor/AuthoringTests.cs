using System.Collections.Generic;
using NUnit.Framework;
using SA.Editor;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Hybrid.Baking;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEditor.Animations;
using UnityEngine;

namespace SA.Tests
{
    public class AuthoringTests
    {
        readonly List<Object> objects = new List<Object>();

        T Own<T>(T value) where T : Object { objects.Add(value); return value; }

        [TearDown]
        public void Cleanup()
        {
            foreach (var item in objects)
                if (item != null)
                    Object.DestroyImmediate(item);
            objects.Clear();
        }

        [Test]
        public void SetupFindsSkinBonesAndConvertsAllMaterialSlotsInOneAction()
        {
            var root = Own(new GameObject("Character"));
            var bone = new GameObject("Hips").transform;
            bone.SetParent(root.transform);
            var meshObject = new GameObject("Body");
            meshObject.transform.SetParent(root.transform);
            var mesh = Own(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 0, 1, 2 }, 1);
            var skin = meshObject.AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = mesh;
            skin.bones = new[] { bone };
            skin.sharedMaterials = new Material[2];
            var rig = CharacterSetup.Configure(root);
            Assert.IsNull(meshObject.GetComponent<SkinnedMeshRenderer>());
            Assert.AreEqual(2, meshObject.GetComponent<MeshRenderer>().sharedMaterials.Length);
            Assert.AreEqual(rig, meshObject.GetComponent<SkinAuthoring>().Rig);
            CollectionAssert.AreEqual(new[] { bone }, meshObject.GetComponent<SkinAuthoring>().Bones);
            Assert.AreEqual(0, rig.ExtraBones.Count);
            Assert.IsTrue(rig.InPlace);
        }

        [Test]
        public void DiscoversChildAnimatorClipsWithoutChangingExplicitChoices()
        {
            var root = Own(new GameObject("Character"));
            var rig = root.AddComponent<RigAuthoring>();
            var child = new GameObject("Model");
            child.transform.SetParent(root.transform);
            var animator = child.AddComponent<Animator>();
            var controller = Own(new AnimatorController());
            controller.AddLayer("Base");
            Own(controller.layers[0].stateMachine);
            var idle = Own(new AnimationClip { name = "Idle" });
            var run = Own(new AnimationClip { name = "Run" });
            Own(controller.layers[0].stateMachine.AddState("Idle")).motion = idle;
            Own(controller.layers[0].stateMachine.AddState("Run")).motion = run;
            animator.runtimeAnimatorController = controller;
            rig.Clips.Add(run);
            CollectionAssert.AreEqual(new[] { run, idle }, AnimationDiscovery.Clips(rig));
            rig.DiscoverClips = false;
            CollectionAssert.AreEqual(new[] { run }, AnimationDiscovery.Clips(rig));
        }

        [Test]
        public void PacksWithDuplicateClipNamesStaySelectableAndCanBeRenamed()
        {
            var root = Own(new GameObject("Character"));
            var rig = root.AddComponent<RigAuthoring>();
            var first = Own(new AnimationClip { name = "Idle" });
            var second = Own(new AnimationClip { name = "Idle" });
            rig.AnimationLibraries.Add(first);
            rig.AnimationLibraries.Add(second);
            var clips = AnimationDiscovery.Clips(rig);
            Assert.AreEqual(2, clips.Count);
            Assert.AreEqual("Idle", AnimationDiscovery.Name(rig, first, clips));
            Assert.AreNotEqual("Idle", AnimationDiscovery.Name(rig, second, clips));
            rig.ClipOptions.Add(new ClipSettings { Clip = second, Name = "Combat Idle", InPlace = true });
            Assert.AreEqual("Combat Idle", AnimationDiscovery.Name(rig, second, clips));
            Assert.IsTrue(rig.IsInPlace(second));
        }

        [Test]
        public void ReadingEditorCurvesDoesNotChangeAClipCacheKey()
        {
            var root = Own(new GameObject("Character"));
            var hips = new GameObject("Hips").transform;
            hips.SetParent(root.transform);
            var clip = Own(new AnimationClip { name = "Idle" });
            clip.SetCurve("Hips", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            var id = RigBuilder.RigId(root.transform, new List<Transform> { hips });
            var first = RigBuilder.ClipKey(root.transform, clip, 30, id);
            foreach (var binding in UnityEditor.AnimationUtility.GetCurveBindings(clip))
                UnityEditor.AnimationUtility.GetEditorCurve(clip, binding);
            Assert.AreEqual(first, RigBuilder.ClipKey(root.transform, clip, 30, id));
        }

        [Test]
        public void InPlaceChangesOnlyTheClipCacheAndRemovesHorizontalTravel()
        {
            var root = Own(new GameObject("Character"));
            var hips = new GameObject("Hips").transform;
            hips.SetParent(root.transform);
            var foot = new GameObject("Foot").transform;
            foot.SetParent(hips);
            foot.localPosition = Vector3.down;
            var clip = Own(new AnimationClip { name = "Run" });
            clip.SetCurve("Hips", typeof(Transform), "m_LocalPosition.z", AnimationCurve.Linear(0, 0, 1, 4));
            clip.SetCurve("Hips", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Linear(0, 1, 1, 1.2f));
            clip.SetCurve("Hips/Foot", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 0.4f));
            var bones = new List<Transform> { hips, foot };
            var id = RigBuilder.RigId(root.transform, bones);
            var normal = RigBuilder.ClipKey(root.transform, clip, 30, id);
            var inplace = RigBuilder.ClipKey(root.transform, clip, 30, id, true);
            Assert.AreNotEqual(normal, inplace);
            using var original = RigBuilder.BuildClip(root.transform, bones, clip, 30, id, normal);
            using var baked = RigBuilder.BuildClip(root.transform, bones, clip, 30, id, inplace, true);
            var last = (baked.Value.FrameCount - 1) * bones.Count;
            Assert.AreEqual(4f, original.Value.Samples[last].Position.z, 1e-5f);
            Assert.AreEqual(0f, baked.Value.Samples[last].Position.z, 1e-5f);
            Assert.AreEqual(1.2f, baked.Value.Samples[last].Position.y, 1e-5f);
            Assert.AreEqual(0.4f, baked.Value.Samples[last + 1].Position.x, 1e-5f);
            Assert.AreEqual(id, RigBuilder.RigId(root.transform, bones));
        }

        [Test]
        public void BindsEveryMaterialEntityButNotUnrelatedAdditionalEntities()
        {
            using var builder = new BlobBuilder(Allocator.Temp);
            builder.ConstructRoot<SkinBlob>();
            using var skin = builder.CreateBlobAssetReference<SkinBlob>(Allocator.Persistent);
            using var world = new World("SA.MultiMaterial");
            var manager = world.EntityManager;
            var rig = manager.CreateEntity();
            var renderer = manager.CreateEntity();
            var first = manager.CreateEntity(typeof(MaterialMeshInfo), typeof(RenderBounds));
            var second = manager.CreateEntity(typeof(MaterialMeshInfo), typeof(RenderBounds));
            var unrelated = manager.CreateEntity();
            var additional = manager.AddBuffer<AdditionalEntitiesBakingData>(renderer);
            additional.Add(new AdditionalEntitiesBakingData { Value = first });
            additional.Add(new AdditionalEntitiesBakingData { Value = second });
            additional.Add(new AdditionalEntitiesBakingData { Value = unrelated });
            manager.AddBuffer<BakedSkin>(rig).Add(new BakedSkin
            {
                Renderer = renderer, Skin = skin,
                Bounds = new AABB { Center = new float3(1, 2, 3), Extents = new float3(2) }
            });
            world.GetOrCreateSystemManaged<SkinBindingBakingSystem>().Update();
            foreach (var entity in new[] { first, second })
            {
                Assert.AreEqual(rig, manager.GetComponentData<Skin>(entity).Rig);
                Assert.AreEqual(skin, manager.GetComponentData<Skin>(entity).Value);
                Assert.IsTrue(manager.HasComponent<SkinBinding>(entity));
                Assert.AreEqual(new float3(1, 2, 3), manager.GetComponentData<RenderBounds>(entity).Value.Center);
            }
            Assert.IsFalse(manager.HasComponent<Skin>(renderer));
            Assert.IsFalse(manager.HasComponent<Skin>(unrelated));
        }
    }
}
