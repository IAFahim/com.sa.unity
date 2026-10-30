using Unity.Mathematics;

namespace SA
{
    public static class Sampling
    {
        public static float Shape(float phase, Wrap wrap) => wrap switch
        {
            Wrap.Loop => math.frac(phase),
            Wrap.Clamp => math.saturate(phase),
            _ => 1f - math.abs(2f * math.frac(phase * 0.5f) - 1f)
        };

        public static float Wrapped(float phase, Wrap wrap) => wrap switch
        {
            Wrap.Loop => math.frac(phase),
            Wrap.Clamp => math.saturate(phase),
            _ => phase - 2f * math.floor(phase * 0.5f)
        };

        public static float Frame(float phase, Wrap wrap, int frameCount) => Shape(phase, wrap) * math.max(0, frameCount - 1);

        public static BonePose At(ref Clip clip, int boneCount, int bone, float frame)
        {
            var last = math.max(0, clip.FrameCount - 1);
            var first = math.clamp((int)math.floor(frame), 0, last);
            var next = math.min(first + 1, last);
            return PoseAlgebra.Lerp(clip.Samples[first * boneCount + bone], clip.Samples[next * boneCount + bone], math.saturate(frame - first));
        }
    }
}
