using System;
using Unity.Entities;
using Unity.Mathematics;

namespace SA
{
    public static class Playback
    {
        public static float Finite(float value) => math.isfinite(value) ? value : 0f;

        public static void Play(ref DynamicBuffer<ClipState> states, ClipId clip, in PlayOptions options) =>
            CrossFade(ref states, clip, 0f, options);

        public static void CrossFade(ref DynamicBuffer<ClipState> states, ClipId clip, float seconds, in PlayOptions options)
        {
            Span<ClipId> clips = stackalloc ClipId[1];
            Span<float> weights = stackalloc float[1];
            clips[0] = clip;
            weights[0] = 1f;
            Converge(ref states, options.Layer, clips, weights, seconds, false, options);
        }

        public static void Blend(ref DynamicBuffer<ClipState> states, ReadOnlySpan<ClipId> clips, ReadOnlySpan<float> weights, float seconds, in PlayOptions options) =>
            Converge(ref states, options.Layer, clips, weights, seconds, true, options);

        public static void Stop(ref DynamicBuffer<ClipState> states, byte layer, float seconds) =>
            Converge(ref states, layer, default, default, seconds, false, PlayOptions.Default.WithLayer(layer));

        public static int Find(in DynamicBuffer<ClipState> states, byte layer, ClipId clip)
        {
            var ending = -1;
            for (var i = 0; i < states.Length; i++)
            {
                var state = states[i];
                if (state.Layer != layer || !state.Clip.Is(clip))
                    continue;
                if (state.FadeRate >= 0f)
                    return i;
                ending = ending < 0 ? i : ending;
            }
            return ending;
        }

        public static bool IsPlaying(in DynamicBuffer<ClipState> states, byte layer, ClipId clip)
        {
            var index = Find(states, layer, clip);
            return index >= 0 && states[index].FadeRate >= 0f;
        }

        public static float NormalizedTime(in DynamicBuffer<ClipState> states, byte layer, ClipId clip)
        {
            var index = Find(states, layer, clip);
            return index < 0 ? 0f : Sampling.Shape(states[index].Phase, states[index].Wrap);
        }

        public static bool HasFinished(in DynamicBuffer<ClipState> states, byte layer, ClipId clip)
        {
            var index = Find(states, layer, clip);
            if (index < 0)
                return true;
            var state = states[index];
            return state.Wrap == Wrap.Clamp && (state.Speed >= 0f ? state.Phase >= 1f : state.Phase <= 0f);
        }

        public static float Rate(ReadOnlySpan<ClipAsset> clips, in ClipState state)
        {
            if (state.Clip.Index >= clips.Length || !clips[state.Clip.Index].Value.IsCreated)
                return 0f;
            var duration = clips[state.Clip.Index].Value.Value.Duration;
            return duration > 0f ? Finite(state.Speed) / duration : 0f;
        }

        public static float SyncedRate(ReadOnlySpan<ClipAsset> clips, ReadOnlySpan<ClipState> states, byte layer)
        {
            float weighted = 0f, weights = 0f, plain = 0f;
            var count = 0;
            for (var i = 0; i < states.Length; i++)
            {
                var state = states[i];
                if (!state.Synced || state.Layer != layer)
                    continue;
                var rate = Rate(clips, state);
                var weight = math.max(0f, state.Weight * state.Fade);
                weighted += weight * rate;
                weights += weight;
                plain += rate;
                count++;
            }
            return weights > 0f ? weighted / weights : count > 0 ? plain / count : 0f;
        }

        public static void Step(ref DynamicBuffer<ClipState> states, ReadOnlySpan<ClipAsset> clips, float deltaTime)
        {
            var dt = Finite(deltaTime);
            var view = states.AsNativeArray().AsReadOnlySpan();
            Span<float> rates = stackalloc float[states.Length];
            for (var i = 0; i < view.Length; i++)
                rates[i] = view[i].Synced ? SyncedRate(clips, view, view[i].Layer) : Rate(clips, view[i]);
            for (var i = states.Length - 1; i >= 0; i--)
            {
                var state = states[i];
                state.Phase = Sampling.Wrapped(state.Phase + rates[i] * dt, state.Wrap);
                state.Fade = math.saturate(state.Fade + state.FadeRate * dt);
                if (state.FadeRate < 0f && state.Fade <= 0f)
                    states.RemoveAt(i);
                else
                    states[i] = state;
            }
        }

        static void Converge(ref DynamicBuffer<ClipState> states, byte layer, ReadOnlySpan<ClipId> clips, ReadOnlySpan<float> weights, float seconds, bool synced, in PlayOptions options)
        {
            seconds = math.max(0f, Finite(seconds));
            var count = math.min(clips.Length, weights.Length);
            var existing = states.Length;
            Span<bool> keep = stackalloc bool[existing + count];
            var groupPhase = 0f;
            for (var i = 0; i < existing; i++)
                if (states[i].Layer == layer && states[i].Synced)
                    groupPhase = states[i].Phase;
            for (var k = 0; k < count; k++)
            {
                if (clips[k].IsNone)
                    continue;
                var weight = math.max(0f, Finite(weights[k]));
                var pick = Pick(states, layer, clips[k], synced, options, existing, keep);
                if (pick >= 0 && keep[pick])
                {
                    var merged = states[pick];
                    merged.Weight += weight;
                    states[pick] = merged;
                    continue;
                }
                if (pick < 0)
                {
                    states.Add(new ClipState
                    {
                        Clip = clips[k], Layer = layer, Mode = options.Mode, Mask = options.Mask, Synced = synced,
                        Wrap = synced ? Wrap.Loop : options.Wrap, Phase = synced ? groupPhase : 0f, Fade = 0f
                    });
                    pick = states.Length - 1;
                }
                var state = states[pick];
                state.Weight = weight;
                state.Speed = Finite(options.Speed);
                state.Wrap = synced ? Wrap.Loop : options.Wrap;
                state.FadeRate = seconds > 0f ? 1f / seconds : 0f;
                state.Fade = seconds > 0f ? state.Fade : 1f;
                states[pick] = state;
                keep[pick] = true;
            }
            for (var i = states.Length - 1; i >= 0; i--)
            {
                if (states[i].Layer != layer || keep[i])
                    continue;
                if (seconds <= 0f)
                {
                    states.RemoveAt(i);
                    continue;
                }
                var state = states[i];
                state.FadeRate = -1f / seconds;
                states[i] = state;
            }
        }

        static int Pick(in DynamicBuffer<ClipState> states, byte layer, ClipId clip, bool synced, in PlayOptions options, int existing, Span<bool> keep)
        {
            var ending = -1;
            for (var i = 0; i < states.Length; i++)
            {
                var state = states[i];
                var match = state.Layer == layer && state.Clip.Is(clip) && state.Mode == options.Mode && state.Mask.Is(options.Mask) && state.Synced == synced;
                if (!match || (options.Restart && i < existing && !keep[i]))
                    continue;
                if (keep[i] || state.FadeRate >= 0f)
                    return i;
                ending = ending < 0 ? i : ending;
            }
            return ending;
        }
    }
}
