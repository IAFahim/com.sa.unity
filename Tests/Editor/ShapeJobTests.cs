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
using SA.Animation;
using SA.Editor;
using SA.Motion.Numerics;
using SA.Shape;
using SA.Shape.Unity;
using Hash128 = Unity.Entities.Hash128;

namespace SA.Tests
{
    public class ShapeJobTests
    {
        const int Canonical = 56;
        const int Steps = 40;
        const int PaletteSteps = 16;
        const string TraceName = "ShapeTrace.bytes";
        const string PaletteName = "ShapePalette.bytes";

        sealed class Shaped : IDisposable
        {
            public SharedFamilyJobTests.Stage Stage;
            public ShapeTimelineSystem Timeline;
            public SkinningSystem Skinning;
            public SharedFamilyPaletteSystem Palette;
            public ShapeSkinPaletteSystem ShapePalette;
            public readonly List<BlobAssetReference<ShapeDefinitionData>> Shapes = new List<BlobAssetReference<ShapeDefinitionData>>();
            public readonly List<BlobAssetReference<SkinBlob>> Blobs = new List<BlobAssetReference<SkinBlob>>();
            public readonly List<Entity> Skins = new List<Entity>();

            public void Dispose()
            {
                Stage.Dispose();
                foreach (var blob in Shapes)
                    blob.Dispose();
                foreach (var blob in Blobs)
                    blob.Dispose();
            }

            public void Step(float seconds)
            {
                Stage.Step(seconds);
                Timeline.Update();
                Stage.World.EntityManager.CompleteAllTrackedJobs();
            }

            public void Frame(float seconds)
            {
                Step(seconds);
                Skinning.Update();
                Palette.Update();
                ShapePalette.Update();
                Stage.World.EntityManager.CompleteAllTrackedJobs();
            }
        }

        static BlobAssetReference<ShapeDefinitionData> Definition(Shaped scene, uint id, AssetId target, ShapeMarker supportA, ShapeMarker supportB,
            ShapeChannel[] channels, OffsetEffect[] offsets, SkinEffect[] skin, ShapeMorph[] morphs, MorphEffect[] effects)
        {
            var profile = new ShapeProfile(new AssetId(id, id, id, id), target, supportA, supportB);
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
            var storedMorphs = builder.Allocate(ref root.Morphs, morphs.Length);
            for (var i = 0; i < morphs.Length; i++)
                storedMorphs[i] = morphs[i];
            var storedEffects = builder.Allocate(ref root.MorphEffects, effects.Length);
            for (var i = 0; i < effects.Length; i++)
                storedEffects[i] = effects[i];
            var blob = builder.CreateBlobAssetReference<ShapeDefinitionData>(Allocator.Persistent);
            builder.Dispose();
            scene.Shapes.Add(blob);
            return blob;
        }

        static BlobAssetReference<ShapeDefinitionData> SmallShape(Shaped scene, BlobAssetReference<SharedRetargetData> table)
        {
            return Definition(scene, 7u, table.Value.Target,
                new ShapeMarker(2, new Float3(0f, -0.1f, 0.05f)), new ShapeMarker(5, new Float3(0f, -0.1f, 0.05f)),
                new[] { new ShapeChannel(7u, -1f, 1f, 0f, 0.05f) },
                new[] { new OffsetEffect(0, 2, new Float3(0f, 0f, 0f), 0.22314355f) },
                new[] { new SkinEffect(0, 2, new Float3(0f, 0.22314355f, 0f)) },
                Array.Empty<ShapeMorph>(), Array.Empty<MorphEffect>());
        }

        static BlobAssetReference<ShapeDefinitionData> FullShape(Shaped scene, BlobAssetReference<SharedRetargetData> table)
        {
            return Definition(scene, 9u, table.Value.Target,
                new ShapeMarker(50, new Float3(0f, -0.08f, 0.04f)), new ShapeMarker(54, new Float3(0f, -0.08f, 0.04f)),
                new[] { new ShapeChannel(11u, -1f, 1f, 0f, 0.08f), new ShapeChannel(12u, 0f, 2f, 0.5f, 0.03f) },
                new[]
                {
                    new OffsetEffect(0, 49, new Float3(0f, 0f, 0f), 0.18232156f),
                    new OffsetEffect(0, 53, new Float3(0f, 0f, 0f), 0.18232156f),
                    new OffsetEffect(1, 12, new Float3(0.01f, 0f, 0f), -0.10536052f),
                    new OffsetEffect(1, 3, new Float3(0f, 0.02f, 0f), 0f)
                },
                new[]
                {
                    new SkinEffect(0, 48, new Float3(0f, 0.18232156f, 0f)),
                    new SkinEffect(0, 52, new Float3(0f, 0.18232156f, 0f)),
                    new SkinEffect(1, 11, new Float3(0.05f, -0.10536052f, 0.05f))
                },
                new[] { new ShapeMorph(0f, 1f, 0f), new ShapeMorph(-1f, 1f, 0.2f) },
                new[] { new MorphEffect(0, 0, 0.5f), new MorphEffect(1, 1, -0.3f) });
        }

        static Entity Shape(Shaped scene, Entity rig, BlobAssetReference<ShapeDefinitionData> shape, bool physical)
        {
            var manager = scene.Stage.World.EntityManager;
            manager.AddComponent<ShapeDriven>(rig);
            manager.AddComponentData(rig, new ShapeDefinitionRef { Value = shape });
            manager.AddComponentData(rig, new ShapeState { Revision = 0 });
            var states = manager.AddBuffer<ShapeChannelState>(rig);
            ref var definition = ref shape.Value;
            for (var i = 0; i < definition.Channels.Length; i++)
                states.Add(new ShapeChannelState { Id = definition.Channels[i].Id, Requested = definition.Channels[i].Default, Value = definition.Channels[i].Default, Velocity = 0f });
            manager.AddBuffer<ShapeSkinDeformation>(rig);
            manager.AddBuffer<ShapeMorphWeight>(rig);
            manager.AddComponentData(rig, new ShapeContactState());
            if (physical)
                manager.AddComponentData(rig, new ShapePhysical { LivePhysical = 1 });
            return rig;
        }

        static BlobAssetReference<SkinBlob> SkinBlob(Shaped scene, uint id, int rigBones, int paletteBones)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var skin = ref builder.ConstructRoot<SkinBlob>();
            skin.Id = new Hash128(id, 11, 11, 11);
            skin.MeshFromRig = PoseAlgebra.Affine(float4x4.TRS(new float3(0.1f, -0.2f, 0.05f), quaternion.RotateY(0.3f), new float3(1f)));
            var bones = builder.Allocate(ref skin.Bones, paletteBones);
            var bindposes = builder.Allocate(ref skin.Bindposes, paletteBones);
            for (var b = 0; b < paletteBones; b++)
            {
                bones[b] = (b * 5 + 2) % rigBones;
                bindposes[b] = PoseAlgebra.Affine(float4x4.TRS(new float3(0.01f * b, 0.02f * b, -0.01f * b), quaternion.Euler(0.05f * b, -0.03f * b, 0.02f * b), new float3(1f)));
            }
            builder.Allocate(ref skin.Influences, 1);
            var result = builder.CreateBlobAssetReference<SkinBlob>(Allocator.Persistent);
            builder.Dispose();
            scene.Blobs.Add(result);
            return result;
        }

        static void Skin(Shaped scene, Entity rig, BlobAssetReference<SkinBlob> blob)
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
        }

        static Shaped Scene(bool skinned)
        {
            var scene = new Shaped { Stage = new SharedFamilyJobTests.Stage { World = new World("SA.ShapeJob") } };
            var stage = scene.Stage;
            var manager = stage.World.EntityManager;
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            var parents = SharedFamilyBake.CanonicalParentsArray();
            var first = SharedFamilyJobTests.Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
            var second = SharedFamilyJobTests.Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
            var smallTable = SharedFamilyJobTests.SmallTable(stage, reference);
            var fullTable = SharedFamilyJobTests.FullTable(stage, reference, parents);
            var small = SharedFamilyJobTests.Rig(stage, SharedFamilyJobTests.SmallParents(), smallTable, new[] { first, second }, new[] { 1f, 0f }, 0f);
            var full = SharedFamilyJobTests.Rig(stage, parents, fullTable, new[] { first, second }, new[] { 1f, 0f }, 0f);
            var locked = SharedFamilyJobTests.Rig(stage, SharedFamilyJobTests.SmallParents(), smallTable, new[] { first, second }, new[] { 0.5f, 0.5f }, 0.2f);
            Shape(scene, small, SmallShape(scene, smallTable), false);
            Shape(scene, full, FullShape(scene, fullTable), false);
            Shape(scene, locked, SmallShape(scene, smallTable), true);
            var fullBones = manager.GetBuffer<BoneEntity>(full).ToNativeArray(Allocator.Temp);
            for (var b = 0; b < fullBones.Length; b += 3)
                manager.AddComponentData(fullBones[b].Value, new PostTransformMatrix { Value = float4x4.identity });
            fullBones.Dispose();
            if (skinned)
            {
                Skin(scene, small, SkinBlob(scene, 1, 7, 7));
                Skin(scene, full, SkinBlob(scene, 2, Canonical, 50));
                Skin(scene, full, SkinBlob(scene, 3, Canonical, 20));
                Skin(scene, locked, SkinBlob(scene, 4, 7, 5));
            }
            stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
            scene.Timeline = stage.World.GetOrCreateSystemManaged<ShapeTimelineSystem>();
            if (skinned)
            {
                scene.Skinning = stage.World.GetOrCreateSystemManaged<SkinningSystem>();
                scene.Palette = stage.World.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
                scene.ShapePalette = stage.World.GetOrCreateSystemManaged<ShapeSkinPaletteSystem>();
            }
            return scene;
        }

        static void Request(Shaped scene, int rig, uint id, float value)
        {
            var channels = scene.Stage.World.EntityManager.GetBuffer<ShapeChannelState>(scene.Stage.Rigs[rig]);
            Assert.IsTrue(ShapeControl.SetRequested(channels, id, value));
        }

        static void Act(Shaped scene, int step)
        {
            var stage = scene.Stage;
            var manager = stage.World.EntityManager;
            if (step == 4)
                Request(scene, 0, 7u, 0.8f);
            if (step == 9)
            {
                var contributions = manager.GetBuffer<SharedContribution>(stage.Rigs[0]);
                SharedFamilyControl.PlaySingle(ref contributions, 1);
            }
            if (step == 12)
            {
                Request(scene, 1, 11u, -0.6f);
                Request(scene, 1, 12u, 1.7f);
                var contributions = manager.GetBuffer<SharedContribution>(stage.Rigs[1]);
                var a = contributions[0];
                var b = contributions[1];
                a.Weight = 0.3f;
                b.Weight = 0.7f;
                contributions[0] = a;
                contributions[1] = b;
            }
            if (step == 16)
                Request(scene, 2, 7u, 0.5f);
            if (step == 21)
                Request(scene, 0, 7u, 0f);
            if (step == 24 || step == 25)
            {
                var clock = manager.GetComponentData<SharedClock>(stage.Rigs[1]);
                SharedFamilyControl.Seek(ref clock, 1.5);
                manager.SetComponentData(stage.Rigs[1], clock);
            }
            if (step == 28 || step == 30)
            {
                var clock = manager.GetComponentData<SharedClock>(stage.Rigs[1]);
                SharedFamilyControl.SetPaused(ref clock, step == 28);
                manager.SetComponentData(stage.Rigs[1], clock);
            }
            if (step == 31)
            {
                var physical = manager.GetComponentData<ShapePhysical>(stage.Rigs[2]);
                physical.LivePhysical = 0;
                manager.SetComponentData(stage.Rigs[2], physical);
                Request(scene, 2, 7u, 0.5f);
            }
            if (step == 33)
                Request(scene, 1, 11u, 0.9f);
        }

        static float Seconds(int step)
        {
            if (step < 2 || step == 24 || step == 25)
                return 0f;
            return step == 19 ? 0.3f : 1f / 60f;
        }

        static void Add(List<float> trace, Float3 value)
        {
            trace.Add(value.X);
            trace.Add(value.Y);
            trace.Add(value.Z);
        }

        static void Record(Shaped scene, List<float> trace)
        {
            var manager = scene.Stage.World.EntityManager;
            foreach (var rig in scene.Stage.Rigs)
            {
                var clock = manager.GetComponentData<SharedClock>(rig);
                trace.Add((float)clock.Seconds);
                trace.Add(clock.Tick);
                trace.Add(clock.Continuity);
                trace.Add(clock.Seek);
                var contributions = manager.GetBuffer<SharedContribution>(rig);
                trace.Add((float)contributions[0].Sample);
                trace.Add((float)contributions[1].Sample);
                var channels = manager.GetBuffer<ShapeChannelState>(rig);
                trace.Add(channels.Length);
                for (var i = 0; i < channels.Length; i++)
                {
                    trace.Add(channels[i].Requested);
                    trace.Add(channels[i].Value);
                    trace.Add(channels[i].Velocity);
                }
                trace.Add(manager.GetComponentData<ShapeState>(rig).Revision);
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
                    if (manager.HasComponent<PostTransformMatrix>(bone.Value))
                    {
                        var post = manager.GetComponentData<PostTransformMatrix>(bone.Value).Value;
                        trace.Add(post.c0.x);
                        trace.Add(post.c1.y);
                        trace.Add(post.c2.z);
                    }
                }
                bones.Dispose();
                var deformations = manager.GetBuffer<ShapeSkinDeformation>(rig);
                trace.Add(deformations.Length);
                for (var i = 0; i < deformations.Length; i++)
                {
                    Add(trace, deformations[i].Value.C0);
                    Add(trace, deformations[i].Value.C1);
                    Add(trace, deformations[i].Value.C2);
                    Add(trace, deformations[i].Value.C3);
                }
                var morphs = manager.GetBuffer<ShapeMorphWeight>(rig);
                trace.Add(morphs.Length);
                for (var i = 0; i < morphs.Length; i++)
                    trace.Add(morphs[i].Value);
                var contact = manager.GetComponentData<ShapeContactState>(rig);
                Add(trace, contact.SoleA);
                Add(trace, contact.SoleB);
                trace.Add(contact.HipHeight);
                trace.Add(contact.GroundY);
                trace.Add(contact.Revision);
                if (manager.HasComponent<ShapePhysical>(rig))
                {
                    var physical = manager.GetComponentData<ShapePhysical>(rig);
                    trace.Add(physical.LivePhysical);
                    trace.Add(physical.PendingRebuild);
                    trace.Add(physical.Diagnostic.Length);
                }
            }
        }

        static void RecordPalettes(Shaped scene, List<float> trace)
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

        static float[] Play(out string warning)
        {
            var trace = new List<float>();
            using (var scene = Scene(false))
            {
                var seen = ShapeTimelineSystem.LastWarning;
                warning = string.Empty;
                for (var step = 0; step <= Steps; step++)
                {
                    if (step < Steps)
                    {
                        Act(scene, step);
                        scene.Step(Seconds(step));
                        Record(scene, trace);
                    }
                    else
                        scene.Step(1f / 60f);
                    if (ShapeTimelineSystem.LastWarning != seen)
                    {
                        seen = ShapeTimelineSystem.LastWarning;
                        warning += "[" + step + "] " + seen + " ";
                    }
                }
            }
            return trace.ToArray();
        }

        static float[] PlayPalettes()
        {
            var trace = new List<float>();
            using (var scene = Scene(true))
            {
                for (var step = 0; step < PaletteSteps; step++)
                {
                    if (step == 3)
                    {
                        Request(scene, 0, 7u, 0.8f);
                        Request(scene, 1, 11u, -0.6f);
                        Request(scene, 1, 12u, 1.7f);
                    }
                    if (step == 8)
                    {
                        var contributions = scene.Stage.World.EntityManager.GetBuffer<SharedContribution>(scene.Stage.Rigs[1]);
                        SharedFamilyControl.PlaySingle(ref contributions, 1);
                    }
                    scene.Frame(step == 0 ? 0f : 1f / 60f);
                    RecordPalettes(scene, trace);
                }
            }
            return trace.ToArray();
        }

        static string FixturePath(string name)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ShapeJobTests).Assembly);
            Assert.IsNotNull(package, "the SA package could not be located");
            return Path.Combine(package.resolvedPath, "Tests", "Editor", "Fixtures", name);
        }

        static void Store(string name, float[] trace)
        {
            var bytes = new byte[trace.Length * 4];
            Buffer.BlockCopy(trace, 0, bytes, 0, bytes.Length);
            var path = FixturePath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            TestContext.WriteLine("wrote " + trace.Length + " values to " + path);
            Assert.Greater(trace.Length, 0);
        }

        static void Compare(string label, string name, float[] actual, int steps)
        {
            var path = FixturePath(name);
            Assert.IsTrue(File.Exists(path), "missing fixture " + path);
            var stored = File.ReadAllBytes(path);
            var expected = new float[stored.Length / 4];
            Buffer.BlockCopy(stored, 0, expected, 0, expected.Length * 4);
            Assert.AreEqual(expected.Length, actual.Length, "trace length");
            var worst = 0f;
            var worstIndex = -1;
            var exact = 0;
            var moved = 0f;
            var stride = steps > 0 ? expected.Length / steps : 0;
            for (var i = 0; i < expected.Length; i++)
            {
                var gap = Math.Abs(expected[i] - actual[i]);
                if (gap == 0f || float.IsNaN(expected[i]) && float.IsNaN(actual[i]))
                    exact++;
                else if (float.IsNaN(gap))
                    gap = float.MaxValue;
                if (gap > worst)
                {
                    worst = gap;
                    worstIndex = i;
                }
                if (stride > 0 && stride * steps == expected.Length)
                    moved = Math.Max(moved, Math.Abs(actual[i] - actual[i % stride]));
            }
            TestContext.WriteLine(label + " values " + expected.Length + " exact " + exact + " worst " + worst.ToString("R", CultureInfo.InvariantCulture) + " at " + worstIndex);
            if (stride > 0 && stride * steps == expected.Length)
                Assert.Greater(moved, 0.01f, "nothing changed over the scene");
            Assert.LessOrEqual(worst, 2e-5f, "value " + worstIndex + " of " + expected.Length + " differs from the recording");
        }

        [Test]
        [Explicit("Overwrites the shape trace fixture with what the system compiled now produces. Run it only when the old behaviour is meant to change.")]
        public void RecordTheShapeTraceFromTheCurrentSystem()
        {
            var trace = Play(out var warning);
            TestContext.WriteLine("warnings: " + warning);
            Store(TraceName, trace);
        }

        [Test]
        [Explicit("Overwrites the shape palette fixture with what the system compiled now uploads. Run it only when the old behaviour is meant to change.")]
        public void RecordTheShapePalettesFromTheCurrentSystem()
        {
            TestRigs.RequireGpu();
            Store(PaletteName, PlayPalettes());
        }

        [Test]
        public void TheShapeSystemReproducesTheTraceRecordedFromTheMainThreadSystem()
        {
            var trace = Play(out var warning);
            TestContext.WriteLine("warnings: " + warning);
            Compare("ShapeTraceGap", TraceName, trace, Steps);
        }

        [Test]
        public void TheShapePalettesMatchTheOnesRecordedFromTheMainThreadSystem()
        {
            TestRigs.RequireGpu();
            Compare("ShapePaletteGap", PaletteName, PlayPalettes(), PaletteSteps);
        }

        [Test]
        public void TheShapeJobRunsAsBurstCode()
        {
            using (var scene = Scene(false))
            {
                scene.Step(0f);
                scene.Step(1f / 60f);
                foreach (var rig in scene.Stage.Rigs)
                    Assert.AreEqual(1, scene.Stage.World.EntityManager.GetComponentData<ShapePoseState>(rig).Native, "the shape job fell back to managed code");
            }
        }

        [Test]
        public void TheShapePaletteStagingHoldsOnlyShapedSkins()
        {
            TestRigs.RequireGpu();
            using (var scene = Scene(true))
            {
                scene.Frame(0f);
                scene.Frame(1f / 60f);
                const int shaped = 7 + 50 + 20 + 5;
                Assert.AreEqual(shaped, scene.ShapePalette.StagedMatrices);
                Assert.LessOrEqual(scene.ShapePalette.StagingCapacity, 2 * shaped);
                Assert.That(scene.ShapePalette.UploadCalls, Is.InRange(1, 4));
                Assert.AreEqual(0, scene.Palette.StagedMatrices, "the shared palette system staged a shaped skin");
            }
        }

        [Test]
        public void ARigThatStopsBeingShapedDropsItsShapeState()
        {
            using (var scene = Scene(false))
            {
                var manager = scene.Stage.World.EntityManager;
                var rig = scene.Stage.Rigs[0];
                for (var step = 0; step < 4; step++)
                    scene.Step(step == 0 ? 0f : 1f / 60f);
                Assert.IsTrue(manager.HasComponent<ShapePoseState>(rig));
                Assert.AreEqual(3, ShapeTimelineSystem.TrackedRigCount);
                manager.RemoveComponent<ShapeDriven>(rig);
                scene.Step(1f / 60f);
                Assert.IsFalse(manager.HasComponent<ShapePoseState>(rig), "shape state stayed on a rig that is no longer shaped");
                Assert.AreEqual(2, ShapeTimelineSystem.TrackedRigCount);
                var before = manager.GetComponentData<LocalTransform>(manager.GetBuffer<BoneEntity>(rig)[2].Value).Rotation;
                scene.Step(1f / 60f);
                scene.Step(1f / 60f);
                var after = manager.GetComponentData<LocalTransform>(manager.GetBuffer<BoneEntity>(rig)[2].Value).Rotation;
                Assert.Greater(math.length(after.value - before.value), 1e-5f, "the unshaped rig stopped animating");
            }
        }

        [Test]
        public void AShapeRigWithTheWrongChannelCountSaysSo()
        {
            using (var scene = Scene(false))
            {
                var manager = scene.Stage.World.EntityManager;
                manager.GetBuffer<ShapeChannelState>(scene.Stage.Rigs[0]).Add(new ShapeChannelState { Id = 99u });
                scene.Step(0f);
                Assert.AreEqual("SA: shape rig has 2 channel states for 1 definition channels.", ShapeTimelineSystem.LastWarning);
            }
        }

        [Test]
        [Explicit("Prints the cost of ShapeTimelineSystem and ShapeSkinPaletteSystem for 300 shaped rigs of 56 bones")]
        public void CostOfTheShapeSystemsPerRig()
        {
            TestRigs.RequireGpu();
            const int count = 300;
            const int warm = 20;
            const int measured = 60;
            using (var scene = new Shaped { Stage = new SharedFamilyJobTests.Stage { World = new World("SA.ShapeJob.Cost") } })
            {
                var stage = scene.Stage;
                var reference = SharedFamilyBake.CanonicalReferencePoses();
                var parents = SharedFamilyBake.CanonicalParentsArray();
                var first = SharedFamilyJobTests.Clip(stage, reference, 33, 1.0, 0.4f, 0.0);
                var second = SharedFamilyJobTests.Clip(stage, reference, 41, 2.0, -0.3f, 0.7);
                var table = SharedFamilyJobTests.FullTable(stage, reference, parents);
                var shape = FullShape(scene, table);
                var skin = SkinBlob(scene, 5, Canonical, Canonical);
                for (var i = 0; i < count; i++)
                {
                    var rig = SharedFamilyJobTests.Rig(stage, parents, table, new[] { first, second }, new[] { 1f, 0f }, i * 0.001f);
                    Shape(scene, rig, shape, false);
                    Skin(scene, rig, skin);
                }
                stage.System = stage.World.GetOrCreateSystemManaged<SharedFamilySystem>();
                scene.Timeline = stage.World.GetOrCreateSystemManaged<ShapeTimelineSystem>();
                scene.Skinning = stage.World.GetOrCreateSystemManaged<SkinningSystem>();
                scene.Palette = stage.World.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
                scene.ShapePalette = stage.World.GetOrCreateSystemManaged<ShapeSkinPaletteSystem>();
                for (var step = 0; step < warm; step++)
                    scene.Frame(1f / 60f);
                var timeline = new double[measured];
                var palette = new double[measured];
                var watch = new Stopwatch();
                for (var step = 0; step < measured; step++)
                {
                    if (step % 10 == 0)
                        for (var i = 0; i < count; i++)
                            Request(scene, i, 11u, (step / 10 % 2 == 0 ? 0.5f : -0.5f) + i * 0.0001f);
                    stage.Step(1f / 60f);
                    watch.Restart();
                    scene.Timeline.Update();
                    stage.World.EntityManager.CompleteAllTrackedJobs();
                    watch.Stop();
                    timeline[step] = watch.Elapsed.TotalMilliseconds;
                    scene.Skinning.Update();
                    scene.Palette.Update();
                    watch.Restart();
                    scene.ShapePalette.Update();
                    stage.World.EntityManager.CompleteAllTrackedJobs();
                    watch.Stop();
                    palette[step] = watch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(timeline);
                Array.Sort(palette);
                var report = "{\n  \"Rigs\": " + count + ",\n  \"Bones\": " + Canonical
                    + ",\n  \"ShapeTimelineMedianMsPerFrame\": " + timeline[measured / 2].ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"ShapeTimelineP95MsPerFrame\": " + timeline[(int)(measured * 0.95)].ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"ShapePaletteMedianMsPerFrame\": " + palette[measured / 2].ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"ShapePaletteP95MsPerFrame\": " + palette[(int)(measured * 0.95)].ToString("F4", CultureInfo.InvariantCulture)
                    + ",\n  \"Workers\": " + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount
                    + ",\n  \"Gpu\": \"" + UnityEngine.SystemInfo.graphicsDeviceName + "\",\n  \"Api\": \"" + UnityEngine.SystemInfo.graphicsDeviceType + "\"\n}\n";
                TestContext.WriteLine(report);
                Assert.Greater(timeline[measured / 2], 0.0);
            }
        }
    }
}
