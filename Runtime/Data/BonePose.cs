using Unity.Mathematics;

namespace SA
{
    public struct BonePose
    {
        public float3 Position;
        public quaternion Rotation;
        public float3 Scale;

        public static BonePose Identity => new BonePose { Position = 0f, Rotation = quaternion.identity, Scale = 1f };
    }

    public struct PoseSum
    {
        public float3 Position;
        public float4 Rotation;
        public float3 Scale;
        public float Weight;
    }
}
