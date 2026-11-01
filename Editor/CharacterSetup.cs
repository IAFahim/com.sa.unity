using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SA.Editor
{
    public static class CharacterSetup
    {
        [MenuItem("Tools/SA/Set Up Character")]
        static void MenuSetup() => SetUpSelection();

        public static void SetUpSelection(Action<GameObject> extra = null)
        {
            if (Selection.gameObjects.Length == 0)
            {
                Debug.LogWarning("SA: select a character in the Hierarchy or the Project window, then run Set Up Character again.");
                return;
            }
            foreach (var selected in Selection.gameObjects)
            {
                if (!EditorUtility.IsPersistent(selected))
                {
                    var configured = Configure(selected);
                    Report(configured, selected);
                    extra?.Invoke(configured.gameObject);
                    continue;
                }
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(selected);
                try
                {
                    var configured = Configure(instance);
                    extra?.Invoke(instance);
                    EnsureFolder("Assets/SA/Characters");
                    var path = AssetDatabase.GenerateUniqueAssetPath("Assets/SA/Characters/" + selected.name + " SA.prefab");
                    var saved = PrefabUtility.SaveAsPrefabAsset(instance, path);
                    Report(configured, saved, "Saved as " + path + ". Your original model was not changed.");
                    Selection.activeObject = saved;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        public static RigAuthoring Configure(GameObject root)
        {
            var owner = root.GetComponentInParent<RigAuthoring>(true) ?? root.GetComponentInChildren<RigAuthoring>(true);
            if (owner != null)
                root = owner.gameObject;
            else
            {
                var animationRoot = root.GetComponentInParent<Animator>(true);
                if (animationRoot != null)
                    root = animationRoot.gameObject;
            }
            if (PrefabUtility.IsPartOfPrefabInstance(root))
            {
                var outer = PrefabUtility.GetNearestPrefabInstanceRoot(root);
                PrefabUtility.UnpackPrefabInstance(outer, PrefabUnpackMode.Completely, InteractionMode.UserAction);
            }
            Undo.RegisterFullObjectHierarchyUndo(root, "Set Up SA Character");
            var rig = root.GetComponent<RigAuthoring>();
            if (rig == null)
            {
                rig = Undo.AddComponent<RigAuthoring>(root);
                rig.InPlace = true;
            }
            var animator = root.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null)
            {
                var avatar = AnimationDiscovery.ModelAvatar(root);
                if (avatar != null)
                {
                    if (animator == null)
                        animator = Undo.AddComponent<Animator>(root);
                    Undo.RecordObject(animator, "Discover Character Avatar");
                    animator.avatar = avatar;
                }
            }
            var clips = AnimationDiscovery.Clips(rig);
            SkinnedMeshConversion.Convert(root);
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer.TryGetComponent<SkinAuthoring>(out _))
                {
                    var materials = renderer.sharedMaterials;
                    for (var i = 0; i < materials.Length; i++)
                        materials[i] = SkinMaterial(materials[i]);
                    renderer.sharedMaterials = materials;
                }
            if (rig.Autoplay == null && clips.Count > 0)
                rig.Autoplay = clips.Find(c => c.name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0) ?? clips[0];
            EditorUtility.SetDirty(rig);
            return rig;
        }

        public static string Describe(RigAuthoring rig)
        {
            var animator = rig.GetComponentInChildren<Animator>(true);
            var avatar = animator != null ? animator.avatar : null;
            var clips = AnimationDiscovery.Clips(rig);
            var skins = rig.GetComponentsInChildren<SkinAuthoring>(true).Length;
            var motions = rig.GetComponents<IMotionClips>().Length;
            var text = new System.Text.StringBuilder("SA: set up '" + rig.name + "'.");
            text.Append(avatar != null ? " Avatar '" + avatar.name + (avatar.isHuman ? "' (humanoid)." : "' (not humanoid: Leg IK then needs bones named like a humanoid).") : " NO AVATAR FOUND: Leg IK and retargeting need one. Assign it on the Animator.");
            text.Append(skins > 0 ? " " + skins + " skinned mesh" + (skins == 1 ? "" : "es") + " converted." : " NO SKINNED MESH converted: the character will be invisible. Check that the model has a Skinned Mesh Renderer.");
            text.Append(clips.Count > 0 ? " " + clips.Count + " clips found." : " NO CLIPS FOUND: drop a model, controller or folder into Animation packs.");
            if (rig.Autoplay != null)
                text.Append(" Starts with '" + rig.Autoplay.name + "'.");
            text.Append(motions == 0 ? " Next: add a motion with the Add buttons on Rig Authoring (Single, 1D, 2D or 8-way)." : " Motions: " + motions + ".");
            text.Append(rig.FootIK || rig.GetComponent<IFootIKSettings>() != null ? " Feet are planted." : " Tick Plant feet on ground to keep feet from sliding.");
            return text.ToString();
        }

        public static void Report(RigAuthoring rig, UnityEngine.Object context, string extra = null)
        {
            var text = Describe(rig) + (extra != null ? " " + extra : "");
            if (text.Contains("NO "))
                Debug.LogWarning(text, context);
            else
                Debug.Log(text, context);
        }

        public static Material SkinMaterial(Material source)
        {
            if (source == null || source.HasProperty("_SaSkin"))
                return source;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.sa.unity/Runtime/Shaders/Skinned Lit.shader");
            if (shader == null)
                return source;
            EnsureFolder("Assets/SA/Materials");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId);
            var key = string.IsNullOrEmpty(guid) ? source.GetEntityId().GetHashCode().ToString() : guid + "_" + localId;
            var path = "Assets/SA/Materials/" + key + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
                return material;
            material = new Material(shader) { name = source.name + " SA", enableInstancing = true };
            var textureProperty = source.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            if (source.HasProperty(textureProperty))
            {
                material.SetTexture("_BaseMap", source.GetTexture(textureProperty));
                material.SetTextureScale("_BaseMap", source.GetTextureScale(textureProperty));
                material.SetTextureOffset("_BaseMap", source.GetTextureOffset(textureProperty));
            }
            material.SetColor("_BaseColor", source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
