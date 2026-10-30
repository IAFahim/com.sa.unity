using System;
using Unity.Mathematics;

namespace SA
{
    public static class Evaluation
    {
        public static bool Contributes(ref RigBlob rig, ReadOnlySpan<ClipAsset> clips, ReadOnlySpan<MaskAsset> masks, in ClipState state) =>
            state.Clip.Index < clips.Length && state.Mask.Index < masks.Length && state.Weight * state.Fade > 0f &&
            clips[state.Clip.Index].Value.IsCreated && masks[state.Mask.Index].Value.IsCreated &&
            clips[state.Clip.Index].Value.Value.RigId == rig.Id && masks[state.Mask.Index].Value.Value.RigId == rig.Id;

        public static float Weight(ref RigBlob rig, ReadOnlySpan<ClipAsset> clips, ReadOnlySpan<MaskAsset> masks, in ClipState state, int bone) =>
            Contributes(ref rig, clips, masks, state) ? state.Weight * state.Fade * masks[state.Mask.Index].Value.Value.Weights[bone] : 0f;

        public static BonePose Sample(ref RigBlob rig, ReadOnlySpan<ClipAsset> clips, in ClipState state, int bone)
        {
            ref var clip = ref clips[state.Clip.Index].Value.Value;
            return Sampling.At(ref clip, rig.Bones.Length, bone, Sampling.Frame(state.Phase, state.Wrap, clip.FrameCount));
        }

        public static bool NextLayer(ReadOnlySpan<ClipState> states, int after, out int layer)
        {
            layer = int.MaxValue;
            for (var i = 0; i < states.Length; i++)
                if (states[i].Layer > after && states[i].Layer < layer)
                    layer = states[i].Layer;
            return layer != int.MaxValue;
        }

        public static BonePose Bone(ref RigBlob rig, ReadOnlySpan<ClipAsset> clips, ReadOnlySpan<MaskAsset> masks, ReadOnlySpan<ClipState> states, int bone)
        {
            var pose = rig.Bones[bone].Rest;
            var layer = -1;
            while (NextLayer(states, layer, out layer))
            {
                var sum = default(PoseSum);
                for (var i = 0; i < states.Length; i++)
                {
                    var state = states[i];
                    if (state.Layer != layer || state.Mode != ClipMode.Override)
                        continue;
                    var weight = Weight(ref rig, clips, masks, state, bone);
                    if (weight > 0f)
                        sum = PoseAlgebra.Add(sum, Sample(ref rig, clips, state, bone), weight);
                }
                if (sum.Weight > 0f)
                    pose = PoseAlgebra.Lerp(pose, PoseAlgebra.Mean(sum), math.min(1f, sum.Weight));
                for (var i = 0; i < states.Length; i++)
                {
                    var state = states[i];
                    if (state.Layer != layer || state.Mode != ClipMode.Additive)
                        continue;
                    var weight = Weight(ref rig, clips, masks, state, bone);
                    if (weight > 0f)
                        pose = PoseAlgebra.Accumulate(pose, PoseAlgebra.Delta(Sample(ref rig, clips, state, bone), clips[state.Clip.Index].Value.Value.Reference[bone]), weight);
                }
            }
            return pose;
        }
    }
}
