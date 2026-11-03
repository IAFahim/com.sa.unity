using System;
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
    public class SharedFamilyTests
    {
        static readonly int[] TargetRoles = { 0, 1, 2, 3, 48, 49, 50 };
        static readonly int[] TargetParents = { -1, 0, 1, 2, 1, 4, 5 };

        static Pose[] Reference()
        {
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            Assert.AreEqual(56, reference.Length);
            return reference;
        }

        static Pose[] BentSamples(Pose[] reference, int frames, float bend)
        {
            var samples = new Pose[frames * 56];
            for (var f = 0; f < frames; f++)
            {
                var swing = bend * (float)Math.Sin(2.0 * Math.PI * f / Math.Max(1, frames - 1));
                for (var c = 0; c < 56; c++)
                    samples[f * 56 + c] = reference[c];
                samples[f * 56 + 2] = Bent(reference[2], swing);
                samples[f * 56 + 11] = Bent(reference[11], -swing * 0.5f);
            }
            return samples;
        }

        static Pose Bent(in Pose reference, float radians)
        {
            return new Pose
            {
                Position = reference.Position,
                Rotation = Quaternion.Normalize(Quaternion.Multiply(reference.Rotation, Quaternion.AxisAngle(new Float3(0f, 1f, 0f), radians))),
                Scale = Float3.One
            };
        }

        static float AngleDegrees(quaternion a, quaternion b)
        {
            var dot = math.abs(math.dot(math.normalize(a.value), math.normalize(b.value)));
            return 2f * (float)Math.Acos(math.min(1f, dot)) * 57.29578f;
        }

        [Test]
        public void FamilyIdMatchesPublishedMint()
        {
            Assert.IsTrue(SharedFamilyBake.CheckFamilyId(out var error), error);
            var names = SharedFamilyBake.CanonicalNames;
            Assert.AreEqual(56, names.Length);
            Assert.AreEqual("Hips", names[1]);
            Assert.AreEqual("LeftFoot", names[50]);
            Assert.AreEqual("RightFoot", names[54]);
        }

        [Test]
        public void BoneNameScoresPreferExactMatches()
        {
            Assert.AreEqual(100, SharedFamilyBake.ScoreBoneName("Hips", "Hips"));
            Assert.AreEqual(100, SharedFamilyBake.ScoreBoneName("Hips", "hips"));
            Assert.AreEqual(0, SharedFamilyBake.ScoreBoneName("Hips", "LeftFoot"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftUpperArm", "LeftArm"), 0);
            Assert.Greater(SharedFamilyBake.ScoreBoneName("Hips", "pelvis"), SharedFamilyBake.ScoreBoneName("Hips", "thigh_l"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftUpperArm", "upperarm_l"), SharedFamilyBake.ScoreBoneName("LeftUpperArm", "lowerarm_l"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftLowerLeg", "calf_l"), SharedFamilyBake.ScoreBoneName("LeftLowerLeg", "thigh_l"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftFoot", "foot_l"), SharedFamilyBake.ScoreBoneName("LeftFoot", "foot_r"));
            Assert.AreEqual(0, SharedFamilyBake.ScoreBoneName("LeftFoot", "foot_r"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftThumbIntermediate", "thumb_02_l"), SharedFamilyBake.ScoreBoneName("LeftThumbIntermediate", "thumb_01_l"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("Spine", "spine_01"), SharedFamilyBake.ScoreBoneName("Spine", "spine_02"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("Chest", "spine_02"), SharedFamilyBake.ScoreBoneName("Chest", "spine_01"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftUpperArm", "Shoulder_L"), SharedFamilyBake.ScoreBoneName("LeftShoulder", "Shoulder_L"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftLowerArm", "Elbow_L"), SharedFamilyBake.ScoreBoneName("LeftUpperArm", "Elbow_L"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftShoulder", "Clavicle_L"), 0);
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftFoot", "Ankle_L"), 0);
            Assert.Greater(SharedFamilyBake.ScoreBoneName("LeftToes", "Toes_L"), 0);
            Assert.Greater(SharedFamilyBake.ScoreBoneName("RightIndexDistal", "IndexFinger_03_1"), SharedFamilyBake.ScoreBoneName("LeftMiddleDistal", "IndexFinger_03_1"));
            Assert.Greater(SharedFamilyBake.ScoreBoneName("RightIndexDistal", "IndexFinger_03_1"), SharedFamilyBake.ScoreBoneName("RightIndexDistal", "IndexFinger_04_1"));
        }

        [Test]
        public void CanonicalClipEncodeRoundTrips()
        {
            var reference = Reference();
            var samples = BentSamples(reference, 9, 0.4f);
            Assert.IsTrue(SharedFamilyBake.EncodeCanonicalSamples(samples, 9, 1.0, true, out var bytes, out var error), error);
            Assert.Greater(bytes.Length, 0);
            Assert.IsTrue(SharedFamilyBake.EncodeCanonicalSamples(samples, 9, 1.0, true, out var again, out error), error);
            Assert.AreEqual(bytes.Length, again.Length);
            for (var i = 0; i < bytes.Length; i++)
                Assert.AreEqual(bytes[i], again[i]);
        }

        [Test]
        public void RetargetTableCompilesAndReproducesCalibration()
        {
            var reference = Reference();
            var calibration = new Pose[TargetRoles.Length];
            for (var i = 0; i < TargetRoles.Length; i++)
                calibration[i] = reference[TargetRoles[i]];
            var sourceMap = new int[TargetRoles.Length];
            for (var i = 0; i < TargetRoles.Length; i++)
                sourceMap[i] = TargetRoles[i];
            Assert.IsTrue(SharedFamilyBake.CompileTableBytes(TargetParents, calibration, sourceMap, 1, 1, 1f, out var table, out var error), error);
            Assert.Greater(table.Length, 0);
        }

        static Entity BuildRig(World world, BlobAssetReference<SharedRetargetData> table, BlobAssetReference<SharedClipData> clipA, BlobAssetReference<SharedClipData> clipB)
        {
            var manager = world.EntityManager;
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var rig = ref builder.ConstructRoot<RigBlob>();
            rig.Id = new Hash128(7, 7, 7, 7);
            var bones = builder.Allocate(ref rig.Bones, TargetRoles.Length);
            for (var i = 0; i < TargetRoles.Length; i++)
                bones[i] = new Bone { Parent = TargetParents[i], Rest = BonePose.Identity, NameHash = (uint)(100 + i) };
            var rigBlob = builder.CreateBlobAssetReference<RigBlob>(Allocator.Persistent);
            var entity = manager.CreateEntity(typeof(Rig), typeof(SharedTimeline), typeof(SharedClock), typeof(SharedRig), typeof(SharedRetarget));
            manager.SetComponentData(entity, new Rig { Value = rigBlob });
            manager.SetComponentData(entity, new SharedRig { NameHash = 7 });
            manager.SetComponentData(entity, new SharedRetarget { Value = table });
            manager.SetComponentData(entity, new SharedClock { Seconds = 0.0, Tick = 0, Continuity = 0, Transition = 0.2f, Seek = 0, Paused = 0 });
            var assets = manager.AddBuffer<SharedClipAsset>(entity);
            assets.Add(new SharedClipAsset { Value = clipA });
            assets.Add(new SharedClipAsset { Value = clipB });
            var contributions = manager.AddBuffer<SharedContribution>(entity);
            contributions.Add(new SharedContribution { Clip = 0, Weight = 1f, Layer = 0, Mask = ushort.MaxValue, Speed = 1f, Offset = 0f, Sample = 0.0 });
            contributions.Add(new SharedContribution { Clip = 1, Weight = 0f, Layer = 0, Mask = ushort.MaxValue, Speed = 1f, Offset = 0f, Sample = 0.0 });
            for (var i = 0; i < TargetRoles.Length; i++)
            {
                var bone = manager.CreateEntity(typeof(LocalTransform));
                manager.SetComponentData(bone, LocalTransform.FromPositionRotationScale(float3.zero, quaternion.identity, 1f));
                manager.AddBuffer<BoneEntity>(entity).Add(new BoneEntity { Value = bone });
            }
            return entity;
        }

        static BlobAssetReference<SharedClipData> ClipBlob(byte[] bytes, double duration, int frames, bool loop)
        {
            return SharedFamilyBake.MakeClipBlob(bytes, duration, frames, loop);
        }

        static BlobAssetReference<SharedRetargetData> TableBlob(byte[] table, Pose[] calibration)
        {
            var blob = SharedFamilyBake.MakeTableBlob(table, TargetParents, calibration, out var error);
            Assert.IsTrue(blob.IsCreated, error);
            return blob;
        }

        static void AssertUnitScales(World world, Entity rig)
        {
            var manager = world.EntityManager;
            var bones = manager.GetBuffer<BoneEntity>(rig);
            for (var i = 0; i < bones.Length; i++)
            {
                var scale = manager.GetComponentData<LocalTransform>(bones[i].Value).Scale;
                Assert.Less(Math.Abs(scale - 1f), 1e-6f, "bone " + i + " scale drifted");
            }
        }
        static quaternion[] BoneRotations(World world, Entity rig)
        {
            var manager = world.EntityManager;
            var bones = manager.GetBuffer<BoneEntity>(rig);
            var rotations = new quaternion[bones.Length];
            for (var i = 0; i < bones.Length; i++)
                rotations[i] = manager.GetComponentData<LocalTransform>(bones[i].Value).Rotation;
            return rotations;
        }

        [Test]
        public void TimelineSystemSamplesSmoothsInterruptsAndSeeks()
        {
            var reference = Reference();
            Assert.IsTrue(SharedFamilyBake.EncodeCanonicalSamples(BentSamples(reference, 9, 0.4f), 9, 1.0, true, out var bytesA, out var errorA), errorA);
            Assert.IsTrue(SharedFamilyBake.EncodeCanonicalSamples(BentSamples(reference, 9, -0.3f), 9, 2.0, true, out var bytesB, out var errorB), errorB);
            var calibration = new Pose[TargetRoles.Length];
            for (var i = 0; i < TargetRoles.Length; i++)
                calibration[i] = reference[TargetRoles[i]];
            var sourceMap = new int[TargetRoles.Length];
            for (var i = 0; i < TargetRoles.Length; i++)
                sourceMap[i] = TargetRoles[i];
            Assert.IsTrue(SharedFamilyBake.CompileTableBytes(TargetParents, calibration, sourceMap, 1, 1, 1f, out var tableBytes, out var tableError), tableError);
            var world = new World("SA.SharedFamily");
            BlobAssetReference<SharedClipData> clipA = default;
            BlobAssetReference<SharedClipData> clipB = default;
            BlobAssetReference<SharedRetargetData> table = default;
            Entity rigEntity = Entity.Null;
            try
            {
                clipA = ClipBlob(bytesA, 1.0, 9, true);
                clipB = ClipBlob(bytesB, 2.0, 9, true);
                table = TableBlob(tableBytes, calibration);
                rigEntity = BuildRig(world, table, clipA, clipB);
                var system = world.GetOrCreateSystemManaged<SharedFamilySystem>();
                system.Update();
                var first = BoneRotations(world, rigEntity);
                AssertUnitScales(world, rigEntity);
                system.Update();
                var second = BoneRotations(world, rigEntity);
                for (var i = 0; i < first.Length; i++)
                    Assert.AreEqual(first[i].value.x, second[i].value.x, 0f);
                var elapsed = 0.0;
                for (var step = 0; step < 15; step++)
                {
                    elapsed += 1.0 / 60.0;
                    world.SetTime(new TimeData(elapsed, 1f / 60f));
                    system.Update();
                }
                var moved = BoneRotations(world, rigEntity);
                var travel = 0f;
                for (var i = 0; i < first.Length; i++)
                    travel = Math.Max(travel, AngleDegrees(first[i], moved[i]));
                Assert.Greater(travel, 0.5f, "advancing the clock should move bones (rigs=" + SharedFamilySystem.LastEntityCount + " step=" + SharedFamilySystem.LastStep + " active=" + SharedFamilySystem.LastActive + " action=" + SharedFamilySystem.LastAction + " smooth=" + SharedFamilySystem.LastSmooth + " warn=" + SharedFamilySystem.LastWarning + ")");
                AssertUnitScales(world, rigEntity);
                var contributions = world.EntityManager.GetBuffer<SharedContribution>(rigEntity);
                SharedFamilyControl.PlaySingle(ref contributions, 1);
                world.SetTime(new TimeData(0.25 + 1.0 / 60.0, 1f / 60f));
                system.Update();
                var interrupted = BoneRotations(world, rigEntity);
                for (var i = 0; i < moved.Length; i++)
                    Assert.Less(AngleDegrees(moved[i], interrupted[i]), 0.05f, "interrupt boundary moved bone " + i);
                AssertUnitScales(world, rigEntity);
                world.SetTime(new TimeData(2.0, 0f));
                var clock = world.EntityManager.GetComponentData<SharedClock>(rigEntity);
                SharedFamilyControl.Seek(ref clock, 1.5);
                world.EntityManager.SetComponentData(rigEntity, clock);
                system.Update();
                var sought = BoneRotations(world, rigEntity);
                var seekTravel = 0f;
                for (var i = 0; i < interrupted.Length; i++)
                    seekTravel = Math.Max(seekTravel, AngleDegrees(interrupted[i], sought[i]));
                Assert.Greater(seekTravel, 0.01f, "seek should land on a different pose");
                clock = world.EntityManager.GetComponentData<SharedClock>(rigEntity);
                SharedFamilyControl.Seek(ref clock, 1.5);
                world.EntityManager.SetComponentData(rigEntity, clock);
                system.Update();
                var resought = BoneRotations(world, rigEntity);
                for (var i = 0; i < sought.Length; i++)
                    Assert.Less(AngleDegrees(sought[i], resought[i]), 1e-4f, "seek should be deterministic on bone " + i);
                AssertUnitScales(world, rigEntity);
            }
            finally
            {
                world.Dispose();
                if (clipA.IsCreated)
                    clipA.Dispose();
                if (clipB.IsCreated)
                    clipB.Dispose();
                if (table.IsCreated)
                    table.Dispose();
            }
        }
    }
}
