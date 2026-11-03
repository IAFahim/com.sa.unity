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
    }
}
