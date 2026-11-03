using Unity.Entities;
using SA.Animation;

namespace SA
{
    public struct SharedTimeline : IComponentData { }

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
}
