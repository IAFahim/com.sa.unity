using SA;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AnimationSystemGroup))]
public partial struct ShowcaseSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state) =>
        state.Dependency = new ScriptJob { Time = (float)SystemAPI.Time.ElapsedTime }.ScheduleParallel(state.Dependency);

    [BurstCompile]
    [WithNone(typeof(CrowdMember))]
    partial struct ScriptJob : IJobEntity
    {
        public float Time;

        void Execute(in DynamicBuffer<ClipAsset> clips, in DynamicBuffer<MaskAsset> masks, ref DynamicBuffer<ClipState> states) =>
            ShowcaseScript.Apply(clips, masks, ref states, Time % ShowcaseScript.CycleSeconds);
    }
}

[UpdateInGroup(typeof(PoseConstraintSystemGroup))]
public partial struct HeadLookSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var time = (float)SystemAPI.Time.ElapsedTime;
        foreach (var (rig, bones) in SystemAPI.Query<RefRO<Rig>, DynamicBuffer<BoneEntity>>().WithAll<CpuEngine>())
        {
            var head = Names.Bone(ref rig.ValueRO.Value.Value, "Head");
            if (head < 0 || head >= bones.Length)
                continue;
            var transform = SystemAPI.GetComponentRW<LocalTransform>(bones[head].Value);
            transform.ValueRW.Rotation = math.mul(transform.ValueRW.Rotation, quaternion.RotateZ(0.9f * math.sin(time * 1.5f)));
        }
    }
}
