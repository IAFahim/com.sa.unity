using System;
using System.Collections.Generic;
using UnityEngine;

namespace SA
{
    [Serializable]
    public struct LinearMotionEntry
    {
        public AnimationClip Clip;
        [Tooltip("The motion value this clip stands for, for example a speed in metres per second.")]
        public float Threshold;
        [Tooltip("The speed the clip was authored at. Used by Match Rate. 0 plays at the base speed.")]
        public float ReferenceSpeed;
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(RigAuthoring))]
    public class LinearMotionAuthoring : MonoBehaviour, IMotionClips
    {
        [Tooltip("Start with this motion enabled. Gameplay or a timeline enables and disables it at runtime.")]
        public bool StartEnabled = true;
        [Tooltip("One clip per value. The clip nearest the current value plays, with no blending.")]
        public List<LinearMotionEntry> Clips = new List<LinearMotionEntry>();
        [Tooltip("The starting value. Gameplay or a timeline writes LinearMotion.Value at runtime.")]
        public float Value;
        [Tooltip("The value must move this much closer to another clip before the motion switches, so it never flickers at a boundary.")]
        public float Hysteresis = 0.25f;
        public MotionPlayback Playback = MotionPlayback.Default;

        public void CollectClips(List<AnimationClip> clips)
        {
            foreach (var entry in Clips)
                if (entry.Clip != null)
                    clips.Add(entry.Clip);
        }
    }
}
