using UnityEditor;
using UnityEngine;

namespace SA.Editor
{
    public static class SkinnedMeshConversion
    {
        [MenuItem("Tools/SA/Convert Skinned Mesh Renderers")]
        static void ConvertSelection()
        {
            foreach (var selected in Selection.gameObjects)
            {
                var path = AssetDatabase.GetAssetPath(selected);
                if (string.IsNullOrEmpty(path))
                    Convert(selected);
                else
                    ConvertPrefab(path);
            }
        }

        public static int ConvertPrefab(string path)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var converted = Convert(contents);
                if (converted > 0)
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                return converted;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        public static int Convert(GameObject selected)
        {
            var rig = selected.GetComponentInParent<RigAuthoring>(true);
            if (rig == null)
                rig = selected.GetComponentInChildren<RigAuthoring>(true);
            if (rig == null)
            {
                Debug.LogError($"SA: {selected.name}: no RigAuthoring on the selection or its parents. Add RigAuthoring to the character root first.", selected);
                return 0;
            }
            var converted = 0;
            foreach (var smr in rig.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var target = smr.gameObject;
                var mesh = smr.sharedMesh;
                var materials = smr.sharedMaterials;
                var bones = smr.bones;
                var bounds = MeshSpaceBounds(smr);
                var shadows = smr.shadowCastingMode;
                var receiveShadows = smr.receiveShadows;
                var layers = smr.renderingLayerMask;
                var probes = smr.lightProbeUsage;
                var reflections = smr.reflectionProbeUsage;
                Undo.DestroyObjectImmediate(smr);
                if (!target.TryGetComponent(out MeshFilter filter))
                    filter = Undo.AddComponent<MeshFilter>(target);
                filter.sharedMesh = mesh;
                if (!target.TryGetComponent(out MeshRenderer renderer))
                    renderer = Undo.AddComponent<MeshRenderer>(target);
                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = shadows;
                renderer.receiveShadows = receiveShadows;
                renderer.renderingLayerMask = layers;
                renderer.lightProbeUsage = probes;
                renderer.reflectionProbeUsage = reflections;
                if (!target.TryGetComponent(out SkinAuthoring skin))
                    skin = Undo.AddComponent<SkinAuthoring>(target);
                skin.Rig = rig;
                skin.Bones = bones;
                skin.LocalBounds = bounds;
                EditorUtility.SetDirty(target);
                converted++;
            }
            return converted;
        }

        static Bounds MeshSpaceBounds(SkinnedMeshRenderer smr)
        {
            var local = smr.localBounds;
            if (smr.rootBone == null)
                return local;
            var toMesh = smr.transform.worldToLocalMatrix * smr.rootBone.localToWorldMatrix;
            var result = new Bounds(toMesh.MultiplyPoint3x4(local.center), Vector3.zero);
            for (var i = 0; i < 8; i++)
            {
                var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                result.Encapsulate(toMesh.MultiplyPoint3x4(corner));
            }
            return result;
        }
    }
}
