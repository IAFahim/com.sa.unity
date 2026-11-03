using System;
using SA.Animation;
using SA.Motion.Mechanics;
using SA.Motion.Numerics;

namespace SA.Shape.Unity
{
    public static class ShapeSkinUpload
    {
        public static AnimationError BuildPalette(in Affine meshFromModel, ReadOnlySpan<RigidPose> rigidModels,
            ReadOnlySpan<Affine> deformations, ReadOnlySpan<int> paletteToRig, ReadOnlySpan<Affine> bindposes,
            Span<Affine> modelScratch, Span<Affine> positions, Span<Float3x3> normals)
        {
            if (rigidModels.Length != deformations.Length || modelScratch.Length < rigidModels.Length)
                return AnimationError.InvalidTable;
            if (modelScratch.Overlaps(positions))
                return AnimationError.AliasedBuffers;
            for (var i = 0; i < rigidModels.Length; i++)
            {
                if (!rigidModels[i].IsValid)
                    return AnimationError.InvalidTable;
                modelScratch[i] = ToAffine(rigidModels[i]);
            }
            return ShapeSkinPalette.Build(modelScratch.Slice(0, rigidModels.Length), deformations, paletteToRig,
                bindposes, meshFromModel, positions, normals);
        }

        public static AnimationError BlendVertex(ReadOnlySpan<Affine> palette, ReadOnlySpan<int> influences,
            ReadOnlySpan<float> weights, out Affine position, out Float3x3 normal)
        {
            return ShapeSkinPalette.Blend(palette, influences, weights, out position, out normal);
        }

        public static bool FinishNormal(in Float3x3 normalMatrix, Float3 objectNormal, out Float3 normal)
        {
            normal = default;
            if (!objectNormal.IsFinite)
                return false;
            var candidate = normalMatrix.Multiply(objectNormal);
            if (!candidate.IsFinite || candidate.LengthSquared < 1e-24f)
                return false;
            normal = candidate * (1f / (float)Math.Sqrt(candidate.LengthSquared));
            return true;
        }

        public static bool FinishTangent(in Affine positionMatrix, Float3 objectTangent, float handedness,
            in Float3 normal, out Float3 tangent, out float outHandedness)
        {
            tangent = default;
            outHandedness = handedness;
            if (!objectTangent.IsFinite || !normal.IsFinite || (handedness != 1f && handedness != -1f))
                return false;
            var candidate = positionMatrix.C0 * objectTangent.X + positionMatrix.C1 * objectTangent.Y + positionMatrix.C2 * objectTangent.Z;
            if (!candidate.IsFinite)
                return false;
            candidate -= normal * Float3.Dot(normal, candidate);
            if (!candidate.IsFinite || candidate.LengthSquared < 1e-24f)
                return false;
            tangent = candidate * (1f / (float)Math.Sqrt(candidate.LengthSquared));
            return true;
        }

        static Affine ToAffine(in RigidPose pose)
        {
            var x = Quaternion.Rotate(pose.Rotation, new Float3(1f, 0f, 0f));
            var y = Quaternion.Rotate(pose.Rotation, new Float3(0f, 1f, 0f));
            var z = Quaternion.Rotate(pose.Rotation, new Float3(0f, 0f, 1f));
            return new Affine(x, y, z, pose.Position);
        }
    }
}
