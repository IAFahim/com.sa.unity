using Unity.Entities;
using SA.Animation;
using SA.Motion.Numerics;

namespace SA
{
    public enum HumanoidTwist : byte
    {
        Avatar = 0,
        PerBone = 1
    }

    public struct SharedTimeline : IComponentData { }
    public struct ShapeDriven : IComponentData
    {
    }

    public struct SharedRig : IComponentData
    {
        public uint NameHash;
    }

    public struct SharedClock : IComponentData
    {
        public double Seconds;
        public ulong Tick;
        public ulong Continuity;
        public float Transition;
        public byte Seek;
        public byte Paused;
    }

    public struct SharedClipData
    {
        public AssetId Family;
        public AssetId Artifact;
        public double Duration;
        public int SampleCount;
        public int BoneCount;
        public byte Loop;
        public BlobArray<byte> Bytes;
    }

    [InternalBufferCapacity(4)]
    public struct SharedClipAsset : IBufferElementData
    {
        public BlobAssetReference<SharedClipData> Value;
    }

    [InternalBufferCapacity(4)]
    public struct SharedContribution : IBufferElementData
    {
        public int Clip;
        public float Weight;
        public byte Layer;
        public ushort Mask;
        public float Speed;
        public float Offset;
        public double Sample;
    }

    public struct SharedRetargetData
    {
        public AssetId Family;
        public AssetId Target;
        public BlobArray<byte> Table;
    }

    public struct SharedRetarget : IComponentData
    {
        public BlobAssetReference<SharedRetargetData> Value;
    }

    internal struct SharedClipLayout
    {
        public double Duration;
        public int SampleCount;
        public byte Loop;
        public BlobArray<TrackDescription> Tracks;
        public BlobArray<SegmentRange> Segments;
        public BlobArray<TrackBlock> Blocks;
        public BlobArray<byte> Bytes;
    }

    internal struct SharedTableLayout
    {
        public RetargetBinding Binding;
        public BlobArray<RetargetRow> Rows;
    }

    internal struct SharedTableRef : IComponentData
    {
        public BlobAssetReference<SharedTableLayout> Value;
        public long Source;
    }

    [InternalBufferCapacity(4)]
    internal struct SharedClipLayoutRef : IBufferElementData
    {
        public BlobAssetReference<SharedClipLayout> Value;
    }

    internal struct SharedPoseState : IComponentData
    {
        public PoseFrameState Frame;
        public long Table;
        public int BoneCount;
        public int Active;
        public uint Stamp;
        public FrameAction Action;
        public AnimationError Smooth;
        public byte Native;
    }

    [InternalBufferCapacity(0)]
    internal struct SharedBoneState : IBufferElementData
    {
        public BoneFrameState Value;
    }

    [InternalBufferCapacity(0)]
    internal struct SharedCommittedPose : IBufferElementData
    {
        public Pose Value;
    }

    [InternalBufferCapacity(8)]
    internal struct SharedPreviousContribution : IBufferElementData
    {
        public ClipContribution Value;
    }

    [InternalBufferCapacity(8)]
    internal struct SharedPreviousLayer : IBufferElementData
    {
        public LayerInput Value;
    }

    internal struct SharedFault
    {
        public Entity Rig;
        public int Kind;
        public int First;
        public int Second;
    }
}
