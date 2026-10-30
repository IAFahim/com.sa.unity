using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;

namespace SA
{
    public struct SkinBlob
    {
        public Hash128 Id;
        public BlobArray<int> Bones;
        public BlobArray<float3x4> Bindposes;
        public float3x4 MeshFromRig;
        public BlobArray<uint4> Influences;
    }

    public struct Skin : IComponentData
    {
        public BlobAssetReference<SkinBlob> Value;
        public Entity Rig;
    }

    [MaterialProperty("_SaSkin")]
    public struct SkinBinding : IComponentData
    {
        public uint MatrixBase;
        public uint InfluenceBase;
    }

    internal struct PoseSlot : IComponentData
    {
        public int Base;
    }
}
