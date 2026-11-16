using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA.Editor;
using Hash128 = Unity.Entities.Hash128;

namespace SA.Tests
{
    public class SharedFamilyPaletteTests
    {
        const int Canonical = 56;
        const int Steps = 14;
        const string TraceName = "SharedFamilyPalette.bytes";

        sealed class Skinned : IDisposable
        {
            public SharedFamilyJobTests.Stage Stage;
            public SkinningSystem Skinning;
            public SharedFamilyPaletteSystem Palette;
            public readonly List<Entity> Skins = new List<Entity>();
            public readonly List<BlobAssetReference<SkinBlob>> Blobs = new List<BlobAssetReference<SkinBlob>>();

            public void Dispose()
            {
                Stage.Dispose();
                foreach (var blob in Blobs)
                    blob.Dispose();
            }

            public void Frame(float seconds)
            {
                Stage.Step(seconds);
                Skinning.Update();
                Palette.Update();
                Stage.World.EntityManager.CompleteAllTrackedJobs();
            }
        }

        static BlobAssetReference<SkinBlob> Blob(uint id, int rigBones, int paletteBones)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var skin = ref builder.ConstructRoot<SkinBlob>();
            skin.Id = new Hash128(id, 9, 9, 9);
            skin.MeshFromRig = PoseAlgebra.Affine(float4x4.TRS(new float3(0.1f, -0.2f, 0.05f), quaternion.RotateY(0.3f), new float3(1f)));
            var bones = builder.Allocate(ref skin.Bones, paletteBones);
            var bindposes = builder.Allocate(ref skin.Bindposes, paletteBones);
            for (var b = 0; b < paletteBones; b++)
            {
                bones[b] = (b * 7 + 3) % rigBones;
                bindposes[b] = PoseAlgebra.Affine(float4x4.TRS(new float3(0.01f * b, 0.02f * b, -0.01f * b), quaternion.Euler(0.05f * b, -0.03f * b, 0.02f * b), new float3(1f)));
            }
            builder.Allocate(ref skin.Influences, 1);
            var result = builder.CreateBlobAssetReference<SkinBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }

        static Entity Skin(Skinned scene, Entity rig, BlobAssetReference<SkinBlob> blob)
        {
            var manager = scene.Stage.World.EntityManager;
            if (!manager.HasComponent<PoseSlot>(rig))
            {
                manager.AddBuffer<ClipState>(rig);
                manager.AddBuffer<ClipAsset>(rig);
                manager.AddBuffer<MaskAsset>(rig);
                manager.AddComponentData(rig, new PoseSlot { Base = -1 });
                manager.AddComponentData(rig, new LocalToWorld { Value = float4x4.identity });
                manager.AddComponent<CpuEngine>(rig);
            }
            var entity = manager.CreateEntity(typeof(Skin), typeof(SkinBinding));
            manager.SetComponentData(entity, new Skin { Value = blob, Rig = rig });
            scene.Skins.Add(entity);
            return entity;
        }

        static Skinned Scene()
        {
            var scene = new Skinned { Stage = new SharedFamilyJobTests.Stage { World = new World("SA.SharedFamilyPalette") } };
            var stage = scene.Stage;
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            var parents = SharedFamilyBake.CanonicalParentsArray();
            var first = SharedFamilyJobTests.Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
            var second = SharedFamilyJobTests.Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
            var small = SharedFamilyJobTests.Rig(stage, SharedFamilyJobTests.SmallParents(), SharedFamilyJobTests.SmallTable(stage, reference), new[] { first, second }, new[] { 1f, 0f }, 0f);
            var full = SharedFamilyJobTests.Rig(stage, parents, SharedFamilyJobTests.FullTable(stage, reference, parents), new[] { first, second }, new[] { 1f, 0f }, 0f);
            scene.Blobs.Add(Blob(1, 7, 7));
            scene.Blobs.Add(Blob(2, Canonical, 50));
            scene.Blobs.Add(Blob(3, Canonical, 20));
            Skin(scene, small, scene.Blobs[0]);
            Skin(scene, full, scene.Blobs[1]);
            Skin(scene, full, scene.Blobs[2]);
            stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
            scene.Skinning = stage.World.GetOrCreateSystemManaged<SkinningSystem>();
            scene.Palette = stage.World.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
            return scene;
        }

        static void Record(Skinned scene, List<float> trace)
        {
            var manager = scene.Stage.World.EntityManager;
            foreach (var entity in scene.Skins)
            {
                var skin = manager.GetComponentData<Skin>(entity);
                var binding = manager.GetComponentData<SkinBinding>(entity);
                var count = skin.Value.Value.Bones.Length;
                Assert.Greater(binding.MatrixBase, 0u, "the skin has no place in the matrix buffer");
                var matrices = new float4x4[count];
                scene.Skinning.SkinMatrixBuffer.GetData(matrices, 0, (int)binding.MatrixBase, count);
                foreach (var matrix in matrices)
                    for (var column = 0; column < 4; column++)
                        for (var row = 0; row < 4; row++)
                            trace.Add(matrix[column][row]);
            }
        }

        static float[] Play()
        {
            var trace = new List<float>();
            using (var scene = Scene())
            {
                var manager = scene.Stage.World.EntityManager;
                for (var step = 0; step < Steps; step++)
                {
                    if (step == 6)
                    {
                        var contributions = manager.GetBuffer<SharedContribution>(scene.Stage.Rigs[0]);
                        SharedFamilyControl.PlaySingle(ref contributions, 1);
                    }
                    scene.Frame(step == 0 ? 0f : 1f / 60f);
                    Record(scene, trace);
                }
            }
            return trace.ToArray();
        }

        static string TracePath()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SharedFamilyPaletteTests).Assembly);
            Assert.IsNotNull(package, "the SA package could not be located");
            return Path.Combine(package.resolvedPath, "Tests", "Editor", "Fixtures", TraceName);
        }

        [Test]
        [Explicit("Overwrites the palette fixture with what the system compiled now uploads. Run it only when the old behaviour is meant to change.")]
        public void RecordThePalettesFromTheCurrentSystem()
        {
            TestRigs.RequireGpu();
            var trace = Play();
            var bytes = new byte[trace.Length * 4];
            Buffer.BlockCopy(trace, 0, bytes, 0, bytes.Length);
            var path = TracePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            TestContext.WriteLine("wrote " + trace.Length + " values to " + path);
            Assert.Greater(trace.Length, 0);
        }

        [Test]
        public void TheUploadedPalettesMatchTheOnesRecordedFromTheMainThreadSystem()
        {
            TestRigs.RequireGpu();
            var path = TracePath();
            Assert.IsTrue(File.Exists(path), "missing fixture " + path);
            var stored = File.ReadAllBytes(path);
            var expected = new float[stored.Length / 4];
            Buffer.BlockCopy(stored, 0, expected, 0, expected.Length * 4);
            var actual = Play();
            Assert.AreEqual(expected.Length, actual.Length, "trace length");
            var worst = 0f;
            var worstIndex = -1;
            var exact = 0;
            var moved = 0f;
            for (var i = 0; i < expected.Length; i++)
            {
                var gap = Math.Abs(expected[i] - actual[i]);
                if (gap == 0f)
                    exact++;
                if (gap > worst)
                {
                    worst = gap;
                    worstIndex = i;
                }
                moved = Math.Max(moved, Math.Abs(actual[i] - actual[i % (expected.Length / Steps)]));
            }
            TestContext.WriteLine("PaletteGap values " + expected.Length + " exact " + exact + " worst " + worst.ToString("R", CultureInfo.InvariantCulture) + " at " + worstIndex);
            Assert.Greater(moved, 0.01f, "the palettes never changed over the scene");
            Assert.LessOrEqual(worst, 2e-5f, "value " + worstIndex + " of " + expected.Length + " differs from the recorded palettes");
        }

        [Test]
        public void TheStagingHoldsOnlyTheSharedPalettes()
        {
            TestRigs.RequireGpu();
            using (var scene = Scene())
            {
                scene.Frame(0f);
                scene.Frame(1f / 60f);
                const int shared = 7 + 50 + 20;
                Assert.AreEqual(shared, scene.Palette.StagedMatrices);
                Assert.LessOrEqual(scene.Palette.StagingCapacity, 2 * shared);
                Assert.Less(scene.Palette.StagingCapacity, scene.Skinning.SkinMatrixBuffer.count, "the staging copy is as large as the whole GPU buffer");
                Assert.That(scene.Palette.UploadCalls, Is.InRange(1, 3));
            }
        }

        [Test]
        [Explicit("Prints the cost of SharedFamilyPaletteSystem for 1000 skins of 56 bones")]
        public void CostOfTheSharedFamilyPaletteSystemPerSkin()
        {
            TestRigs.RequireGpu();
            const int count = 1000;
            const int warm = 20;
            const int measured = 60;
            using (var scene = new Skinned { Stage = new SharedFamilyJobTests.Stage { World = new World("SA.SharedFamilyPalette.Cost") } })
            {
                var stage = scene.Stage;
                var reference = SharedFamilyBake.CanonicalReferencePoses();
                var parents = SharedFamilyBake.CanonicalParentsArray();
                var first = SharedFamilyJobTests.Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
                var second = SharedFamilyJobTests.Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
                var table = SharedFamilyJobTests.FullTable(stage, reference, parents);
                scene.Blobs.Add(Blob(5, Canonical, Canonical));
                for (var i = 0; i < count; i++)
                    Skin(scene, SharedFamilyJobTests.Rig(stage, parents, table, new[] { first, second }, new[] { 1f, 0f }, i * 0.001f), scene.Blobs[0]);
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                scene.Skinning = stage.World.GetOrCreateSystemManaged<SkinningSystem>();
                scene.Palette = stage.World.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
                for (var step = 0; step < warm; step++)
                    scene.Frame(1f / 60f);
                var samples = new double[measured];
                var watch = new Stopwatch();
                for (var step = 0; step < measured; step++)
                {
                    stage.Step(1f / 60f);
                    scene.Skinning.Update();
                    watch.Restart();
                    scene.Palette.Update();
                    stage.World.EntityManager.CompleteAllTrackedJobs();
                    watch.Stop();
                    samples[step] = watch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(samples);
                var median = samples[measured / 2];
                var report = "{\n  \"Skins\": " + count + ",\n  \"Bones\": " + Canonical
                    + ",\n  \"PaletteMedianMsPerFrame\": " + median.ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"PaletteP95MsPerFrame\": " + samples[(int)(measured * 0.95)].ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"PaletteMicrosecondsPerSkin\": " + (median * 1000.0 / count).ToString("F3", CultureInfo.InvariantCulture)
                    + ",\n  \"Gpu\": \"" + UnityEngine.SystemInfo.graphicsDeviceName + "\",\n  \"Api\": \"" + UnityEngine.SystemInfo.graphicsDeviceType + "\"\n}\n";
                TestContext.WriteLine(report);
                Assert.Greater(median, 0.0);
            }
        }
    }
}
