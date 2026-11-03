using Unity.Collections;
using Unity.Entities;
using SA.Animation;
using SA.Motion.Numerics;

namespace SA.Shape.Unity
{
    public struct ShapeSkinDeformation : IBufferElementData
    {
        public Affine Value;
    }

    public struct ShapeMorphWeight : IBufferElementData
    {
        public float Value;
    }

    public struct ShapeMorphBinding : IBufferElementData
    {
        public FixedString64Bytes Name;
    }

    public struct ShapeContactState : IComponentData
    {
        public Float3 SoleA;
        public Float3 SoleB;
        public float HipHeight;
        public float GroundY;
        public ulong Revision;
    }
}
