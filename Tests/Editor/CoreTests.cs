using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace SA.Tests
{
    public class CoreTests
    {
        readonly System.Collections.Generic.List<World> worlds = new System.Collections.Generic.List<World>();
        TestRig rig;

        [SetUp]
        public void SetUp()
        {
            rig = TestRigs.Build();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var world in worlds)
                world.Dispose();
            worlds.Clear();
            rig.Dispose();
        }

        DynamicBuffer<ClipState> States()
        {
            var world = new World("SA.Tests");
            worlds.Add(world);
            return world.EntityManager.AddBuffer<ClipState>(world.EntityManager.CreateEntity());
        }

        static BonePose RandomPose(ref Random random) => new BonePose
        {
            Position = random.NextFloat3(-2f, 2f),
            Rotation = random.NextQuaternionRotation(),
            Scale = random.NextFloat3(0.5f, 2f)
        };

        [Test]
        public void LerpEndpointsAreItsInputs()
        {
            var random = new Random(7);
            for (var i = 0; i < 100; i++)
            {
                var a = RandomPose(ref random);
                var b = RandomPose(ref random);
                Assert.Less(TestRigs.Distance(PoseAlgebra.Lerp(a, b, 0f), a), 1e-5f);
                Assert.Less(TestRigs.Distance(PoseAlgebra.Lerp(a, b, 1f), b), 1e-5f);
            }
        }

        [Test]
        public void AccumulatingADeltaRecoversThePose()
        {
            var random = new Random(11);
            for (var i = 0; i < 100; i++)
            {
                var reference = RandomPose(ref random);
                var pose = RandomPose(ref random);
                Assert.Less(TestRigs.Distance(PoseAlgebra.Accumulate(reference, PoseAlgebra.Delta(pose, reference), 1f), pose), 1e-4f);
                Assert.Less(TestRigs.Distance(PoseAlgebra.Accumulate(pose, PoseAlgebra.Delta(reference, reference), 0.6f), pose), 1e-5f);
            }
        }

        [Test]
        public void MeanOfOneIsItselfAndIgnoresQuaternionSign()
        {
            var random = new Random(3);
            var pose = RandomPose(ref random);
            var flipped = pose;
            flipped.Rotation = new quaternion(-pose.Rotation.value);
            var sum = PoseAlgebra.Add(PoseAlgebra.Add(default, pose, 0.3f), flipped, 0.7f);
            Assert.Less(TestRigs.Distance(PoseAlgebra.Mean(sum), pose), 1e-5f);
        }

        [Test]
        public void AffineMatchesFloat4x4()
        {
            var random = new Random(5);
            var a = RandomPose(ref random);
            var b = RandomPose(ref random);
            var expected = math.mul(float4x4.TRS(a.Position, a.Rotation, a.Scale), float4x4.TRS(b.Position, b.Rotation, b.Scale));
            var actual = PoseAlgebra.Mul(PoseAlgebra.Affine(a), PoseAlgebra.Affine(b));
            Assert.Less(math.cmax(math.abs(actual.c3 - expected.c3.xyz)), 1e-4f);
            Assert.Less(math.cmax(math.abs(actual.c1 - expected.c1.xyz)), 1e-4f);
        }

        [Test]
        public void ShapesAreTotalForEveryWrap()
        {
            Assert.AreEqual(0.25f, Sampling.Shape(1.25f, Wrap.Loop), 1e-6f);
            Assert.AreEqual(0.75f, Sampling.Shape(-0.25f, Wrap.Loop), 1e-6f);
            Assert.AreEqual(1f, Sampling.Shape(3f, Wrap.Clamp), 1e-6f);
            Assert.AreEqual(0f, Sampling.Shape(-3f, Wrap.Clamp), 1e-6f);
            Assert.AreEqual(0.5f, Sampling.Shape(1.5f, Wrap.PingPong), 1e-6f);
            Assert.AreEqual(1f, Sampling.Shape(1f, Wrap.PingPong), 1e-6f);
            Assert.AreEqual(0.5f, Sampling.Shape(-0.5f, Wrap.PingPong), 1e-6f);
        }

        [Test]
        public void SamplingHitsFramesAndInterpolates()
        {
            ref var clip = ref rig.Clips[0].Value.Value;
            Assert.AreEqual(2f, Sampling.At(ref clip, 2, 1, 1f).Position.x, 1e-5f);
            Assert.AreEqual(1f, Sampling.At(ref clip, 2, 1, 0.5f).Position.x, 1e-5f);
            Assert.AreEqual(4f, Sampling.At(ref clip, 2, 1, 99f).Position.x, 1e-5f);
            Assert.AreEqual(0f, Sampling.At(ref clip, 2, 1, -5f).Position.x, 1e-5f);
        }

        [Test]
        public void EmptyStatesGiveRestPose()
        {
            var states = States();
            Assert.Less(TestRigs.Distance(Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(states), 1), rig.Value.Bones[1].Rest), 1e-6f);
        }

        [Test]
        public void SingleClipIsItsSampleAndHalfWeightIsHalfway()
        {
            var states = States();
            Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default.WithWrap(Wrap.Clamp));
            var s = states[0];
            s.Phase = 0.5f;
            states[0] = s;
            Assert.AreEqual(2f, Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(states), 1).Position.x, 1e-5f);
            s.Weight = 0.5f;
            states[0] = s;
            Assert.AreEqual(1f, Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(states), 1).Position.x, 1e-5f);
        }

        [Test]
        public void OverrideLayerStacksOnTheLayerBelow()
        {
            var states = States();
            Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default);
            Playback.Play(ref states, TestRigs.Clip(rig, 1), PlayOptions.Default.WithLayer(1).WithWrap(Wrap.Clamp));
            var upper = states[1];
            upper.Phase = 1f;
            upper.Weight = 0.8f;
            states[1] = upper;
            Assert.AreEqual(0.8f * 8f, Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(states), 1).Position.x, 1e-4f);
        }

        [Test]
        public void MaskZeroLeavesTheLayerBelow()
        {
            var states = States();
            Playback.Play(ref states, TestRigs.Clip(rig, 1), PlayOptions.Default.WithLayer(1).WithMask(TestRigs.Mask(rig, 1)).WithWrap(Wrap.Clamp));
            var s = states[0];
            s.Phase = 1f;
            states[0] = s;
            Assert.Less(TestRigs.Distance(Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(states), 0), rig.Value.Bones[0].Rest), 1e-6f);
            Assert.AreEqual(8f, Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(states), 1).Position.x, 1e-4f);
        }

        [Test]
        public void AdditiveAppliesDeltaFromItsReferenceOnNonIdentityRest()
        {
            var states = States();
            Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default.WithLayer(1).WithMode(ClipMode.Additive).WithWrap(Wrap.Clamp));
            var s = states[0];
            s.Phase = 1f;
            states[0] = s;
            ref var clip = ref rig.Clips[0].Value.Value;
            var expected = PoseAlgebra.Accumulate(rig.Value.Bones[1].Rest, PoseAlgebra.Delta(clip.Samples[2 * 2 + 1], clip.Reference[1]), 1f);
            Assert.Less(TestRigs.Distance(Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(states), 1), expected), 1e-5f);
            var turned = math.mul(rig.Value.Bones[1].Rest.Rotation, quaternion.RotateY(0.6f));
            Assert.Less(1f - math.abs(math.dot(expected.Rotation.value, turned.value)), 1e-5f);
        }

        [Test]
        public void EvaluationIgnoresStateOrder()
        {
            var a = States();
            Playback.Play(ref a, TestRigs.Clip(rig, 0), PlayOptions.Default.WithLayer(2).WithMode(ClipMode.Additive));
            Playback.Blend(ref a, new[] { TestRigs.Clip(rig, 0), TestRigs.Clip(rig, 1) }, new[] { 0.3f, 0.7f }, 0f, PlayOptions.Default);
            Playback.Step(ref a, rig.Clips, 0.37f);
            var b = States();
            for (var i = a.Length - 1; i >= 0; i--)
                b.Add(a[i]);
            for (var bone = 0; bone < 2; bone++)
                Assert.Less(TestRigs.Distance(Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(a), bone), Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(b), bone)), 1e-5f);
        }

        [Test]
        public void CommandsAreIdempotent()
        {
            var once = States();
            var twice = States();
            var clips = new[] { TestRigs.Clip(rig, 0), TestRigs.Clip(rig, 1) };
            var weights = new[] { 0.25f, 0.75f };
            Playback.Play(ref once, clips[0], PlayOptions.Default);
            Playback.Play(ref twice, clips[0], PlayOptions.Default);
            Playback.Step(ref once, rig.Clips, 0.2f);
            Playback.Step(ref twice, rig.Clips, 0.2f);
            Playback.CrossFade(ref once, clips[1], 0.3f, PlayOptions.Default);
            Playback.CrossFade(ref twice, clips[1], 0.3f, PlayOptions.Default);
            Playback.CrossFade(ref twice, clips[1], 0.3f, PlayOptions.Default);
            Playback.Blend(ref once, clips, weights, 0.2f, PlayOptions.Default.WithLayer(1));
            Playback.Blend(ref twice, clips, weights, 0.2f, PlayOptions.Default.WithLayer(1));
            Playback.Blend(ref twice, clips, weights, 0.2f, PlayOptions.Default.WithLayer(1));
            Playback.Stop(ref once, 3, 0.1f);
            Playback.Stop(ref twice, 3, 0.1f);
            Assert.AreEqual(once.Length, twice.Length);
            for (var i = 0; i < once.Length; i++)
                Assert.AreEqual(once[i], twice[i]);
        }

        [Test]
        public void CrossFadeEveryFrameStillAdvancesAndConverges()
        {
            var states = States();
            Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default);
            for (var frame = 0; frame < 60; frame++)
            {
                Playback.CrossFade(ref states, TestRigs.Clip(rig, 1), 0.25f, PlayOptions.Default);
                Playback.Step(ref states, rig.Clips, 1f / 60f);
            }
            Assert.AreEqual(1, states.Length);
            Assert.AreEqual(1f, states[0].Fade, 1e-6f);
            Assert.Greater(states[0].Phase, 0.4f);
        }

        [Test]
        public void CrossFadeBackRevivesInsteadOfDuplicating()
        {
            var states = States();
            Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default);
            Playback.CrossFade(ref states, TestRigs.Clip(rig, 1), 1f, PlayOptions.Default);
            Playback.Step(ref states, rig.Clips, 0.3f);
            Playback.CrossFade(ref states, TestRigs.Clip(rig, 0), 1f, PlayOptions.Default);
            Assert.AreEqual(2, states.Length);
            Assert.IsTrue(Playback.IsPlaying(states, 0, TestRigs.Clip(rig, 0)));
            Assert.IsFalse(Playback.IsPlaying(states, 0, TestRigs.Clip(rig, 1)));
        }

        [Test]
        public void RestartCreatesAFreshState()
        {
            var states = States();
            Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default);
            Playback.Step(ref states, rig.Clips, 0.4f);
            Playback.CrossFade(ref states, TestRigs.Clip(rig, 0), 0.2f, PlayOptions.Default.WithRestart(true));
            Assert.AreEqual(2, states.Length);
            Assert.AreEqual(0f, states[1].Phase);
        }

        [Test]
        public void StopRemovesAfterTheFadeAndZeroSpeedPauses()
        {
            var states = States();
            Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default.WithSpeed(0f));
            Playback.Step(ref states, rig.Clips, 0.5f);
            Assert.AreEqual(0f, states[0].Phase);
            Playback.Stop(ref states, 0, 0.5f);
            Playback.Step(ref states, rig.Clips, 0.25f);
            Assert.AreEqual(1, states.Length);
            Playback.Step(ref states, rig.Clips, 0.25f);
            Assert.AreEqual(0, states.Length);
            Playback.Play(ref states, ClipId.None, PlayOptions.Default);
            Assert.AreEqual(0, states.Length);
        }

        [Test]
        public void SyncedStatesShareOnePhaseUnderRandomCommands()
        {
            var states = States();
            var random = new Random(42);
            var clips = new[] { TestRigs.Clip(rig, 0), TestRigs.Clip(rig, 1) };
            for (var step = 0; step < 1000; step++)
            {
                var w = random.NextFloat();
                if (random.NextInt(4) == 0)
                    Playback.CrossFade(ref states, clips[random.NextInt(2)], random.NextFloat(0.3f), PlayOptions.Default);
                else
                    Playback.Blend(ref states, clips, new[] { w, 1f - w }, random.NextFloat(0.3f), PlayOptions.Default);
                Playback.Step(ref states, rig.Clips, random.NextFloat(0.05f));
                var phase = float.NaN;
                for (var i = 0; i < states.Length; i++)
                {
                    if (!states[i].Synced)
                        continue;
                    if (float.IsNaN(phase))
                        phase = states[i].Phase;
                    Assert.AreEqual(phase, states[i].Phase);
                }
            }
        }

        [Test]
        public void SameSequenceIsBitIdentical()
        {
            DynamicBuffer<ClipState> Run()
            {
                var states = States();
                var random = new Random(9);
                for (var i = 0; i < 300; i++)
                {
                    Playback.CrossFade(ref states, TestRigs.Clip(rig, random.NextInt(2)), random.NextFloat(0.5f), PlayOptions.Default.WithLayer((byte)random.NextInt(2)));
                    Playback.Step(ref states, rig.Clips, random.NextFloat(0.1f));
                }
                return states;
            }
            var a = Run();
            var b = Run();
            Assert.AreEqual(a.Length, b.Length);
            for (var i = 0; i < a.Length; i++)
                Assert.AreEqual(a[i], b[i]);
        }

        [Test]
        public void BlendSpaceWeightsPartitionUnity()
        {
            Span<float> weights = stackalloc float[3];
            BlendSpace.Linear(new[] { 0f, 2f, 5f }, 3.5f, weights);
            Assert.AreEqual(0.5f, weights[1], 1e-6f);
            Assert.AreEqual(0.5f, weights[2], 1e-6f);
            BlendSpace.Linear(new[] { 5f, 0f, 2f }, -1f, weights);
            Assert.AreEqual(1f, weights[1]);
            var points = new[] { new float2(0f, 0f), new float2(1f, 0f), new float2(0f, 1f), new float2(-1f, 0f) };
            Span<float> planar = stackalloc float[4];
            var random = new Random(1);
            for (var i = 0; i < 100; i++)
            {
                var value = random.NextFloat2(-1.5f, 1.5f);
                BlendSpace.Cartesian(points, value, planar);
                Assert.AreEqual(1f, planar[0] + planar[1] + planar[2] + planar[3], 1e-5f);
                BlendSpace.Directional(points, value, planar);
                Assert.AreEqual(1f, planar[0] + planar[1] + planar[2] + planar[3], 1e-5f);
                for (var k = 0; k < 4; k++)
                    Assert.GreaterOrEqual(planar[k], 0f);
            }
            BlendSpace.Cartesian(points, points[2], planar);
            Assert.AreEqual(1f, planar[2], 1e-6f);
        }

        [Test]
        public void NamesFindClipsAndFallBackTotally()
        {
            Assert.IsTrue(ClipId.None.Is(Names.Clip(rig.Clips, 12345u)));
            Assert.AreEqual(1, Names.Clip(rig.Clips, 11u).Index);
            Assert.IsTrue(ClipId.None.Is(Names.ClipAt(rig.Clips, 9)));
            Assert.AreEqual(Names.Hash("Run"), Names.Hash("Run"));
        }
    }
}
