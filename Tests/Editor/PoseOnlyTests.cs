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
    public class PoseOnlyTests
    {
        const int Canonical = 56;

        static void Write(List<float> trace, in BonePose pose)
        {
            trace.Add(pose.Position.x);
            trace.Add(pose.Position.y);
            trace.Add(pose.Position.z);
            trace.Add(pose.Rotation.value.x);
            trace.Add(pose.Rotation.value.y);
            trace.Add(pose.Rotation.value.z);
            trace.Add(pose.Rotation.value.w);
            trace.Add(pose.Scale.x);
        }

        static float[] PlayPoses(bool entities, out int waiting)
        {
            var trace = new List<float>();
            waiting = 0;
            using (var stage = SharedFamilyJobTests.Populate(entities))
            {
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                var manager = stage.World.EntityManager;
                for (var step = 0; step < SharedFamilyJobTests.Steps; step++)
                {
                    SharedFamilyJobTests.Act(stage, step);
                    stage.Step(SharedFamilyJobTests.Seconds(step));
                    foreach (var rig in stage.Rigs)
                    {
                        var clock = manager.GetComponentData<SharedClock>(rig);
                        trace.Add((float)clock.Seconds);
                        trace.Add(clock.Tick);
                        trace.Add(clock.Continuity);
                        var contributions = manager.GetBuffer<SharedContribution>(rig);
                        trace.Add((float)contributions[0].Sample);
                        trace.Add((float)contributions[1].Sample);
                        var count = SharedFamilyPose.BoneCount(manager, rig);
                        var local = new NativeArray<BonePose>(count, Allocator.Temp);
                        if (!SharedFamilyPose.TryCopyLocal(manager, rig, local))
                        {
                            waiting++;
                            for (var b = 0; b < count; b++)
                                local[b] = BonePose.Identity;
                        }
                        for (var b = 0; b < count; b++)
                            Write(trace, local[b]);
                        local.Dispose();
                    }
                }
            }
            return trace.ToArray();
        }

        static int Different(float[] expected, float[] actual, out int first)
        {
            Assert.AreEqual(expected.Length, actual.Length, "trace length");
            var different = 0;
            first = -1;
            for (var i = 0; i < expected.Length; i++)
                if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
                {
                    if (first < 0)
                        first = i;
                    different++;
                }
            return different;
        }

        static float Worst(float[] expected, float[] actual, out int exact)
        {
            Assert.AreEqual(expected.Length, actual.Length, "trace length");
            var worst = 0f;
            exact = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                var gap = Math.Abs(expected[i] - actual[i]);
                if (gap == 0f)
                    exact++;
                worst = Math.Max(worst, gap);
            }
            return worst;
        }

        [Test]
        public void ARigWithoutBoneEntitiesEvaluatesTheRecordedPoses()
        {
            var poseOnly = PlayPoses(false, out var waiting);
            Assert.AreEqual(0, waiting, "a rig had no pose to read after an update");
            var expected = TraceFixtures.Load(SharedFamilyJobTests.TraceName);
            var worst = Worst(expected, poseOnly, out var exact);
            TestContext.WriteLine("PoseOnlyTraceGap values " + expected.Length + " exact " + exact + " worst " + worst.ToString("R", CultureInfo.InvariantCulture));
            Assert.LessOrEqual(worst, 2e-5f, "the pose kept on the rig differs from the recorded bone transforms");
        }

        [Test]
        public void ARigWithoutBoneEntitiesEvaluatesExactlyWhatBoneEntitiesReceive()
        {
            var poseOnly = PlayPoses(false, out _);
            var withBones = PlayPoses(true, out _);
            var different = Different(withBones, poseOnly, out var first);
            Assert.AreEqual(0, different, "value " + first + " of " + withBones.Length + " is not bit for bit the same in the two modes");
        }

        [Test]
        public void ASkinOnARigWithoutBoneEntitiesUploadsTheRecordedPalettes()
        {
            TestRigs.RequireGpu();
            var expected = TraceFixtures.Load(SharedFamilyPaletteTests.TraceName);
            var poseOnly = SharedFamilyPaletteTests.Play(false, false);
            var worst = Worst(expected, poseOnly, out var exact);
            TestContext.WriteLine("PoseOnlyPaletteGap values " + expected.Length + " exact " + exact + " worst " + worst.ToString("R", CultureInfo.InvariantCulture));
            Assert.LessOrEqual(worst, 2e-5f, "the palettes built from the pose kept on the rig differ from the recorded ones");
            var withBones = SharedFamilyPaletteTests.Play(true, true);
            var different = Different(withBones, poseOnly, out var first);
            Assert.AreEqual(0, different, "palette value " + first + " is not bit for bit the same in the two modes");
        }

        [Test]
        public void RigsWithAndWithoutBoneEntitiesShareOneWorld()
        {
            TestRigs.RequireGpu();
            var withBones = SharedFamilyPaletteTests.Play(true, true);
            var mixed = SharedFamilyPaletteTests.Play(false, true);
            Assert.AreEqual(0, Different(withBones, mixed, out var first), "palette value " + first + " changed when one rig lost its bone entities");
            var other = SharedFamilyPaletteTests.Play(true, false);
            Assert.AreEqual(0, Different(withBones, other, out first), "palette value " + first + " changed when the other rig lost its bone entities");
        }

        [Test]
        public void ARigWithoutBoneEntitiesRunsAsBurstCodeAndCreatesNoBones()
        {
            using (var stage = SharedFamilyJobTests.Populate(false))
            {
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                stage.Step(0f);
                stage.Step(1f / 60f);
                var manager = stage.World.EntityManager;
                foreach (var rig in stage.Rigs)
                {
                    Assert.AreEqual(1, manager.GetComponentData<SharedPoseState>(rig).Native, "the rig job fell back to managed code");
                    Assert.IsFalse(manager.HasBuffer<BoneEntity>(rig));
                }
                using (var transforms = manager.CreateEntityQuery(typeof(LocalTransform)))
                    Assert.AreEqual(0, transforms.CalculateEntityCount(), "something created bone entities");
            }
        }

        [Test]
        public void ThePoseCanBeReadFromTheRig()
        {
            using (var withBones = SharedFamilyJobTests.Populate(true))
            using (var poseOnly = SharedFamilyJobTests.Populate(false))
            {
                withBones.System = withBones.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                poseOnly.System = poseOnly.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                var bones = withBones.World.EntityManager;
                var manager = poseOnly.World.EntityManager;
                var early = new NativeArray<BonePose>(Canonical, Allocator.Temp);
                Assert.IsFalse(SharedFamilyPose.TryCopyLocal(manager, poseOnly.Rigs[1], early), "a rig that never updated has no pose");
                Assert.IsFalse(SharedFamilyPose.TryCopyLocal(manager, Entity.Null, early));
                for (var step = 0; step < 12; step++)
                {
                    SharedFamilyJobTests.Act(withBones, step);
                    SharedFamilyJobTests.Act(poseOnly, step);
                    withBones.Step(SharedFamilyJobTests.Seconds(step));
                    poseOnly.Step(SharedFamilyJobTests.Seconds(step));
                }
                for (var r = 0; r < poseOnly.Rigs.Count; r++)
                {
                    var rig = poseOnly.Rigs[r];
                    var count = SharedFamilyPose.BoneCount(manager, rig);
                    Assert.AreEqual(bones.GetBuffer<BoneEntity>(withBones.Rigs[r]).Length, count);
                    var local = new NativeArray<BonePose>(count, Allocator.Temp);
                    var model = new NativeArray<float4x4>(count, Allocator.Temp);
                    Assert.IsTrue(SharedFamilyPose.TryCopyLocal(manager, rig, local));
                    Assert.IsTrue(SharedFamilyPose.TryCopyModel(manager, rig, model));
                    Assert.IsFalse(SharedFamilyPose.TryCopyLocal(manager, rig, new NativeArray<BonePose>(count - 1, Allocator.Temp)), "a short array must be refused");
                    Assert.IsFalse(SharedFamilyPose.TryCopyModel(manager, rig, new NativeArray<float4x4>(count - 1, Allocator.Temp)), "a short array must be refused");
                    var same = new NativeArray<BonePose>(count, Allocator.Temp);
                    Assert.IsTrue(SharedFamilyPose.TryCopyLocal(bones, withBones.Rigs[r], same), "a rig with bone entities keeps its pose too");
                    var entities = bones.GetBuffer<BoneEntity>(withBones.Rigs[r]).ToNativeArray(Allocator.Temp);
                    var reference = bones.GetComponentData<Rig>(withBones.Rigs[r]).Value;
                    var expected = new float4x4[count];
                    for (var b = 0; b < count; b++)
                    {
                        var transform = bones.GetComponentData<LocalTransform>(entities[b].Value);
                        Assert.IsTrue(transform.Position.Equals(local[b].Position) && transform.Rotation.value.Equals(local[b].Rotation.value) && transform.Scale == local[b].Scale.x,
                            "rig " + r + " bone " + b + " reads differently from its bone entity");
                        Assert.IsTrue(same[b].Position.Equals(local[b].Position) && same[b].Rotation.value.Equals(local[b].Rotation.value));
                        var matrix = float4x4.TRS(transform.Position, transform.Rotation, new float3(transform.Scale));
                        var parent = reference.Value.Bones[b].Parent;
                        expected[b] = parent >= 0 ? math.mul(expected[parent], matrix) : matrix;
                        for (var column = 0; column < 4; column++)
                            for (var row = 0; row < 4; row++)
                                Assert.AreEqual(expected[b][column][row], model[b][column][row], 1e-6f, "rig " + r + " bone " + b + " model matrix");
                    }
                    entities.Dispose();
                }
            }
        }

        static BonePose[] Rest(int count, float scale)
        {
            var rest = new BonePose[count];
            for (var i = 0; i < count; i++)
                rest[i] = new BonePose
                {
                    Position = new float3(0.1f * i, 0.2f, -0.05f * i),
                    Rotation = quaternion.Euler(0.1f * i, 0.2f, -0.3f * i),
                    Scale = scale
                };
            return rest;
        }

        static SharedFamilyPaletteTests.Skinned Resting(BonePose[] rest, out Entity rig, out Entity skin)
        {
            var scene = new SharedFamilyPaletteTests.Skinned { Stage = new SharedFamilyJobTests.Stage { World = new World("SA.PoseOnly.Rest") } };
            var stage = scene.Stage;
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            var clip = SharedFamilyJobTests.Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
            rig = SharedFamilyJobTests.Rig(stage, SharedFamilyJobTests.SmallParents(), SharedFamilyJobTests.SmallTable(stage, reference), new[] { clip }, new[] { 0f }, 0f, false, rest);
            scene.Blobs.Add(SharedFamilyPaletteTests.Blob(21, rest.Length, rest.Length));
            skin = SharedFamilyPaletteTests.Skin(scene, rig, scene.Blobs[0]);
            stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
            scene.Skinning = stage.World.GetOrCreateSystemManaged<SkinningSystem>();
            scene.Palette = stage.World.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
            return scene;
        }

        static float4x4[] Uploaded(SharedFamilyPaletteTests.Skinned scene, Entity skin)
        {
            var manager = scene.Stage.World.EntityManager;
            var binding = manager.GetComponentData<SkinBinding>(skin);
            var count = manager.GetComponentData<Skin>(skin).Value.Value.Bones.Length;
            Assert.Greater(binding.MatrixBase, 0u, "the skin has no place in the matrix buffer");
            var matrices = new float4x4[count];
            scene.Skinning.SkinMatrixBuffer.GetData(matrices, 0, (int)binding.MatrixBase, count);
            return matrices;
        }

        static float Gap(in float4x4 a, in float4x4 b)
        {
            var gap = 0f;
            for (var column = 0; column < 4; column++)
                for (var row = 0; row < 4; row++)
                    gap = math.max(gap, math.abs(a[column][row] - b[column][row]));
            return gap;
        }

        [Test]
        public void ARigWithoutBoneEntitiesThatHasNotPlayedShowsItsRestPose()
        {
            TestRigs.RequireGpu();
            var rest = Rest(7, 1f);
            using (var scene = Resting(rest, out var rig, out var skin))
            {
                scene.Frame(0f);
                scene.Frame(1f / 60f);
                var manager = scene.Stage.World.EntityManager;
                Assert.IsFalse(SharedFamilyPose.TryCopyLocal(manager, rig, new NativeArray<BonePose>(7, Allocator.Temp)), "nothing has played yet");
                var reference = scene.Blobs[0];
                ref var blob = ref reference.Value;
                var parents = SharedFamilyJobTests.SmallParents();
                var model = new float4x4[7];
                for (var b = 0; b < 7; b++)
                {
                    var matrix = float4x4.TRS(rest[b].Position, rest[b].Rotation, rest[b].Scale);
                    model[b] = parents[b] >= 0 ? math.mul(model[parents[b]], matrix) : matrix;
                }
                var resting = Uploaded(scene, skin);
                for (var k = 0; k < resting.Length; k++)
                {
                    var expected = math.mul(math.mul(GpuFrame.Pack(blob.MeshFromRig), model[blob.Bones[k]]), GpuFrame.Pack(blob.Bindposes[k]));
                    Assert.LessOrEqual(Gap(expected, resting[k]), 1e-5f, "palette bone " + k + " is not in the rest pose");
                }
                var contributions = manager.GetBuffer<SharedContribution>(rig);
                SharedFamilyControl.PlaySingle(ref contributions, 0);
                for (var step = 0; step < 6; step++)
                    scene.Frame(1f / 60f);
                Assert.IsTrue(SharedFamilyPose.TryCopyLocal(manager, rig, new NativeArray<BonePose>(7, Allocator.Temp)));
                var playing = Uploaded(scene, skin);
                var moved = 0f;
                for (var k = 0; k < playing.Length; k++)
                    moved = math.max(moved, Gap(resting[k], playing[k]));
                Assert.Greater(moved, 0.01f, "the palette did not leave the rest pose once a clip played");
            }
        }

        [Test]
        public void ARigWithoutBoneEntitiesAndNoUsablePoseShowsItsMeshAsBound()
        {
            TestRigs.RequireGpu();
            using (var scene = Resting(Rest(7, 2f), out _, out var skin))
            {
                scene.Frame(0f);
                scene.Frame(1f / 60f);
                foreach (var matrix in Uploaded(scene, skin))
                    Assert.AreEqual(0f, Gap(GpuFrame.Identity, matrix), "a palette that cannot be built must be the identity, never leftovers");
                Assert.GreaterOrEqual(scene.Palette.UploadCalls, 1);
            }
        }

        [Test]
        public void RigsWithoutBoneEntitiesSendNoBoneMatricesToTheGpu()
        {
            TestRigs.RequireGpu();
            using (var scene = SharedFamilyPaletteTests.Scene(true, true))
            {
                scene.Frame(0f);
                Assert.AreEqual(7 + Canonical, scene.Skinning.CpuPoseCount);
            }
            using (var scene = SharedFamilyPaletteTests.Scene(false, true))
            {
                scene.Frame(0f);
                Assert.AreEqual(Canonical, scene.Skinning.CpuPoseCount);
            }
            using (var scene = SharedFamilyPaletteTests.Scene(false, false))
            {
                scene.Frame(0f);
                Assert.AreEqual(0, scene.Skinning.CpuPoseCount);
                Assert.AreEqual(7 + 50 + 20, scene.Palette.StagedMatrices);
            }
        }

        static double Median(double[] samples)
        {
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        static string Cost(bool entities, int count)
        {
            const int warm = 30;
            const int measured = 90;
            using (var scene = new SharedFamilyPaletteTests.Skinned { Stage = new SharedFamilyJobTests.Stage { World = new World("SA.PoseOnly.Cost") } })
            {
                var stage = scene.Stage;
                var manager = stage.World.EntityManager;
                var reference = SharedFamilyBake.CanonicalReferencePoses();
                var parents = SharedFamilyBake.CanonicalParentsArray();
                var first = SharedFamilyJobTests.Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
                var second = SharedFamilyJobTests.Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
                var table = SharedFamilyJobTests.FullTable(stage, reference, parents);
                scene.Blobs.Add(SharedFamilyPaletteTests.Blob(31, Canonical, Canonical));
                for (var i = 0; i < count; i++)
                {
                    var rig = SharedFamilyJobTests.Rig(stage, parents, table, new[] { first, second }, new[] { 1f, 0f }, i * 0.001f, entities);
                    SharedFamilyPaletteTests.Skin(scene, rig, scene.Blobs[0]);
                    manager.AddComponentData(rig, LocalTransform.FromPosition(new float3(i % 40, 0f, i / 40)));
                    if (!entities)
                        continue;
                    var bones = manager.GetBuffer<BoneEntity>(rig).ToNativeArray(Allocator.Temp);
                    for (var b = 0; b < bones.Length; b++)
                    {
                        manager.AddComponentData(bones[b].Value, new Parent { Value = parents[b] >= 0 ? bones[parents[b]].Value : rig });
                        manager.AddComponentData(bones[b].Value, new LocalToWorld { Value = float4x4.identity });
                    }
                    bones.Dispose();
                }
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                scene.Skinning = stage.World.GetOrCreateSystemManaged<SkinningSystem>();
                scene.Palette = stage.World.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
                var transforms = stage.World.GetOrCreateSystemManaged<TransformSystemGroup>();
                transforms.AddSystemToUpdateList(stage.World.GetOrCreateSystem<ParentSystem>());
                transforms.AddSystemToUpdateList(stage.World.GetOrCreateSystem<LocalToWorldSystem>());
                transforms.SortSystems();
                var pose = new double[measured];
                var hierarchy = new double[measured];
                var skinning = new double[measured];
                var palette = new double[measured];
                var total = new double[measured];
                var watch = new Stopwatch();
                for (var step = 0; step < warm + measured; step++)
                {
                    var at = step - warm;
                    watch.Restart();
                    stage.Step(1f / 60f);
                    var posed = watch.Elapsed.TotalMilliseconds;
                    transforms.Update();
                    manager.CompleteAllTrackedJobs();
                    var moved = watch.Elapsed.TotalMilliseconds;
                    scene.Skinning.Update();
                    var skinned = watch.Elapsed.TotalMilliseconds;
                    scene.Palette.Update();
                    manager.CompleteAllTrackedJobs();
                    var done = watch.Elapsed.TotalMilliseconds;
                    if (at < 0)
                        continue;
                    pose[at] = posed;
                    hierarchy[at] = moved - posed;
                    skinning[at] = skinned - moved;
                    palette[at] = done - skinned;
                    total[at] = done;
                }
                using (var plain = manager.UniversalQuery.ToEntityArray(Allocator.Temp))
                {
                    return "{ \"BoneEntities\": " + (entities ? "true" : "false") + ", \"Characters\": " + count + ", \"Bones\": " + Canonical
                        + ", \"Entities\": " + plain.Length
                        + ", \"PoseMs\": " + Median(pose).ToString("F3", CultureInfo.InvariantCulture)
                        + ", \"TransformMs\": " + Median(hierarchy).ToString("F3", CultureInfo.InvariantCulture)
                        + ", \"SkinningMs\": " + Median(skinning).ToString("F3", CultureInfo.InvariantCulture)
                        + ", \"PaletteMs\": " + Median(palette).ToString("F3", CultureInfo.InvariantCulture)
                        + ", \"TotalMs\": " + Median(total).ToString("F3", CultureInfo.InvariantCulture)
                        + ", \"Workers\": " + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " }";
                }
            }
        }

        [Test]
        [Explicit("Prints the cost of 1000 and 5000 shared-family characters with and without bone entities")]
        public void CostWithAndWithoutBoneEntities()
        {
            TestRigs.RequireGpu();
            foreach (var count in new[] { 1000, 5000 })
            {
                TestContext.WriteLine("PoseOnlyCost " + Cost(true, count));
                TestContext.WriteLine("PoseOnlyCost " + Cost(false, count));
            }
        }
    }
}
