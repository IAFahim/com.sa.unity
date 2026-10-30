using Unity.Mathematics;

namespace SA
{
    public static class PoseAlgebra
    {
        public static quaternion Align(quaternion q, quaternion reference) =>
            math.dot(q.value, reference.value) < 0f ? new quaternion(-q.value) : q;

        public static quaternion Nlerp(quaternion a, quaternion b, float x) =>
            math.normalizesafe(new quaternion(math.lerp(a.value, Align(b, a).value, x)), quaternion.identity);

        public static BonePose Lerp(in BonePose a, in BonePose b, float x) => new BonePose
        {
            Position = math.lerp(a.Position, b.Position, x),
            Rotation = Nlerp(a.Rotation, b.Rotation, x),
            Scale = math.lerp(a.Scale, b.Scale, x)
        };

        public static BonePose Delta(in BonePose pose, in BonePose reference) => new BonePose
        {
            Position = pose.Position - reference.Position,
            Rotation = math.normalizesafe(math.mul(math.conjugate(reference.Rotation), pose.Rotation), quaternion.identity),
            Scale = pose.Scale - reference.Scale
        };

        public static BonePose Accumulate(in BonePose pose, in BonePose delta, float weight) => new BonePose
        {
            Position = pose.Position + weight * delta.Position,
            Rotation = math.normalizesafe(math.mul(pose.Rotation, Nlerp(quaternion.identity, delta.Rotation, weight)), quaternion.identity),
            Scale = pose.Scale + weight * delta.Scale
        };

        public static PoseSum Add(in PoseSum sum, in BonePose pose, float weight)
        {
            var anchor = sum.Weight > 0f ? sum.Rotation : pose.Rotation.value;
            var rotation = math.dot(pose.Rotation.value, anchor) < 0f ? -pose.Rotation.value : pose.Rotation.value;
            return new PoseSum
            {
                Position = sum.Position + weight * pose.Position,
                Rotation = sum.Rotation + weight * rotation,
                Scale = sum.Scale + weight * pose.Scale,
                Weight = sum.Weight + weight
            };
        }

        public static BonePose Mean(in PoseSum sum)
        {
            var inverse = sum.Weight > 0f ? 1f / sum.Weight : 0f;
            return new BonePose
            {
                Position = sum.Position * inverse,
                Rotation = math.normalizesafe(new quaternion(sum.Rotation), quaternion.identity),
                Scale = sum.Scale * inverse
            };
        }

        public static float3x4 Affine(in BonePose pose)
        {
            var rotation = new float3x3(pose.Rotation);
            return new float3x4(rotation.c0 * pose.Scale.x, rotation.c1 * pose.Scale.y, rotation.c2 * pose.Scale.z, pose.Position);
        }

        public static float3x4 Mul(in float3x4 a, in float3x4 b)
        {
            var linear = new float3x3(a.c0, a.c1, a.c2);
            return new float3x4(math.mul(linear, b.c0), math.mul(linear, b.c1), math.mul(linear, b.c2), math.mul(linear, b.c3) + a.c3);
        }

        public static float3x4 Affine(in float4x4 m) => new float3x4(m.c0.xyz, m.c1.xyz, m.c2.xyz, m.c3.xyz);
    }
}
