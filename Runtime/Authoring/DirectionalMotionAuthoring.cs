using System.Collections.Generic;
using UnityEngine;

namespace SA
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RigAuthoring))]
    public class DirectionalMotionAuthoring : MonoBehaviour, IMotionClips
    {
        [Tooltip("Start with this motion enabled. Gameplay or a timeline enables and disables it at runtime.")]
        public bool StartEnabled = true;
        [Tooltip("Played below Idle Speed. Leave empty to keep the last heading.")]
        public AnimationClip Idle;
        public AnimationClip Forward;
        public AnimationClip ForwardRight;
        public AnimationClip Right;
        public AnimationClip BackRight;
        public AnimationClip Back;
        public AnimationClip BackLeft;
        public AnimationClip Left;
        public AnimationClip ForwardLeft;
        [Tooltip("The speed the heading clips were authored at, in metres per second. Used by Match Rate.")]
        public float ReferenceSpeed = 1.4f;
        [Tooltip("Below this speed the idle clip plays.")]
        public float IdleSpeed = 0.2f;
        [Tooltip("Speed must rise this far above Idle Speed before the character leaves idle.")]
        public float SpeedHysteresis = 0.1f;
        [Tooltip("Radians the heading must move past a sector boundary before the clip switches.")]
        public float AngleHysteresis = 0.2f;
        public MotionPlayback Playback = MotionPlayback.Default;

        public AnimationClip[] Headings => new[] { Forward, ForwardRight, Right, BackRight, Back, BackLeft, Left, ForwardLeft };

        public void CollectClips(List<AnimationClip> clips)
        {
            if (Idle != null)
                clips.Add(Idle);
            foreach (var clip in Headings)
                if (clip != null)
                    clips.Add(clip);
        }
    }
}
