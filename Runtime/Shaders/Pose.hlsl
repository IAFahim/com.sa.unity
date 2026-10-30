#ifndef SA_POSE_INCLUDED
#define SA_POSE_INCLUDED

struct BonePose
{
    float3 Position;
    float4 Rotation;
    float3 Scale;
};

struct Affine
{
    float3 C0;
    float3 C1;
    float3 C2;
    float3 C3;
};

float4 AlignRotation(float4 q, float4 reference)
{
    return dot(q, reference) < 0 ? -q : q;
}

float4 NormalizeRotation(float4 q)
{
    float lengthSquared = dot(q, q);
    return lengthSquared > 1e-30 ? q * rsqrt(lengthSquared) : float4(0, 0, 0, 1);
}

float4 NlerpRotation(float4 a, float4 b, float x)
{
    return NormalizeRotation(lerp(a, AlignRotation(b, a), x));
}

float4 MulRotation(float4 a, float4 b)
{
    return float4(a.w * b.xyz + b.w * a.xyz + cross(a.xyz, b.xyz), a.w * b.w - dot(a.xyz, b.xyz));
}

float4 ConjugateRotation(float4 q)
{
    return float4(-q.xyz, q.w);
}

BonePose LerpPose(BonePose a, BonePose b, float x)
{
    BonePose result;
    result.Position = lerp(a.Position, b.Position, x);
    result.Rotation = NlerpRotation(a.Rotation, b.Rotation, x);
    result.Scale = lerp(a.Scale, b.Scale, x);
    return result;
}

BonePose DeltaPose(BonePose pose, BonePose reference)
{
    BonePose result;
    result.Position = pose.Position - reference.Position;
    result.Rotation = NormalizeRotation(MulRotation(ConjugateRotation(reference.Rotation), pose.Rotation));
    result.Scale = pose.Scale - reference.Scale;
    return result;
}

BonePose AccumulatePose(BonePose pose, BonePose delta, float weight)
{
    BonePose result;
    result.Position = pose.Position + weight * delta.Position;
    result.Rotation = NormalizeRotation(MulRotation(pose.Rotation, NlerpRotation(float4(0, 0, 0, 1), delta.Rotation, weight)));
    result.Scale = pose.Scale + weight * delta.Scale;
    return result;
}

Affine AffineFromPose(BonePose pose)
{
    float4 q = pose.Rotation;
    float x2 = q.x + q.x, y2 = q.y + q.y, z2 = q.z + q.z;
    float xx = q.x * x2, yy = q.y * y2, zz = q.z * z2;
    float xy = q.x * y2, xz = q.x * z2, yz = q.y * z2;
    float wx = q.w * x2, wy = q.w * y2, wz = q.w * z2;
    Affine result;
    result.C0 = float3(1 - yy - zz, xy + wz, xz - wy) * pose.Scale.x;
    result.C1 = float3(xy - wz, 1 - xx - zz, yz + wx) * pose.Scale.y;
    result.C2 = float3(xz + wy, yz - wx, 1 - xx - yy) * pose.Scale.z;
    result.C3 = pose.Position;
    return result;
}

float3 TransformVector(Affine m, float3 v)
{
    return m.C0 * v.x + m.C1 * v.y + m.C2 * v.z;
}

float3 TransformPoint(Affine m, float3 p)
{
    return TransformVector(m, p) + m.C3;
}

Affine MulAffine(Affine a, Affine b)
{
    Affine result;
    result.C0 = TransformVector(a, b.C0);
    result.C1 = TransformVector(a, b.C1);
    result.C2 = TransformVector(a, b.C2);
    result.C3 = TransformPoint(a, b.C3);
    return result;
}

#endif
