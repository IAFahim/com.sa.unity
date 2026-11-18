using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA.Editor;

namespace SA.Tests
{
    public class CullingTests
    {
        const int Canonical = 56;
        const int CullFrom = 8;
        const int CullUntil = 21;

        static List<float> Clocks(SharedFamilyJobTests.Stage stage)
        {
            var values = new List<float>();
            var manager = stage.World.EntityManager;
            foreach (var rig in stage.Rigs)
            {
                var clock = manager.GetComponentData<SharedClock>(rig);
                values.Add((float)clock.Seconds);
                values.Add(clock.Tick);
                values.Add(clock.Paused);
                values.Add(clock.Seek);
                foreach (var contribution in manager.GetBuffer<SharedContribution>(rig))
                {
                    values.Add((float)contribution.Sample);
                    values.Add(contribution.Weight);
                }
            }
            return values;
        }

        static List<float> Bones(SharedFamilyJobTests.Stage stage)
        {
            var values = new List<float>();
            var manager = stage.World.EntityManager;
            foreach (var rig in stage.Rigs)
            {
                var bones = manager.GetBuffer<BoneEntity>(rig).ToNativeArray(Allocator.Temp);
                foreach (var bone in bones)
                {
                    var local = manager.GetComponentData<LocalTransform>(bone.Value);
                    values.Add(local.Position.x);
                    values.Add(local.Position.y);
                    values.Add(local.Position.z);
                    values.Add(local.Rotation.value.x);
                    values.Add(local.Rotation.value.y);
                    values.Add(local.Rotation.value.z);
                    values.Add(local.Rotation.value.w);
                }
                bones.Dispose();
            }
            return values;
        }

        static List<float> Destinations(SharedFamilyJobTests.Stage stage)
        {
            var values = new List<float>();
            var manager = stage.World.EntityManager;
            foreach (var rig in stage.Rigs)
                foreach (var bone in manager.GetBuffer<SharedBoneState>(rig))
                {
                    var pose = bone.Value.Destination;
                    values.Add(pose.Position.X);
                    values.Add(pose.Position.Y);
                    values.Add(pose.Position.Z);
                    values.Add(pose.Rotation.X);
                    values.Add(pose.Rotation.Y);
                    values.Add(pose.Rotation.Z);
                    values.Add(pose.Rotation.W);
                }
            return values;
        }

        static int Different(List<float> a, List<float> b)
        {
            Assert.AreEqual(a.Count, b.Count);
            var different = 0;
            for (var i = 0; i < a.Count; i++)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                    different++;
            return different;
        }

        static float Worst(List<float> a, List<float> b)
        {
            var worst = 0f;
            for (var i = 0; i < a.Count; i++)
                worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
            return worst;
        }

        static void Cull(SharedFamilyJobTests.Stage stage, bool culled)
        {
            foreach (var rig in stage.Rigs)
                SharedFamilyControl.SetCulled(stage.World.EntityManager, rig, culled);
        }

        [Test]
        public void ACulledRigKeepsItsClockRunningHoldsItsPoseAndResumesWhereItsClipsAre()
        {
            using (var shown = SharedFamilyJobTests.Populate(true))
            using (var culled = SharedFamilyJobTests.Populate(true))
            {
                shown.System = shown.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                culled.System = culled.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                List<float> held = null;
                var moved = 0f;
                var compared = 0;
                for (var step = 0; step < SharedFamilyJobTests.Steps; step++)
                {
                    if (step == CullFrom)
                    {
                        held = Bones(culled);
                        Cull(culled, true);
                    }
                    if (step == CullUntil)
                        Cull(culled, false);
                    SharedFamilyJobTests.Act(shown, step);
                    SharedFamilyJobTests.Act(culled, step);
                    shown.Step(SharedFamilyJobTests.Seconds(step));
                    culled.Step(SharedFamilyJobTests.Seconds(step));
                    Assert.AreEqual(0, Different(Clocks(shown), Clocks(culled)), "step " + step + ": a culled rig's clock or clip times left those of a rig that was never culled");
                    var expected = Bones(shown);
                    var actual = Bones(culled);
                    if (step >= CullFrom && step < CullUntil)
                    {
                        Assert.AreEqual(0, Different(held, actual), "step " + step + ": a culled rig moved its bones");
                        moved = Math.Max(moved, Worst(expected, held));
                        foreach (var rig in culled.Rigs)
                            Assert.IsTrue(SharedFamilyControl.IsCulled(culled.World.EntityManager, rig));
                        continue;
                    }
                    if (step < CullFrom)
                    {
                        Assert.AreEqual(0, Different(expected, actual), "step " + step + ": the two scenes differ before anything was culled");
                        continue;
                    }
                    var destinations = Destinations(culled);
                    Assert.AreEqual(0, Different(Destinations(shown), destinations), "step " + step + ": after culling the clips evaluate to another pose than in a rig that was never culled");
                    compared += destinations.Count;
                    if (step == CullUntil)
                        Assert.AreEqual(0, Different(destinations, actual), "the first frame after culling must show the clip pose itself, with no blend from the pose the rig was held in");
                }
                TestContext.WriteLine("CullingResume destination values " + compared + " moved while culled " + moved.ToString("R", CultureInfo.InvariantCulture));
                Assert.Greater(moved, 0.01f, "the rig that was never culled did not move while the other was held, so the test proves nothing");
            }
        }

        static float4x4[] Uploaded(SharedFamilyPaletteTests.Skinned scene, int skin)
        {
            var manager = scene.Stage.World.EntityManager;
            var entity = scene.Skins[skin];
            var binding = manager.GetComponentData<SkinBinding>(entity);
            var count = manager.GetComponentData<Skin>(entity).Value.Value.Bones.Length;
            Assert.Greater(binding.MatrixBase, 0u, "the skin has no place in the matrix buffer");
            var matrices = new float4x4[count];
            scene.Skinning.SkinMatrixBuffer.GetData(matrices, 0, (int)binding.MatrixBase, count);
            return matrices;
        }

        static bool Same(float4x4[] a, float4x4[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
                if (!a[i].Equals(b[i]))
                    return false;
            return true;
        }

        [Test]
        public void ACulledSkinIsNotRebuiltAndKeepsItsLastMatrices()
        {
            TestRigs.RequireGpu();
            using (var scene = SharedFamilyPaletteTests.Scene(false, false))
            {
                var manager = scene.Stage.World.EntityManager;
                for (var step = 0; step < 6; step++)
                    scene.Frame(step == 0 ? 0f : 1f / 60f);
                Assert.AreEqual(7 + 50 + 20, scene.Palette.StagedMatrices);
                var before = Uploaded(scene, 0);
                var other = Uploaded(scene, 1);
                SharedFamilyControl.SetCulled(manager, scene.Stage.Rigs[0], true);
                for (var step = 0; step < 5; step++)
                {
                    scene.Frame(1f / 60f);
                    Assert.AreEqual(50 + 20, scene.Palette.StagedMatrices, "the culled skin was rebuilt");
                    Assert.IsTrue(Same(before, Uploaded(scene, 0)), "the culled skin lost the matrices it had");
                }
                Assert.IsFalse(Same(other, Uploaded(scene, 1)), "the skin that was not culled stopped moving");
                SharedFamilyControl.SetCulled(manager, scene.Stage.Rigs[0], false);
                scene.Frame(1f / 60f);
                Assert.AreEqual(7 + 50 + 20, scene.Palette.StagedMatrices);
                Assert.IsFalse(Same(before, Uploaded(scene, 0)), "the skin did not move again once culling ended");
            }
        }

        [Test]
        public void AChangeOfLayoutRewritesCulledSkinsOnce()
        {
            TestRigs.RequireGpu();
            using (var scene = SharedFamilyPaletteTests.Scene(false, false))
            {
                var manager = scene.Stage.World.EntityManager;
                for (var step = 0; step < 4; step++)
                    scene.Frame(step == 0 ? 0f : 1f / 60f);
                SharedFamilyControl.SetCulled(manager, scene.Stage.Rigs[0], true);
                scene.Frame(1f / 60f);
                scene.Frame(1f / 60f);
                Assert.AreEqual(50 + 20, scene.Palette.StagedMatrices);
                var held = Uploaded(scene, 0);
                scene.Blobs.Add(SharedFamilyPaletteTests.Blob(41, Canonical, 12));
                SharedFamilyPaletteTests.Skin(scene, scene.Stage.Rigs[1], scene.Blobs[scene.Blobs.Count - 1]);
                scene.Frame(1f / 60f);
                Assert.IsTrue(scene.Skinning.LayoutChanged, "a new skin did not count as a change of layout");
                Assert.AreEqual(7 + 50 + 20 + 12, scene.Palette.StagedMatrices, "a change of layout must rewrite culled skins too");
                Assert.IsTrue(Same(held, Uploaded(scene, 0)), "the culled skin was rewritten with another pose");
                scene.Frame(1f / 60f);
                Assert.IsFalse(scene.Skinning.LayoutChanged);
                Assert.AreEqual(50 + 20 + 12, scene.Palette.StagedMatrices);
            }
        }

        [Test]
        public void OneUploadAndAScatterWriteTheSamePalettesAsOneUploadPerRun()
        {
            TestRigs.RequireGpu();
            var expected = TraceFixtures.Load(SharedFamilyPaletteTests.TraceName);
            foreach (var entities in new[] { true, false })
            {
                var trace = new List<float>();
                using (var scene = SharedFamilyPaletteTests.Scene(entities, entities))
                {
                    scene.Palette.DirectUploadLimit = 0;
                    var manager = scene.Stage.World.EntityManager;
                    for (var step = 0; step < SharedFamilyPaletteTests.Steps; step++)
                    {
                        if (step == 6)
                        {
                            var contributions = manager.GetBuffer<SharedContribution>(scene.Stage.Rigs[0]);
                            SharedFamilyControl.PlaySingle(ref contributions, 1);
                        }
                        scene.Frame(step == 0 ? 0f : 1f / 60f);
                        Assert.AreEqual(2, scene.Palette.UploadCalls, "the palettes did not go through the single upload");
                        SharedFamilyPaletteTests.Record(scene, trace);
                    }
                }
                Assert.AreEqual(expected.Length, trace.Count, "trace length");
                for (var i = 0; i < expected.Length; i++)
                    Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(trace[i]), "palette value " + i + " differs through the scatter (bone entities " + entities + ")");
            }
        }

        [Test]
        public void TheSwitchIsAddedOnlyWhenARigIsCulled()
        {
            using (var stage = SharedFamilyJobTests.Populate(false))
            {
                var manager = stage.World.EntityManager;
                var rig = stage.Rigs[0];
                Assert.IsFalse(SharedFamilyControl.IsCulled(manager, rig));
                SharedFamilyControl.SetCulled(manager, rig, false);
                Assert.IsFalse(manager.HasComponent<SharedCulled>(rig), "showing a rig that was never culled must not change it");
                SharedFamilyControl.SetCulled(manager, rig, true);
                Assert.IsTrue(SharedFamilyControl.IsCulled(manager, rig));
                SharedFamilyControl.SetCulled(manager, rig, false);
                Assert.IsTrue(manager.HasComponent<SharedCulled>(rig));
                Assert.IsFalse(SharedFamilyControl.IsCulled(manager, rig));
            }
        }

        [Test]
        public void ACulledRigWithoutBoneEntitiesStillReportsTheLastPoseItHad()
        {
            using (var stage = SharedFamilyJobTests.Populate(false))
            {
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                var manager = stage.World.EntityManager;
                for (var step = 0; step < 6; step++)
                    stage.Step(step == 0 ? 0f : 1f / 60f);
                var rig = stage.Rigs[1];
                var before = new NativeArray<BonePose>(Canonical, Allocator.Temp);
                var during = new NativeArray<BonePose>(Canonical, Allocator.Temp);
                var after = new NativeArray<BonePose>(Canonical, Allocator.Temp);
                Assert.IsTrue(SharedFamilyPose.TryCopyLocal(manager, rig, before));
                SharedFamilyControl.SetCulled(manager, rig, true);
                for (var step = 0; step < 20; step++)
                    stage.Step(1f / 60f);
                Assert.IsTrue(SharedFamilyPose.TryCopyLocal(manager, rig, during), "a culled rig still has the pose it was last given");
                for (var b = 0; b < Canonical; b++)
                    Assert.IsTrue(before[b].Rotation.value.Equals(during[b].Rotation.value) && before[b].Position.Equals(during[b].Position), "bone " + b + " moved while culled");
                SharedFamilyControl.SetCulled(manager, rig, false);
                stage.Step(1f / 60f);
                Assert.IsTrue(SharedFamilyPose.TryCopyLocal(manager, rig, after));
                var turned = 0f;
                for (var b = 0; b < Canonical; b++)
                    turned = math.max(turned, math.degrees(2f * math.acos(math.min(1f, math.abs(math.dot(before[b].Rotation.value, after[b].Rotation.value))))));
                Assert.Greater(turned, 1f, "the rig did not jump to where its clip is after 20 culled frames");
                Assert.AreEqual("Initialized", manager.GetComponentData<SharedPoseState>(rig).Action.ToString(), "a rig coming back from culling starts fresh, with no blend from the pose it was held in");
            }
        }

        static double Median(double[] samples)
        {
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        static string Cost(int count, int culledOfTen)
        {
            const int warm = 30;
            const int measured = 90;
            using (var scene = new SharedFamilyPaletteTests.Skinned { Stage = new SharedFamilyJobTests.Stage { World = new World("SA.Culling.Cost") } })
            {
                var stage = scene.Stage;
                var manager = stage.World.EntityManager;
                var reference = SharedFamilyBake.CanonicalReferencePoses();
                var parents = SharedFamilyBake.CanonicalParentsArray();
                var first = SharedFamilyJobTests.Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
                var second = SharedFamilyJobTests.Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
                var table = SharedFamilyJobTests.FullTable(stage, reference, parents);
                scene.Blobs.Add(SharedFamilyPaletteTests.Blob(51, Canonical, Canonical));
                for (var i = 0; i < count; i++)
                {
                    var rig = SharedFamilyJobTests.Rig(stage, parents, table, new[] { first, second }, new[] { 1f, 0f }, i * 0.001f, false);
                    SharedFamilyPaletteTests.Skin(scene, rig, scene.Blobs[0]);
                    manager.AddComponent<SharedCulled>(rig);
                    manager.SetComponentEnabled<SharedCulled>(rig, false);
                }
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                scene.Skinning = stage.World.GetOrCreateSystemManaged<SkinningSystem>();
                scene.Palette = stage.World.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
                var pose = new double[measured];
                var skinning = new double[measured];
                var palette = new double[measured];
                var total = new double[measured];
                var watch = new Stopwatch();
                for (var step = 0; step < warm + measured; step++)
                {
                    var at = step - warm;
                    if (step == 10)
                        for (var i = 0; i < count; i++)
                            manager.SetComponentEnabled<SharedCulled>(stage.Rigs[i], i % 10 < culledOfTen);
                    watch.Restart();
                    stage.Step(1f / 60f);
                    var posed = watch.Elapsed.TotalMilliseconds;
                    scene.Skinning.Update();
                    var skinned = watch.Elapsed.TotalMilliseconds;
                    scene.Palette.Update();
                    manager.CompleteAllTrackedJobs();
                    var done = watch.Elapsed.TotalMilliseconds;
                    if (at < 0)
                        continue;
                    pose[at] = posed;
                    skinning[at] = skinned - posed;
                    palette[at] = done - skinned;
                    total[at] = done;
                }
                return "{ \"Characters\": " + count + ", \"CulledPercent\": " + culledOfTen * 10 + ", \"Bones\": " + Canonical
                    + ", \"PoseMs\": " + Median(pose).ToString("F3", CultureInfo.InvariantCulture)
                    + ", \"SkinningMs\": " + Median(skinning).ToString("F3", CultureInfo.InvariantCulture)
                    + ", \"PaletteMs\": " + Median(palette).ToString("F3", CultureInfo.InvariantCulture)
                    + ", \"TotalMs\": " + Median(total).ToString("F3", CultureInfo.InvariantCulture)
                    + ", \"StagedMatrices\": " + scene.Palette.StagedMatrices
                    + ", \"Workers\": " + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " }";
            }
        }

        [Test]
        [Explicit("Prints the cost of 5000 characters without bone entities when none, half and nine in ten are culled")]
        public void CostOfCulledCharacters()
        {
            TestRigs.RequireGpu();
            foreach (var culled in new[] { 0, 5, 9 })
                TestContext.WriteLine("CullingCost " + Cost(5000, culled));
            TestContext.WriteLine("CullingCost " + Cost(40000, 9));
        }
    }
}
