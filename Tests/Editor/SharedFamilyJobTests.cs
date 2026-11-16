using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA.Editor;
using SA.Motion.Numerics;

namespace SA.Tests
{
    public class SharedFamilyJobTests
    {
        const int Canonical = 56;
        const int Steps = 36;
        const string TraceName = "SharedFamilyTrace.bytes";

        internal sealed class Stage : IDisposable
        {
            public World World;
            public SharedFamilySystem System;
            public readonly List<Entity> Rigs = new List<Entity>();
            public readonly List<BlobAssetReference<SharedClipData>> Clips = new List<BlobAssetReference<SharedClipData>>();
            public readonly List<BlobAssetReference<SharedRetargetData>> Tables = new List<BlobAssetReference<SharedRetargetData>>();
            public readonly List<BlobAssetReference<RigBlob>> Skeletons = new List<BlobAssetReference<RigBlob>>();
            public double Elapsed;

            public void Dispose()
            {
                World.Dispose();
                foreach (var blob in Clips)
                    blob.Dispose();
                foreach (var blob in Tables)
                    blob.Dispose();
                foreach (var blob in Skeletons)
                    blob.Dispose();
            }

            public void Step(float seconds)
            {
                Elapsed += seconds;
                World.SetTime(new TimeData(Elapsed, seconds));
                System.Update();
                World.EntityManager.CompleteAllTrackedJobs();
            }
        }

        static int[] SmallRoles() => new[] { 0, 1, 2, 3, 48, 49, 50 };

        internal static int[] SmallParents() => new[] { -1, 0, 1, 2, 1, 4, 5 };

        static Pose Turned(in Pose reference, Float3 axis, float radians, Float3 offset)
        {
            return new Pose
            {
                Position = reference.Position + offset,
                Rotation = Quaternion.Normalize(Quaternion.Multiply(reference.Rotation, Quaternion.AxisAngle(axis, radians))),
                Scale = Float3.One
            };
        }

        static Pose[] Motion(Pose[] reference, int frames, float bend, double phase)
        {
            var x = new Float3(1f, 0f, 0f);
            var y = new Float3(0f, 1f, 0f);
            var z = new Float3(0f, 0f, 1f);
            var samples = new Pose[frames * Canonical];
            for (var f = 0; f < frames; f++)
            {
                var angle = 2.0 * Math.PI * f / Math.Max(1, frames - 1) + phase;
                var swing = bend * (float)Math.Sin(angle);
                var lift = bend * (float)Math.Cos(angle);
                for (var c = 0; c < Canonical; c++)
                    samples[f * Canonical + c] = reference[c];
                samples[f * Canonical + 1] = Turned(reference[1], y, 0.3f * swing, new Float3(0.03f * swing, 0.05f * lift, 0.04f * swing));
                samples[f * Canonical + 2] = Turned(reference[2], x, swing, new Float3(0f, 0.01f * swing, 0f));
                samples[f * Canonical + 3] = Turned(reference[3], z, 0.5f * lift, Float3.Zero);
                samples[f * Canonical + 11] = Turned(reference[11], y, -0.5f * swing, Float3.Zero);
                samples[f * Canonical + 12] = Turned(reference[12], x, 0.7f * lift, Float3.Zero);
                samples[f * Canonical + 48] = Turned(reference[48], x, swing, Float3.Zero);
                samples[f * Canonical + 49] = Turned(reference[49], x, -0.8f * lift, Float3.Zero);
                samples[f * Canonical + 52] = Turned(reference[52], x, -swing, Float3.Zero);
                samples[f * Canonical + 53] = Turned(reference[53], z, 0.4f * lift, Float3.Zero);
            }
            return samples;
        }

        internal static BlobAssetReference<SharedClipData> Clip(Stage stage, Pose[] reference, int frames, double duration, float bend, double phase)
        {
            Assert.IsTrue(SharedFamilyBake.EncodeCanonicalSamples(Motion(reference, frames, bend, phase), frames, duration, true, out var bytes, out var error), error);
            var blob = SharedFamilyBake.MakeClipBlob(bytes, duration, frames, true);
            stage.Clips.Add(blob);
            return blob;
        }

        static BlobAssetReference<SharedRetargetData> Table(Stage stage, int[] parents, Pose[] calibration, int[] sourceMap, float heightRatio)
        {
            Assert.IsTrue(SharedFamilyBake.CompileTableBytes(parents, calibration, sourceMap, 1, 1, heightRatio, out var bytes, out var error), error);
            var blob = SharedFamilyBake.MakeTableBlob(bytes, parents, calibration, out error);
            Assert.IsTrue(blob.IsCreated, error);
            stage.Tables.Add(blob);
            return blob;
        }

        internal static BlobAssetReference<SharedRetargetData> SmallTable(Stage stage, Pose[] reference)
        {
            var roles = SmallRoles();
            var calibration = new Pose[roles.Length];
            for (var i = 0; i < roles.Length; i++)
                calibration[i] = reference[roles[i]];
            return Table(stage, SmallParents(), calibration, roles, 1f);
        }

        internal static BlobAssetReference<SharedRetargetData> FullTable(Stage stage, Pose[] reference, int[] parents)
        {
            var calibration = (Pose[])reference.Clone();
            calibration[11] = Turned(reference[11], new Float3(0f, 0f, 1f), 0.35f, Float3.Zero);
            calibration[48] = Turned(reference[48], new Float3(1f, 0f, 0f), -0.2f, Float3.Zero);
            calibration[3] = Turned(reference[3], new Float3(0f, 1f, 0f), 0.15f, Float3.Zero);
            var sourceMap = new int[Canonical];
            for (var i = 0; i < Canonical; i++)
                sourceMap[i] = i;
            return Table(stage, parents, calibration, sourceMap, 1.25f);
        }

        internal static Entity Rig(Stage stage, int[] parents, BlobAssetReference<SharedRetargetData> table, BlobAssetReference<SharedClipData>[] clips, float[] weights, float offset)
        {
            var manager = stage.World.EntityManager;
            var builder = new BlobBuilder(Allocator.Temp);
            ref var rig = ref builder.ConstructRoot<RigBlob>();
            rig.Id = new Hash128((uint)parents.Length, 7, 7, 7);
            var bones = builder.Allocate(ref rig.Bones, parents.Length);
            for (var i = 0; i < parents.Length; i++)
                bones[i] = new Bone { Parent = parents[i], Rest = BonePose.Identity, NameHash = (uint)(100 + i) };
            var skeleton = builder.CreateBlobAssetReference<RigBlob>(Allocator.Persistent);
            builder.Dispose();
            stage.Skeletons.Add(skeleton);
            var entity = manager.CreateEntity(typeof(Rig), typeof(SharedTimeline), typeof(SharedClock), typeof(SharedRig), typeof(SharedRetarget));
            manager.SetComponentData(entity, new Rig { Value = skeleton });
            manager.SetComponentData(entity, new SharedRig { NameHash = 7 });
            manager.SetComponentData(entity, new SharedRetarget { Value = table });
            manager.SetComponentData(entity, new SharedClock { Seconds = 0.0, Tick = 0, Continuity = 0, Transition = 0.2f, Seek = 0, Paused = 0 });
            var assets = manager.AddBuffer<SharedClipAsset>(entity);
            foreach (var clip in clips)
                assets.Add(new SharedClipAsset { Value = clip });
            var contributions = manager.AddBuffer<SharedContribution>(entity);
            for (var i = 0; i < clips.Length; i++)
                contributions.Add(new SharedContribution { Clip = i, Weight = weights[i], Layer = 0, Mask = ushort.MaxValue, Speed = 1f, Offset = offset, Sample = offset });
            manager.AddBuffer<BoneEntity>(entity);
            for (var i = 0; i < parents.Length; i++)
            {
                var bone = manager.CreateEntity(typeof(LocalTransform));
                manager.SetComponentData(bone, LocalTransform.FromPositionRotationScale(float3.zero, quaternion.identity, 1f));
                manager.GetBuffer<BoneEntity>(entity).Add(new BoneEntity { Value = bone });
            }
            stage.Rigs.Add(entity);
            return entity;
        }

        static Stage Scene()
        {
            var stage = Populate();
            stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
            return stage;
        }

        internal static Stage Populate()
        {
            var stage = new Stage { World = new World("SA.SharedFamilyJob") };
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            var parents = SharedFamilyBake.CanonicalParentsArray();
            Assert.AreEqual(Canonical, reference.Length);
            var first = Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
            var second = Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
            var third = Clip(stage, reference, 19, 0.75, 0.25f, 1.9);
            Rig(stage, SmallParents(), SmallTable(stage, reference), new[] { first, second }, new[] { 1f, 0f }, 0f);
            Rig(stage, parents, FullTable(stage, reference, parents), new[] { first, second }, new[] { 1f, 0f }, 0f);
            Rig(stage, SmallParents(), SmallTable(stage, reference), new[] { first, second, third }, new[] { 0.6f, 0.4f, 0f }, 0f);
            return stage;
        }

        static void Weights(Stage stage, int rig, float a, float b)
        {
            var contributions = stage.World.EntityManager.GetBuffer<SharedContribution>(stage.Rigs[rig]);
            var first = contributions[0];
            var second = contributions[1];
            first.Weight = a;
            second.Weight = b;
            contributions[0] = first;
            contributions[1] = second;
        }

        static void Act(Stage stage, int step)
        {
            var manager = stage.World.EntityManager;
            if (step == 10)
            {
                var contributions = manager.GetBuffer<SharedContribution>(stage.Rigs[0]);
                SharedFamilyControl.PlaySingle(ref contributions, 1);
                Weights(stage, 2, 0.2f, 0.8f);
            }
            if (step == 22 || step == 23)
            {
                var clock = manager.GetComponentData<SharedClock>(stage.Rigs[1]);
                SharedFamilyControl.Seek(ref clock, 1.5);
                manager.SetComponentData(stage.Rigs[1], clock);
            }
            if (step == 27 || step == 29)
            {
                var clock = manager.GetComponentData<SharedClock>(stage.Rigs[1]);
                SharedFamilyControl.SetPaused(ref clock, step == 27);
                manager.SetComponentData(stage.Rigs[1], clock);
            }
            if (step == 29)
            {
                var contributions = manager.GetBuffer<SharedContribution>(stage.Rigs[2]);
                var layered = contributions[2];
                layered.Weight = 0.5f;
                layered.Layer = 1;
                contributions[2] = layered;
            }
        }

        static float Seconds(int step)
        {
            if (step < 2 || step == 22 || step == 23)
                return 0f;
            return step == 19 ? 0.3f : 1f / 60f;
        }

        static void Record(Stage stage, List<float> trace)
        {
            var manager = stage.World.EntityManager;
            foreach (var rig in stage.Rigs)
            {
                var clock = manager.GetComponentData<SharedClock>(rig);
                trace.Add((float)clock.Seconds);
                trace.Add(clock.Tick);
                trace.Add(clock.Continuity);
                var contributions = manager.GetBuffer<SharedContribution>(rig);
                trace.Add((float)contributions[0].Sample);
                trace.Add((float)contributions[1].Sample);
                var bones = manager.GetBuffer<BoneEntity>(rig).ToNativeArray(Allocator.Temp);
                foreach (var bone in bones)
                {
                    var local = manager.GetComponentData<LocalTransform>(bone.Value);
                    trace.Add(local.Position.x);
                    trace.Add(local.Position.y);
                    trace.Add(local.Position.z);
                    trace.Add(local.Rotation.value.x);
                    trace.Add(local.Rotation.value.y);
                    trace.Add(local.Rotation.value.z);
                    trace.Add(local.Rotation.value.w);
                    trace.Add(local.Scale);
                }
                bones.Dispose();
            }
        }

        static float[] Play()
        {
            var trace = new List<float>();
            using (var stage = Scene())
            {
                for (var step = 0; step < Steps; step++)
                {
                    Act(stage, step);
                    stage.Step(Seconds(step));
                    Record(stage, trace);
                }
            }
            return trace.ToArray();
        }

        [Test]
        [Explicit(TraceFixtures.RecordReason)]
        public void RecordTheTraceFromTheCurrentSystem()
        {
            TraceFixtures.Record(TraceName, Play());
        }

        [Test]
        public void TheSystemReproducesTheTraceRecordedFromTheMainThreadSystem()
        {
            var expected = TraceFixtures.Load(TraceName);
            var actual = Play();
            Assert.AreEqual(expected.Length, actual.Length, "trace length");
            var worst = 0f;
            var worstIndex = -1;
            var exact = 0;
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
            }
            TestContext.WriteLine("TraceGap values " + expected.Length + " exact " + exact + " worst " + worst.ToString("R", CultureInfo.InvariantCulture) + " at " + worstIndex);
            Assert.LessOrEqual(worst, 2e-5f, "value " + worstIndex + " of " + expected.Length + " differs from the recorded trace");
        }

        [Test]
        public void ManyRigsWithTheSameInputsEndOnTheSamePose()
        {
            const int count = 300;
            using (var stage = new Stage { World = new World("SA.SharedFamilyJob.Many") })
            {
                var reference = SharedFamilyBake.CanonicalReferencePoses();
                var parents = SharedFamilyBake.CanonicalParentsArray();
                var first = Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
                var second = Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
                var table = FullTable(stage, reference, parents);
                for (var i = 0; i < count; i++)
                    Rig(stage, parents, table, new[] { first, second }, new[] { 1f, 0f }, 0f);
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                var manager = stage.World.EntityManager;
                for (var step = 0; step < 24; step++)
                {
                    if (step == 12)
                        foreach (var rig in stage.Rigs)
                        {
                            var contributions = manager.GetBuffer<SharedContribution>(rig);
                            SharedFamilyControl.PlaySingle(ref contributions, 1);
                        }
                    stage.Step(step == 0 ? 0f : 1f / 60f);
                }
                var expected = manager.GetBuffer<BoneEntity>(stage.Rigs[0]).ToNativeArray(Allocator.Temp);
                for (var i = 1; i < count; i++)
                {
                    var bones = manager.GetBuffer<BoneEntity>(stage.Rigs[i]).ToNativeArray(Allocator.Temp);
                    for (var b = 0; b < bones.Length; b++)
                    {
                        var a = manager.GetComponentData<LocalTransform>(expected[b].Value);
                        var c = manager.GetComponentData<LocalTransform>(bones[b].Value);
                        Assert.IsTrue(a.Position.Equals(c.Position) && a.Rotation.value.Equals(c.Rotation.value) && a.Scale == c.Scale, "rig " + i + " bone " + b + " differs from rig 0");
                    }
                    bones.Dispose();
                }
                expected.Dispose();
            }
        }

        [Test]
        public void TheRigJobRunsAsBurstCode()
        {
            using (var stage = Scene())
            {
                stage.Step(0f);
                stage.Step(1f / 60f);
                foreach (var rig in stage.Rigs)
                    Assert.AreEqual(1, stage.World.EntityManager.GetComponentData<SharedPoseState>(rig).Native, "the rig job fell back to managed code");
            }
        }

        [Test]
        public void AClipAddedToARunningRigPlaysOnTheNextUpdate()
        {
            using (var stage = Scene())
            {
                var manager = stage.World.EntityManager;
                var rig = stage.Rigs[1];
                for (var step = 0; step < 6; step++)
                    stage.Step(step == 0 ? 0f : 1f / 60f);
                var before = manager.GetComponentData<LocalTransform>(manager.GetBuffer<BoneEntity>(rig)[2].Value).Rotation;
                var added = Clip(stage, SharedFamilyBake.CanonicalReferencePoses(), 25, 1.5, 0.9f, 2.4);
                var clips = manager.GetBuffer<SharedClipAsset>(rig);
                var index = clips.Length;
                clips.Add(new SharedClipAsset { Value = added });
                var contributions = manager.GetBuffer<SharedContribution>(rig);
                contributions.Add(new SharedContribution { Clip = index, Weight = 1f, Layer = 0, Mask = ushort.MaxValue, Speed = 1f, Offset = 0f, Sample = 0.0 });
                SharedFamilyControl.PlaySingle(ref contributions, index);
                for (var step = 0; step < 30; step++)
                    stage.Step(1f / 60f);
                Assert.AreEqual(1, manager.GetComponentData<SharedPoseState>(rig).Active);
                var layouts = manager.GetBuffer<SharedClipLayoutRef>(rig);
                Assert.AreEqual(index + 1, layouts.Length);
                Assert.IsTrue(layouts[index].Value.IsCreated, "the added clip was not resolved");
                Assert.Greater(manager.GetBuffer<SharedContribution>(rig)[index].Sample, 0.4);
                var after = manager.GetComponentData<LocalTransform>(manager.GetBuffer<BoneEntity>(rig)[2].Value).Rotation;
                var turned = math.degrees(2f * math.acos(math.min(1f, math.abs(math.dot(before.value, after.value)))));
                Assert.Greater(turned, 1f, "the added clip did not move the spine");
            }
        }

        [Test]
        [Explicit("Prints the cost of SharedFamilySystem for 1000 rigs of 56 bones")]
        public void CostOfTheSharedFamilySystemPerRig()
        {
            const int count = 1000;
            const int warm = 30;
            const int measured = 120;
            using (var stage = new Stage { World = new World("SA.SharedFamilyJob.Cost") })
            {
                var reference = SharedFamilyBake.CanonicalReferencePoses();
                var parents = SharedFamilyBake.CanonicalParentsArray();
                var first = Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
                var second = Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
                var table = FullTable(stage, reference, parents);
                for (var i = 0; i < count; i++)
                    Rig(stage, parents, table, new[] { first, second }, new[] { 1f, 0f }, i * 0.001f);
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                for (var step = 0; step < warm; step++)
                    stage.Step(1f / 60f);
                var samples = new double[measured];
                var watch = new Stopwatch();
                for (var step = 0; step < measured; step++)
                {
                    watch.Restart();
                    stage.Step(1f / 60f);
                    watch.Stop();
                    samples[step] = watch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(samples);
                var total = 0.0;
                foreach (var sample in samples)
                    total += sample;
                var mean = total / measured;
                var median = samples[measured / 2];
                var report = "{\n  \"Rigs\": " + count + ",\n  \"Bones\": " + Canonical
                    + ",\n  \"MeanMsPerFrame\": " + mean.ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"MedianMsPerFrame\": " + median.ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"P95MsPerFrame\": " + samples[(int)(measured * 0.95)].ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"MedianMicrosecondsPerRig\": " + (median * 1000.0 / count).ToString("F3", CultureInfo.InvariantCulture)
                    + ",\n  \"Workers\": " + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount
                    + ",\n  \"Cpu\": \"" + UnityEngine.SystemInfo.processorType + "\"\n}\n";
                Directory.CreateDirectory("Temp/sa");
                File.WriteAllText("Temp/sa/shared_family_cost.json", report);
                TestContext.WriteLine(report);
                Assert.Greater(median, 0.0);
            }
        }
    }
}
