using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SA.Animation;
using SA.Motion.Mechanics;
using SA.Motion.Numerics;
using SA.Motion.Unity;

namespace SA.Shape.Unity
{
    public struct ShapePhysical : IComponentData
    {
        public byte LivePhysical;
        public byte PendingRebuild;
        public ulong AppliedRevision;
        public byte MassPolicy;
        public FixedString512Bytes Diagnostic;
    }

    public struct ShapePhysicalRest : IBufferElementData
    {
        public float3 RestOffset;
        public float3 RestDirection;
        public float RestLength;
        public float RestRadius;
    }

    public struct ShapePhysicalBody : IBufferElementData
    {
        public int Bone;
        public float3 ColliderCenter;
        public float ColliderRadius;
        public float ColliderHeight;
        public float Mass;
        public float3 CenterOfMass;
        public float3 InertiaMoments;
        public quaternion InertiaRotation;
        public float MotorScale;
    }

    public struct ShapePhysicalJoint : IBufferElementData
    {
        public int Bone;
        public int ParentBone;
        public float3 Anchor;
        public float MotorScale;
    }

    public static class ShapePhysicalControl
    {
        public static void RequestRebuild(ref ShapePhysical physical)
        {
            physical.PendingRebuild = 1;
        }

        public static void GoLive(ref ShapePhysical physical)
        {
            physical.LivePhysical = 1;
            physical.PendingRebuild = 0;
        }

        public static void GoKinematic(ref ShapePhysical physical)
        {
            physical.LivePhysical = 0;
        }

        public static ShapeMassPolicy PolicyOrDefault(byte stored)
        {
            if (stored == (byte)ShapeMassPolicy.FixedDensity)
                return ShapeMassPolicy.FixedDensity;
            return ShapeMassPolicy.FixedMass;
        }
    }

    public static class ShapePhysicalSync
    {
        public const float RestRadiusFraction = 0.25f;
        public const float MinRestSpan = 0.1f;
        public const float SphereInertiaFactor = 0.4f;

        public const string LivePhysicalRefusal = "SA: shape edits refused while live-physical. Clear LivePhysical to customize, then request a rebuild.";
        public const string LivePhysicalUnbuilt = "SA: physical snapshot missing while live-physical. Clear LivePhysical, request a rebuild, then go live.";

        public static string RebuildFailed(AnimationError error)
        {
            return "SA: physical rebuild failed (" + error + "). Kept the previous colliders, anchors and mass.";
        }

        public static string StaleSnapshot(ulong applied, ulong current)
        {
            return "SA: shape revision " + current + " is not in the physical snapshot (" + applied + "). Request a rebuild to move colliders, anchors and mass.";
        }

        public static string WaitingForGeometry(ulong revision)
        {
            return "SA: physical rebuild waits for shape revision " + revision + " geometry and contacts.";
        }

        public static bool HasPendingChange(DynamicBuffer<ShapeChannelState> channels)
        {
            for (var i = 0; i < channels.Length; i++)
            {
                var delta = channels[i].Requested - channels[i].Value;
                if (delta > ShapeRevision.ChangeTolerance || delta < -ShapeRevision.ChangeTolerance)
                    return true;
            }
            return false;
        }

        public static void RevertRequests(DynamicBuffer<ShapeChannelState> channels)
        {
            for (var i = 0; i < channels.Length; i++)
            {
                var channel = channels[i];
                channel.Requested = channel.Value;
                channels[i] = channel;
            }
        }

        public static float RestRadius(float restLength)
        {
            var span = restLength > MinRestSpan ? restLength : MinRestSpan;
            return RestRadiusFraction * span;
        }

        public static Float3 RestCenter(Float3 direction, float length)
        {
            return direction * (0.5f * length);
        }

        public static float CapsuleWeight(float length, float radius)
        {
            return radius * radius * (1.3333334f * radius + length);
        }

        public static Float3x3 SphereInertia(float mass, float radius)
        {
            var diagonal = SphereInertiaFactor * mass * radius * radius;
            return new Float3x3(
                new Float3(diagonal, 0f, 0f),
                new Float3(0f, diagonal, 0f),
                new Float3(0f, 0f, diagonal));
        }

        public static Float3 DeformPoint(in Affine deformation, Float3 point)
        {
            return deformation.C0 * point.X + deformation.C1 * point.Y + deformation.C2 * point.Z + deformation.C3;
        }

        public static float MeanColumnLength(in Affine deformation)
        {
            return (deformation.C0.Length + deformation.C1.Length + deformation.C2.Length) / 3f;
        }

        public static bool ValidBody(float radius, float height, float mass, Float3 moments, Quaternion rotation)
        {
            if (!Scalar.IsFinite(radius) || !Scalar.IsFinite(height) || !Scalar.IsFinite(mass))
                return false;
            if (radius <= 0f || height <= 0f || mass <= 0f)
                return false;
            if (!moments.IsPositive || !rotation.IsValidRotation)
                return false;
            return true;
        }

        public static AnimationError RefitBody(
            Float3 anchor,
            Float3 restDirection,
            float restRadius,
            float baseMass,
            Float3 baseCenter,
            in Float3x3 baseInertia,
            ShapeMassPolicy policy,
            in Affine deformation,
            float previousMass,
            out float3 colliderCenter,
            out float colliderRadius,
            out float colliderHeight,
            out float mass,
            out Float3 centerOfMass,
            out Float3 moments,
            out Quaternion rotation,
            out float motorScale)
        {
            colliderCenter = float3.zero;
            colliderRadius = 0f;
            colliderHeight = 0f;
            mass = 0f;
            centerOfMass = Float3.Zero;
            moments = Float3.Zero;
            rotation = Quaternion.Identity;
            motorScale = 1f;
            if (!anchor.IsFinite || !restDirection.IsFinite || !Scalar.IsFinite(restRadius) || restRadius <= 0f)
                return AnimationError.InvalidTable;
            if (!Scalar.IsFinite(baseMass) || baseMass <= 0f)
                return AnimationError.InvalidTable;
            var length = anchor.Length;
            if (!Scalar.IsFinite(length))
                return AnimationError.InvalidTable;
            var scale = MeanColumnLength(deformation);
            if (!Scalar.IsFinite(scale) || scale <= 0f)
                return AnimationError.UnsupportedScale;
            var radius = restRadius * scale;
            var center = RestCenter(restDirection, length);
            var error = ShapeInertia.Transform(baseMass, baseCenter, baseInertia, deformation, policy, out mass, out centerOfMass, out var inertia);
            if (error != AnimationError.None)
                return error;
            error = ShapeInertia.PrincipalAxes(inertia, out moments, out rotation);
            if (error != AnimationError.None)
                return error;
            if (!ValidBody(radius, length + 2f * radius, mass, moments, rotation))
                return AnimationError.UnsupportedScale;
            colliderCenter = Conversions.ToUnity(center);
            colliderRadius = radius;
            colliderHeight = length + 2f * radius;
            if (Scalar.IsFinite(previousMass) && previousMass > 0f)
            {
                var ratio = mass / previousMass;
                if (Scalar.IsFinite(ratio) && ratio > 0f)
                    motorScale = ratio;
            }
            return AnimationError.None;
        }

        public static AnimationError RefitRig(
            ReadOnlySpan<int> parents,
            ReadOnlySpan<ShapePhysicalRest> rest,
            ReadOnlySpan<Affine> deformations,
            ReadOnlySpan<float> previousMass,
            ShapeMassPolicy policy,
            Span<ShapePhysicalBody> bodies,
            Span<ShapePhysicalJoint> joints)
        {
            var count = rest.Length;
            if (count == 0 || parents.Length != count || deformations.Length != count || bodies.Length != count || joints.Length != count)
                return AnimationError.InvalidTable;
            if (previousMass.Length != 0 && previousMass.Length != count)
                return AnimationError.InvalidTable;
            var total = 0f;
            for (var b = 0; b < count; b++)
            {
                if (!Scalar.IsFinite(rest[b].RestLength) || rest[b].RestLength < 0f || !Scalar.IsFinite(rest[b].RestRadius) || rest[b].RestRadius <= 0f)
                    return AnimationError.InvalidTable;
                total += CapsuleWeight(rest[b].RestLength, rest[b].RestRadius);
            }
            if (!Scalar.IsFinite(total) || total <= 0f)
                return AnimationError.InvalidTable;
            for (var b = 0; b < count; b++)
            {
                var parent = parents[b];
                if (b == 0 ? parent != -1 : (parent < 0 || parent >= count || parent == b))
                    return AnimationError.InvalidTable;
                var anchor = Float3.Zero;
                if (parent >= 0)
                {
                    anchor = DeformPoint(deformations[parent], Conversions.ToMotion(rest[b].RestOffset));
                    if (!anchor.IsFinite)
                        return AnimationError.UnsupportedScale;
                }
                var baseMass = CapsuleWeight(rest[b].RestLength, rest[b].RestRadius) / total;
                var baseCenter = RestCenter(Conversions.ToMotion(rest[b].RestDirection), rest[b].RestLength);
                var error = RefitBody(anchor, Conversions.ToMotion(rest[b].RestDirection), rest[b].RestRadius,
                    baseMass, baseCenter, SphereInertia(baseMass, rest[b].RestRadius), policy, deformations[b],
                    previousMass.Length == 0 ? 0f : previousMass[b],
                    out var center, out var radius, out var height, out var mass, out var com, out var moments, out var rotation, out var motor);
                if (error != AnimationError.None)
                    return error;
                bodies[b] = new ShapePhysicalBody
                {
                    Bone = b,
                    ColliderCenter = center,
                    ColliderRadius = radius,
                    ColliderHeight = height,
                    Mass = mass,
                    CenterOfMass = Conversions.ToUnity(com),
                    InertiaMoments = Conversions.ToUnity(moments),
                    InertiaRotation = Conversions.ToUnity(rotation),
                    MotorScale = motor
                };
                joints[b] = new ShapePhysicalJoint
                {
                    Bone = b,
                    ParentBone = parent,
                    Anchor = Conversions.ToUnity(anchor),
                    MotorScale = motor
                };
            }
            return AnimationError.None;
        }

        public static bool ValidateWardrobe(string[] wardrobe, string[] definitionMorphs, string[] meshMorphs, out string error)
        {
            error = string.Empty;
            if (wardrobe == null || wardrobe.Length == 0)
                return true;
            if (definitionMorphs == null || definitionMorphs.Length == 0)
            {
                error = "SA: wardrobe needs " + wardrobe.Length + " morphs but the shape definition carries none. First entry: " + wardrobe[0] + ".";
                return false;
            }
            for (var i = 0; i < wardrobe.Length; i++)
            {
                if (!ContainsName(definitionMorphs, wardrobe[i]))
                {
                    error = "SA: wardrobe entry " + wardrobe[i] + " is not a shape morph. Add it to the definition or drop the entry.";
                    return false;
                }
                if (meshMorphs == null || !ContainsName(meshMorphs, wardrobe[i]))
                {
                    error = "SA: wardrobe entry " + wardrobe[i] + " has no mesh blend shape. The corrective stays on the definition only.";
                    return false;
                }
            }
            return true;
        }

        static bool ContainsName(string[] names, string target)
        {
            for (var i = 0; i < names.Length; i++)
                if (names[i] == target)
                    return true;
            return false;
        }
    }
}
