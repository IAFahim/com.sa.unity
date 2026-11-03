using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using SA.Animation;
using SA.Animation.Content;
using SA.Motion.Numerics;
using SA.Motion.Unity;
using Pose = SA.Motion.Numerics.Pose;
namespace SA.Editor
{
    public static class ShapeRigContext
    {
        public static bool Describe(RigAuthoring rig, SharedFamilyAuthoring family, out string[] boneNames, out Pose[] calibration, out AssetId target, out string error)
        {
            boneNames = Array.Empty<string>();
            calibration = Array.Empty<Pose>();
            target = default;
            error = string.Empty;
            if (rig == null)
            {
                error = "no RigAuthoring found.";
                return false;
            }
            var clips = new List<AnimationClip>();
            if (family != null)
                foreach (var clip in family.Clips)
                    if (clip != null && !clips.Contains(clip))
                        clips.Add(clip);
            if (clips.Count == 0)
                foreach (var clip in AnimationDiscovery.Clips(rig))
                    if (clip != null && !clips.Contains(clip))
                        clips.Add(clip);
            var skins = new List<SkinAuthoring>();
            foreach (var skin in rig.GetComponentsInChildren<SkinAuthoring>(true))
                if (skin.Rig == rig || skin.Rig == null)
                    skins.Add(skin);
            var root = rig.transform;
            var resolveErrors = new List<string>();
            var skeleton = RigBuilder.Skeleton(root, clips, skins, resolveErrors, rig.ExtraBones);
            if (resolveErrors.Count > 0)
            {
                error = resolveErrors[0];
                return false;
            }
            var count = skeleton.Count;
            if (count == 0 || count > SampleLocator.MaxBones)
            {
                error = "skeleton is empty or exceeds the bone cap.";
                return false;
            }
            var parents = new int[count];
            for (var b = 0; b < count; b++)
                parents[b] = skeleton.IndexOf(skeleton[b].parent);
            if (parents[0] != -1)
            {
                error = "skeleton has no single root bone.";
                return false;
            }
            for (var b = 1; b < count; b++)
                if (parents[b] < 0)
                {
                    error = "bone '" + skeleton[b].name + "' is detached from the skeleton root.";
                    return false;
                }
            boneNames = new string[count];
            calibration = new Pose[count];
            for (var b = 0; b < count; b++)
            {
                var rest = RigBuilder.Local(skeleton[b]);
                if (!IsUnitScale(rest.Scale))
                {
                    error = "bone '" + skeleton[b].name + "' has a non-unit rest scale.";
                    return false;
                }
                boneNames[b] = skeleton[b].name;
                calibration[b] = new Pose
                {
                    Position = Conversions.ToMotion(rest.Position),
                    Rotation = Conversions.ToMotion(rest.Rotation),
                    Scale = Float3.One
                };
            }
            var identityNames = new string[count];
            for (var b = 0; b < count; b++)
                identityNames[b] = AnimationUtility.CalculateTransformPath(skeleton[b], root) + "#" + skeleton[b].name;
            if (ContentIdentity.Skeleton("SA.Target." + rig.name, 1, identityNames, parents, calibration, out target) != AnimationError.None)
            {
                error = "target identity failed to compute.";
                return false;
            }
            return true;
        }

        static bool IsUnitScale(float3 scale)
        {
            return Math.Abs(scale.x - 1f) <= 1e-5f && Math.Abs(scale.y - 1f) <= 1e-5f && Math.Abs(scale.z - 1f) <= 1e-5f;
        }
    }
}
