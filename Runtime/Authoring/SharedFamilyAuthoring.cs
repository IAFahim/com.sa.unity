using System.Collections.Generic;
using UnityEngine;

namespace SA
{
    [DisallowMultipleComponent]
    public class SharedFamilyAuthoring : MonoBehaviour
    {
        public RigAuthoring Rig;
        public List<AnimationClip> Clips = new List<AnimationClip>();
        public AnimationClip Autoplay;
        public float TransitionSeconds = 0.2f;
        [Tooltip("Avatar: limb twist spreads over the arm and leg bones as the avatar's twist settings say, like Unity's Animator. PerBone: each twist muscle turns its own bone only, as if every avatar twist setting were 1, which is how engines that ignore those settings play Humanoid clips.")]
        public HumanoidTwist Twist = HumanoidTwist.Avatar;
    }
}
