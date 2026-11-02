using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SA.Editor
{
    public class AnimationLibraryWindow : EditorWindow
    {
        const string ExtractMenu = "Tools/SA/Animation Library/Extract Canonical Samples";
        const string ManifestPath = "Temp/sa/c08/_manifest.json";

        const string Ual1Path = "Assets/UAL/UAL1_Standard.fbx";
        const string Ual2Path = "Assets/UAL/UAL2_Standard.fbx";
        const string KyleModelPath = "Assets/Samples/Rukhanka Animation System 2/2.10.0/Rukhanka Animation Samples/SampleAssets/Robot Kyle/Model/Robot Kyle.fbx";
        const string KyleAnimDir = "Assets/Samples/Rukhanka Animation System 2/2.10.0/Rukhanka Animation Samples/SampleAssets/Robot Kyle/Animations/";

        sealed class RigRow
        {
            public string RigPath;
            public string RigName;
            public GameObject Model;
            public string AvatarLine;
            public List<AnimationClip> Clips = new List<AnimationClip>();
            public List<string> Problems = new List<string>();
            public bool Approved;
        }

        List<RigRow> rows;
        List<string> results = new List<string>();
        Vector2 scroll;

        [MenuItem("Tools/SA/Animation Library/Audit and Bake")]
        static void Open()
        {
            var window = GetWindow<AnimationLibraryWindow>();
            window.titleContent = new GUIContent("Animation Audit and Bake");
            window.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox("Audit reads the AssetDatabase only and Bake samples hidden previews through the canonical extractor. No scene objects are touched, so Undo is not needed.", MessageType.Info);
            if (GUILayout.Button(new GUIContent("Audit", "List the 3 rigs with clip counts and Avatar state, and tick Approve where the Avatar is valid.")))
                Audit();
            if (rows != null)
            {
                scroll = EditorGUILayout.BeginScrollView(scroll);
                foreach (var row in rows)
                {
                    EditorGUILayout.LabelField(row.RigName + ": " + row.Clips.Count + " clips found at " + row.RigPath, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(row.AvatarLine);
                    foreach (var problem in row.Problems)
                        EditorGUILayout.HelpBox(problem, MessageType.Warning);
                    row.Approved = EditorGUILayout.ToggleLeft(new GUIContent("Approve " + row.RigName, "Bake Approved extracts this rig's clips through the canonical extractor."), row.Approved);
                    EditorGUILayout.Space();
                }
                EditorGUILayout.EndScrollView();
            }
            using (new EditorGUI.DisabledScope(rows == null))
                if (GUILayout.Button(new GUIContent("Bake Approved", "Extract the approved rigs' clips through the canonical extractor and report clips written plus the manifest path.")))
                    Bake();
            foreach (var line in results)
                EditorGUILayout.LabelField(line);
        }

        void Audit()
        {
            rows = new List<RigRow>
            {
                AuditRig(Ual1Path, new[] { "Idle_Loop", "Walk_Loop", "Sprint_Loop", "Jump_Start" }),
                AuditRig(Ual2Path, new[] { "Idle_Rail_Loop", "Walk_Carry_Loop", "Zombie_Walk_Fwd_Loop", "NinjaJump_Start" }),
                AuditKyle()
            };
            results = new List<string>();
            foreach (var row in rows)
            {
                if (row.Problems.Count == 0)
                    Debug.Log("SA: " + row.RigName + ": Audit found " + row.Clips.Count + " clips. " + row.AvatarLine, row.Model);
                else
                    foreach (var problem in row.Problems)
                        Debug.LogWarning("SA: " + row.RigName + ": " + problem, row.Model);
            }
        }

        static RigRow AuditRig(string rigPath, string[] takes)
        {
            var row = new RigRow { RigPath = rigPath, RigName = DisplayName(rigPath) };
            row.Model = AssetDatabase.LoadAssetAtPath<GameObject>(rigPath);
            if (row.Model == null)
            {
                row.Problems.Add(row.RigName + " at " + rigPath + ": model asset missing. Fix: restore the asset, then run Tools/SA/Animation Library/Audit and Bake again.");
                row.AvatarLine = "Avatar: no model to read.";
                row.Approved = false;
                return row;
            }
            var avatar = FindAvatar(rigPath);
            if (avatar == null)
            {
                row.AvatarLine = "Avatar: none found on " + rigPath + ".";
                row.Problems.Add(row.RigName + " at " + rigPath + ": no Avatar. Fix: set the model Rig to Humanoid and press Apply, then run Tools/SA/Animation Library/Audit and Bake again.");
            }
            else if (!avatar.isHuman)
            {
                row.AvatarLine = "Avatar '" + avatar.name + "' is not Humanoid, so it cannot bake.";
                row.Problems.Add(row.RigName + " at " + rigPath + ": Avatar '" + avatar.name + "' is not Humanoid. Fix: change the model Rig Animation Type to Humanoid, then run Tools/SA/Animation Library/Audit and Bake again.");
            }
            else if (!avatar.isValid)
            {
                row.AvatarLine = "Avatar '" + avatar.name + "' is Humanoid but failed validation.";
                row.Problems.Add(row.RigName + " at " + rigPath + ": Avatar '" + avatar.name + "' failed validation. Fix: reimport the model, then run Tools/SA/Animation Library/Audit and Bake again.");
            }
            else
            {
                row.AvatarLine = "Avatar '" + avatar.name + "' is Humanoid and valid.";
            }
            var byName = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(rigPath))
            {
                var take = asset as AnimationClip;
                if (take != null && !byName.ContainsKey(take.name))
                    byName.Add(take.name, take);
            }
            foreach (var take in takes)
            {
                if (!TryFindTake(byName, take, out var clip) || clip == null)
                {
                    row.Problems.Add(row.RigName + " at " + rigPath + ": take '" + take + "' not found. Fix: check the take name in the model import settings, then run Tools/SA/Animation Library/Audit and Bake again.");
                    continue;
                }
                CheckRate(row, clip);
                row.Clips.Add(clip);
            }
            row.Approved = avatar != null && avatar.isHuman && avatar.isValid && row.Clips.Count > 0;
            return row;
        }

        static RigRow AuditKyle()
        {
            var row = new RigRow { RigPath = KyleModelPath, RigName = DisplayName(KyleModelPath) };
            row.Model = AssetDatabase.LoadAssetAtPath<GameObject>(KyleModelPath);
            if (row.Model == null)
            {
                row.Problems.Add(row.RigName + " at " + KyleModelPath + ": model asset missing. Fix: restore the asset, then run Tools/SA/Animation Library/Audit and Bake again.");
                row.AvatarLine = "Avatar: no model to read.";
                row.Approved = false;
                return row;
            }
            var avatar = FindAvatar(KyleModelPath);
            if (avatar == null)
            {
                row.AvatarLine = "Avatar: none found on " + KyleModelPath + ".";
                row.Problems.Add(row.RigName + " at " + KyleModelPath + ": no Avatar. Fix: set the model Rig to Humanoid and press Apply, then run Tools/SA/Animation Library/Audit and Bake again.");
            }
            else if (!avatar.isHuman)
            {
                row.AvatarLine = "Avatar '" + avatar.name + "' is not Humanoid, so it cannot bake.";
                row.Problems.Add(row.RigName + " at " + KyleModelPath + ": Avatar '" + avatar.name + "' is not Humanoid. Fix: change the model Rig Animation Type to Humanoid, then run Tools/SA/Animation Library/Audit and Bake again.");
            }
            else if (!avatar.isValid)
            {
                row.AvatarLine = "Avatar '" + avatar.name + "' is Humanoid but failed validation.";
                row.Problems.Add(row.RigName + " at " + KyleModelPath + ": Avatar '" + avatar.name + "' failed validation. Fix: reimport the model, then run Tools/SA/Animation Library/Audit and Bake again.");
            }
            else
            {
                row.AvatarLine = "Avatar '" + avatar.name + "' is Humanoid and valid.";
            }
            foreach (var animPath in new[] { KyleAnimDir + "Idle.anim", KyleAnimDir + "Kyle_Walk.anim", KyleAnimDir + "Kyle_Run.anim", KyleAnimDir + "Kyle_Sprint.anim" })
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(animPath);
                if (clip == null)
                {
                    row.Problems.Add(row.RigName + " at " + animPath + ": animation missing. Fix: restore the asset, then run Tools/SA/Animation Library/Audit and Bake again.");
                    continue;
                }
                CheckRate(row, clip);
                row.Clips.Add(clip);
            }
            row.Approved = avatar != null && avatar.isHuman && avatar.isValid && row.Clips.Count > 0;
            return row;
        }

        static void CheckRate(RigRow row, AnimationClip clip)
        {
            if (clip.frameRate <= 0f)
                row.Problems.Add(clip.name + " at " + AssetDatabase.GetAssetPath(clip) + ": imported frame rate is zero. Fix: reimport the animation, then run Tools/SA/Animation Library/Audit and Bake again.");
        }

        static bool TryFindTake(Dictionary<string, AnimationClip> byName, string take, out AnimationClip clip)
        {
            if (byName.TryGetValue(take, out clip) && clip != null)
                return true;
            foreach (var pair in byName)
            {
                if (pair.Value == null || pair.Key.StartsWith("__preview__", StringComparison.Ordinal))
                    continue;
                var bar = pair.Key.LastIndexOf('|');
                if (bar >= 0 && string.Equals(pair.Key.Substring(bar + 1), take, StringComparison.Ordinal))
                {
                    clip = pair.Value;
                    return true;
                }
            }
            clip = null;
            return false;
        }

        static Avatar FindAvatar(string rigPath)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(rigPath))
            {
                var avatar = asset as Avatar;
                if (avatar != null)
                    return avatar;
            }
            return null;
        }

        static string DisplayName(string assetPath)
        {
            return System.IO.Path.GetFileNameWithoutExtension(assetPath);
        }

        void Bake()
        {
            if (rows == null)
            {
                Debug.LogWarning("SA: Audit and Bake: nothing to bake. Press Audit first, then press Bake Approved again.");
                return;
            }
            var approved = new List<RigRow>();
            var clips = new List<AnimationClip>();
            foreach (var row in rows)
            {
                if (!row.Approved)
                    continue;
                approved.Add(row);
                foreach (var clip in row.Clips)
                    if (clip != null && !clips.Contains(clip))
                        clips.Add(clip);
            }
            if (approved.Count == 0 || clips.Count == 0)
            {
                Debug.LogWarning("SA: Audit and Bake: no rig approved. Tick Approve on a rig with a valid Avatar, then press Bake Approved again.");
                return;
            }
            var previous = Selection.objects;
            Selection.objects = clips.ToArray();
            EditorUtility.DisplayProgressBar("SA: Baking approved animations", approved.Count + " rigs, " + clips.Count + " clips", 0.5f);
            try
            {
                EditorApplication.ExecuteMenuItem(ExtractMenu);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Selection.objects = previous;
            }
            results = new List<string>();
            foreach (var row in approved)
            {
                var line = row.RigName + ": baked " + row.Clips.Count + " clips. Manifest: " + ManifestPath + ".";
                results.Add(line);
                Debug.Log("SA: " + line + " Open " + ManifestPath + " to review the slice.", row.Model);
            }
        }
    }
}
