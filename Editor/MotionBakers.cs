using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SA.Editor
{
    abstract class MotionBaker<T> : Baker<T> where T : Component
    {
        protected List<AnimationClip> RigClips(RigAuthoring rig)
        {
            foreach (var source in rig.AnimationLibraries)
                if (source != null)
                    DependsOn(source);
            foreach (var motion in rig.GetComponents<IMotionClips>())
                DependsOn((Object)motion);
            return AnimationDiscovery.Clips(rig);
        }

        protected ClipId Resolve(List<AnimationClip> clips, AnimationClip clip, Component owner)
        {
            if (clip == null)
                return ClipId.None;
            DependsOn(clip);
            var index = clips.IndexOf(clip);
            if (index >= 0 && index < ushort.MaxValue)
                return new ClipId((ushort)index);
            Debug.LogError($"SA: {owner.name}: clip '{clip.name}' is not among the character's animations.", owner);
            return ClipId.None;
        }
    }

    class SingleMotionBaker : MotionBaker<SingleMotionAuthoring>
    {
        public override void Bake(SingleMotionAuthoring authoring)
        {
            var rig = GetComponent<RigAuthoring>();
            if (rig == null)
                return;
            var clips = RigClips(rig);
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new SingleMotion
            {
                Clip = Resolve(clips, authoring.Clip, authoring),
                Wrap = authoring.Wrap,
                TimeDriven = authoring.TimeDriven,
                Playback = authoring.Playback
            });
            SetComponentEnabled<SingleMotion>(entity, authoring.StartEnabled);
        }
    }

    class LinearMotionBaker : MotionBaker<LinearMotionAuthoring>
    {
        public override void Bake(LinearMotionAuthoring authoring)
        {
            var rig = GetComponent<RigAuthoring>();
            if (rig == null)
                return;
            var clips = RigClips(rig);
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new LinearMotion { Value = authoring.Value, Hysteresis = authoring.Hysteresis, Playback = authoring.Playback });
            var entries = AddBuffer<LinearMotionClip>(entity);
            foreach (var entry in authoring.Clips)
                entries.Add(new LinearMotionClip { Clip = Resolve(clips, entry.Clip, authoring), Threshold = entry.Threshold, ReferenceSpeed = entry.ReferenceSpeed });
            SetComponentEnabled<LinearMotion>(entity, authoring.StartEnabled);
        }
    }

    class PlanarMotionBaker : MotionBaker<PlanarMotionAuthoring>
    {
        public override void Bake(PlanarMotionAuthoring authoring)
        {
            var rig = GetComponent<RigAuthoring>();
            if (rig == null)
                return;
            var clips = RigClips(rig);
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new PlanarMotion { Value = authoring.Value, Hysteresis = authoring.Hysteresis, Playback = authoring.Playback });
            var entries = AddBuffer<PlanarMotionClip>(entity);
            foreach (var entry in authoring.Clips)
                entries.Add(new PlanarMotionClip { Clip = Resolve(clips, entry.Clip, authoring), Position = (float2)entry.Position, ReferenceSpeed = entry.ReferenceSpeed });
            SetComponentEnabled<PlanarMotion>(entity, authoring.StartEnabled);
        }
    }

    class DirectionalMotionBaker : MotionBaker<DirectionalMotionAuthoring>
    {
        public override void Bake(DirectionalMotionAuthoring authoring)
        {
            var rig = GetComponent<RigAuthoring>();
            if (rig == null)
                return;
            var clips = RigClips(rig);
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new DirectionalMotion
            {
                Idle = Resolve(clips, authoring.Idle, authoring),
                IdleSpeed = authoring.IdleSpeed,
                SpeedHysteresis = authoring.SpeedHysteresis,
                AngleHysteresis = authoring.AngleHysteresis,
                Playback = authoring.Playback
            });
            var entries = AddBuffer<DirectionalMotionClip>(entity);
            foreach (var clip in authoring.Headings)
                entries.Add(new DirectionalMotionClip { Clip = Resolve(clips, clip, authoring), ReferenceSpeed = authoring.ReferenceSpeed });
            SetComponentEnabled<DirectionalMotion>(entity, authoring.StartEnabled);
        }
    }
}
