using System;
using SA;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

public static class ShowcaseScript
{
    public const float CycleSeconds = 24f;

    public static void Apply(in DynamicBuffer<ClipAsset> clips, in DynamicBuffer<MaskAsset> masks, ref DynamicBuffer<ClipState> states, float seconds)
    {
        var speed = 3f - 3f * math.cos(seconds / CycleSeconds * math.PI * 2f);
        var locomotion = new FixedList64Bytes<ClipId>
        {
            Names.Clip(clips, "Idle"),
            Names.Clip(clips, "Kyle_Walk"),
            Names.Clip(clips, "Kyle_Run"),
            Names.Clip(clips, "Kyle_Sprint")
        };
        Span<float> thresholds = stackalloc float[] { 0f, 2f, 4f, 6f };
        Span<float> weights = stackalloc float[4];
        BlendSpace.Linear(thresholds, speed, weights);
        Playback.Blend(ref states, locomotion.AsReadOnlySpan(), weights, 0.2f, PlayOptions.Default);

        if (seconds >= 8f && seconds < 14f)
            Playback.CrossFade(ref states, Names.Clip(clips, "Kyle_Clap"), 0.3f,
                PlayOptions.Default.WithLayer(1).WithMode(ClipMode.Additive).WithMask(Names.Mask(masks, "UpperBody")));
        else
            Playback.Stop(ref states, 1, 0.3f);
    }
}
