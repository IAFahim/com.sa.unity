using System;
using SA.Motion.Inertialization;
using SA.Motion.Unity;
using MotionPose = SA.Motion.Numerics.Pose;

namespace SA
{
    public static class Inertial
    {
        public static MotionPose ToMotion(in BonePose pose) => new MotionPose
        {
            Position = Conversions.ToMotion(pose.Position),
            Rotation = Conversions.ToMotion(pose.Rotation),
            Scale = Conversions.ToMotion(pose.Scale)
        };

        public static BonePose ToBone(in MotionPose pose) => new BonePose
        {
            Position = Conversions.ToUnity(pose.Position),
            Rotation = Conversions.ToUnity(pose.Rotation),
            Scale = Conversions.ToUnity(pose.Scale)
        };

        public static BonePose Step(ref BoneTransition bone, in BonePose destination, bool begin, float seconds, float elapsed, float deltaTime)
        {
            var target = ToMotion(destination);
            if (begin)
                bone.Offset = PoseTransition.Create(bone.History, target, seconds);
            var output = PoseTransition.Apply(ref bone.Offset, target, elapsed);
            bone.History.Capture(output, deltaTime);
            return ToBone(output);
        }

        public static uint Membership(ReadOnlySpan<ClipId> clips, in PlayOptions options)
        {
            var hash = 2166136261u;
            hash = (hash ^ options.Layer) * 16777619u;
            hash = (hash ^ (uint)options.Mode) * 16777619u;
            hash = (hash ^ options.Mask.Index) * 16777619u;
            for (var i = 0; i < clips.Length; i++)
                hash = (hash ^ clips[i].Index) * 16777619u;
            return hash;
        }
    }
}
