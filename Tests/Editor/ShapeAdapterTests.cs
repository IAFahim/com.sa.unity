using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using SA;
using SA.Animation;
using SA.Animation.Content;
using SA.Editor;
using SA.Motion.Inertialization;
using SA.Motion.Mechanics;
using SA.Motion.Numerics;
using SA.Shape;
using SA.Shape.Unity;
using SA.Shape.Unity.Editor;
using Pose = SA.Motion.Numerics.Pose;
using Hash128 = Unity.Entities.Hash128;
using SharedMode = SA.Animation.ClipMode;

namespace SA.Tests
{
    public class ShapeAdapterTests
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
                Rotation = global::SA.Motion.Numerics.Quaternion.Normalize(global::SA.Motion.Numerics.Quaternion.Multiply(reference.Rotation, global::SA.Motion.Numerics.Quaternion.AxisAngle(new Float3(0f, 1f, 0f), radians))),
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

        static void LoadBinding(byte[] table, Pose[] calibration, out RetargetBinding binding, out RetargetRow[] rows)
        {
            Assert.IsTrue(SharedFamilyBake.FamilyId(out var fa, out var fb, out var fc, out var fd));
            var family = new AssetId(fa, fb, fc, fd);
            Assert.AreEqual(AnimationError.None, ContentIdentity.Skeleton("SA.Target", 1, BoneNames(), TargetParents, calibration, out var target));
            var parents = SharedFamilyBake.CanonicalParentsArray();
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            Assert.AreEqual(AnimationError.None, RetargetFile.Load(table, family, target, parents, reference, out binding, out rows));
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

        static ShapeDefinition TestDefinition(AssetId target, out ShapeChannel[] channels, out OffsetEffect[] offsets, out SkinEffect[] skin)
        {
            channels = TestChannels();
            offsets = TestOffsets();
            skin = TestSkin();
            return new ShapeDefinition(TestProfile(target), channels, offsets, skin, Array.Empty<SkinFrame>());
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

        static BlobAssetReference<ShapeDefinitionData> ShapeBlob(ShapeProfile profile, ShapeChannel[] channels, OffsetEffect[] offsets, SkinEffect[] skin, ShapeMorph[] morphs = null, MorphEffect[] effects = null)
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
            var storedMorphs = builder.Allocate(ref root.Morphs, morphs == null ? 0 : morphs.Length);
            for (var i = 0; i < storedMorphs.Length; i++)
                storedMorphs[i] = morphs[i];
            var storedEffects = builder.Allocate(ref root.MorphEffects, effects == null ? 0 : effects.Length);
            for (var i = 0; i < storedEffects.Length; i++)
                storedEffects[i] = effects[i];
            var blob = builder.CreateBlobAssetReference<ShapeDefinitionData>(Allocator.Persistent);
            builder.Dispose();
            return blob;
        }

        static Entity BuildRig(World world, BlobAssetReference<SharedRetargetData> table, BlobAssetReference<SharedClipData> clipA, BlobAssetReference<SharedClipData> clipB,
            BlobAssetReference<ShapeDefinitionData> shape, float requested)
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
            if (shape.IsCreated)
            {
                manager.AddComponent<ShapeDriven>(entity);
                manager.AddComponentData(entity, new ShapeDefinitionRef { Value = shape });
                manager.AddComponentData(entity, new ShapeState { Revision = 0 });
                var states = manager.AddBuffer<ShapeChannelState>(entity);
                states.Add(new ShapeChannelState { Id = 7u, Requested = requested, Value = 0f, Velocity = 0f });
                manager.AddBuffer<ShapeSkinDeformation>(entity);
                manager.AddBuffer<ShapeMorphWeight>(entity);
                manager.AddComponentData(entity, new ShapeContactState());
            }
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

        static quaternion[] BoneRotations(World world, Entity rig)
        {
            var manager = world.EntityManager;
            var bones = manager.GetBuffer<BoneEntity>(rig);
            var rotations = new quaternion[bones.Length];
            for (var i = 0; i < bones.Length; i++)
                rotations[i] = manager.GetComponentData<LocalTransform>(bones[i].Value).Rotation;
            return rotations;
        }

        static float AngleDegrees(quaternion a, quaternion b)
        {
            var dot = math.abs(math.dot(math.normalize(a.value), math.normalize(b.value)));
            return 2f * (float)Math.Acos(math.min(1f, dot)) * 57.29578f;
        }

        static void StepWorld(World world, double elapsed)
        {
            world.SetTime(new TimeData(elapsed, 1f / 60f));
            world.GetOrCreateSystemManaged<SharedFamilySystem>().Update();
            world.GetOrCreateSystemManaged<ShapeTimelineSystem>().Update();
        }

        [Test]
        public void RevisionHoldsWhenRequestedMatchesDefault()
        {
            var calibration = Calibration();
            LoadBinding(TableBytes(calibration), calibration, out _, out _);
            var channels = TestChannels();
            var requested = new float[] { 0f };
            var current = new ShapeParameter[] { new ShapeParameter(0f, 0f) };
            var next = new ShapeParameter[1];
            Assert.AreEqual(AnimationError.None, ShapeParameters.Step(channels, requested, current, 1f / 60f, next));
            Assert.AreEqual(0f, next[0].Value, 1e-6f);
            Assert.IsFalse(ShapeRevision.HasEffectiveChange(new float[] { 0f }, new float[] { next[0].Value }));
        }

        [Test]
        public void RevisionBumpsWhenRequestedChangesThenSettles()
        {
            var channels = TestChannels();
            var committed = new float[] { 0f };
            var current = new ShapeParameter[] { new ShapeParameter(0f, 0f) };
            var next = new ShapeParameter[1];
            var requested = new float[] { 1f };
            Assert.AreEqual(AnimationError.None, ShapeParameters.Step(channels, requested, current, 1f / 60f, next));
            Assert.IsTrue(ShapeRevision.HasEffectiveChange(committed, new float[] { next[0].Value }));
            committed[0] = next[0].Value;
            var settled = false;
            for (var i = 0; i < 600; i++)
            {
                current[0] = next[0];
                Assert.AreEqual(AnimationError.None, ShapeParameters.Step(channels, requested, current, 1f / 60f, next));
                if (!ShapeRevision.HasEffectiveChange(committed, new float[] { next[0].Value }))
                {
                    settled = true;
                    break;
                }
                committed[0] = next[0].Value;
            }
            Assert.IsTrue(settled, "filter did not settle on the requested value");
            Assert.AreEqual(1f, next[0].Value, 1e-6f);
            Assert.AreEqual(0f, next[0].Velocity, 1e-4f);
        }

        [Test]
        public void MissingDefinitionReportsActionableError()
        {
            Assert.IsFalse(ShapeInspectorModel.Describe(null, BoneNames(), Array.Empty<string>(), default, Array.Empty<Float3>(),
                Array.Empty<ShapeRequest>(), out _, out _, out _, out var error));
            Assert.IsTrue(error.Contains("no shape definition"), "unexpected message: " + error);
        }

        [Test]
        public void MalformedDefinitionReportsUnsupported()
        {
            var source = new TextAsset("{not a shape}");
            Assert.IsFalse(ShapeInspectorModel.Describe(source, BoneNames(), Array.Empty<string>(), default, Array.Empty<Float3>(),
                Array.Empty<ShapeRequest>(), out _, out _, out _, out var error));
            Assert.IsTrue(error.Contains("not supported"), "unexpected message: " + error);
        }

        static string ValidShapeJson(bool withMorph)
        {
            var morphs = withMorph ? ",\"morphs\":[{\"name\":\"Smile\",\"min\":0,\"max\":1,\"base\":0}],\"morphEffects\":[{\"channel\":0,\"morph\":\"Smile\",\"delta\":0.5}]" : ",\"morphs\":[],\"morphEffects\":[]";
            return "{\"schemaVersion\":1,\"targetRigId\":\"0500000000000000060000000000000007000000000000000800000000000000\","
                + "\"definitionId\":\"f215f7ec40534784735bfcce78d59eec317357c8f57610b2740aa54b1d8e70f7\","
                + "\"units\":\"metres\",\"up\":\"+Y\",\"grounding\":\"Free\","
                + "\"supports\":[{\"bone\":\"bone2\",\"offset\":[0,-0.1,0.05]},{\"bone\":\"bone4\",\"offset\":[0,-0.1,0.05]}],"
                + "\"lengthRange\":[0.25,4],\"skinScaleRange\":[0.125,8],"
                + "\"channels\":[{\"id\":7,\"name\":\"legLength\",\"min\":-1,\"max\":1,\"default\":0,\"halfLifeSeconds\":0.05}],"
                + "\"offsetEffects\":[{\"channel\":0,\"bone\":\"bone2\",\"delta\":[0,0,0],\"logLength\":0.22314355}],"
                + "\"skinEffects\":[{\"channel\":0,\"bone\":\"bone2\",\"logScale\":[0,0.22314355,0]}],"
                + "\"skinFrames\":[]" + morphs + "}";
        }

        [Test]
        public void InspectorModelShowsNamedChannels()
        {
            var source = new TextAsset(ValidShapeJson(false));
            var requests = new[] { new ShapeRequest { Id = 7u, Name = string.Empty, Value = 0.5f }, new ShapeRequest { Id = 99u, Name = string.Empty, Value = 1f } };
            Assert.IsTrue(ShapeInspectorModel.Describe(source, BoneNames(), Array.Empty<string>(), default, Array.Empty<Float3>(), requests,
                out _, out var rows, out var warning, out var error), error);
            Assert.AreEqual(1, rows.Length);
            Assert.AreEqual(7u, rows[0].Id);
            Assert.AreEqual("legLength", rows[0].Name);
            Assert.AreEqual(0.5f, rows[0].Requested, 1e-6f);
            Assert.IsTrue(rows[0].HasRequest);
            Assert.IsTrue(warning.Contains("1 saved requests"), "unexpected warning: " + warning);
        }

        [Test]
        public void InspectorModelDefaultsWithoutRequests()
        {
            var source = new TextAsset(ValidShapeJson(false));
            Assert.IsTrue(ShapeInspectorModel.Describe(source, BoneNames(), Array.Empty<string>(), default, Array.Empty<Float3>(),
                Array.Empty<ShapeRequest>(), out _, out var rows, out var warning, out var error), error);
            Assert.AreEqual(1, rows.Length);
            Assert.AreEqual("legLength", rows[0].Name);
            Assert.AreEqual(rows[0].Default, rows[0].Requested, 1e-6f);
            Assert.IsFalse(rows[0].HasRequest);
            Assert.AreEqual(string.Empty, warning);
        }

        [Test]
        public void MorphDefinitionAcceptedWhenMeshMorphsMatch()
        {
            var source = new TextAsset(ValidShapeJson(true));
            Assert.IsTrue(ShapeInspectorModel.Describe(source, BoneNames(), new[] { "Smile" }, default, Array.Empty<Float3>(),
                Array.Empty<ShapeRequest>(), out var asset, out var rows, out _, out var error), error);
            Assert.AreEqual(1, rows.Length);
            Assert.AreEqual(1, asset.Morphs.Length);
            Assert.AreEqual("Smile", asset.MorphNames[0]);
        }

        [Test]
        public void MorphDefinitionWithoutMeshMorphsIsActionable()
        {
            var source = new TextAsset(ValidShapeJson(true));
            Assert.IsFalse(ShapeInspectorModel.Describe(source, BoneNames(), Array.Empty<string>(), default, Array.Empty<Float3>(),
                Array.Empty<ShapeRequest>(), out _, out _, out _, out var error));
            Assert.IsTrue(error.Contains("blend shape"), "unexpected message: " + error);
        }

        [Test]
        public void NeutralGeometryRestoresBaseExactly()
        {
            var calibration = Calibration();
            LoadBinding(TableBytes(calibration), calibration, out var binding, out var rows);
            var definition = TestDefinition(binding.Target, out _, out _, out _);
            var values = new float[] { 0f };
            var workspace = new ShapeWorkspace(new Float3[BoneCount], new float[BoneCount], new float[BoneCount],
                new Float3[BoneCount], new Affine[BoneCount], new RigidPose[BoneCount]);
            Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(definition, binding, rows, values, 0uL, workspace, out var geometry));
            Assert.AreEqual(binding.HeightRatio, geometry.Retarget.HeightRatio);
            for (var b = 0; b < BoneCount; b++)
            {
                Assert.AreEqual(calibration[b].Position.X, workspace.Offsets[b].X, 1e-6f);
                Assert.AreEqual(calibration[b].Position.Y, workspace.Offsets[b].Y, 1e-6f);
                Assert.AreEqual(calibration[b].Position.Z, workspace.Offsets[b].Z, 1e-6f);
            }
        }

        static void PrepareFrame(ulong tick, out AnimationFrame frame, out ClipContribution[] ordered, out LayerInput[] layers)
        {
            frame = new AnimationFrame(tick, 0, 0uL, tick / 60.0, 1f / 60f, 0.2f, FrameMode.Advance, 0.1f);
            ordered = new[] { new ClipContribution(new ClipHandle(0u, 1u), 0.5, 1f, 0, ushort.MaxValue) };
            layers = new[] { new LayerInput(0, SharedMode.Override, 0, 1, 1f) };
        }

        [Test]
        public void PrepareCommitSingleWriter()
        {
            var calibration = Calibration();
            LoadBinding(TableBytes(calibration), calibration, out var binding, out var rows);
            var definition = TestDefinition(binding.Target, out _, out _, out _);
            var values = new float[] { 0f };
            var offsets = new Float3[BoneCount];
            var scales = new float[BoneCount];
            var workspace = new ShapeWorkspace(offsets, new float[BoneCount], scales, new Float3[BoneCount], new Affine[BoneCount], new RigidPose[BoneCount]);
            Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(definition, binding, rows, values, 0uL, workspace, out var geometry));
            for (var b = 0; b < BoneCount; b++)
                scales[b] = 1f;
            var destination = new Pose[BoneCount];
            for (var b = 0; b < BoneCount; b++)
                destination[b] = calibration[b];
            var incoming = new PoseVelocity[BoneCount];
            var bones = new BoneFrameState[BoneCount];
            var scratch = new BoneFrameState[BoneCount];
            var previousContributions = new ClipContribution[SharedFamilyControl.MaxContributions];
            var previousLayers = new LayerInput[SharedFamilyControl.MaxContributions];
            var poseWorkspace = new ShapePoseWorkspace(new Pose[BoneCount], new Pose[BoneCount], new Pose[BoneCount], new Pose[BoneCount], new Pose[BoneCount], new RigidPose[BoneCount]);
            var state = new ShapeFrameState();
            PrepareFrame(1uL, out var first, out var ordered, out var layers);
            Assert.AreEqual(AnimationError.None, ShapeTimelinePose.Prepare(first, ordered, layers, true, destination, incoming,
                geometry, binding, rows, offsets, scales, state, bones, scratch, previousContributions, previousLayers, poseWorkspace, out var prepared, out var action));
            Assert.AreNotEqual(FrameAction.Reused, action);
            Assert.AreEqual(AnimationError.None, ShapeTimelinePose.Commit(prepared, ordered, layers, scratch, poseWorkspace, ref state, bones, previousContributions, previousLayers));
            Assert.AreEqual(1uL, state.Motion.LastFrame.Tick);
            Assert.AreEqual(0uL, state.Revision);
            PrepareFrame(1uL, out var repeat, out var sameOrdered, out var sameLayers);
            Assert.AreEqual(AnimationError.None, ShapeTimelinePose.Prepare(repeat, sameOrdered, sameLayers, true, destination, incoming,
                geometry, binding, rows, offsets, scales, state, bones, scratch, previousContributions, previousLayers, poseWorkspace, out _, out var reused));
            Assert.AreEqual(FrameAction.Reused, reused);
            var changedValues = new float[] { 1f };
            var changedWorkspace = new ShapeWorkspace(new Float3[BoneCount], new float[BoneCount], new float[BoneCount],
                new Float3[BoneCount], new Affine[BoneCount], new RigidPose[BoneCount]);
            Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(definition, binding, rows, changedValues, 1uL, changedWorkspace, out var changedGeometry));
            Assert.AreEqual(AnimationError.TickConflict, ShapeTimelinePose.Prepare(repeat, sameOrdered, sameLayers, true, destination, incoming,
                changedGeometry, binding, rows, changedWorkspace.Offsets, changedWorkspace.MotionScales, state, bones, scratch,
                previousContributions, previousLayers, poseWorkspace, out _, out _));
            PrepareFrame(2uL, out var second, out var ordered2, out var layers2);
            Assert.AreEqual(AnimationError.None, ShapeTimelinePose.Prepare(second, ordered2, layers2, true, destination, incoming,
                geometry, binding, rows, offsets, scales, state, bones, scratch, previousContributions, previousLayers, poseWorkspace, out var prepared2, out _));
            var corrupted = new BoneFrameState[BoneCount];
            for (var b = 0; b < BoneCount; b++)
                corrupted[b] = scratch[b];
            corrupted[0] = default;
            Assert.AreEqual(AnimationError.InvalidFrame, ShapeTimelinePose.Commit(prepared2, ordered2, layers2, corrupted, poseWorkspace, ref state, bones, previousContributions, previousLayers));
            Assert.AreEqual(1uL, state.Motion.LastFrame.Tick);
        }

        [Test]
        public void NeedsDataLeavesCommittedPoseAlone()
        {
            var calibration = Calibration();
            LoadBinding(TableBytes(calibration), calibration, out var binding, out var rows);
            var definition = TestDefinition(binding.Target, out _, out _, out _);
            var values = new float[] { 0f };
            var offsets = new Float3[BoneCount];
            var scales = new float[BoneCount];
            for (var b = 0; b < BoneCount; b++)
                scales[b] = 1f;
            var workspace = new ShapeWorkspace(offsets, new float[BoneCount], scales, new Float3[BoneCount], new Affine[BoneCount], new RigidPose[BoneCount]);
            Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(definition, binding, rows, values, 0uL, workspace, out var geometry));
            var destination = new Pose[BoneCount];
            for (var b = 0; b < BoneCount; b++)
                destination[b] = calibration[b];
            var incoming = new PoseVelocity[BoneCount];
            var bones = new BoneFrameState[BoneCount];
            var scratch = new BoneFrameState[BoneCount];
            var previousContributions = new ClipContribution[SharedFamilyControl.MaxContributions];
            var previousLayers = new LayerInput[SharedFamilyControl.MaxContributions];
            var displayed = new Pose[BoneCount];
            var poseWorkspace = new ShapePoseWorkspace(new Pose[BoneCount], new Pose[BoneCount], new Pose[BoneCount], displayed, new Pose[BoneCount], new RigidPose[BoneCount]);
            var state = new ShapeFrameState();
            PrepareFrame(1uL, out var frame, out var ordered, out var layers);
            Assert.AreEqual(AnimationError.None, ShapeTimelinePose.Prepare(frame, ordered, layers, true, destination, incoming,
                geometry, binding, rows, offsets, scales, state, bones, scratch, previousContributions, previousLayers, poseWorkspace, out var prepared, out _));
            Assert.AreEqual(AnimationError.None, ShapeTimelinePose.Commit(prepared, ordered, layers, scratch, poseWorkspace, ref state, bones, previousContributions, previousLayers));
            var committed = displayed[2].Position;
            PrepareFrame(2uL, out var next, out var ordered2, out var layers2);
            Assert.AreEqual(AnimationError.NeedsData, ShapeTimelinePose.Evaluate(next, ordered2, layers2, false, destination, incoming,
                geometry, binding, rows, offsets, scales, ref state, bones, scratch, previousContributions, previousLayers, poseWorkspace, out _));
            Assert.AreEqual(1uL, state.Motion.Waiting);
            Assert.AreEqual(committed.X, displayed[2].Position.X, 1e-6f);
            Assert.AreEqual(committed.Y, displayed[2].Position.Y, 1e-6f);
            Assert.AreEqual(committed.Z, displayed[2].Position.Z, 1e-6f);
        }

        [Test]
        public void NeutralShapeMatchesUnshapedTimeline()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding, out _);
            var definition = TestDefinition(binding.Target, out var channels, out var offsets, out var skin);
            var world = new World("SA.ShapeAdapter.Neutral");
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
                var plain = BuildRig(world, tableBlob, clipA, clipB, default, 0f);
                var shaped = BuildRig(world, tableBlob, clipA, clipB, shapeBlob, 0f);
                var elapsed = 0.0;
                for (var step = 0; step < 30; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                Assert.AreEqual(1, SharedFamilySystem.LastEntityCount);
                Assert.AreEqual(1, ShapeTimelineSystem.LastEntityCount);
                Assert.AreEqual(0uL, world.EntityManager.GetComponentData<ShapeState>(shaped).Revision);
                var plainPositions = BonePositions(world, plain);
                var shapedPositions = BonePositions(world, shaped);
                var plainRotations = BoneRotations(world, plain);
                var shapedRotations = BoneRotations(world, shaped);
                for (var b = 0; b < BoneCount; b++)
                {
                    Assert.AreEqual(plainPositions[b].x, shapedPositions[b].x, 1e-5f, "position x drifted on bone " + b);
                    Assert.AreEqual(plainPositions[b].y, shapedPositions[b].y, 1e-5f, "position y drifted on bone " + b);
                    Assert.AreEqual(plainPositions[b].z, shapedPositions[b].z, 1e-5f, "position z drifted on bone " + b);
                    Assert.Less(AngleDegrees(plainRotations[b], shapedRotations[b]), 1e-3f, "rotation drifted on bone " + b);
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
        public void RequestedShapeLengthensLimbAndSurvivesInterrupt()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding, out _);
            var definition = TestDefinition(binding.Target, out var channels, out var offsets, out var skin);
            var world = new World("SA.ShapeAdapter.Shaped");
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
                var neutral = BuildRig(world, tableBlob, clipA, clipB, shapeBlob, 0f);
                var shaped = BuildRig(world, tableBlob, clipA, clipB, shapeBlob, 1f);
                var elapsed = 0.0;
                for (var step = 0; step < 120; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                var revision = world.EntityManager.GetComponentData<ShapeState>(shaped).Revision;
                Assert.Greater(revision, 0uL);
                Assert.AreEqual(0uL, world.EntityManager.GetComponentData<ShapeState>(neutral).Revision);
                var neutralPositions = BonePositions(world, neutral);
                var shapedPositions = BonePositions(world, shaped);
                Assert.Greater(shapedPositions[2].y - neutralPositions[2].y, 0.01f, "lengthened limb did not rise");
                for (var b = 0; b < BoneCount; b++)
                {
                    var scale = world.EntityManager.GetComponentData<LocalTransform>(
                        world.EntityManager.GetBuffer<BoneEntity>(shaped)[b].Value).Scale;
                    Assert.AreEqual(1f, scale, 1e-6f, "bone " + b + " scale drifted");
                }
                var contributions = world.EntityManager.GetBuffer<SharedContribution>(shaped);
                SharedFamilyControl.PlaySingle(ref contributions, 1);
                for (var step = 0; step < 3; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                Assert.AreEqual(revision, world.EntityManager.GetComponentData<ShapeState>(shaped).Revision);
                var after = BonePositions(world, shaped);
                for (var b = 0; b < BoneCount; b++)
                    Assert.IsTrue(math.all(math.isfinite(after[b])), "bone " + b + " left finite space after interrupt");
                Assert.AreEqual("None", ShapeTimelineSystem.LastSmooth);
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
        public void MismatchedTargetFallsBackWithoutWriting()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding, out _);
            var wrong = TestProfile(new AssetId(1uL, 2uL, 3uL, 4uL));
            var mismatched = new ShapeProfile(wrong.Definition, wrong.Target, wrong.SupportA, wrong.SupportB, wrong.Grounding,
                wrong.MinimumLength, wrong.MaximumLength, wrong.MinimumSkinScale, wrong.MaximumSkinScale);
            var world = new World("SA.ShapeAdapter.Mismatch");
            BlobAssetReference<SharedRetargetData> tableBlob = default;
            BlobAssetReference<SharedClipData> clipA = default;
            BlobAssetReference<SharedClipData> clipB = default;
            BlobAssetReference<ShapeDefinitionData> shapeBlob = default;
            try
            {
                clipA = ClipBlob(ClipBytes(0.4f, 1.0), 1.0);
                clipB = ClipBlob(ClipBytes(-0.3f, 2.0), 2.0);
                tableBlob = TableBlob(table, calibration);
                shapeBlob = ShapeBlob(mismatched, TestChannels(), TestOffsets(), TestSkin());
                var shaped = BuildRig(world, tableBlob, clipA, clipB, shapeBlob, 1f);
                var elapsed = 0.0;
                for (var step = 0; step < 5; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                Assert.AreEqual(0uL, world.EntityManager.GetComponentData<ShapeState>(shaped).Revision);
                var positions = BonePositions(world, shaped);
                for (var b = 0; b < BoneCount; b++)
                    Assert.AreEqual(0f, positions[b].x + positions[b].y + positions[b].z, 0f, "mismatched definition wrote bone " + b);
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

        static Affine ToAffineModel(in RigidPose model)
        {
            var x = global::SA.Motion.Numerics.Quaternion.Rotate(model.Rotation, new Float3(1f, 0f, 0f));
            var y = global::SA.Motion.Numerics.Quaternion.Rotate(model.Rotation, new Float3(0f, 1f, 0f));
            var z = global::SA.Motion.Numerics.Quaternion.Rotate(model.Rotation, new Float3(0f, 0f, 1f));
            return new Affine(x, y, z, model.Position);
        }

        static Pose[] LocalFromOffsets(Pose[] calibration, Float3[] offsets)
        {
            var local = new Pose[calibration.Length];
            for (var b = 0; b < local.Length; b++)
                local[b] = new Pose { Position = offsets[b], Rotation = calibration[b].Rotation, Scale = Float3.One };
            return local;
        }

        static int[] RigParents(RetargetRow[] rows)
        {
            var parents = new int[rows.Length];
            for (var b = 0; b < parents.Length; b++)
                parents[b] = rows[b].Parent;
            return parents;
        }

        [Test]
        public void RestPoseRestoresExactPaletteOnSyntheticBinding()
        {
            var calibration = Calibration();
            LoadBinding(TableBytes(calibration), calibration, out var binding, out var rows);
            var definition = TestDefinition(binding.Target, out _, out _, out _);
            var offsets = new Float3[BoneCount];
            var deform = new Affine[BoneCount];
            var models = new RigidPose[BoneCount];
            var workspace = new ShapeWorkspace(offsets, new float[BoneCount], new float[BoneCount],
                new Float3[BoneCount], deform, new RigidPose[BoneCount]);
            Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(definition, binding, rows, new float[] { 0f }, 0uL, workspace, out _));
            Assert.AreEqual(AnimationError.None, RigidMath.Model(TargetParents, calibration, models));
            var paletteToRig = new[] { 2, 0, 1, 3, 5, 4, 6 };
            var bindposes = new Affine[BoneCount];
            for (var p = 0; p < BoneCount; p++)
            {
                Assert.IsTrue(RigidMath.TryInverse(models[paletteToRig[p]], out var inverse));
                Assert.IsTrue(RigidMath.TryCompose(inverse, new RigidPose(new Float3(p, 0f, 0f), global::SA.Motion.Numerics.Quaternion.Identity), out var shifted));
                bindposes[p] = ToAffineModel(shifted);
            }
            var scratch = new Affine[BoneCount];
            var positions = new Affine[BoneCount];
            var normals = new Float3x3[BoneCount];
            Assert.AreEqual(AnimationError.None, ShapeSkinUpload.BuildPalette(Affine.Identity, models, deform, paletteToRig, bindposes, scratch, positions, normals));
            for (var p = 0; p < BoneCount; p++)
            {
                Assert.AreEqual(1f, positions[p].C0.X, 1e-5f);
                Assert.AreEqual(0f, positions[p].C0.Y, 1e-5f);
                Assert.AreEqual(0f, positions[p].C0.Z, 1e-5f);
                Assert.AreEqual(0f, positions[p].C1.X, 1e-5f);
                Assert.AreEqual(1f, positions[p].C1.Y, 1e-5f);
                Assert.AreEqual(0f, positions[p].C1.Z, 1e-5f);
                Assert.AreEqual(0f, positions[p].C2.X, 1e-5f);
                Assert.AreEqual(0f, positions[p].C2.Y, 1e-5f);
                Assert.AreEqual(1f, positions[p].C2.Z, 1e-5f);
                Assert.AreEqual((float)p, positions[p].C3.X, 1e-5f);
                Assert.AreEqual(0f, positions[p].C3.Y, 1e-5f);
                Assert.AreEqual(0f, positions[p].C3.Z, 1e-5f);
                Assert.AreEqual(1f, normals[p].C0.X, 1e-5f);
                Assert.AreEqual(0f, normals[p].C1.X, 1e-5f);
                Assert.AreEqual(0f, normals[p].C2.X, 1e-5f);
            }
        }

        [Test]
        public void ThickSoleShiftsContactMarker()
        {
            var calibration = Calibration();
            LoadBinding(TableBytes(calibration), calibration, out var binding, out var rows);
            var definition = TestDefinition(binding.Target, out _, out _, out _);
            var workspace = new ShapeWorkspace(new Float3[BoneCount], new float[BoneCount], new float[BoneCount],
                new Float3[BoneCount], new Affine[BoneCount], new RigidPose[BoneCount]);
            Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(definition, binding, rows, new float[] { 0f }, 0uL, workspace, out _));
            var sole = definition.Profile.SupportA;
            Assert.IsTrue(ShapeContactUpload.MarkerPoint(workspace.Models[sole.Bone], workspace.Deformations[sole.Bone], sole.Offset, out var bare));
            var thick = new Affine(new Float3(1f, 0f, 0f), new Float3(0f, 2f, 0f), new Float3(0f, 0f, 1f), Float3.Zero);
            Assert.IsTrue(ShapeContactUpload.MarkerPoint(workspace.Models[sole.Bone], thick, sole.Offset, out var shod));
            Assert.AreEqual(0.1f, (shod - bare).Length, 1e-5f);
        }

        [Test]
        public void AsymmetricReachKeepsTranslationsBitForBit()
        {
            var calibration = Calibration();
            LoadBinding(TableBytes(calibration), calibration, out var binding, out var rows);
            var channels = TestChannels();
            var longOffsets = new[] { new OffsetEffect(0, 2, new Float3(0f, 0f, 0f), 0.22314355f) };
            var profile = TestProfile(binding.Target);
            var longDefinition = new ShapeDefinition(profile, channels, longOffsets, Array.Empty<SkinEffect>(), Array.Empty<SkinFrame>());
            var shortDefinition = new ShapeDefinition(profile, channels, Array.Empty<OffsetEffect>(), Array.Empty<SkinEffect>(), Array.Empty<SkinFrame>());
            var shortOffsets = new Float3[BoneCount];
            var longOffsetsOut = new Float3[BoneCount];
            var shortWorkspace = new ShapeWorkspace(shortOffsets, new float[BoneCount], new float[BoneCount],
                new Float3[BoneCount], new Affine[BoneCount], new RigidPose[BoneCount]);
            var longWorkspace = new ShapeWorkspace(longOffsetsOut, new float[BoneCount], new float[BoneCount],
                new Float3[BoneCount], new Affine[BoneCount], new RigidPose[BoneCount]);
            Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(shortDefinition, binding, rows, new float[] { 0f }, 0uL, shortWorkspace, out _));
            Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(longDefinition, binding, rows, new float[] { 1f }, 1uL, longWorkspace, out _));
            var limb = new ShapeLimb(1, 2, 3);
            var scratch = new RigidPose[BoneCount];
            var shortLocal = LocalFromOffsets(calibration, shortOffsets);
            var longLocal = LocalFromOffsets(calibration, longOffsetsOut);
            Assert.AreEqual(AnimationError.None, ShapeContactUpload.Measure(rows, shortLocal, limb, scratch, out var shortMetrics));
            Assert.AreEqual(AnimationError.None, ShapeContactUpload.Measure(rows, longLocal, limb, scratch, out var longMetrics));
            Assert.Greater(longMetrics.UpperLength, shortMetrics.UpperLength * 1.2f);
            Assert.AreEqual(shortMetrics.LowerLength, longMetrics.LowerLength, 1e-5f);
            Assert.Greater(longMetrics.MaximumReach - shortMetrics.MaximumReach, 0.005f);
            var parents = RigParents(rows);
            Assert.AreEqual(AnimationError.None, RigidMath.Model(parents, longLocal, scratch));
            var direction = scratch[limb.End].Position - scratch[limb.Upper].Position;
            direction *= 1f / direction.Length;
            var target = new ShapeContactTarget(
                scratch[limb.Upper].Position + direction * (longMetrics.MaximumReach + 0.05f),
                scratch[limb.End].Position + new Float3(0f, 0f, 1f),
                scratch[limb.End].Rotation);
            var shortOutput = new Pose[BoneCount];
            var longOutput = new Pose[BoneCount];
            var reachScratch = new RigidPose[BoneCount];
            Assert.AreEqual(AnimationError.None, ShapeContactUpload.Reach(rows, shortLocal, limb, target, reachScratch, shortOutput, out var shortResult));
            Assert.AreEqual(AnimationError.None, ShapeContactUpload.Reach(rows, longLocal, limb, target, reachScratch, longOutput, out var longResult));
            Assert.AreEqual((byte)1, shortResult.Clamped);
            Assert.AreEqual((byte)1, longResult.Clamped);
            Assert.Greater(shortResult.Residual, 0.04f);
            Assert.Greater(longResult.Residual, 0.04f);
            Assert.Greater(shortResult.Residual, longResult.Residual);
            for (var b = 0; b < BoneCount; b++)
            {
                Assert.AreEqual(shortLocal[b].Position.X, shortOutput[b].Position.X);
                Assert.AreEqual(shortLocal[b].Position.Y, shortOutput[b].Position.Y);
                Assert.AreEqual(shortLocal[b].Position.Z, shortOutput[b].Position.Z);
                Assert.AreEqual(longLocal[b].Position.X, longOutput[b].Position.X);
                Assert.AreEqual(longLocal[b].Position.Y, longOutput[b].Position.Y);
                Assert.AreEqual(longLocal[b].Position.Z, longOutput[b].Position.Z);
            }
            foreach (var b in new[] { 0, 4, 5, 6 })
            {
                Assert.AreEqual(shortLocal[b].Rotation.X, shortOutput[b].Rotation.X);
                Assert.AreEqual(longLocal[b].Rotation.X, longOutput[b].Rotation.X);
            }
        }

        [Test]
        public void SingularBlendFallsBackToUnsupportedScale()
        {
            var palette = new Affine[2];
            palette[0] = Affine.Identity;
            palette[1] = new Affine(new Float3(-1f, 0f, 0f), new Float3(0f, 1f, 0f), new Float3(0f, 0f, -1f), Float3.Zero);
            var influences = new[] { 0, 1 };
            Assert.AreEqual(AnimationError.UnsupportedScale,
                ShapeSkinUpload.BlendVertex(palette, influences, new[] { 0.5f, 0.5f }, out _, out _));
            Assert.AreEqual(AnimationError.None,
                ShapeSkinUpload.BlendVertex(palette, influences, new[] { 0.75f, 0.25f }, out var position, out var normal));
            Assert.IsTrue(ShapeSkinUpload.FinishNormal(normal, new Float3(0f, 1f, 0f), out var up));
            Assert.AreEqual(0f, up.X, 1e-5f);
            Assert.AreEqual(1f, up.Y, 1e-5f);
            Assert.AreEqual(0f, up.Z, 1e-5f);
            Assert.IsTrue(ShapeSkinUpload.FinishTangent(position, new Float3(1f, 0f, 0f), 1f, up, out var tangent, out var handedness));
            Assert.AreEqual(1f, handedness);
            Assert.AreEqual(0f, Float3.Dot(up, tangent), 1e-5f);
        }

        [Test]
        public void MorphWeightsClampToAuthoredRange()
        {
            var channels = new[] { new ShapeChannel(7u, -10f, 10f, 0f, 0f) };
            var morphs = new[] { new ShapeMorph(0f, 1f, 0.2f), new ShapeMorph(-1f, 0f, -0.2f) };
            var effects = new[] { new MorphEffect(0, 0, 0.5f), new MorphEffect(0, 1, 0.5f) };
            var output = new float[2];
            Assert.AreEqual(AnimationError.None, ShapeMorphs.Evaluate(channels, new float[] { 5f }, morphs, effects, output));
            Assert.AreEqual(1f, output[0], 1e-6f);
            Assert.AreEqual(0f, output[1], 1e-6f);
            Assert.AreEqual(AnimationError.None, ShapeMorphs.Evaluate(channels, new float[] { -5f }, morphs, effects, output));
            Assert.AreEqual(0f, output[0], 1e-6f);
            Assert.AreEqual(-1f, output[1], 1e-6f);
            Assert.AreEqual(AnimationError.None, ShapeMorphs.Evaluate(channels, new float[] { 0f }, morphs, effects, output));
            Assert.AreEqual(0.2f, output[0], 1e-6f);
            Assert.AreEqual(-0.2f, output[1], 1e-6f);
        }

        [Test]
        public void InterruptedClipNeutralParity()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding, out _);
            var definition = TestDefinition(binding.Target, out var channels, out var offsets, out var skin);
            var world = new World("SA.ShapeAdapter.InterruptParity");
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
                var plain = BuildRig(world, tableBlob, clipA, clipB, default, 0f);
                var shaped = BuildRig(world, tableBlob, clipA, clipB, shapeBlob, 0f);
                var elapsed = 0.0;
                for (var step = 0; step < 10; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                var plainContributions = world.EntityManager.GetBuffer<SharedContribution>(plain);
                var shapedContributions = world.EntityManager.GetBuffer<SharedContribution>(shaped);
                SharedFamilyControl.PlaySingle(ref plainContributions, 1);
                SharedFamilyControl.PlaySingle(ref shapedContributions, 1);
                for (var step = 0; step < 5; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                Assert.AreEqual(0uL, world.EntityManager.GetComponentData<ShapeState>(shaped).Revision);
                var plainPositions = BonePositions(world, plain);
                var shapedPositions = BonePositions(world, shaped);
                var plainRotations = BoneRotations(world, plain);
                var shapedRotations = BoneRotations(world, shaped);
                for (var b = 0; b < BoneCount; b++)
                {
                    Assert.AreEqual(plainPositions[b].x, shapedPositions[b].x, 1e-5f, "position x drifted on bone " + b);
                    Assert.AreEqual(plainPositions[b].y, shapedPositions[b].y, 1e-5f, "position y drifted on bone " + b);
                    Assert.AreEqual(plainPositions[b].z, shapedPositions[b].z, 1e-5f, "position z drifted on bone " + b);
                    Assert.Less(AngleDegrees(plainRotations[b], shapedRotations[b]), 1e-3f, "rotation drifted on bone " + b);
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
        public void SkinOutputsFollowRevision()
        {
            var calibration = Calibration();
            var table = TableBytes(calibration);
            LoadBinding(table, calibration, out var binding, out var rows);
            var definition = TestDefinition(binding.Target, out var channels, out var offsets, out var skin);
            var morphs = new[] { new ShapeMorph(0f, 1f, 0.2f) };
            var effects = new[] { new MorphEffect(0, 0, 0.5f) };
            var world = new World("SA.ShapeAdapter.SkinOutputs");
            BlobAssetReference<SharedRetargetData> tableBlob = default;
            BlobAssetReference<SharedClipData> clipA = default;
            BlobAssetReference<SharedClipData> clipB = default;
            BlobAssetReference<ShapeDefinitionData> shapeBlob = default;
            try
            {
                clipA = ClipBlob(ClipBytes(0.4f, 1.0), 1.0);
                clipB = ClipBlob(ClipBytes(-0.3f, 2.0), 2.0);
                tableBlob = TableBlob(table, calibration);
                shapeBlob = ShapeBlob(TestProfile(binding.Target), channels, offsets, skin, morphs, effects);
                var neutral = BuildRig(world, tableBlob, clipA, clipB, shapeBlob, 0f);
                var shaped = BuildRig(world, tableBlob, clipA, clipB, shapeBlob, 1f);
                var elapsed = 0.0;
                for (var step = 0; step < 120; step++)
                {
                    elapsed += 1.0 / 60.0;
                    StepWorld(world, elapsed);
                }
                var shapedRevision = world.EntityManager.GetComponentData<ShapeState>(shaped).Revision;
                Assert.Greater(shapedRevision, 0uL);
                var shapedDeforms = world.EntityManager.GetBuffer<ShapeSkinDeformation>(shaped);
                var neutralDeforms = world.EntityManager.GetBuffer<ShapeSkinDeformation>(neutral);
                Assert.AreEqual(BoneCount, shapedDeforms.Length);
                Assert.AreEqual(BoneCount, neutralDeforms.Length);
                Assert.Greater(Math.Abs(shapedDeforms[2].Value.C1.Y - 1f), 0.1f);
                var shapedWeights = world.EntityManager.GetBuffer<ShapeMorphWeight>(shaped);
                var neutralWeights = world.EntityManager.GetBuffer<ShapeMorphWeight>(neutral);
                Assert.AreEqual(1, shapedWeights.Length);
                Assert.AreEqual(1, neutralWeights.Length);
                var expected = new float[1];
                Assert.AreEqual(AnimationError.None, ShapeMorphs.Evaluate(channels, new float[] { 1f }, morphs, effects, expected));
                Assert.AreEqual(expected[0], shapedWeights[0].Value, 1e-6f);
                Assert.AreEqual(0.7f, shapedWeights[0].Value, 1e-6f);
                Assert.AreEqual(0.2f, neutralWeights[0].Value, 1e-6f);
                var shapedContact = world.EntityManager.GetComponentData<ShapeContactState>(shaped);
                var neutralContact = world.EntityManager.GetComponentData<ShapeContactState>(neutral);
                Assert.AreEqual(shapedRevision, shapedContact.Revision);
                Assert.Greater(shapedContact.HipHeight, 0f);
                Assert.IsTrue(shapedContact.SoleA.IsFinite);
                Assert.IsTrue(shapedContact.SoleB.IsFinite);
                var checkWorkspace = new ShapeWorkspace(new Float3[BoneCount], new float[BoneCount], new float[BoneCount],
                    new Float3[BoneCount], new Affine[BoneCount], new RigidPose[BoneCount]);
                var checkDefinition = new ShapeDefinition(TestProfile(binding.Target), channels, offsets, skin, Array.Empty<SkinFrame>());
                Assert.AreEqual(AnimationError.None, ShapeRig.Evaluate(checkDefinition, binding, rows, new float[] { 1f }, shapedRevision, checkWorkspace, out _));
                Assert.IsTrue(ShapeContactUpload.MarkerPoint(checkWorkspace.Models[2], shapedDeforms[2].Value, TestProfile(binding.Target).SupportA.Offset, out var expectedSole));
                Assert.AreEqual(expectedSole.X, shapedContact.SoleA.X, 1e-5f);
                Assert.AreEqual(expectedSole.Y, shapedContact.SoleA.Y, 1e-5f);
                Assert.AreEqual(expectedSole.Z, shapedContact.SoleA.Z, 1e-5f);
                Assert.Greater((shapedContact.SoleA - neutralContact.SoleA).Length, 1e-4f);
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
        public void ShapeControlRoutesByStableId()
        {
            var world = new World("SA.ShapeAdapter.Control");
            try
            {
                var entity = world.EntityManager.CreateEntity(typeof(ShapeChannelState));
                var states = world.EntityManager.GetBuffer<ShapeChannelState>(entity);
                states.Add(new ShapeChannelState { Id = 7u, Requested = 0f, Value = 0f, Velocity = 0f });
                Assert.IsTrue(ShapeControl.SetRequested(states, 7u, 0.5f));
                Assert.IsFalse(ShapeControl.SetRequested(states, 99u, 0.5f));
                Assert.IsTrue(ShapeControl.TryGetRequested(states, 7u, out var value));
                Assert.AreEqual(0.5f, value, 1e-6f);
                Assert.AreEqual(0, ShapeControl.FindChannel(states, 7u));
                Assert.AreEqual(-1, ShapeControl.FindChannel(states, 99u));
            }
            finally
            {
                world.Dispose();
            }
        }
    }
}
