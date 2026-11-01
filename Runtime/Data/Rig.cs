using Unity.Entities;

namespace SA
{
    public struct Bone
    {
        public int Parent;
        public BonePose Rest;
        public uint NameHash;
    }

    public struct Clip
    {
        public Hash128 Id;
        public Hash128 RigId;
        public uint NameHash;
        public float Duration;
        public int FrameCount;
        public BlobArray<BonePose> Samples;
        public BlobArray<BonePose> Reference;
    }

    public struct BoneMask
    {
        public Hash128 Id;
        public Hash128 RigId;
        public uint NameHash;
        public BlobArray<float> Weights;
    }

    public struct RigBlob
    {
        public Hash128 Id;
        public BlobArray<Bone> Bones;
    }

    [InternalBufferCapacity(0)]
    public struct ClipAsset : IBufferElementData
    {
        public BlobAssetReference<Clip> Value;
        public uint NameHash;
        public float FootIK;
    }

    [InternalBufferCapacity(0)]
    public struct MaskAsset : IBufferElementData
    {
        public BlobAssetReference<BoneMask> Value;
    }

    public struct Rig : IComponentData
    {
        public BlobAssetReference<RigBlob> Value;
    }

    public struct TimeScale : IComponentData
    {
        public float Value;
    }
}
