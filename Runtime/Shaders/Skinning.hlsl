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
    skinnedNormal = normalize(TransformVector(m, normal));
    skinnedTangent = normalize(TransformVector(m, tangent));
}

#endif
