using SA.Motion.Inertialization;
using Unity.Entities;

namespace SA
{
    public struct TransitionIntent : IComponentData
    {
        public RequestState Request;
        public float Seconds;
        public float Elapsed;
        public bool Pending;
    }

    [InternalBufferCapacity(0)]
    public struct BoneTransition : IBufferElementData
    {
        public PoseHistory History;
        public PoseTransition Offset;
    }
}
