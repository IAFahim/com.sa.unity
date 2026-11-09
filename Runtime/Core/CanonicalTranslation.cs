using System;
using SA.Animation;
using SA.Motion.Numerics;

namespace SA
{
    public static class CanonicalTranslation
    {
        public static Float3 ToCanonical(in Quaternion targetParent, in Quaternion canonicalParent, in Float3 targetOffset, float heightRatio)
        {
            if (!Scalar.IsFinite(heightRatio) || heightRatio <= 0f || !targetOffset.IsFinite)
                return Float3.Zero;
            var world = Quaternion.Rotate(Quaternion.Normalize(targetParent), targetOffset);
            var local = Quaternion.Rotate(Quaternion.InverseUnit(Quaternion.Normalize(canonicalParent)), world) * (1f / heightRatio);
            return local.IsFinite ? local : Float3.Zero;
        }

        public static Float3 ToTarget(in Quaternion targetParent, in Quaternion canonicalParent, in Float3 canonicalOffset, float heightRatio)
        {
            if (!Scalar.IsFinite(heightRatio) || heightRatio <= 0f || !canonicalOffset.IsFinite)
                return Float3.Zero;
            var world = Quaternion.Rotate(Quaternion.Normalize(canonicalParent), canonicalOffset) * heightRatio;
            var local = Quaternion.Rotate(Quaternion.InverseUnit(Quaternion.Normalize(targetParent)), world);
            return local.IsFinite ? local : Float3.Zero;
        }

        public static void Apply(ReadOnlySpan<RetargetRow> rows, in RetargetBinding binding, ReadOnlySpan<int> canonicalParents,
            ReadOnlySpan<Pose> canonicalLocal, ReadOnlySpan<Pose> canonicalReference, ReadOnlySpan<RigidPose> canonicalModel,
            ReadOnlySpan<RigidPose> targetModel, Span<Pose> targetLocal)
        {
            var count = Math.Min(rows.Length, Math.Min(targetModel.Length, targetLocal.Length));
            var canonicalCount = Math.Min(canonicalParents.Length, Math.Min(canonicalLocal.Length, Math.Min(canonicalReference.Length, canonicalModel.Length)));
            for (var i = 1; i < count; i++)
            {
                ref readonly var row = ref rows[i];
                var source = row.Source;
                if (source <= 0 || source >= canonicalCount || i == binding.TargetHips || source == binding.SourceHips)
                    continue;
                var offset = canonicalLocal[source].Position - canonicalReference[source].Position;
                if (!offset.IsFinite || offset.LengthSquared <= 1e-14f)
                    continue;
                var canonicalParent = canonicalParents[source] < 0 ? Quaternion.Identity : canonicalModel[canonicalParents[source]].Rotation;
                var targetParent = row.Parent < 0 || row.Parent >= count ? Quaternion.Identity : targetModel[row.Parent].Rotation;
                var local = targetLocal[i];
                local.Position = local.Position + ToTarget(targetParent, canonicalParent, offset, binding.HeightRatio);
                targetLocal[i] = local;
            }
        }
    }
}
