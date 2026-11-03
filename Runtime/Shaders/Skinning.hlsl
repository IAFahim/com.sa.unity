#ifndef SA_SKINNING_INCLUDED
#define SA_SKINNING_INCLUDED

#include "Packages/com.sa.unity/Runtime/Shaders/Pose.hlsl"

StructuredBuffer<Affine> _SaSkinMatrices;
StructuredBuffer<uint4> _SaInfluences;

Affine WeightedAffine(Affine a, Affine b, Affine c, Affine d, float4 w)
{
    Affine result;
    result.C0 = a.C0 * w.x + b.C0 * w.y + c.C0 * w.z + d.C0 * w.w;
    result.C1 = a.C1 * w.x + b.C1 * w.y + c.C1 * w.z + d.C1 * w.w;
    result.C2 = a.C2 * w.x + b.C2 * w.y + c.C2 * w.z + d.C2 * w.w;
    result.C3 = a.C3 * w.x + b.C3 * w.y + c.C3 * w.z + d.C3 * w.w;
    return result;
}

float3 SkinDirection(float3 direction, float3 fallback)
{
    float lengthSquared = dot(direction, direction);
    float3 result = fallback;
    if (all(isfinite(direction)) && isfinite(lengthSquared) && lengthSquared > 1e-20)
        result = direction * rsqrt(lengthSquared);
    return result;
}

float3 SkinNormal(Affine m, float3 normal)
{
    float3 x = cross(m.C1.xyz, m.C2.xyz);
    float3 y = cross(m.C2.xyz, m.C0.xyz);
    float3 z = cross(m.C0.xyz, m.C1.xyz);
    float determinant = dot(m.C0.xyz, x);
    float3 fallback = SkinDirection(TransformVector(m, normal), SkinDirection(normal, float3(0, 1, 0)));
    float3 result = fallback;
    if (isfinite(determinant) && abs(determinant) >= 1e-20)
        result = SkinDirection((x * normal.x + y * normal.y + z * normal.z) / determinant, fallback);
    return result;
}

float3 SkinTangent(Affine m, float3 tangent, float3 normal)
{
    float3 direction = TransformVector(m, tangent);
    direction -= normal * dot(normal, direction);
    float3 axis = abs(normal.x) < .8 ? float3(1, 0, 0) : float3(0, 1, 0);
    float3 fallback = SkinDirection(cross(normal, axis), float3(0, 0, 1));
    return SkinDirection(direction, fallback);
}

void Skin_float(float2 skin, uint vertexId, float3 position, float3 normal, float3 tangent, out float3 skinnedPosition, out float3 skinnedNormal, out float3 skinnedTangent)
{
    skinnedPosition = position;
    skinnedNormal = normal;
    skinnedTangent = tangent;
    uint matrixBase = asuint(skin.x);
    if (matrixBase == 0u)
        return;
    uint4 packed = _SaInfluences[asuint(skin.y) + vertexId];
    uint4 bones = uint4(packed.x & 0xFFFFu, packed.x >> 16, packed.y & 0xFFFFu, packed.y >> 16);
    float4 weights = float4(packed.z & 0xFFFFu, packed.z >> 16, packed.w & 0xFFFFu, packed.w >> 16) / 65535.0;
    Affine m = WeightedAffine(_SaSkinMatrices[matrixBase + bones.x], _SaSkinMatrices[matrixBase + bones.y], _SaSkinMatrices[matrixBase + bones.z], _SaSkinMatrices[matrixBase + bones.w], weights);
    skinnedPosition = TransformPoint(m, position);
    skinnedNormal = SkinNormal(m, normal);
    skinnedTangent = SkinTangent(m, tangent, skinnedNormal);
}

#endif
