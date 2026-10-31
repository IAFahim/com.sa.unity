using Unity.Entities;

namespace SA
{
    public struct CpuEngine : IComponentData, IEnableableComponent { }
    public struct ExternalPoseControl : IComponentData, IEnableableComponent { }

    [InternalBufferCapacity(0)]
    public struct BoneEntity : IBufferElementData
    {
        public Entity Value;
    }

    public struct PoseDriven : IComponentData, IEnableableComponent { }
}
