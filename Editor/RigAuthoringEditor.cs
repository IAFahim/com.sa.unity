using System;
using UnityEditor;
using UnityEngine;

namespace SA.Editor
{
    [CustomEditor(typeof(RigAuthoring))]
    public class RigAuthoringEditor : UnityEditor.Editor
    {
        static readonly (string Label, Type Type)[] Motions =
        {
            ("Single", typeof(SingleMotionAuthoring)),
            ("1D", typeof(LinearMotionAuthoring)),
            ("2D", typeof(PlanarMotionAuthoring)),
            ("8-way", typeof(DirectionalMotionAuthoring))
        };

        bool animations;
        bool advanced;

        public override void OnInspectorGUI()
        {
            var rig = (RigAuthoring)target;
            if (GUILayout.Button(new GUIContent("Set Up Character", "Find the bones, the Avatar and the clips, and convert the meshes. The Console lists what changed.")))
            {
                CharacterSetup.Report(CharacterSetup.Configure(rig.gameObject), rig);
                serializedObject.Update();
            }
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimationLibraries"), new GUIContent("Animation packs"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Autoplay"), new GUIContent("Start animation", "Plays from the first frame until a motion or your code picks another clip."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("InPlace"), new GUIContent("In place by default", "Remove the clips' forward travel so the character stays where it stands."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Inertialize"), new GUIContent("Smooth transitions", "On: a clip switch is a cut smoothed by inertialization, with no blending. Off: switches cross-fade."));
            var settings = rig.GetComponent<IFootIKSettings>() as Component;
            if (settings != null)
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.Toggle(new GUIContent("Plant feet on ground", "Turned on by " + Title(settings) + "."), true);
            else
                EditorGUILayout.PropertyField(serializedObject.FindProperty("FootIK"), new GUIContent("Plant feet on ground", "Keep planted feet still and roll them from heel to ball on any ground with a collider."));
            serializedObject.ApplyModifiedProperties();
            var plant = settings != null || rig.FootIK;
            FootHelp(rig, settings);
            MotionHelp(rig);
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
                    var foot = rig.UsesFootIK(clip);
                    bool footNext;
                    using (new EditorGUI.DisabledScope(!plant))
                        footNext = EditorGUILayout.ToggleLeft(new GUIContent("Foot IK", plant ? "Let the leg solver plant and roll the feet while this clip plays. Untick for flips, jumps and anything airborne." : "Turn on Plant feet on ground first."), foot, GUILayout.Width(70));
                    if (next != value || renamed != name || footNext != foot)
                    {
                        Undo.RecordObject(rig, "Change Clip Motion");
                        var index = rig.ClipOptions.FindIndex(c => c.Clip == clip);
                        var option = new ClipSettings { Clip = clip, InPlace = next, Name = renamed, DisableFootIK = !footNext };
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

        static void FootHelp(RigAuthoring rig, Component settings)
        {
            if (settings != null)
            {
                EditorGUILayout.HelpBox("Feet are planted with the settings in " + Title(settings) + " below. Remove that component to stop planting.", MessageType.None);
                return;
            }
            if (!rig.FootIK)
                return;
            var type = SettingsType();
            if (type == null)
            {
                EditorGUILayout.HelpBox("This does nothing yet. The leg solver is in the SA Reactions package (com.sa.unity.reactions), which is not installed.", MessageType.Warning);
                return;
            }
            EditorGUILayout.HelpBox("Feet use the Gentle preset and stand on colliders on the Default layer. On flat ground an in-place clip already touches the floor, so the change is small: feet stop sliding and roll from heel to ball. Steps and slopes show it most. The bones are posed on the CPU.", MessageType.Info);
            if (GUILayout.Button("Customize planting (preset, ground layers, heel and ball)"))
                Undo.AddComponent(rig.gameObject, type);
        }

        static void MotionHelp(RigAuthoring rig)
        {
            if (rig.GetComponent<IMotionClips>() != null)
                return;
            EditorGUILayout.HelpBox("No motion yet: the start animation plays and your own code can switch clips. A motion picks the clip for you. Single plays one clip, 1D picks by a value such as speed, 2D by a point, 8-way by a direction.", MessageType.None);
            EditorGUILayout.BeginHorizontal();
            foreach (var motion in Motions)
                if (GUILayout.Button("Add " + motion.Label))
                    Undo.AddComponent(rig.gameObject, motion.Type);
            EditorGUILayout.EndHorizontal();
        }

        static Type SettingsType()
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<IFootIKSettings>())
                if (!type.IsAbstract && typeof(MonoBehaviour).IsAssignableFrom(type))
                    return type;
            return null;
        }

        static string Title(Component component) => ObjectNames.NicifyVariableName(component.GetType().Name);
    }
}
