using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using HeadingClip = SA.Animation.ClipIndex;
using Select = SA.Animation.MotionSelect;
using Rate = SA.Animation.PlayRate;
using Planar2 = SA.Motion.Numerics.Float2;

namespace SA
{
    public struct MotionRequest
    {
        public MotionKind Kind;
        public ClipId Clip;
        public int Selection;
        public bool Idle;
        public float Rate;
        public float Seconds;
        public Wrap Wrap;
        public bool Synced;
        public bool TimeDriven;
        public float Time;
    }

    public static class Motions
    {
        public const int MaxEntries = 64;

        public static uint Key(MotionKind kind, ClipId clip) => math.hash(new uint2((uint)kind, clip.Index)) | 1u;

        public static float Matched(in MotionPlayback playback, float speed, float referenceSpeed) =>
            playback.Speed * (playback.MatchRate ? Rate.Match(speed, referenceSpeed, playback.MinRate, playback.MaxRate) : 1f);

        public static MotionRequest Single(in SingleMotion motion) => new MotionRequest
        {
            Kind = MotionKind.Single,
            Clip = motion.Clip,
            Rate = motion.Playback.Speed,
            Seconds = motion.Playback.TransitionSeconds,
            Wrap = motion.Wrap,
            TimeDriven = motion.TimeDriven,
            Time = motion.Time
        };

        public static MotionRequest Linear(in LinearMotion motion, ReadOnlySpan<LinearMotionClip> clips, int current)
        {
            Span<float> thresholds = stackalloc float[math.min(clips.Length, MaxEntries)];
            for (var i = 0; i < thresholds.Length; i++)
                thresholds[i] = clips[i].Threshold;
            var selection = Select.Linear(thresholds, motion.Value, current, motion.Hysteresis);
            if (selection < 0)
                return default;
            return Looping(MotionKind.Linear, clips[selection].Clip, selection, in motion.Playback, Matched(motion.Playback, math.abs(motion.Value), clips[selection].ReferenceSpeed));
        }

        public static MotionRequest Planar(in PlanarMotion motion, ReadOnlySpan<PlanarMotionClip> clips, int current)
        {
            Span<Planar2> points = stackalloc Planar2[math.min(clips.Length, MaxEntries)];
            for (var i = 0; i < points.Length; i++)
                points[i] = new Planar2(clips[i].Position.x, clips[i].Position.y);
            var selection = Select.Planar(points, new Planar2(motion.Value.x, motion.Value.y), current, motion.Hysteresis);
            if (selection < 0)
                return default;
            return Looping(MotionKind.Planar, clips[selection].Clip, selection, in motion.Playback, Matched(motion.Playback, math.length(motion.Value), clips[selection].ReferenceSpeed));
        }

        public static MotionRequest Directional(in DirectionalMotion motion, ReadOnlySpan<DirectionalMotionClip> clips, int current, bool wasIdle)
        {
            var speed = math.length(motion.Velocity);
            if (!motion.Idle.IsNone && Select.Idle(speed, wasIdle, motion.IdleSpeed, motion.SpeedHysteresis))
            {
                var idle = Looping(MotionKind.Directional, motion.Idle, -1, in motion.Playback, motion.Playback.Speed);
                idle.Idle = true;
                return idle;
            }
            Span<HeadingClip> headings = stackalloc HeadingClip[Select.Headings];
            for (var i = 0; i < Select.Headings; i++)
                headings[i] = i < clips.Length && !clips[i].Clip.IsNone ? new HeadingClip(clips[i].Clip.Index) : HeadingClip.None;
            var selection = Select.Directional(headings, new Planar2(motion.Velocity.x, motion.Velocity.y), wasIdle ? -1 : current, motion.AngleHysteresis);
            if (selection < 0)
                return default;
            return Looping(MotionKind.Directional, clips[selection].Clip, selection, in motion.Playback, Matched(motion.Playback, speed, clips[selection].ReferenceSpeed));
        }

        static MotionRequest Looping(MotionKind kind, ClipId clip, int selection, in MotionPlayback playback, float rate) => new MotionRequest
        {
            Kind = kind,
            Clip = clip,
            Selection = selection,
            Rate = rate,
            Seconds = playback.TransitionSeconds,
            Wrap = Wrap.Loop,
            Synced = true
        };

        public static void Apply(ref DynamicBuffer<ClipState> states, ReadOnlySpan<ClipAsset> clips, ref TransitionIntent intent, bool inertialized, in MotionRequest request)
        {
            var options = PlayOptions.Default.WithWrap(request.Wrap).WithSpeed(request.TimeDriven ? 0f : request.Rate);
            var key = Key(request.Kind, request.Clip);
            if (!inertialized)
                Playback.CrossFade(ref states, request.Clip, request.Seconds, options);
            else if (request.Synced)
                TransitionPlayback.PlaySynced(ref states, ref intent, key, request.Clip, request.Seconds, options);
            else
                TransitionPlayback.Play(ref states, ref intent, key, request.Clip, request.Seconds, options);
            if (request.TimeDriven)
                Seek(ref states, clips, request.Clip, request.Time);
        }

        public static void Seek(ref DynamicBuffer<ClipState> states, ReadOnlySpan<ClipAsset> clips, ClipId clip, float seconds)
        {
            var index = Playback.Find(states, 0, clip);
            if (index < 0 || clip.Index >= clips.Length || !clips[clip.Index].Value.IsCreated)
                return;
            var duration = clips[clip.Index].Value.Value.Duration;
            var state = states[index];
            state.Phase = Sampling.Wrapped(duration > 0f ? Playback.Finite(seconds) / duration : 0f, state.Wrap);
            states[index] = state;
        }
    }

    [UpdateInGroup(typeof(AnimationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(PlaybackSystem))]
    public partial struct MotionSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state) =>
            state.Dependency = new DriveJob
            {
                Singles = SystemAPI.GetComponentLookup<SingleMotion>(true),
                Linears = SystemAPI.GetComponentLookup<LinearMotion>(true),
                LinearClips = SystemAPI.GetBufferLookup<LinearMotionClip>(true),
                Planars = SystemAPI.GetComponentLookup<PlanarMotion>(true),
                PlanarClips = SystemAPI.GetBufferLookup<PlanarMotionClip>(true),
                Directionals = SystemAPI.GetComponentLookup<DirectionalMotion>(true),
                DirectionalClips = SystemAPI.GetBufferLookup<DirectionalMotionClip>(true),
                Intents = SystemAPI.GetComponentLookup<TransitionIntent>()
            }.ScheduleParallel(state.Dependency);

        [BurstCompile]
        partial struct DriveJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<SingleMotion> Singles;
            [ReadOnly] public ComponentLookup<LinearMotion> Linears;
            [ReadOnly] public BufferLookup<LinearMotionClip> LinearClips;
            [ReadOnly] public ComponentLookup<PlanarMotion> Planars;
            [ReadOnly] public BufferLookup<PlanarMotionClip> PlanarClips;
            [ReadOnly] public ComponentLookup<DirectionalMotion> Directionals;
            [ReadOnly] public BufferLookup<DirectionalMotionClip> DirectionalClips;
            [NativeDisableParallelForRestriction] public ComponentLookup<TransitionIntent> Intents;

            void Execute(Entity entity, in DynamicBuffer<ClipAsset> clips, ref DynamicBuffer<ClipState> states, ref MotionDriver driver)
            {
                var kind = MotionKind.None;
                var priority = -1;
                if (Singles.HasComponent(entity) && Singles.IsComponentEnabled(entity))
                    Pick(MotionKind.Single, Singles[entity].Playback.Priority, ref kind, ref priority);
                if (Linears.HasComponent(entity) && Linears.IsComponentEnabled(entity) && LinearClips.HasBuffer(entity))
                    Pick(MotionKind.Linear, Linears[entity].Playback.Priority, ref kind, ref priority);
                if (Planars.HasComponent(entity) && Planars.IsComponentEnabled(entity) && PlanarClips.HasBuffer(entity))
                    Pick(MotionKind.Planar, Planars[entity].Playback.Priority, ref kind, ref priority);
                if (Directionals.HasComponent(entity) && Directionals.IsComponentEnabled(entity) && DirectionalClips.HasBuffer(entity))
                    Pick(MotionKind.Directional, Directionals[entity].Playback.Priority, ref kind, ref priority);
                var current = driver.Active == kind ? driver.Selection : -1;
                var request = kind switch
                {
                    MotionKind.Single => Motions.Single(Singles[entity]),
                    MotionKind.Linear => Motions.Linear(Linears[entity], LinearClips[entity].AsNativeArray().AsReadOnlySpan(), current),
                    MotionKind.Planar => Motions.Planar(Planars[entity], PlanarClips[entity].AsNativeArray().AsReadOnlySpan(), current),
                    MotionKind.Directional => Motions.Directional(Directionals[entity], DirectionalClips[entity].AsNativeArray().AsReadOnlySpan(), current, driver.Active == kind && driver.Idle),
                    _ => default
                };
                if (request.Kind == MotionKind.None || request.Clip.IsNone)
                {
                    driver = default;
                    return;
                }
                var inertialized = Intents.HasComponent(entity);
                var intent = inertialized ? Intents[entity] : default;
                Motions.Apply(ref states, clips.AsNativeArray().AsReadOnlySpan(), ref intent, inertialized, request);
                if (inertialized)
                    Intents[entity] = intent;
                driver = new MotionDriver { Active = request.Kind, Selection = request.Selection, Idle = request.Idle };
            }

            static void Pick(MotionKind candidate, byte candidatePriority, ref MotionKind kind, ref int priority)
            {
                if (candidatePriority < priority)
                    return;
                kind = candidate;
                priority = candidatePriority;
            }
        }
    }
}
