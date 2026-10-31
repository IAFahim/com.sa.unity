using System;
using SA.Motion.Inertialization;
using Unity.Entities;
using Unity.Mathematics;

namespace SA
{
    public static class TransitionPlayback
    {
        public static RequestDecision PlaySynced(ref DynamicBuffer<ClipState> states, ref TransitionIntent intent, uint key, ClipId clip, float seconds, in PlayOptions options)
        {
            var phase = 0f;
            var weight = -1f;
            foreach (var state in states)
                if (state.Layer == options.Layer && state.Wrap == Wrap.Loop && state.Weight * state.Fade > weight)
                {
                    phase = state.Phase;
                    weight = state.Weight * state.Fade;
                }
            var decision = Play(ref states, ref intent, key, clip, seconds, options);
            if (decision == RequestDecision.Start && !options.Restart && options.Wrap == Wrap.Loop)
            {
                var index = Playback.Find(states, options.Layer, clip);
                if (index >= 0)
                {
                    var state = states[index];
                    state.Phase = phase;
                    states[index] = state;
                }
            }
            return decision;
        }

        public static RequestDecision Play(ref DynamicBuffer<ClipState> states, ref TransitionIntent intent, uint key, ClipId clip, float seconds, in PlayOptions options)
        {
            Span<ClipId> clips = stackalloc ClipId[1];
            clips[0] = clip;
            var decision = Decide(ref intent, key, clips, seconds, options);
            if (decision != RequestDecision.Misuse)
                Playback.Play(ref states, clip, Started(decision, options));
            return decision;
        }

        public static RequestDecision Blend(ref DynamicBuffer<ClipState> states, ref TransitionIntent intent, uint key, ReadOnlySpan<ClipId> clips, ReadOnlySpan<float> weights, float seconds, in PlayOptions options)
        {
            var decision = Decide(ref intent, key, clips, seconds, options);
            if (decision != RequestDecision.Misuse)
                Playback.Blend(ref states, clips, weights, 0f, Started(decision, options));
            return decision;
        }

        static PlayOptions Started(RequestDecision decision, in PlayOptions options) =>
            decision == RequestDecision.Start ? options.WithRestart(true) : options;

        static RequestDecision Decide(ref TransitionIntent intent, uint key, ReadOnlySpan<ClipId> clips, float seconds, in PlayOptions options)
        {
            var decision = TransitionRequests.Decide(intent.Request, key, Inertial.Membership(clips, options), options.Restart, out var next);
            if (decision != RequestDecision.Start)
                return decision;
            intent.Request = next;
            intent.Seconds = math.max(0f, Playback.Finite(seconds));
            intent.Pending = true;
            return decision;
        }
    }
}
