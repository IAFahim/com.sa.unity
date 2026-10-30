using SA;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct CrowdSpawnSystem : ISystem
{
    public void OnCreate(ref SystemState state) => state.RequireForUpdate<Crowd>();

    public void OnUpdate(ref SystemState state)
    {
        var crowdEntity = SystemAPI.GetSingletonEntity<Crowd>();
        var crowd = SystemAPI.GetSingleton<Crowd>();
        if (crowd.Count <= 0)
            return;
        var manager = state.EntityManager;
        var instances = manager.Instantiate(crowd.Prefab, crowd.Count, Allocator.Temp);
        manager.AddComponent<CrowdMember>(instances);
        var side = (int)math.ceil(math.sqrt(crowd.Count));
        for (var i = 0; i < instances.Length; i++)
        {
            var position = new float3((i % side - side * 0.5f) * crowd.Spacing, 0f, (i / side) * crowd.Spacing);
            manager.SetComponentData(instances[i], LocalTransform.FromPosition(position));
            manager.SetComponentData(instances[i], new TimeScale { Value = 0.85f + 0.3f * math.frac(i * 0.618034f) });
        }
        crowd.Count = 0;
        manager.SetComponentData(crowdEntity, crowd);
    }
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AnimationSystemGroup))]
public partial struct CrowdWaveSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state) =>
        state.Dependency = new WaveJob { Time = (float)SystemAPI.Time.ElapsedTime }.ScheduleParallel(state.Dependency);

    [BurstCompile]
    [WithAll(typeof(CrowdMember))]
    partial struct WaveJob : IJobEntity
    {
        public float Time;

        void Execute(in DynamicBuffer<ClipAsset> clips, in DynamicBuffer<MaskAsset> masks, ref DynamicBuffer<ClipState> states, in LocalTransform transform)
        {
            var cycle = Time / ShowcaseScript.CycleSeconds - (transform.Position.x + transform.Position.z) * 0.02f;
            ShowcaseScript.Apply(clips, masks, ref states, math.frac(cycle) * ShowcaseScript.CycleSeconds);
        }
    }
}
