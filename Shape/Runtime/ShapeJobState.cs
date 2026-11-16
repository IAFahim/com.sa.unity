using Unity.Entities;
using SA.Animation;
using SA.Motion.Inertialization;
using SA.Motion.Numerics;
using SA.Shape;

namespace SA.Shape.Unity
{
    internal struct ShapePoseState : IComponentData
    {
        public ShapeFrameState Frame;
        public ShapeGeometry Geometry;
        public AssetId BuiltDefinition;
        public AssetId BuiltTarget;
        public long Table;
        public int BoneCount;
        public int ChannelCount;
        public int Active;
        public uint Stamp;
        public FrameAction Action;
        public AnimationError Smooth;
        public byte HasGeometry;
        public byte Native;
    }

    [InternalBufferCapacity(0)]
    internal struct ShapeBoneFrame : IBufferElementData
    {
        public BoneFrameState Value;
    }

    [InternalBufferCapacity(0)]
    internal struct ShapeNeutralPose : IBufferElementData
    {
        public Pose Value;
    }

    [InternalBufferCapacity(0)]
    internal struct ShapeDisplayedPose : IBufferElementData
    {
        public Pose Value;
    }

    [InternalBufferCapacity(0)]
    internal struct ShapeBoneOffset : IBufferElementData
    {
        public Float3 Value;
    }

    [InternalBufferCapacity(0)]
    internal struct ShapeScalar : IBufferElementData
    {
        public float Value;
    }

    [InternalBufferCapacity(8)]
    internal struct ShapePreviousContribution : IBufferElementData
    {
        public ClipContribution Value;
    }

    [InternalBufferCapacity(8)]
    internal struct ShapePreviousLayer : IBufferElementData
    {
        public LayerInput Value;
    }
}
