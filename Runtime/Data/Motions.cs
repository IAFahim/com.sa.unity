using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SA
{
    public enum MotionKind : byte
    {
        None,
        Single,
        Linear,
        Planar,
        Directional
    }

    [Serializable]
    public struct MotionPlayback
    {
        [Tooltip("When several motions are enabled, the highest priority drives the character. Ties go to the more specific motion: directional, planar, linear, then single.")]
        public byte Priority;
        [Tooltip("Playback rate. 1 plays the clip as authored.")]
        public float Speed;
        [Tooltip("Seconds to settle into a newly chosen clip. With inertialization this is the decay time, otherwise a cross-fade.")]
        public float TransitionSeconds;
        [Tooltip("Scale the playback rate by the motion's speed over the clip's reference speed, so feet slide less.")]
        public bool MatchRate;
        [Tooltip("Lowest rate speed matching may use.")]
        public float MinRate;
        [Tooltip("Highest rate speed matching may use.")]
        public float MaxRate;

        public static MotionPlayback Default => new MotionPlayback { Speed = 1f, TransitionSeconds = 0.25f, MinRate = 0.5f, MaxRate = 2f };
    }

    public struct MotionDriver : IComponentData
    {
        public MotionKind Active;
        public int Selection;
        public bool Idle;
    }

    public struct SingleMotion : IComponentData, IEnableableComponent
    {
        public ClipId Clip;
        public Wrap Wrap;
        public bool TimeDriven;
        public float Time;
        public MotionPlayback Playback;
    }

    public struct LinearMotion : IComponentData, IEnableableComponent
    {
        public float Value;
        public float Hysteresis;
        public MotionPlayback Playback;
    }

    [InternalBufferCapacity(4)]
    public struct LinearMotionClip : IBufferElementData
    {
        public ClipId Clip;
        public float Threshold;
        public float ReferenceSpeed;
    }

    public struct PlanarMotion : IComponentData, IEnableableComponent
    {
        public float2 Value;
        public float Hysteresis;
        public MotionPlayback Playback;
    }

    [InternalBufferCapacity(4)]
    public struct PlanarMotionClip : IBufferElementData
    {
        public ClipId Clip;
        public float2 Position;
        public float ReferenceSpeed;
    }

    public struct DirectionalMotion : IComponentData, IEnableableComponent
    {
        public float2 Velocity;
        public ClipId Idle;
        public float IdleSpeed;
        public float SpeedHysteresis;
        public float AngleHysteresis;
        public MotionPlayback Playback;
    }

    [InternalBufferCapacity(8)]
    public struct DirectionalMotionClip : IBufferElementData
    {
        public ClipId Clip;
        public float ReferenceSpeed;
    }
}
