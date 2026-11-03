using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA;
using SA.Animation;
using SA.Animation.Content;
using SA.Editor;
using SA.Motion.Mechanics;
using SA.Motion.Numerics;
using SA.Motion.Unity;
using SA.Shape;
using SA.Shape.Unity;
using Pose = SA.Motion.Numerics.Pose;
using Hash128 = Unity.Entities.Hash128;

namespace SA.Tests
{
    public class ShapePhysicalTests
    {
        static readonly int[] TargetRoles = { 0, 1, 2, 3, 48, 49, 50 };
        static readonly int[] TargetParents = { -1, 0, 1, 2, 1, 4, 5 };
        const int BoneCount = 7;

        static string[] BoneNames()
        {
            var names = new string[BoneCount];
            for (var i = 0; i < BoneCount; i++)
                names[i] = "bone" + i;
            return names;
        }

        static Pose[] Reference()
        {
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            Assert.AreEqual(56, reference.Length);
            return reference;
        }

        static Pose[] Calibration()
        {
            var reference = Reference();
            var calibration = new Pose[BoneCount];
            for (var i = 0; i < BoneCount; i++)
                calibration[i] = reference[TargetRoles[i]];
            return calibration;
        }

        static Pose Bent(in Pose reference, float radians)
        {
            return new Pose
            {
                Position = reference.Position,
                Rotation = global::SA.Motion.Numerics.Quaternion.Multiply(reference.Rotation, global::SA.Motion.Numerics.Quaternion.AxisAngle(new Float3(0f, 1f, 0f), radians)),
                Scale = Float3.One
            };
        }

        static byte[] ClipBytes(float bend, double duration)
        {
            var reference = Reference();
            var samples = new Pose[9 * 56];
            for (var f = 0; f < 9; f++)
            {
                var swing = bend * (float)Math.Sin(2.0 * Math.PI * f / 8.0);
                for (var c = 0; c < 56; c++)
                    samples[f * 56 + c] = reference[c];
                samples[f * 56 + 2] = Bent(reference[2], swing);
            }
            Assert.IsTrue(SharedFamilyBake.EncodeCanonicalSamples(samples, 9, duration, true, out var bytes, out var error), error);
            return bytes;
        }

        static byte[] TableBytes(Pose[] calibration)
        {
            var sourceMap = new int[BoneCount];
            for (var i = 0; i < BoneCount; i++)
                sourceMap[i] = TargetRoles[i];
            Assert.IsTrue(SharedFamilyBake.CompileTableBytes(TargetParents, calibration, sourceMap, 1, 1, 1f, out var table, out var error), error);
            return table;
        }

        static void LoadBinding(byte[] table, Pose[] calibration, out RetargetBinding binding)
        {
            Assert.IsTrue(SharedFamilyBake.FamilyId(out var fa, out var fb, out var fc, out var fd));
            var family = new AssetId(fa, fb, fc, fd);
            Assert.AreEqual(AnimationError.None, ContentIdentity.Skeleton("SA.Target", 1, BoneNames(), TargetParents, calibration, out var target));
            var parents = SharedFamilyBake.CanonicalParentsArray();
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            Assert.AreEqual(AnimationError.None, RetargetFile.Load(table, family, target, parents, reference, out binding, out var rows));
            Assert.AreEqual(BoneCount, rows.Length);
        }

        static ShapeChannel[] TestChannels()
        {
            return new[] { new ShapeChannel(7u, -1f, 1f, 0f, 0.05f) };
        }

        static OffsetEffect[] TestOffsets()
        {
            return new[] { new OffsetEffect(0, 2, new Float3(0f, 0f, 0f), 0.22314355f) };
        }

        static SkinEffect[] TestSkin()
        {
            return new[] { new SkinEffect(0, 2, new Float3(0f, 0.22314355f, 0f)) };
        }

        static ShapeProfile TestProfile(AssetId target)
        {
            return new ShapeProfile(new AssetId(7u, 7u, 7u, 7u), target,
                new ShapeMarker(2, new Float3(0f, -0.1f, 0.05f)), new ShapeMarker(5, new Float3(0f, -0.1f, 0.05f)));
        }

        static BlobAssetReference<SharedRetargetData> TableBlob(byte[] table, Pose[] calibration)
        {
            var blob = SharedFamilyBake.MakeTableBlob(table, TargetParents, calibration, out var error);
            Assert.IsTrue(blob.IsCreated, error);
            return blob;
        }

        static BlobAssetReference<SharedClipData> ClipBlob(byte[] bytes, double duration)
        {
            return SharedFamilyBake.MakeClipBlob(bytes, duration, 9, true);
        }

        static BlobAssetReference<ShapeDefinitionData> ShapeBlob(ShapeProfile profile, ShapeChannel[] channels, OffsetEffect[] offsets, SkinEffect[] skin)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<ShapeDefinitionData>();
            root.Definition = profile.Definition;
            root.Target = profile.Target;
            root.Grounding = profile.Grounding;
            root.SupportA = profile.SupportA;
            root.SupportB = profile.SupportB;
            root.MinimumLength = profile.MinimumLength;
            root.MaximumLength = profile.MaximumLength;
            root.MinimumSkinScale = profile.MinimumSkinScale;
            root.MaximumSkinScale = profile.MaximumSkinScale;
            var storedChannels = builder.Allocate(ref root.Channels, channels.Length);
            for (var i = 0; i < channels.Length; i++)
                storedChannels[i] = channels[i];
            var storedOffsets = builder.Allocate(ref root.Offsets, offsets.Length);
            for (var i = 0; i < offsets.Length; i++)
                storedOffsets[i] = offsets[i];
            var storedSkin = builder.Allocate(ref root.Skin, skin.Length);
            for (var i = 0; i < skin.Length; i++)
                storedSkin[i] = skin[i];
            builder.Allocate(ref root.Frames, 0);
            builder.Allocate(ref root.Morphs, 0);
            builder.Allocate(ref root.MorphEffects, 0);
            var blob = builder.CreateBlobAssetReference<ShapeDefinitionData>(Allocator.Persistent);
            builder.Dispose();
            return blob;
        }

        static Entity BuildPhysicalRig(World world, BlobAssetReference<SharedRetargetData> table, BlobAssetReference<SharedClipData> clipA,
            BlobAssetReference<SharedClipData> clipB, BlobAssetReference<ShapeDefinitionData> shape, Pose[] calibration, float requested, bool live)
        {
            var manager = world.EntityManager;
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var rig = ref builder.ConstructRoot<RigBlob>();
            rig.Id = new Hash128(7, 7, 7, 7);
            var bones = builder.Allocate(ref rig.Bones, BoneCount);
            for (var i = 0; i < BoneCount; i++)
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
            for (var i = 0; i < BoneCount; i++)
            {
                var bone = manager.CreateEntity(typeof(LocalTransform));
                manager.SetComponentData(bone, LocalTransform.FromPositionRotationScale(float3.zero, quaternion.identity, 1f));
                manager.AddBuffer<BoneEntity>(entity).Add(new BoneEntity { Value = bone });
            }
            manager.AddComponent<ShapeDriven>(entity);
            manager.AddComponentData(entity, new ShapeDefinitionRef { Value = shape });
            manager.AddComponentData(entity, new ShapeState { Revision = 0 });
            var states = manager.AddBuffer<ShapeChannelState>(entity);
            states.Add(new ShapeChannelState { Id = 7u, Requested = requested, Value = 0f, Velocity = 0f });
            manager.AddBuffer<ShapeSkinDeformation>(entity);
            manager.AddBuffer<ShapeMorphWeight>(entity);
            manager.AddComponentData(entity, new ShapeContactState());
            manager.AddComponentData(entity, new ShapePhysical { LivePhysical = live ? (byte)1 : (byte)0, MassPolicy = (byte)ShapeMassPolicy.FixedMass });
            var rest = manager.AddBuffer<ShapePhysicalRest>(entity);
            for (var b = 0; b < BoneCount; b++)
            {
                var offset = Conversions.ToUnity(calibration[b].Position);
                var length = math.length(offset);
                var direction = length > 1e-9f ? math.normalize(math.rotate(math.inverse(Conversions.ToUnity(calibration[b].Rotation)), -offset)) : float3.zero;
                rest.Add(new ShapePhysicalRest { RestOffset = offset, RestDirection = direction, RestLength = length, RestRadius = ShapePhysicalSync.RestRadius(length) });
            }
            manager.AddBuffer<ShapePhysicalBody>(entity);
            manager.AddBuffer<ShapePhysicalJoint>(entity);
            return entity;
        }

        static float3[] BonePositions(World world, Entity rig)
        {
            var manager = world.EntityManager;
            var bones = manager.GetBuffer<BoneEntity>(rig);
            var positions = new float3[bones.Length];
            for (var i = 0; i < bones.Length; i++)
                positions[i] = manager.GetComponentData<LocalTransform>(bones[i].Value).Position;
            return positions;
        }

        static void StepWorld(World world, double elapsed)
        {
            world.SetTime(new TimeData(elapsed, 1f / 60f));
            world.GetOrCreateSystemManaged<SharedFamilySystem>().Update();
            world.GetOrCreateSystemManaged<ShapeTimelineSystem>().Update();
        }

        static void StepFixed(World world)
        {
            world.GetOrCreateSystemManaged<ShapePhysicalSyncSystem>().Update();
        }

        static void RequestRebuild(World world, Entity rig)
        {
            var physical = world.EntityManager.GetComponentData<ShapePhysical>(rig);
            ShapePhysicalControl.RequestRebuild(ref physical);
            world.EntityManager.SetComponentData(rig, physical);
        }

        static void GoLive(World world, Entity rig)
        {
            var physical = world.EntityManager.GetComponentData<ShapePhysical>(rig);
            ShapePhysicalControl.GoLive(ref physical);
            world.EntityManager.SetComponentData(rig, physical);
        }

        static float AbsDiff(float a, float b)
        {
            return a > b ? a - b : b - a;
        }

        [Test]
        public void RebuildMovesCollidersAnchorsAndMassCoherently()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding);
            var channels = TestChannels();
            var offsets = TestOffsets();
            var skin = TestSkin();
            var world = new World("SA.ShapePhysical.Rebuild");
            BlobAssetReference<SharedRetargetData> tableBlob = default;
            BlobAssetReference<SharedClipData> clipA = default;
            BlobAssetReference<SharedClipData> clipB = default;
            BlobAssetReference<ShapeDefinitionData> shapeBlob = default;
            try
            {
                clipA = ClipBlob(ClipBytes(0.4f, 1.0), 1.0);
                clipB = ClipBlob(ClipBytes(-0.3f, 2.0), 2.0);
                tableBlob = TableBlob(table, calibration);
                shapeBlob = ShapeBlob(TestProfile(binding.Target), channels, offsets, skin);
                var rig = BuildPhysicalRig(world, tableBlob, clipA, clipB, shapeBlob, calibration, 1f, false);
                var elapsed = 0.0;
                for (var step = 0; step < 120; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                var manager = world.EntityManager;
                var revision = manager.GetComponentData<ShapeState>(rig).Revision;
                Assert.Greater(revision, 0uL);
                Assert.AreEqual(revision, manager.GetComponentData<ShapeContactState>(rig).Revision);
                RequestRebuild(world, rig);
                StepFixed(world);
                Assert.AreEqual(1, ShapePhysicalSyncSystem.LastRebuilt);
                var physical = manager.GetComponentData<ShapePhysical>(rig);
                Assert.AreEqual(revision, physical.AppliedRevision);
                Assert.AreEqual(0, physical.Diagnostic.Length);
                var rest = manager.GetBuffer<ShapePhysicalRest>(rig);
                var bodies = manager.GetBuffer<ShapePhysicalBody>(rig);
                var joints = manager.GetBuffer<ShapePhysicalJoint>(rig);
                var deformations = manager.GetBuffer<ShapeSkinDeformation>(rig);
                Assert.AreEqual(BoneCount, bodies.Length);
                Assert.AreEqual(BoneCount, joints.Length);
                var total = 0f;
                for (var b = 0; b < BoneCount; b++)
                    total += ShapePhysicalSync.CapsuleWeight(rest[b].RestLength, rest[b].RestRadius);
                for (var b = 0; b < BoneCount; b++)
                {
                    Assert.AreEqual(b, bodies[b].Bone);
                    Assert.AreEqual(TargetParents[b], joints[b].ParentBone);
                    var parent = TargetParents[b];
                    var anchor = Float3.Zero;
                    if (parent >= 0)
                        anchor = ShapePhysicalSync.DeformPoint(deformations[parent].Value, Conversions.ToMotion(rest[b].RestOffset));
                    Assert.AreEqual(anchor.X, joints[b].Anchor.x, 1e-6f, "anchor x drifted on bone " + b);
                    Assert.AreEqual(anchor.Y, joints[b].Anchor.y, 1e-6f, "anchor y drifted on bone " + b);
                    Assert.AreEqual(anchor.Z, joints[b].Anchor.z, 1e-6f, "anchor z drifted on bone " + b);
                    var baseMass = ShapePhysicalSync.CapsuleWeight(rest[b].RestLength, rest[b].RestRadius) / total;
                    var baseCenter = ShapePhysicalSync.RestCenter(Conversions.ToMotion(rest[b].RestDirection), rest[b].RestLength);
                    var error = ShapeInertia.Transform(baseMass, baseCenter, ShapePhysicalSync.SphereInertia(baseMass, rest[b].RestRadius),
                        deformations[b].Value, ShapeMassPolicy.FixedMass, out var mass, out var com, out _);
                    Assert.AreEqual(AnimationError.None, error);
                    Assert.AreEqual(mass, bodies[b].Mass, 1e-6f, "mass drifted on bone " + b);
                    Assert.AreEqual(com.X, bodies[b].CenterOfMass.x, 1e-6f, "center x drifted on bone " + b);
                    Assert.AreEqual(com.Y, bodies[b].CenterOfMass.y, 1e-6f, "center y drifted on bone " + b);
                    Assert.AreEqual(com.Z, bodies[b].CenterOfMass.z, 1e-6f, "center z drifted on bone " + b);
                    var scale = ShapePhysicalSync.MeanColumnLength(deformations[b].Value);
                    Assert.AreEqual(rest[b].RestRadius * scale, bodies[b].ColliderRadius, 1e-6f, "radius drifted on bone " + b);
                    Assert.AreEqual(anchor.Length + 2f * bodies[b].ColliderRadius, bodies[b].ColliderHeight, 1e-6f, "height drifted on bone " + b);
                    Assert.AreEqual(1f, bodies[b].MotorScale, 1e-6f, "first build motor scale drifted on bone " + b);
                    Assert.AreEqual(bodies[b].MotorScale, joints[b].MotorScale, 1e-9f, "joint motor drifted on bone " + b);
                    Assert.IsTrue(bodies[b].InertiaMoments.x > 0f && bodies[b].InertiaMoments.y > 0f && bodies[b].InertiaMoments.z > 0f, "moments not positive on bone " + b);
                }
            }
            finally
            {
                world.Dispose();
                if (clipA.IsCreated)
                    clipA.Dispose();
                if (clipB.IsCreated)
                    clipB.Dispose();
                if (tableBlob.IsCreated)
                    tableBlob.Dispose();
                if (shapeBlob.IsCreated)
                    shapeBlob.Dispose();
            }
        }

        [Test]
        public void RefitMatchesPortableKernelUnderRotationMixing()
        {
            var angle = 0.7853982f;
            var cosineFull = (float)Math.Cos(angle);
            var sineFull = (float)Math.Sin(angle);
            var deformation = new Affine(
                new Float3(2f * cosineFull, 2f * sineFull, 0f),
                new Float3(-sineFull, cosineFull, 0f),
                new Float3(0f, 0f, 0.5f),
                new Float3(0.1f, 0.2f, 0.3f));
            var anchor = new Float3(0f, 1.5f, 0f);
            var direction = new Float3(0f, -1f, 0f);
            var baseCenter = ShapePhysicalSync.RestCenter(direction, 1.5f);
            var baseInertia = new Float3x3(
                new Float3(0.5f, 0f, 0f),
                new Float3(0f, 0.2f, 0f),
                new Float3(0f, 0f, 0.3f));
            var error = ShapePhysicalSync.RefitBody(anchor, direction, 0.25f, 2f, baseCenter, baseInertia, ShapeMassPolicy.FixedMass,
                deformation, 1f, out var center, out var radius, out var height, out var mass, out var com,
                out var moments, out var turned, out var motor);
            Assert.AreEqual(AnimationError.None, error);
            Assert.AreEqual(AnimationError.None, ShapeInertia.Transform(2f, baseCenter, baseInertia, deformation, ShapeMassPolicy.FixedMass,
                out var expectedMass, out var expectedCom, out var expectedInertia));
            Assert.AreEqual(AnimationError.None, ShapeInertia.PrincipalAxes(expectedInertia, out var expectedMoments, out var expectedRotation));
            Assert.AreEqual(expectedMass, mass, 1e-5f);
            Assert.AreEqual(2f, motor, 1e-5f);
            Assert.AreEqual(expectedCom.X, com.X, 1e-5f);
            Assert.AreEqual(expectedCom.Y, com.Y, 1e-5f);
            Assert.AreEqual(expectedCom.Z, com.Z, 1e-5f);
            Assert.AreEqual(expectedMoments.X, moments.X, 1e-5f);
            Assert.AreEqual(expectedMoments.Y, moments.Y, 1e-5f);
            Assert.AreEqual(expectedMoments.Z, moments.Z, 1e-5f);
            var dot = turned.X * expectedRotation.X + turned.Y * expectedRotation.Y + turned.Z * expectedRotation.Z + turned.W * expectedRotation.W;
            Assert.Greater(Math.Abs(dot), 1.0 - 1e-5, "principal frame drifted");
            var naive = new float3(baseInertia.C0.X * 4f, baseInertia.C1.Y, baseInertia.C2.Z * 0.25f);
            var proper = Conversions.ToUnity(moments);
            var mixed = AbsDiff(proper.x, naive.x) + AbsDiff(proper.y, naive.y) + AbsDiff(proper.z, naive.z);
            Assert.Greater(mixed, 1e-3f, "axes did not mix; componentwise scaling would have matched");
            Assert.AreEqual(-0.75f, center.y, 1e-6f);
            Assert.AreEqual(1.5f + 2f * radius, height, 1e-6f);
        }

        [Test]
        public void LivePhysicalRefusesShapeEditsAndLeavesBonesUntouched()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding);
            var world = new World("SA.ShapePhysical.Refusal");
            BlobAssetReference<SharedRetargetData> tableBlob = default;
            BlobAssetReference<SharedClipData> clipA = default;
            BlobAssetReference<SharedClipData> clipB = default;
            BlobAssetReference<ShapeDefinitionData> shapeBlob = default;
            try
            {
                clipA = ClipBlob(ClipBytes(0.4f, 1.0), 1.0);
                clipB = ClipBlob(ClipBytes(-0.3f, 2.0), 2.0);
                tableBlob = TableBlob(table, calibration);
                shapeBlob = ShapeBlob(TestProfile(binding.Target), TestChannels(), TestOffsets(), TestSkin());
                var rig = BuildPhysicalRig(world, tableBlob, clipA, clipB, shapeBlob, calibration, 0f, false);
                var plain = BuildPhysicalRig(world, tableBlob, clipA, clipB, shapeBlob, calibration, 0f, false);
                var elapsed = 0.0;
                for (var step = 0; step < 5; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                RequestRebuild(world, rig);
                StepFixed(world);
                Assert.AreEqual(2, ShapePhysicalSyncSystem.LastRebuilt);
                var applied = world.EntityManager.GetComponentData<ShapePhysical>(rig).AppliedRevision;
                GoLive(world, rig);
                var channels = world.EntityManager.GetBuffer<ShapeChannelState>(rig);
                channels[0] = new ShapeChannelState { Id = 7u, Requested = 1f, Value = channels[0].Value, Velocity = channels[0].Velocity };
                StepFixed(world);
                Assert.AreEqual(1, ShapePhysicalSyncSystem.LastRefused);
                for (var step = 0; step < 3; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                StepFixed(world);
                Assert.AreEqual(0, ShapePhysicalSyncSystem.LastRefused);
                var manager = world.EntityManager;
                Assert.AreEqual(applied, manager.GetComponentData<ShapeState>(rig).Revision);
                var afterChannels = manager.GetBuffer<ShapeChannelState>(rig);
                Assert.AreEqual(afterChannels[0].Value, afterChannels[0].Requested, 1e-9f);
                var diagnostic = manager.GetComponentData<ShapePhysical>(rig).Diagnostic.ToString();
                Assert.IsTrue(diagnostic.Contains("live-physical"), "unexpected diagnostic: " + diagnostic);
                var after = BonePositions(world, rig);
                var expected = BonePositions(world, plain);
                for (var b = 0; b < BoneCount; b++)
                {
                    Assert.AreEqual(expected[b].x, after[b].x, 0f, "visual position moved on live bone " + b);
                    Assert.AreEqual(expected[b].y, after[b].y, 0f, "visual position moved on live bone " + b);
                    Assert.AreEqual(expected[b].z, after[b].z, 0f, "visual position moved on live bone " + b);
                }
            }
            finally
            {
                world.Dispose();
                if (clipA.IsCreated)
                    clipA.Dispose();
                if (clipB.IsCreated)
                    clipB.Dispose();
                if (tableBlob.IsCreated)
                    tableBlob.Dispose();
                if (shapeBlob.IsCreated)
                    shapeBlob.Dispose();
            }
        }

        [Test]
        public void FailedRefitKeepsPreviousSnapshot()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding);
            var world = new World("SA.ShapePhysical.Rollback");
            BlobAssetReference<SharedRetargetData> tableBlob = default;
            BlobAssetReference<SharedClipData> clipA = default;
            BlobAssetReference<SharedClipData> clipB = default;
            BlobAssetReference<ShapeDefinitionData> shapeBlob = default;
            try
            {
                clipA = ClipBlob(ClipBytes(0.4f, 1.0), 1.0);
                clipB = ClipBlob(ClipBytes(-0.3f, 2.0), 2.0);
                tableBlob = TableBlob(table, calibration);
                shapeBlob = ShapeBlob(TestProfile(binding.Target), TestChannels(), TestOffsets(), TestSkin());
                var rig = BuildPhysicalRig(world, tableBlob, clipA, clipB, shapeBlob, calibration, 1f, false);
                var elapsed = 0.0;
                for (var step = 0; step < 120; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                RequestRebuild(world, rig);
                StepFixed(world);
                Assert.AreEqual(1, ShapePhysicalSyncSystem.LastRebuilt);
                var manager = world.EntityManager;
                var bodies = manager.GetBuffer<ShapePhysicalBody>(rig);
                var kept = new float[BoneCount];
                for (var b = 0; b < BoneCount; b++)
                    kept[b] = bodies[b].Mass;
                var deformations = manager.GetBuffer<ShapeSkinDeformation>(rig);
                deformations[2] = new ShapeSkinDeformation { Value = default };
                RequestRebuild(world, rig);
                StepFixed(world);
                Assert.AreEqual(0, ShapePhysicalSyncSystem.LastRebuilt);
                var after = manager.GetBuffer<ShapePhysicalBody>(rig);
                for (var b = 0; b < BoneCount; b++)
                    Assert.AreEqual(kept[b], after[b].Mass, 0f, "previous mass lost on bone " + b);
                var diagnostic = manager.GetComponentData<ShapePhysical>(rig).Diagnostic.ToString();
                Assert.IsTrue(diagnostic.Contains("Kept"), "unexpected diagnostic: " + diagnostic);
            }
            finally
            {
                world.Dispose();
                if (clipA.IsCreated)
                    clipA.Dispose();
                if (clipB.IsCreated)
                    clipB.Dispose();
                if (tableBlob.IsCreated)
                    tableBlob.Dispose();
                if (shapeBlob.IsCreated)
                    shapeBlob.Dispose();
            }
        }

        [Test]
        public void KinematicEditWithoutRebuildMarksStale()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding);
            var world = new World("SA.ShapePhysical.Stale");
            BlobAssetReference<SharedRetargetData> tableBlob = default;
            BlobAssetReference<SharedClipData> clipA = default;
            BlobAssetReference<SharedClipData> clipB = default;
            BlobAssetReference<ShapeDefinitionData> shapeBlob = default;
            try
            {
                clipA = ClipBlob(ClipBytes(0.4f, 1.0), 1.0);
                clipB = ClipBlob(ClipBytes(-0.3f, 2.0), 2.0);
                tableBlob = TableBlob(table, calibration);
                shapeBlob = ShapeBlob(TestProfile(binding.Target), TestChannels(), TestOffsets(), TestSkin());
                var rig = BuildPhysicalRig(world, tableBlob, clipA, clipB, shapeBlob, calibration, 0f, false);
                var elapsed = 0.0;
                for (var step = 0; step < 5; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                RequestRebuild(world, rig);
                StepFixed(world);
                Assert.AreEqual(1, ShapePhysicalSyncSystem.LastRebuilt);
                var applied = world.EntityManager.GetComponentData<ShapePhysical>(rig).AppliedRevision;
                var channels = world.EntityManager.GetBuffer<ShapeChannelState>(rig);
                channels[0] = new ShapeChannelState { Id = 7u, Requested = 0.5f, Value = channels[0].Value, Velocity = channels[0].Velocity };
                for (var step = 0; step < 30; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                StepFixed(world);
                Assert.AreEqual(1, ShapePhysicalSyncSystem.LastStale);
                Assert.AreEqual(0, ShapePhysicalSyncSystem.LastRebuilt);
                var manager = world.EntityManager;
                var diagnostic = manager.GetComponentData<ShapePhysical>(rig).Diagnostic.ToString();
                Assert.IsTrue(diagnostic.Contains("Request a rebuild"), "unexpected diagnostic: " + diagnostic);
                Assert.AreEqual(applied, manager.GetComponentData<ShapePhysical>(rig).AppliedRevision);
            }
            finally
            {
                world.Dispose();
                if (clipA.IsCreated)
                    clipA.Dispose();
                if (clipB.IsCreated)
                    clipB.Dispose();
                if (tableBlob.IsCreated)
                    tableBlob.Dispose();
                if (shapeBlob.IsCreated)
                    shapeBlob.Dispose();
            }
        }

        [Test]
        public void WardrobeValidationReportsUnsupportedData()
        {
            Assert.IsTrue(ShapePhysicalSync.ValidateWardrobe(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), out var none));
            Assert.AreEqual(string.Empty, none);
            Assert.IsTrue(ShapePhysicalSync.ValidateWardrobe(new[] { "Smile" }, new[] { "Smile" }, new[] { "Smile", "Brow" }, out _));
            Assert.IsFalse(ShapePhysicalSync.ValidateWardrobe(new[] { "Smile" }, Array.Empty<string>(), new[] { "Smile" }, out var missing));
            Assert.IsTrue(missing.Contains("Smile"), "unexpected message: " + missing);
            Assert.IsFalse(ShapePhysicalSync.ValidateWardrobe(new[] { "Smile", "Frown" }, new[] { "Smile" }, new[] { "Smile" }, out var unknown));
            Assert.IsTrue(unknown.Contains("Frown"), "unexpected message: " + unknown);
            Assert.IsFalse(ShapePhysicalSync.ValidateWardrobe(new[] { "Smile" }, new[] { "Smile" }, Array.Empty<string>(), out var mesh));
            Assert.IsTrue(mesh.Contains("blend shape"), "unexpected message: " + mesh);
        }
    }
}
