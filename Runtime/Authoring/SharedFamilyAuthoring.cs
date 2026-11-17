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
        [Tooltip("Entities: every bone is an entity, so IK, ragdolls, attachments and your own systems can read and move bones. PoseOnly: the pose stays on the character and only its skins read it. Use PoseOnly for crowds: far fewer entities and less work every frame, but nothing can be attached to a bone and nothing else can move one.")]
        public SharedBones Bones = SharedBones.Entities;
    }
}
