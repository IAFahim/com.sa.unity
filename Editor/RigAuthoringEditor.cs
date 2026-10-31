using UnityEditor;
using UnityEngine;

namespace SA.Editor
{
    [CustomEditor(typeof(RigAuthoring))]
    public class RigAuthoringEditor : UnityEditor.Editor
    {
        bool animations;
        bool advanced;

        public override void OnInspectorGUI()
        {
            var rig = (RigAuthoring)target;
            if (GUILayout.Button("Set Up Character"))
            {
                CharacterSetup.Configure(rig.gameObject);
                serializedObject.Update();
            }
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimationLibraries"), new GUIContent("Animation packs"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Autoplay"), new GUIContent("Start animation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("InPlace"), new GUIContent("In place by default"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Inertialize"), new GUIContent("Smooth transitions"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FootIK"), new GUIContent("Plant feet on ground"));
            serializedObject.ApplyModifiedProperties();
            if (rig.FootIK)
                EditorGUILayout.HelpBox("Foot IK uses the Reactions package and Unity Physics ground colliders. It enables CPU bones automatically. Physics-owned ragdoll bones remain controlled by physics.", MessageType.Info);
            var clips = AnimationDiscovery.Clips(rig);
            animations = EditorGUILayout.Foldout(animations, "Resolved animations (" + clips.Count + ")", true);
            if (animations)
                foreach (var clip in clips)
                {
                    EditorGUILayout.BeginHorizontal();
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false);
                    var name = AnimationDiscovery.Name(rig, clip, clips);
                    var renamed = EditorGUILayout.TextField(name);
                    var value = rig.IsInPlace(clip);
                    var next = EditorGUILayout.ToggleLeft("In place", value, GUILayout.Width(75));
                    if (next != value || renamed != name)
                    {
                        Undo.RecordObject(rig, "Change Clip Motion");
                        var index = rig.ClipOptions.FindIndex(c => c.Clip == clip);
                        var option = new ClipSettings { Clip = clip, InPlace = next, Name = renamed };
                        if (index < 0)
                            rig.ClipOptions.Add(option);
                        else
                            rig.ClipOptions[index] = option;
                        EditorUtility.SetDirty(rig);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            advanced = EditorGUILayout.Foldout(advanced, "Advanced (optional)", true);
            if (advanced)
            {
                serializedObject.Update();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("DiscoverClips"), new GUIContent("Use model/controller clips"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Clips"), new GUIContent("Extra clips"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Masks"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Engine"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("SampleRate"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("TimeScale"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("MotionBone"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ExtraBones"), true);
                serializedObject.ApplyModifiedProperties();
            }
            EditorGUILayout.HelpBox("Bones are discovered from skins and the Avatar. Clip libraries accept controllers, models or folders. Animator is used for authoring and sampling only.", MessageType.Info);
        }
    }
}
