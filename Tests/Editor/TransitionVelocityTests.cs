using System;
using NUnit.Framework;
using SA.Animation;
using SA.Motion.Inertialization;
using SA.Motion.Numerics;

namespace SA.Tests
{
    public class TransitionVelocityTests
    {
        const float Gap = 1f / 60f;
        const float MaxGap = 0.1f;
        const float Transition = 0.2f;

        static Pose At(float x, float radians)
        {
            return new Pose
            {
                Position = new Float3(x, 0f, 0f),
                Rotation = Quaternion.AxisAngle(new Float3(0f, 1f, 0f), radians),
                Scale = Float3.One
            };
        }

        [Test]
        public void MovingDestinationProducesNonzeroLinearVelocity()
        {
            var previous = new Pose[] { At(0f, 0f) };
            var current = new Pose[] { At(0.06f, 0f) };
            var incoming = new PoseVelocity[1];
            SharedFamilyControl.ResolveDestinationVelocity(previous.AsSpan(), current.AsSpan(), Gap, Transition, true, false, MaxGap, incoming.AsSpan());
            Assert.Greater(incoming[0].Linear.X, 0f);
            Assert.Less(Math.Abs(incoming[0].Linear.X - 3.6f), 0.02f);
            Assert.AreEqual(0f, incoming[0].Linear.Y);
            Assert.AreEqual(0f, incoming[0].Linear.Z);
            Assert.IsTrue(incoming[0].IsFinite);
        }

        [Test]
        public void MovingDestinationProducesNonzeroAngularVelocity()
        {
            var previous = new Pose[] { At(0f, 0f) };
            var current = new Pose[] { At(0f, 0.12f) };
            var incoming = new PoseVelocity[1];
            SharedFamilyControl.ResolveDestinationVelocity(previous.AsSpan(), current.AsSpan(), Gap, Transition, true, false, MaxGap, incoming.AsSpan());
            Assert.Greater(incoming[0].Angular.Length, 0f);
            Assert.Less(Math.Abs(incoming[0].Angular.Y - 7.2f), 0.05f);
            Assert.IsTrue(incoming[0].IsFinite);
        }

        [Test]
        public void FirstTickProducesZeroVelocity()
        {
            var previous = new Pose[] { At(0f, 0f) };
            var current = new Pose[] { At(0.06f, 0.1f) };
            var incoming = new PoseVelocity[1];
            incoming[0] = new PoseVelocity(new Float3(9f, 9f, 9f), new Float3(9f, 9f, 9f));
            SharedFamilyControl.ResolveDestinationVelocity(previous.AsSpan(), current.AsSpan(), Gap, Transition, false, false, MaxGap, incoming.AsSpan());
            Assert.AreEqual(0f, incoming[0].Linear.X);
            Assert.AreEqual(0f, incoming[0].Linear.Y);
            Assert.AreEqual(0f, incoming[0].Linear.Z);
            Assert.AreEqual(0f, incoming[0].Angular.X);
            Assert.AreEqual(0f, incoming[0].Angular.Y);
            Assert.AreEqual(0f, incoming[0].Angular.Z);
        }

        [Test]
        public void GapBeyondLimitProducesZeroVelocity()
        {
            var previous = new Pose[] { At(0f, 0f) };
            var current = new Pose[] { At(0.06f, 0f) };
            var incoming = new PoseVelocity[1];
            incoming[0] = new PoseVelocity(new Float3(9f, 9f, 9f), new Float3(9f, 9f, 9f));
            SharedFamilyControl.ResolveDestinationVelocity(previous.AsSpan(), current.AsSpan(), 0.25, Transition, true, false, MaxGap, incoming.AsSpan());
            Assert.AreEqual(0f, incoming[0].Linear.X);
            Assert.AreEqual(0f, incoming[0].Linear.Y);
            Assert.AreEqual(0f, incoming[0].Linear.Z);
            Assert.AreEqual(0f, incoming[0].Angular.X);
            Assert.AreEqual(0f, incoming[0].Angular.Y);
            Assert.AreEqual(0f, incoming[0].Angular.Z);
        }

        [Test]
        public void SeekProducesZeroVelocity()
        {
            var previous = new Pose[] { At(0f, 0f) };
            var current = new Pose[] { At(0.06f, 0f) };
            var incoming = new PoseVelocity[1];
            SharedFamilyControl.ResolveDestinationVelocity(previous.AsSpan(), current.AsSpan(), Gap, Transition, true, true, MaxGap, incoming.AsSpan());
            Assert.AreEqual(0f, incoming[0].Linear.X);
            Assert.IsTrue(incoming[0].IsFinite);
        }

        [Test]
        public void ZeroTransitionProducesZeroVelocity()
        {
            var previous = new Pose[] { At(0f, 0f) };
            var current = new Pose[] { At(0.06f, 0f) };
            var incoming = new PoseVelocity[1];
            SharedFamilyControl.ResolveDestinationVelocity(previous.AsSpan(), current.AsSpan(), Gap, 0f, true, false, MaxGap, incoming.AsSpan());
            Assert.AreEqual(0f, incoming[0].Linear.X);
            Assert.IsTrue(incoming[0].IsFinite);
        }

        [Test]
        public void FrozenDestinationMatchesZeroFill()
        {
            var previous = new Pose[] { At(0.5f, 0.2f) };
            var current = new Pose[] { At(0.5f, 0.2f) };
            var incoming = new PoseVelocity[1];
            SharedFamilyControl.ResolveDestinationVelocity(previous.AsSpan(), current.AsSpan(), Gap, Transition, true, false, MaxGap, incoming.AsSpan());
            Assert.AreEqual(0f, incoming[0].Linear.X);
            Assert.AreEqual(0f, incoming[0].Linear.Y);
            Assert.AreEqual(0f, incoming[0].Linear.Z);
            Assert.AreEqual(0f, incoming[0].Angular.X);
            Assert.AreEqual(0f, incoming[0].Angular.Y);
            Assert.AreEqual(0f, incoming[0].Angular.Z);
        }

        [Test]
        public void MovingDestinationVelocityIsAcceptedOnTransition()
        {
            var contributions = new ClipContribution[] { new ClipContribution(new ClipHandle(0, 1), 0.0, 1f) };
            var layers = new LayerInput[] { new LayerInput(0, SA.Animation.ClipMode.Override, 0, 1) };
            var first = new Pose[] { At(0f, 0f) };
            var second = new Pose[] { At(0.06f, 0f) };
            var state = default(PoseFrameState);
            var bones = new BoneFrameState[1];
            var boneScratch = new BoneFrameState[1];
            var previousContributions = new ClipContribution[8];
            var previousLayers = new LayerInput[4];
            var output = new Pose[1];
            var scratch = new Pose[1];
            var zero = new PoseVelocity[] { default };
            var start = new AnimationFrame(0, 1, 1, 0.0, 0.01f, Transition, FrameMode.Advance, MaxGap);
            Assert.AreEqual(AnimationError.None, TimelinePose.Evaluate(start, contributions, layers, true, first.AsSpan(), zero.AsSpan(), ref state, bones, boneScratch, previousContributions, previousLayers, output, scratch, out var startAction));
            Assert.AreEqual(FrameAction.Initialized, startAction);
            var computed = new PoseVelocity[1];
            SharedFamilyControl.ResolveDestinationVelocity(first.AsSpan(), second.AsSpan(), 0.01, Transition, true, false, MaxGap, computed.AsSpan());
            Assert.Greater(computed[0].Linear.X, 0f);
            var next = new AnimationFrame(1, 1, 2, 0.01, 0.01f, Transition, FrameMode.Advance, MaxGap);
            Assert.AreEqual(AnimationError.None, TimelinePose.Evaluate(next, contributions, layers, true, second.AsSpan(), computed.AsSpan(), ref state, bones, boneScratch, previousContributions, previousLayers, output, scratch, out var nextAction));
            Assert.AreEqual(FrameAction.Transitioned, nextAction);
        }

        [Test]
        public void FrozenDestinationTimelineMatchesZeroFill()
        {
            var contributions = new ClipContribution[] { new ClipContribution(new ClipHandle(0, 1), 0.0, 1f) };
            var layers = new LayerInput[] { new LayerInput(0, SA.Animation.ClipMode.Override, 0, 1) };
            var destination = new Pose[] { At(0.5f, 0.2f) };
            var zero = new PoseVelocity[] { default };
            var computed = new PoseVelocity[1];
            SharedFamilyControl.ResolveDestinationVelocity(destination.AsSpan(), destination.AsSpan(), Gap, Transition, true, false, MaxGap, computed.AsSpan());
            Assert.AreEqual(0f, computed[0].Linear.X);
            var stateZero = default(PoseFrameState);
            var bonesZero = new BoneFrameState[1];
            var scratchZero = new BoneFrameState[1];
            var prevZero = new ClipContribution[8];
            var prevLayersZero = new LayerInput[4];
            var outputZero = new Pose[1];
            var poseScratchZero = new Pose[1];
            var frame = new AnimationFrame(1, 1, 1, 0.01, 0.01f, Transition, FrameMode.Advance, MaxGap);
            Assert.AreEqual(AnimationError.None, TimelinePose.Evaluate(frame, contributions, layers, true, destination.AsSpan(), zero.AsSpan(), ref stateZero, bonesZero, scratchZero, prevZero, prevLayersZero, outputZero, poseScratchZero, out var actionZero));
            var stateComputed = default(PoseFrameState);
            var bonesComputed = new BoneFrameState[1];
            var scratchComputed = new BoneFrameState[1];
            var prevComputed = new ClipContribution[8];
            var prevLayersComputed = new LayerInput[4];
            var outputComputed = new Pose[1];
            var poseScratchComputed = new Pose[1];
            Assert.AreEqual(AnimationError.None, TimelinePose.Evaluate(frame, contributions, layers, true, destination.AsSpan(), computed.AsSpan(), ref stateComputed, bonesComputed, scratchComputed, prevComputed, prevLayersComputed, outputComputed, poseScratchComputed, out var actionComputed));
            Assert.AreEqual(actionZero, actionComputed);
            Assert.AreEqual(outputZero[0].Position.X, outputComputed[0].Position.X);
            Assert.AreEqual(outputZero[0].Position.Y, outputComputed[0].Position.Y);
            Assert.AreEqual(outputZero[0].Position.Z, outputComputed[0].Position.Z);
        }
    }
}
