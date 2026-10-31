using System.Collections.Generic;
using UnityEngine;

namespace SA
{
    public enum Engine { Gpu, Cpu }

    [System.Serializable]
    public struct ClipSettings
    {
        public AnimationClip Clip;
        public string Name;
        public bool InPlace;
    }

    [DisallowMultipleComponent]
    public class RigAuthoring : MonoBehaviour
    {
        public List<AnimationClip> Clips = new List<AnimationClip>();
        public bool DiscoverClips = true;
        [Tooltip("Drop animation models, controllers, clips or folders here. Humanoid packs use this character's Avatar.")]
        public List<Object> AnimationLibraries = new List<Object>();
        public bool InPlace;
        public List<ClipSettings> ClipOptions = new List<ClipSettings>();
        public Transform MotionBone;
        public List<AvatarMask> Masks = new List<AvatarMask>();
        public List<Transform> ExtraBones = new List<Transform>();
        public float SampleRate = 30f;
        public Engine Engine = Engine.Gpu;
        public AnimationClip Autoplay;
        public float TimeScale = 1f;
        public bool Inertialize;
        public bool FootIK;

        public bool IsInPlace(AnimationClip clip)
        {
            foreach (var option in ClipOptions)
                if (option.Clip == clip)
                    return option.InPlace;
            return InPlace;
        }
    }
}
