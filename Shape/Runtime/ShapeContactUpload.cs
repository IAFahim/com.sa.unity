using System;
using SA.Animation;
using SA.Motion.Numerics;

namespace SA.Shape.Unity
{
    public static class ShapeContactUpload
    {
        public static bool MarkerPoint(in RigidPose model, in Affine deformation, Float3 boneOffset, out Float3 point)
        {
            return ShapeContacts.TryMarker(model, deformation, boneOffset, out point);
        }

        public static bool AnkleTarget(in ShapeMarker sole, ReadOnlySpan<Affine> deformations, Float3 supportAnchor,
            Quaternion footRotation, out Float3 ankle)
        {
            return ShapeContacts.TryAnkleTarget(sole, deformations, supportAnchor, footRotation, out ankle);
        }

        public static AnimationError Measure(ReadOnlySpan<RetargetRow> rows, ReadOnlySpan<Pose> local,
            in ShapeLimb limb, Span<RigidPose> modelScratch, out ShapeLimbMetrics metrics)
        {
            return ShapeContacts.Measure(rows, local, limb, modelScratch, out metrics);
        }

        public static AnimationError Reach(ReadOnlySpan<RetargetRow> rows, ReadOnlySpan<Pose> source,
            in ShapeLimb limb, in ShapeContactTarget target, Span<RigidPose> modelScratch, Span<Pose> output,
            out ShapeReachResult result)
        {
            return ShapeContacts.Reach(rows, source, limb, target, modelScratch, output, out result);
        }
    }
}
