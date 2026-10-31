using NUnit.Framework;
using SA.Motion.Inertialization;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace SA.Tests
{
    public class TransitionTests
    {
        const float Frame = 1f / 60f;

        static BonePose Pose(float3 position, quaternion rotation) => new BonePose { Position = position, Rotation = rotation, Scale = 1f };

        static string Show(BonePose p) => $"p=({p.Position.x:F3},{p.Position.y:F3},{p.Position.z:F3}) q=({p.Rotation.value.x:F3},{p.Rotation.value.y:F3},{p.Rotation.value.z:F3},{p.Rotation.value.w:F3}) s=({p.Scale.x:F3},{p.Scale.y:F3},{p.Scale.z:F3})";

        static BonePose Read(LocalTransform transform) => new BonePose { Position = transform.Position, Rotation = transform.Rotation, Scale = transform.Scale };

        [Test]
        public void RepeatingTheSameRequestKeepsRevisionAndPhase()
        {
            using var rig = TestRigs.Build();
            using var world = new World("SA.Transition.Keep");
            var entity = world.EntityManager.CreateEntity(typeof(ClipState), typeof(TransitionIntent));
            var states = world.EntityManager.GetBuffer<ClipState>(entity);
            var intent = new TransitionIntent();
            var clip = TestRigs.Clip(rig, 0);
            Assert.AreEqual(RequestDecision.Start, TransitionPlayback.Play(ref states, ref intent, 7u, clip, 0.3f, PlayOptions.Default));
            Assert.IsTrue(intent.Pending);
            Assert.AreEqual(1u, intent.Request.Revision);
            Assert.AreEqual(0.3f, intent.Seconds, 1e-6f);
            Playback.Step(ref states, rig.Clips, 0.4f);
            intent.Pending = false;
            Assert.AreEqual(RequestDecision.Keep, TransitionPlayback.Play(ref states, ref intent, 7u, clip, 0.3f, PlayOptions.Default));
            Assert.IsFalse(intent.Pending);
            Assert.AreEqual(1u, intent.Request.Revision);
            Assert.AreEqual(1, states.Length);
            Assert.AreEqual(0.4f, states[0].Phase, 1e-6f);
        }

        [Test]
        public void ANewKeyCutsToTheDestinationAndLeavesNoOutgoingState()
        {
            using var rig = TestRigs.Build();
            using var world = new World("SA.Transition.Cut");
            var entity = world.EntityManager.CreateEntity(typeof(ClipState), typeof(TransitionIntent));
            var states = world.EntityManager.GetBuffer<ClipState>(entity);
            var intent = new TransitionIntent();
            TransitionPlayback.Play(ref states, ref intent, 1u, TestRigs.Clip(rig, 0), 0.5f, PlayOptions.Default);
            Playback.Step(ref states, rig.Clips, 0.3f);
            intent.Pending = false;
            var next = TestRigs.Clip(rig, 1);
            Assert.AreEqual(RequestDecision.Start, TransitionPlayback.Play(ref states, ref intent, 2u, next, 0.5f, PlayOptions.Default));
            Assert.AreEqual(2u, intent.Request.Revision);
            Assert.IsTrue(intent.Pending);
            Assert.AreEqual(1, states.Length);
            Assert.IsTrue(states[0].Clip.Is(next));
            Assert.AreEqual(0f, states[0].Phase);
            Assert.AreEqual(1f, states[0].Fade);
            Assert.GreaterOrEqual(states[0].FadeRate, 0f);
        }

        [Test]
        public void ReusingAKeyForADifferentRecipeIsMisuseAndChangesNothing()
        {
            using var rig = TestRigs.Build();
            using var world = new World("SA.Transition.Misuse");
            var entity = world.EntityManager.CreateEntity(typeof(ClipState), typeof(TransitionIntent));
            var states = world.EntityManager.GetBuffer<ClipState>(entity);
            var intent = new TransitionIntent();
            TransitionPlayback.Play(ref states, ref intent, 5u, TestRigs.Clip(rig, 0), 0.5f, PlayOptions.Default);
            intent.Pending = false;
            var before = intent;
            Assert.AreEqual(RequestDecision.Misuse, TransitionPlayback.Play(ref states, ref intent, 5u, TestRigs.Clip(rig, 1), 0.5f, PlayOptions.Default));
            Assert.AreEqual(before.Request, intent.Request);
            Assert.IsFalse(intent.Pending);
            Assert.AreEqual(1, states.Length);
            Assert.IsTrue(states[0].Clip.Is(TestRigs.Clip(rig, 0)));
        }

        [Test]
        public void ChangingBlendWeightsInsideOneRecipeIsNotANewTransition()
        {
            using var rig = TestRigs.Build();
            using var world = new World("SA.Transition.Blend");
            var entity = world.EntityManager.CreateEntity(typeof(ClipState), typeof(TransitionIntent));
            var states = world.EntityManager.GetBuffer<ClipState>(entity);
            var intent = new TransitionIntent();
            var clips = new[] { TestRigs.Clip(rig, 0), TestRigs.Clip(rig, 1) };
            Assert.AreEqual(RequestDecision.Start, TransitionPlayback.Blend(ref states, ref intent, 9u, clips, new[] { 0.3f, 0.7f }, 0.4f, PlayOptions.Default));
            intent.Pending = false;
            Assert.AreEqual(RequestDecision.Keep, TransitionPlayback.Blend(ref states, ref intent, 9u, clips, new[] { 0.6f, 0.4f }, 0.4f, PlayOptions.Default));
            Assert.IsFalse(intent.Pending);
            Assert.AreEqual(1u, intent.Request.Revision);
            Assert.AreEqual(2, states.Length);
            Assert.AreEqual(0.6f, states[0].Weight, 1e-6f);
            Assert.AreEqual(0.4f, states[1].Weight, 1e-6f);
        }

        [Test]
        public void StepIsContinuousAtTheSwitchConvergesAndSurvivesAnInterruption()
        {
            var bone = new BoneTransition();
            var a = Pose(new float3(1f, 2f, 3f), quaternion.RotateY(0.5f));
            var b = Pose(new float3(-2f, 0f, 1f), quaternion.RotateX(1.2f));
            var c = Pose(new float3(0.5f, -1f, 4f), quaternion.RotateZ(-0.8f));
            var last = a;
            for (var i = 0; i < 3; i++)
                last = Inertial.Step(ref bone, a, false, 0f, 0f, Frame);
            var start = Inertial.Step(ref bone, b, true, 0.5f, 0f, Frame);
            Assert.Less(TestRigs.Distance(last, start), 1e-5f);
            var previous = TestRigs.Distance(start, b);
            Assert.Greater(previous, 1f);
            var elapsed = 0f;
            var output = start;
            while (elapsed < 0.2f)
            {
                elapsed += Frame;
                output = Inertial.Step(ref bone, b, false, 0.5f, elapsed, Frame);
                var distance = TestRigs.Distance(output, b);
                Assert.LessOrEqual(distance, previous + 1e-6f);
                previous = distance;
            }
            var interrupted = Inertial.Step(ref bone, c, true, 0.5f, 0f, Frame);
            Assert.Less(TestRigs.Distance(output, interrupted), 1e-5f);
            elapsed = 0f;
            while (elapsed < 0.6f)
            {
                elapsed += Frame;
                output = Inertial.Step(ref bone, c, false, 0.5f, elapsed, Frame);
            }
            Assert.Less(TestRigs.Distance(output, c), 1e-6f);
        }

        static (Entity Owner, Entity Child) CpuRig(EntityManager manager, TestRig rig, bool inertial)
        {
            var root = manager.CreateEntity(typeof(LocalTransform), typeof(PoseDriven));
            var child = manager.CreateEntity(typeof(LocalTransform), typeof(PoseDriven));
            var owner = manager.CreateEntity(typeof(Rig), typeof(TimeScale), typeof(ClipState), typeof(CpuEngine), typeof(BoneEntity));
            rig.Attach(manager, owner);
            manager.SetComponentData(owner, new Rig { Value = rig.Skeleton });
            manager.SetComponentData(owner, new TimeScale { Value = 1f });
            var bones = manager.GetBuffer<BoneEntity>(owner);
            bones.Add(new BoneEntity { Value = root });
            bones.Add(new BoneEntity { Value = child });
            if (inertial)
            {
                manager.AddComponent<TransitionIntent>(owner);
                manager.AddBuffer<BoneTransition>(owner).Resize(2, NativeArrayOptions.ClearMemory);
            }
            return (owner, child);
        }

        [Test]
        public void CpuSystemWritesBonePosesForRigsWithoutTransitions()
        {
            using var rig = TestRigs.Build();
            using var world = new World("SA.Transition.Control");
            var manager = world.EntityManager;
            var (owner, child) = CpuRig(manager, rig, false);
            var states = manager.GetBuffer<ClipState>(owner);
            Playback.Play(ref states, TestRigs.Clip(rig, 0), PlayOptions.Default);
            world.SetTime(new TimeData(Frame, Frame));
            world.GetOrCreateSystem<PlaybackSystem>().Update(world.Unmanaged);
            world.GetOrCreateSystem<CpuPoseSystem>().Update(world.Unmanaged);
            manager.CompleteAllTrackedJobs();
            var written = Read(manager.GetComponentData<LocalTransform>(child));
            var expected = Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(manager.GetBuffer<ClipState>(owner)), 1);
            Assert.Less(TestRigs.Distance(written, expected), 1e-4f, $"written {Show(written)} expected {Show(expected)}");
        }

        [Test]
        public void CpuSystemSwitchesClipsWithoutAPopAndLandsOnTheDestination()
        {
            using var rig = TestRigs.Build();
            using var world = new World("SA.Transition.System");
            var manager = world.EntityManager;
            var (owner, child) = CpuRig(manager, rig, true);
            var playback = world.GetOrCreateSystem<PlaybackSystem>();
            var pose = world.GetOrCreateSystem<CpuPoseSystem>();
            var time = 0.0;
            void Tick()
            {
                time += Frame;
                world.SetTime(new TimeData(time, Frame));
                playback.Update(world.Unmanaged);
                pose.Update(world.Unmanaged);
                manager.CompleteAllTrackedJobs();
            }

            void Request(uint key, int clip)
            {
                var states = manager.GetBuffer<ClipState>(owner);
                var intent = manager.GetComponentData<TransitionIntent>(owner);
                TransitionPlayback.Play(ref states, ref intent, key, TestRigs.Clip(rig, clip), 0.5f, PlayOptions.Default);
                manager.SetComponentData(owner, intent);
            }

            Request(1u, 0);
            for (var i = 0; i < 10; i++)
                Tick();
            var before = Read(manager.GetComponentData<LocalTransform>(child));

            Request(2u, 1);
            Tick();
            var switched = Read(manager.GetComponentData<LocalTransform>(child));
            var raw = Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(manager.GetBuffer<ClipState>(owner)), 1);
            Assert.Greater(TestRigs.Distance(before, raw), 0.1f, "without inertialization the switch would pop");
            Assert.Less(TestRigs.Distance(before, switched), 1e-4f, $"before {Show(before)} switched {Show(switched)} raw {Show(raw)}");

            for (var i = 0; i < 40; i++)
                Tick();
            var landed = Read(manager.GetComponentData<LocalTransform>(child));
            var expected = Evaluation.Bone(ref rig.Value, rig.Clips, rig.Masks, TestRigs.View(manager.GetBuffer<ClipState>(owner)), 1);
            Assert.Less(TestRigs.Distance(landed, expected), 1e-4f, $"landed {Show(landed)} expected {Show(expected)}");
            Assert.IsFalse(manager.GetComponentData<TransitionIntent>(owner).Pending);
        }
    }
}
