using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using SA;
using SA.Shape.Unity;

namespace SA.Tests
{
    public class ShapeMorphUploadTests
    {
        static Mesh TwoMorphMesh(string first, string second)
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            var still = new[] { Vector3.zero, Vector3.zero, Vector3.zero };
            var nudge = new[] { new Vector3(0.1f, 0f, 0f), Vector3.zero, Vector3.zero };
            mesh.AddBlendShapeFrame(first, 100f, nudge, still, still);
            mesh.AddBlendShapeFrame(second, 100f, still, still, still);
            mesh.name = "SA.MorphMesh";
            return mesh;
        }

        static Entity BuildMorphRig(World world, string[] names, float[] values, SkinnedMeshRenderer renderer, bool shaped)
        {
            var manager = world.EntityManager;
            var entity = manager.CreateEntity();
            if (shaped)
                manager.AddComponent<ShapeDriven>(entity);
            var weights = manager.AddBuffer<ShapeMorphWeight>(entity);
            for (var i = 0; i < values.Length; i++)
                weights.Add(new ShapeMorphWeight { Value = values[i] });
            var bindings = manager.AddBuffer<ShapeMorphBinding>(entity);
            for (var i = 0; i < names.Length; i++)
                bindings.Add(new ShapeMorphBinding { Name = names[i] });
            manager.AddComponentData(entity, new ShapeMorphRenderer { Renderer = renderer });
            return entity;
        }

        static void StepUpload(World world)
        {
            world.GetOrCreateSystemManaged<ShapeMorphUploadSystem>().Update();
        }

        [Test]
        public void WeightReachesRenderer()
        {
            var mesh = TwoMorphMesh("calf_left", "calf_right");
            var body = new GameObject("SA.MorphBody").AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = mesh;
            var world = new World("SA.MorphUpload.Weight");
            try
            {
                var rig = BuildMorphRig(world, new[] { "calf_left", "calf_right" }, new[] { 37.5f, 12.25f }, body, true);
                Assert.IsTrue(world.EntityManager.Exists(rig));
                StepUpload(world);
                Assert.AreEqual(37.5f, body.GetBlendShapeWeight(0));
                Assert.AreEqual(12.25f, body.GetBlendShapeWeight(1));
                Assert.AreEqual(1, ShapeMorphUploadSystem.LastUploaded);
                Assert.AreEqual(0, ShapeMorphUploadSystem.LastMissing);
                Assert.AreEqual(0, ShapeMorphUploadSystem.LastUnmapped);
                Assert.AreEqual(1, ShapeMorphUploadSystem.TrackedRigCount);
            }
            finally
            {
                world.Dispose();
                Object.DestroyImmediate(body.gameObject);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MissingNameDiagnosed()
        {
            var mesh = TwoMorphMesh("calf_left", "calf_right");
            var body = new GameObject("SA.MorphBody").AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = mesh;
            var world = new World("SA.MorphUpload.Missing");
            try
            {
                BuildMorphRig(world, new[] { "calf_left", "ghost_morph" }, new[] { 50f, 25f }, body, true);
                StepUpload(world);
                Assert.AreEqual(50f, body.GetBlendShapeWeight(0));
                Assert.AreEqual(1, ShapeMorphUploadSystem.LastUploaded);
                Assert.AreEqual(1, ShapeMorphUploadSystem.LastUnmapped);
                StringAssert.Contains("ghost_morph", ShapeMorphUploadSystem.LastDiagnostic);
            }
            finally
            {
                world.Dispose();
                Object.DestroyImmediate(body.gameObject);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void UnbindingOnDestroy()
        {
            var mesh = TwoMorphMesh("calf_left", "calf_right");
            var body = new GameObject("SA.MorphBody").AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = mesh;
            var bodyObject = body.gameObject;
            var world = new World("SA.MorphUpload.Unbind");
            try
            {
                var rig = BuildMorphRig(world, new[] { "calf_left", "calf_right" }, new[] { 10f, 20f }, body, true);
                StepUpload(world);
                Assert.AreEqual(1, ShapeMorphUploadSystem.TrackedRigCount);
                world.EntityManager.DestroyEntity(rig);
                StepUpload(world);
                Assert.AreEqual(1, ShapeMorphUploadSystem.LastUnbound);
                Assert.AreEqual(0, ShapeMorphUploadSystem.TrackedRigCount);
                BuildMorphRig(world, new[] { "calf_left", "calf_right" }, new[] { 10f, 20f }, body, true);
                StepUpload(world);
                Assert.AreEqual(1, ShapeMorphUploadSystem.TrackedRigCount);
                Object.DestroyImmediate(bodyObject);
                StepUpload(world);
                Assert.AreEqual(1, ShapeMorphUploadSystem.LastMissing);
                Assert.AreEqual(0, ShapeMorphUploadSystem.TrackedRigCount);
            }
            finally
            {
                world.Dispose();
                if (bodyObject != null)
                    Object.DestroyImmediate(bodyObject);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void NonShapeRigSkipped()
        {
            var mesh = TwoMorphMesh("calf_left", "calf_right");
            var body = new GameObject("SA.MorphBody").AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = mesh;
            var world = new World("SA.MorphUpload.Skip");
            try
            {
                BuildMorphRig(world, new[] { "calf_left", "calf_right" }, new[] { 10f, 20f }, body, false);
                StepUpload(world);
                Assert.AreEqual(0, ShapeMorphUploadSystem.LastUploaded);
                Assert.AreEqual(0, ShapeMorphUploadSystem.TrackedRigCount);
                Assert.AreEqual(0f, body.GetBlendShapeWeight(0));
            }
            finally
            {
                world.Dispose();
                Object.DestroyImmediate(body.gameObject);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
