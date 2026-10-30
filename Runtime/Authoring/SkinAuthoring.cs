using System;
using UnityEngine;

namespace SA
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SkinAuthoring : MonoBehaviour
    {
        public RigAuthoring Rig;
        public Transform[] Bones = Array.Empty<Transform>();
        public Bounds LocalBounds;
    }
}
