using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace SA
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    public partial class AnimationSystemGroup : ComponentSystemGroup { }

    [UpdateInGroup(typeof(AnimationSystemGroup), OrderFirst = true)]
    public partial struct PlaybackSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state) =>
            state.Dependency = new StepJob { DeltaTime = SystemAPI.Time.DeltaTime }.ScheduleParallel(state.Dependency);

        [BurstCompile]
        partial struct StepJob : IJobEntity
        {
            public float DeltaTime;

            void Execute(in Rig rig, in TimeScale scale, in DynamicBuffer<ClipAsset> clips, ref DynamicBuffer<ClipState> states)
            {
                if (rig.Value.IsCreated)
                    Playback.Step(ref states, clips.AsNativeArray().AsReadOnlySpan(), DeltaTime * scale.Value);
            }
        }
    }

    [UpdateInGroup(typeof(AnimationSystemGroup))]
    [UpdateAfter(typeof(PlaybackSystem))]
    public partial struct CpuPoseSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state) =>
            state.Dependency = new PoseJob
            {
                Transforms = SystemAPI.GetComponentLookup<LocalTransform>(),
                PostTransforms = SystemAPI.GetComponentLookup<PostTransformMatrix>(),
                Driven = SystemAPI.GetComponentLookup<PoseDriven>(true)
            }.ScheduleParallel(state.Dependency);

        [BurstCompile]
        [WithAll(typeof(CpuEngine))]
        partial struct PoseJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public ComponentLookup<LocalTransform> Transforms;
            [NativeDisableParallelForRestriction] public ComponentLookup<PostTransformMatrix> PostTransforms;
            [ReadOnly] public ComponentLookup<PoseDriven> Driven;

            void Execute(in Rig rig, in DynamicBuffer<ClipAsset> clips, in DynamicBuffer<MaskAsset> masks, in DynamicBuffer<ClipState> states, in DynamicBuffer<BoneEntity> bones)
            {
                if (!rig.Value.IsCreated)
                    return;
                ref var blob = ref rig.Value.Value;
                var view = states.AsNativeArray().AsReadOnlySpan();
                var count = math.min(bones.Length, blob.Bones.Length);
                for (var b = 0; b < count; b++)
                {
                    var entity = bones[b].Value;
                    if (!Driven.HasComponent(entity) || !Driven.IsComponentEnabled(entity) || !Transforms.HasComponent(entity))
                        continue;
                    var pose = Evaluation.Bone(ref blob, clips.AsNativeArray().AsReadOnlySpan(), masks.AsNativeArray().AsReadOnlySpan(), view, b);
                    var uniform = pose.Scale.x;
                    Transforms[entity] = LocalTransform.FromPositionRotationScale(pose.Position, pose.Rotation, uniform);
                    if (PostTransforms.HasComponent(entity))
                        PostTransforms[entity] = new PostTransformMatrix { Value = float4x4.Scale(uniform != 0f ? pose.Scale / uniform : 1f) };
                }
            }
        }
    }

    [UpdateInGroup(typeof(AnimationSystemGroup))]
    [UpdateAfter(typeof(CpuPoseSystem))]
    public partial class PoseConstraintSystemGroup : ComponentSystemGroup { }
}
