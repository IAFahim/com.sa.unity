using System.Collections.Generic;
using UnityEngine;

namespace SA
{
    public enum Engine { Gpu, Cpu }

    [DisallowMultipleComponent]
    public class RigAuthoring : MonoBehaviour
    {
        public List<AnimationClip> Clips = new List<AnimationClip>();
        public List<AvatarMask> Masks = new List<AvatarMask>();
        public List<Transform> ExtraBones = new List<Transform>();
        public float SampleRate = 30f;
        public Engine Engine = Engine.Gpu;
        public AnimationClip Autoplay;
        public float TimeScale = 1f;
        public bool Inertialize;
    }
}
