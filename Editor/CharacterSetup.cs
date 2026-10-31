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
            foreach (var selected in Selection.gameObjects)
            {
                if (!EditorUtility.IsPersistent(selected))
                {
                    var configured = Configure(selected);
                    extra?.Invoke(configured.gameObject);
                    continue;
                }
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(selected);
                try
                {
                    Configure(instance);
                    extra?.Invoke(instance);
                    EnsureFolder("Assets/SA/Characters");
                    var path = AssetDatabase.GenerateUniqueAssetPath("Assets/SA/Characters/" + selected.name + " SA.prefab");
                    PrefabUtility.SaveAsPrefabAsset(instance, path);
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(path);
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
