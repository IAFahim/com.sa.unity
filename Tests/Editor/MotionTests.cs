using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;

namespace SA.Tests
{
    public class MotionTests
    {
        TestRig rig;
        World world;
        EntityManager manager;

        [SetUp]
        public void SetUp()
        {
            rig = TestRigs.Build();
            world = new World("SA.Motion");
            manager = world.EntityManager;
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            rig.Dispose();
        }

        Entity Character(bool inertial)
        {
            var entity = manager.CreateEntity(typeof(Rig), typeof(TimeScale), typeof(ClipState), typeof(MotionDriver));
            manager.SetComponentData(entity, new Rig { Value = rig.Skeleton });
            manager.SetComponentData(entity, new TimeScale { Value = 1f });
            rig.Attach(manager, entity);
            if (inertial)
                manager.AddComponent<TransitionIntent>(entity);
            return entity;
        }

        void Tick()
        {
            world.GetOrCreateSystem<MotionSystem>().Update(world.Unmanaged);
            manager.CompleteAllTrackedJobs();
        }

        DynamicBuffer<ClipState> States(Entity entity) => manager.GetBuffer<ClipState>(entity);

        void Linear(Entity entity, float value)
        {
            if (!manager.HasComponent<LinearMotion>(entity))
            {
                manager.AddComponentData(entity, new LinearMotion { Hysteresis = 0.3f, Playback = MotionPlayback.Default });
                var clips = manager.AddBuffer<LinearMotionClip>(entity);
                clips.Add(new LinearMotionClip { Clip = TestRigs.Clip(rig, 0), Threshold = 0f });
                clips.Add(new LinearMotionClip { Clip = TestRigs.Clip(rig, 1), Threshold = 2f });
            }
            var motion = manager.GetComponentData<LinearMotion>(entity);
            motion.Value = value;
            manager.SetComponentData(entity, motion);
        }

        [Test]
        public void LinearMotionPlaysOneClipAndCutsWithInertializationWhenTheValueMoves()
        {
            var entity = Character(true);
            Linear(entity, 0.2f);
            Tick();
            Assert.AreEqual(1, States(entity).Length);
            Assert.IsTrue(States(entity)[0].Clip.Is(TestRigs.Clip(rig, 0)));
            Assert.IsTrue(manager.GetComponentData<TransitionIntent>(entity).Pending);
            var intent = manager.GetComponentData<TransitionIntent>(entity);
            intent.Pending = false;
            manager.SetComponentData(entity, intent);
            Linear(entity, 1.1f);
            Tick();
            Assert.IsTrue(States(entity)[0].Clip.Is(TestRigs.Clip(rig, 0)), "inside the hysteresis band the clip holds");
            Assert.IsFalse(manager.GetComponentData<TransitionIntent>(entity).Pending);
            Linear(entity, 1.9f);
            Tick();
            Assert.AreEqual(1, States(entity).Length, "no blending: the old clip is cut");
            Assert.IsTrue(States(entity)[0].Clip.Is(TestRigs.Clip(rig, 1)));
            Assert.IsTrue(manager.GetComponentData<TransitionIntent>(entity).Pending);
            Assert.AreEqual(MotionKind.Linear, manager.GetComponentData<MotionDriver>(entity).Active);
        }

        [Test]
        public void HigherPriorityWinsAndDisablingHandsControlBack()
        {
            var entity = Character(true);
            Linear(entity, 0f);
            var single = MotionPlayback.Default;
            single.Priority = 5;
            manager.AddComponentData(entity, new SingleMotion { Clip = TestRigs.Clip(rig, 1), Wrap = Wrap.Clamp, Playback = single });
            Tick();
            Assert.AreEqual(MotionKind.Single, manager.GetComponentData<MotionDriver>(entity).Active);
            Assert.IsTrue(States(entity)[0].Clip.Is(TestRigs.Clip(rig, 1)));
            Assert.AreEqual(Wrap.Clamp, States(entity)[0].Wrap);
            manager.SetComponentEnabled<SingleMotion>(entity, false);
            Tick();
            Assert.AreEqual(MotionKind.Linear, manager.GetComponentData<MotionDriver>(entity).Active);
            Assert.IsTrue(States(entity)[0].Clip.Is(TestRigs.Clip(rig, 0)));
            manager.SetComponentEnabled<LinearMotion>(entity, false);
            Tick();
            Assert.AreEqual(MotionKind.None, manager.GetComponentData<MotionDriver>(entity).Active);
            Assert.AreEqual(1, States(entity).Length, "with no motion enabled the last pose is left alone");
        }

        [Test]
        public void TimeDrivenMotionShowsTheExactTimeAndDoesNotAdvance()
        {
            var entity = Character(false);
            ref var clip = ref rig.Clips[1].Value.Value;
            manager.AddComponentData(entity, new SingleMotion { Clip = TestRigs.Clip(rig, 1), Wrap = Wrap.Clamp, TimeDriven = true, Time = clip.Duration * 0.25f, Playback = MotionPlayback.Default });
            Tick();
            Assert.AreEqual(0.25f, States(entity)[0].Phase, 1e-5f);
            Assert.AreEqual(0f, States(entity)[0].Speed);
            var motion = manager.GetComponentData<SingleMotion>(entity);
            motion.Time = clip.Duration * 0.75f;
            manager.SetComponentData(entity, motion);
            Tick();
            Assert.AreEqual(1, States(entity).Length);
            Assert.AreEqual(0.75f, States(entity)[0].Phase, 1e-5f);
        }

        [Test]
        public void DirectionalMotionPicksTheHeadingAndIdlesWithHysteresis()
        {
            var entity = Character(true);
            var playback = MotionPlayback.Default;
            playback.MatchRate = true;
            manager.AddComponentData(entity, new DirectionalMotion
            {
                Velocity = new float2(0f, 0.05f),
                Idle = TestRigs.Clip(rig, 0),
                IdleSpeed = 0.2f,
                SpeedHysteresis = 0.1f,
                AngleHysteresis = 0.2f,
                Playback = playback
            });
            var headings = manager.AddBuffer<DirectionalMotionClip>(entity);
            for (var i = 0; i < 8; i++)
                headings.Add(new DirectionalMotionClip { Clip = i == 2 ? TestRigs.Clip(rig, 1) : ClipId.None, ReferenceSpeed = 1f });
            Tick();
            Assert.IsTrue(manager.GetComponentData<MotionDriver>(entity).Idle);
            Assert.IsTrue(States(entity)[0].Clip.Is(TestRigs.Clip(rig, 0)));
            SetVelocity(entity, new float2(0.25f, 0f));
            Tick();
            Assert.IsTrue(manager.GetComponentData<MotionDriver>(entity).Idle, "still inside the idle hysteresis band");
            SetVelocity(entity, new float2(1.5f, 0f));
            Tick();
            var driver = manager.GetComponentData<MotionDriver>(entity);
            Assert.IsFalse(driver.Idle);
            Assert.AreEqual(2, driver.Selection);
            Assert.IsTrue(States(entity)[0].Clip.Is(TestRigs.Clip(rig, 1)));
            Assert.AreEqual(1.5f, States(entity)[0].Speed, 1e-5f, "speed matching scales the rate by 1.5 / 1");
        }

        void SetVelocity(Entity entity, float2 velocity)
        {
            var motion = manager.GetComponentData<DirectionalMotion>(entity);
            motion.Velocity = velocity;
            manager.SetComponentData(entity, motion);
        }
    }
}
