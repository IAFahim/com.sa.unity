using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SA;

namespace SA.Shape.Unity.Editor
{
    [CustomEditor(typeof(ShapeAuthoring))]
    public class ShapeAuthoringInspector : UnityEditor.Editor
    {
        TextAsset cachedSource;
        RigAuthoring cachedRig;
        int cachedScaffold;
        string[] cachedNames = System.Array.Empty<string>();
        string[] cachedMorphs = System.Array.Empty<string>();

        public override void OnInspectorGUI()
        {
            var authoring = (ShapeAuthoring)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Family"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Definition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("LivePhysical"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("MassPolicy"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Wardrobe"), true);
            serializedObject.ApplyModifiedProperties();
            if (authoring.LivePhysical)
                EditorGUILayout.HelpBox("Live-physical start: runtime shape requests are refused while live-physical. Clear LivePhysical to customize, then request one fixed-step rebuild.", MessageType.Info);
            if (authoring.Definition == null || authoring.Definition.Source == null)
            {
                EditorGUILayout.HelpBox("No shape definition assigned. Assign a .sashape TextAsset, then rebake.", MessageType.Error);
                return;
            }
            var family = authoring.Family != null ? authoring.Family : authoring.GetComponent<SharedFamilyAuthoring>();
            var rig = family != null && family.Rig != null ? family.Rig : authoring.GetComponent<RigAuthoring>();
            if (family == null || rig == null)
            {
                EditorGUILayout.HelpBox("Assign the family and its rig to preview named channels. Saved requests stay editable below.", MessageType.Info);
                serializedObject.Update();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Requests"), true);
                serializedObject.ApplyModifiedProperties();
                return;
            }
            var scaffold = rig.transform.hierarchyCount;
            if (cachedSource != authoring.Definition.Source || cachedRig != rig || cachedScaffold != scaffold)
            {
                cachedSource = authoring.Definition.Source;
                cachedRig = rig;
                cachedScaffold = scaffold;
                cachedMorphs = ShapeBaker.MeshMorphNames(rig);
                string rigError;
                string[] rigNames;
                if (!SA.Editor.ShapeRigContext.Describe(rig, family, out rigNames, out _, out _, out rigError))
                {
                    EditorGUILayout.HelpBox(rigError, MessageType.Error);
                    cachedSource = null;
                    return;
                }
                cachedNames = rigNames;
            }
            var saved = authoring.Requests != null ? authoring.Requests.ToArray() : System.Array.Empty<ShapeRequest>();
            ShapeInspectorRow[] rows;
            string warning;
            string error;
            if (!ShapeInspectorModel.Describe(authoring.Definition.Source, cachedNames, cachedMorphs,
                default, System.Array.Empty<SA.Motion.Numerics.Float3>(), saved,
                out var asset, out rows, out warning, out error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                return;
            }
            if (!string.IsNullOrEmpty(warning))
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            EditorGUILayout.LabelField("Channels (" + rows.Length + ")", EditorStyles.boldLabel);
            foreach (var row in rows)
            {
                var value = EditorGUILayout.Slider(new GUIContent(row.Name, "Channel " + row.Id + ", default " + row.Default), row.Requested, row.Minimum, row.Maximum);
                if (value != row.Requested)
                    StoreRequest(authoring, row, value);
            }
            if (asset != null && asset.MorphNames.Length > 0)
                EditorGUILayout.HelpBox("Corrective morphs (" + asset.MorphNames.Length + "): " + string.Join(", ", asset.MorphNames) + ". Weights ride the channel values at bake and upload with the skin.", MessageType.Info);
        }

        static void StoreRequest(ShapeAuthoring authoring, ShapeInspectorRow row, float value)
        {
            Undo.RecordObject(authoring, "Edit shape request");
            if (authoring.Requests == null)
                authoring.Requests = new List<ShapeRequest>();
            for (var i = 0; i < authoring.Requests.Count; i++)
                if (authoring.Requests[i].Id == row.Id)
                {
                    authoring.Requests[i] = new ShapeRequest { Id = row.Id, Name = row.Name, Value = value };
                    EditorUtility.SetDirty(authoring);
                    return;
                }
            authoring.Requests.Add(new ShapeRequest { Id = row.Id, Name = row.Name, Value = value });
            EditorUtility.SetDirty(authoring);
        }
    }
}
