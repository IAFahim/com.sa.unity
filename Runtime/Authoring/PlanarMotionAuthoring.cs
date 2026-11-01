using System;
using System.Collections.Generic;
using UnityEngine;

namespace SA
{
    [Serializable]
    public struct PlanarMotionEntry
    {
        public AnimationClip Clip;
        [Tooltip("The 2D motion value this clip stands for, for example a local velocity.")]
        public Vector2 Position;
        [Tooltip("The speed the clip was authored at. Used by Match Rate. 0 plays at the base speed.")]
        public float ReferenceSpeed;
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(RigAuthoring))]
    public class PlanarMotionAuthoring : MonoBehaviour, IMotionClips
    {
        [Tooltip("Start with this motion enabled. Gameplay or a timeline enables and disables it at runtime.")]
        public bool StartEnabled = true;
        [Tooltip("One clip per point. The clip nearest the current value plays, with no blending.")]
        public List<PlanarMotionEntry> Clips = new List<PlanarMotionEntry>();
        [Tooltip("The starting value. Gameplay or a timeline writes PlanarMotion.Value at runtime.")]
        public Vector2 Value;
        [Tooltip("The value must move this much closer to another clip before the motion switches.")]
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
