using System.Collections.Generic;
using UnityEngine;

namespace SA
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RigAuthoring))]
    public class SingleMotionAuthoring : MonoBehaviour, IMotionClips
    {
        [Tooltip("The clip this motion plays. It is added to the character's animations automatically.")]
        public AnimationClip Clip;
        [Tooltip("Start with this motion enabled. Gameplay or a timeline enables and disables it at runtime.")]
        public bool StartEnabled = true;
        public Wrap Wrap = Wrap.Loop;
        [Tooltip("Show the clip at Time seconds instead of advancing it. Timelines set Time every frame.")]
        public bool TimeDriven;
        public MotionPlayback Playback = MotionPlayback.Default;

        public void CollectClips(List<AnimationClip> clips)
        {
            if (Clip != null)
                clips.Add(Clip);
        }
    }
}
