using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SA.Editor
{
    public static class AnimationDiscovery
    {
        public static Avatar ModelAvatar(GameObject root)
        {
            var paths = new HashSet<string>();
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (skin.sharedMesh != null)
                    paths.Add(AssetDatabase.GetAssetPath(skin.sharedMesh));
            foreach (var mesh in root.GetComponentsInChildren<MeshFilter>(true))
                if (mesh.sharedMesh != null)
                    paths.Add(AssetDatabase.GetAssetPath(mesh.sharedMesh));
            foreach (var path in paths)
                if (!string.IsNullOrEmpty(path))
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                        if (asset is Avatar avatar && avatar.isValid)
                            return avatar;
            return null;
        }

        public static List<AnimationClip> Clips(RigAuthoring rig)
        {
            var result = new List<AnimationClip>();
            var names = new HashSet<string>();
            foreach (var clip in rig.Clips)
                if (clip != null && !result.Contains(clip))
                {
                    result.Add(clip);
                    names.Add(clip.name);
                }
            Add(rig.Autoplay, result, names);
            if (rig.DiscoverClips)
            {
                foreach (var animator in rig.GetComponentsInChildren<Animator>(true))
                {
                    Load(animator.runtimeAnimatorController, result, names);
                    Load(animator.avatar, result, names);
                }
                Load(PrefabUtility.GetCorrespondingObjectFromOriginalSource(rig.gameObject), result, names);
                foreach (var mesh in rig.GetComponentsInChildren<MeshFilter>(true))
                    Load(mesh.sharedMesh, result, names);
                foreach (var skin in rig.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    Load(skin.sharedMesh, result, names);
            }
            foreach (var source in rig.AnimationLibraries)
                Load(source, result, names);
            return result;
        }

        static void Add(AnimationClip clip, List<AnimationClip> result, HashSet<string> names)
        {
            if (clip != null && !clip.name.StartsWith("__preview__", StringComparison.Ordinal) && !result.Contains(clip))
                result.Add(clip);
        }

        public static string Name(RigAuthoring rig, AnimationClip clip, IReadOnlyList<AnimationClip> clips)
        {
            foreach (var option in rig.ClipOptions)
                if (option.Clip == clip && !string.IsNullOrWhiteSpace(option.Name))
                    return option.Name;
            AnimationClip first = null;
            foreach (var candidate in clips)
                if (candidate.name == clip.name)
                {
                    first = candidate;
                    break;
                }
            if (first == clip)
                return clip.name;
            var path = AssetDatabase.GetAssetPath(clip);
            var label = string.IsNullOrEmpty(path) ? clip.GetEntityId().GetHashCode().ToString() : Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileNameWithoutExtension(path);
            var name = clip.name + "@" + label;
            if (name.Length <= 60)
                return name;
            var suffix = RigBuilder.Hash(path + clip.GetEntityId()).ToString("x8");
            return name.Substring(0, 50) + "_" + suffix;
        }

        static void Load(UnityEngine.Object source, List<AnimationClip> result, HashSet<string> names)
        {
            if (source == null)
                return;
            if (source is AnimationClip clip)
            {
                Add(clip, result, names);
                return;
            }
            else if (source is RuntimeAnimatorController controller)
            {
                foreach (var animation in controller.animationClips)
                    Add(animation, result, names);
                return;
            }
            else if (source is GameObject model)
            {
                foreach (var animator in model.GetComponentsInChildren<Animator>(true))
                {
                    Load(animator.runtimeAnimatorController, result, names);
                    Load(animator.avatar, result, names);
                }
                foreach (var skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    Load(skin.sharedMesh, result, names);
                foreach (var mesh in model.GetComponentsInChildren<MeshFilter>(true))
                    Load(mesh.sharedMesh, result, names);
            }
            var path = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(path))
                return;
            if (AssetDatabase.IsValidFolder(path))
            {
                var paths = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { path }))
                    paths.Add(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var child in paths)
                    LoadPath(child, result, names);
            }
            else
                LoadPath(path, result, names);
        }

        static void LoadPath(string path, List<AnimationClip> result, HashSet<string> names)
        {
            var clips = new List<AnimationClip>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is AnimationClip clip)
                    clips.Add(clip);
            clips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            foreach (var clip in clips)
                Add(clip, result, names);
        }
    }
}
