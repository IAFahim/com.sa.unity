using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SA.Motion.Unity;

namespace SA
{
    public static class SharedFamilyPose
    {
        public static int BoneCount(EntityManager manager, Entity rig)
        {
            if (!manager.Exists(rig) || !manager.HasComponent<Rig>(rig))
                return 0;
            var reference = manager.GetComponentData<Rig>(rig).Value;
            return reference.IsCreated ? reference.Value.Bones.Length : 0;
        }

        public static bool TryCopyLocal(EntityManager manager, Entity rig, NativeArray<BonePose> local)
        {
            var count = BoneCount(manager, rig);
            if (count == 0 || local.Length < count)
                return false;
            if (!manager.HasComponent<SharedPoseState>(rig) || !manager.HasBuffer<SharedCommittedPose>(rig))
                return false;
            if (manager.GetComponentData<SharedPoseState>(rig).Frame.Initialized == 0)
                return false;
            var committed = manager.GetBuffer<SharedCommittedPose>(rig, true);
            if (committed.Length != count)
                return false;
            for (var b = 0; b < count; b++)
            {
                var pose = committed[b].Value;
                local[b] = new BonePose
                {
                    Position = Conversions.ToUnity(pose.Position),
                    Rotation = Conversions.ToUnity(pose.Rotation),
                    Scale = Conversions.ToUnity(pose.Scale)
                };
            }
            return true;
        }

        public static bool TryCopyModel(EntityManager manager, Entity rig, NativeArray<float4x4> rigFromBone)
        {
            var count = BoneCount(manager, rig);
            if (count == 0 || rigFromBone.Length < count)
                return false;
            var local = new NativeArray<BonePose>(count, Allocator.Temp);
            if (!TryCopyLocal(manager, rig, local))
                return false;
            var reference = manager.GetComponentData<Rig>(rig).Value;
            ref var blob = ref reference.Value;
            for (var b = 0; b < count; b++)
            {
                var parent = blob.Bones[b].Parent;
                var matrix = float4x4.TRS(local[b].Position, local[b].Rotation, local[b].Scale);
                if (parent >= b)
                    return false;
                rigFromBone[b] = parent >= 0 ? math.mul(rigFromBone[parent], matrix) : matrix;
            }
            return true;
        }
    }
}
