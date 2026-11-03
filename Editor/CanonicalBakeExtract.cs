using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SA.Editor
{
    public static class CanonicalBakeExtract
    {
        const string NextControl = "Tools/SA/Animation Library/Extract Canonical Samples";
        const string ManifestPath = "Temp/sa/c08/_manifest.json";
        const float MaxRate = 60f;
        const float UnitScaleTolerance = 1e-5f;

        const string Ual1Path = "Assets/UAL/UAL1_Standard.fbx";
        const string Ual2Path = "Assets/UAL/UAL2_Standard.fbx";
        static readonly string[] KyleAnimNames = { "Idle.anim", "Kyle_Walk.anim", "Kyle_Run.anim", "Kyle_Sprint.anim" };

        static string FindSampleAssetPath(string fileName)
        {
            foreach (var guid in AssetDatabase.FindAssets(System.IO.Path.GetFileNameWithoutExtension(fileName)))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("/" + fileName, StringComparison.Ordinal))
                {
                    return path;
                }
            }
            return "";
        }

        sealed class SliceEntry
        {
            public string RigPath;
            public string RigName;
            public AnimationClip Clip;
        }

        sealed class RigSlice
        {
            public string RigPath;
            public string RigName;
            public List<AnimationClip> Clips = new List<AnimationClip>();
        }

        [MenuItem("Tools/SA/Animation Library/Extract Canonical Samples")]
        static void Extract()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            var entries = CollectEntries();
            if (entries.Count == 0)
            {
                Debug.LogWarning("SA: Extract Canonical Samples: no animations found. Select a model or animation asset, or clear the selection to extract the default slice, then run " + NextControl + " again.");
                return;
            }
            var rigs = GroupByRig(entries);
            var root = ProjectRoot();
            var wrote = 0;
            var rigJson = new List<string>();
            var clipJson = new List<string>();
            foreach (var rig in rigs)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(rig.RigPath);
                if (model == null)
                {
                    Debug.LogWarning("SA: " + rig.RigName + ": model asset missing at " + rig.RigPath + ". Restore the asset, then run " + NextControl + " again.");
                    continue;
                }
                var avatar = FindAvatar(rig.RigPath);
                var avatarName = avatar != null ? avatar.name : string.Empty;
                var isHuman = avatar != null && avatar.isHuman;
                var isValid = avatar != null && avatar.isValid;
                if (avatar == null)
                    Debug.LogWarning("SA: " + rig.RigName + ": no Avatar on " + rig.RigPath + ". Set the model Rig to Humanoid and press Apply, then run " + NextControl + " again.", model);
                else if (!isHuman)
                    Debug.LogWarning("SA: " + rig.RigName + ": Avatar '" + avatarName + "' is not Humanoid. Change the model Rig Animation Type to Humanoid, then run " + NextControl + " again.", model);
                else if (!isValid)
                    Debug.LogWarning("SA: " + rig.RigName + ": Avatar '" + avatarName + "' failed validation. Reimport the model, then run " + NextControl + " again.", model);
                else
                    Debug.Log("SA: " + rig.RigName + ": Avatar '" + avatarName + "' is Humanoid and valid. Sampling " + rig.Clips.Count + " clips.", model);
                var preview = (GameObject)UnityEngine.Object.Instantiate(model);
                preview.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    var errors = new List<string>();
                    var skeleton = RigBuilder.Skeleton(preview.transform, rig.Clips, new List<SkinAuthoring>(), errors);
                    foreach (var error in errors)
                        Debug.LogWarning("SA: " + rig.RigName + ": " + error + " Fix the rig, then run " + NextControl + " again.", model);
                    var animator = preview.GetComponentInChildren<Animator>(true);
                    var target = animator != null ? animator.gameObject : preview;
                    var clipNames = new List<string>();
                    foreach (var clip in rig.Clips)
                    {
                        if (clip == null)
                            continue;
                        var file = WriteClip(root, rig, clip, avatarName, isHuman, isValid, preview, target, animator, skeleton);
                        if (string.IsNullOrEmpty(file))
                            continue;
                        wrote++;
                        clipNames.Add(Quote(clip.name));
                        clipJson.Add("{\"clip\":" + Quote(clip.name) + ",\"rig\":" + Quote(rig.RigName) + ",\"file\":" + Quote(file) + "}");
                    }
                    rigJson.Add("{\"rig\":" + Quote(rig.RigName) + ",\"rigGuid\":" + Quote(AssetDatabase.AssetPathToGUID(rig.RigPath)) + ",\"rigPath\":" + Quote(rig.RigPath) + ",\"avatar\":" + Quote(avatarName) + ",\"avatarIsHuman\":" + JsonBool(isHuman) + ",\"avatarIsValid\":" + JsonBool(isValid) + ",\"clips\":[" + string.Join(",", clipNames) + "]}");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(preview);
                }
            }
            Directory.CreateDirectory(Path.Combine(root, "Temp/sa/c08"));
            File.WriteAllText(Path.Combine(root, ManifestPath), "{\"tool\":\"Extract Canonical Samples\",\"rigs\":[" + string.Join(",", rigJson) + "],\"clips\":[" + string.Join(",", clipJson) + "]}");
            if (EditorSceneManager.GetActiveScene().isDirty && !wasDirty)
                Debug.LogWarning("SA: Extract Canonical Samples: the open scene was marked dirty. Save or revert the scene, then run " + NextControl + " again.");
            Debug.Log("SA: Extract Canonical Samples: wrote " + wrote + " of " + entries.Count + " clips to " + ManifestPath + ". Open the manifest to review the slice.");
        }

        static List<SliceEntry> CollectEntries()
        {
            var clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets);
            var models = Selection.GetFiltered<GameObject>(SelectionMode.Assets);
            if (clips.Length == 0 && models.Length == 0)
                return DefaultSlice();
            var entries = new List<SliceEntry>();
            var fallbackRig = models.Length > 0 ? AssetDatabase.GetAssetPath(models[0]) : FindSampleAssetPath("Robot Kyle.fbx");
            foreach (var clip in clips)
            {
                var path = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(path))
                {
                    Debug.LogWarning("SA: " + clip.name + ": selection is not an asset. Select the animation asset in the Project window, then run " + NextControl + " again.", clip);
                    continue;
                }
                var rigPath = path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ? path : fallbackRig;
                entries.Add(new SliceEntry { RigPath = rigPath, RigName = DisplayName(rigPath), Clip = clip });
            }
            foreach (var model in models)
            {
                var path = AssetDatabase.GetAssetPath(model);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning("SA: " + model.name + ": selection is not a model asset. Select the model asset in the Project window, then run " + NextControl + " again.", model);
                    continue;
                }
                var covered = false;
                foreach (var entry in entries)
                    if (entry.RigPath == path)
                        covered = true;
                if (covered)
                    continue;
                var added = 0;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    var take = asset as AnimationClip;
                    if (take == null || take.name.StartsWith("__preview__", StringComparison.Ordinal))
                        continue;
                    entries.Add(new SliceEntry { RigPath = path, RigName = DisplayName(path), Clip = take });
                    added++;
                }
                if (added == 0)
                    Debug.LogWarning("SA: " + model.name + ": no animation takes found at " + path + ". Check the model import settings, then run " + NextControl + " again.", model);
            }
            return entries;
        }

        static List<SliceEntry> DefaultSlice()
        {
            var entries = new List<SliceEntry>();
            AddTakes(entries, Ual1Path, new[] { "Idle_Loop", "Walk_Loop", "Sprint_Loop", "Jump_Start" });
            AddTakes(entries, Ual2Path, new[] { "Idle_Rail_Loop", "Walk_Carry_Loop", "Zombie_Walk_Fwd_Loop", "NinjaJump_Start" });
            var kyleModel = FindSampleAssetPath("Robot Kyle.fbx");
            if (string.IsNullOrEmpty(kyleModel))
            {
                Debug.LogWarning("SA: Robot Kyle.fbx was not found by name. Fix: restore the Robot Kyle sample model, then run " + NextControl + " again.");
                return entries;
            }
            var anims = new List<string>();
            foreach (var anim in KyleAnimNames)
            {
                var found = FindSampleAssetPath(anim);
                if (string.IsNullOrEmpty(found))
                {
                    Debug.LogWarning("SA: " + anim + " was not found by name. Fix: restore the Robot Kyle sample animations, then run " + NextControl + " again.");
                    continue;
                }
                anims.Add(found);
            }
            AddAnims(entries, kyleModel, anims.ToArray());
            return entries;
        }

        static void AddTakes(List<SliceEntry> entries, string rigPath, string[] takes)
        {
            var rigName = DisplayName(rigPath);
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
                    Debug.LogWarning("SA: " + rigName + ": take '" + take + "' not found at " + rigPath + ". Check the take name in the model import settings, then run " + NextControl + " again.");
                    continue;
                }
                entries.Add(new SliceEntry { RigPath = rigPath, RigName = rigName, Clip = clip });
            }
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

        static void AddAnims(List<SliceEntry> entries, string rigPath, string[] animPaths)
        {
            var rigName = DisplayName(rigPath);
            foreach (var animPath in animPaths)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(animPath);
                if (clip == null)
                {
                    Debug.LogWarning("SA: " + rigName + ": animation missing at " + animPath + ". Restore the asset, then run " + NextControl + " again.");
                    continue;
                }
                entries.Add(new SliceEntry { RigPath = rigPath, RigName = rigName, Clip = clip });
            }
        }

        static List<RigSlice> GroupByRig(List<SliceEntry> entries)
        {
            var rigs = new List<RigSlice>();
            var index = new Dictionary<string, RigSlice>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!index.TryGetValue(entry.RigPath, out var rig))
                {
                    rig = new RigSlice { RigPath = entry.RigPath, RigName = entry.RigName };
                    index.Add(entry.RigPath, rig);
                    rigs.Add(rig);
                }
                if (!rig.Clips.Contains(entry.Clip))
                    rig.Clips.Add(entry.Clip);
            }
            return rigs;
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

        static string WriteClip(string root, RigSlice rig, AnimationClip clip, string avatarName, bool isHuman, bool isValid, GameObject preview, GameObject target, Animator animator, List<Transform> skeleton)
        {
            var rate = clip.frameRate > MaxRate ? MaxRate : clip.frameRate;
            if (rate <= 0f)
            {
                Debug.LogWarning("SA: " + clip.name + ": imported frame rate is zero. Reimport the animation, then run " + NextControl + " again.", clip);
                return null;
            }
            var bones = skeleton.Count;
            if (bones == 0)
            {
                Debug.LogWarning("SA: " + clip.name + ": skeleton is empty under " + rig.RigName + ". Fix the rig errors above, then run " + NextControl + " again.", clip);
                return null;
            }
            var frames = Mathf.Max(2, Mathf.CeilToInt(clip.length * rate) + 1);
            var order = new Dictionary<Transform, int>(bones);
            for (var b = 0; b < bones; b++)
                order[skeleton[b]] = b;
            var mapped = new Transform[bones];
            for (var b = 0; b < bones; b++)
                mapped[b] = preview.transform.Find(AnimationUtility.CalculateTransformPath(skeleton[b], preview.transform));
            var all = preview.GetComponentsInChildren<Transform>(true);
            var restPosition = new Vector3[all.Length];
            var restRotation = new Quaternion[all.Length];
            var restScale = new Vector3[all.Length];
            for (var i = 0; i < all.Length; i++)
            {
                restPosition[i] = all[i].localPosition;
                restRotation[i] = all[i].localRotation;
                restScale[i] = all[i].localScale;
            }
            var useBody = animator != null && isHuman;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long clipId);
            var json = new StringBuilder(1024 + frames * bones * 64);
            json.Append("{\"rig\":").Append(Quote(rig.RigName));
            json.Append(",\"rigGuid\":").Append(Quote(AssetDatabase.AssetPathToGUID(rig.RigPath)));
            json.Append(",\"rigPath\":").Append(Quote(rig.RigPath));
            json.Append(",\"clip\":").Append(Quote(clip.name));
            json.Append(",\"clipId\":").Append(Quote(clipId.ToString(CultureInfo.InvariantCulture)));
            json.Append(",\"sampleRate\":").Append(F(rate));
            json.Append(",\"sampleCount\":").Append(frames.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"duration\":").Append(F(clip.length));
            json.Append(",\"loop\":").Append(JsonBool(clip.isLooping));
            json.Append(",\"avatar\":").Append(Quote(avatarName));
            json.Append(",\"avatarIsHuman\":").Append(JsonBool(isHuman));
            json.Append(",\"avatarIsValid\":").Append(JsonBool(isValid));
            var positions = new Vector3[bones];
            var rotations = new Quaternion[bones];
            var scales = new Vector3[bones];
            var previous = new Quaternion[bones];
            for (var b = 0; b < bones; b++)
                previous[b] = Quaternion.identity;
            json.Append(",\"bones\":[");
            for (var b = 0; b < bones; b++)
            {
                if (b > 0)
                    json.Append(',');
                var parent = -1;
                if (skeleton[b].parent != null)
                    order.TryGetValue(skeleton[b].parent, out parent);
                json.Append("{\"path\":").Append(Quote(AnimationUtility.CalculateTransformPath(skeleton[b], preview.transform)));
                json.Append(",\"parent\":").Append(parent.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            json.Append("],\"frames\":[");
            for (var f = 0; f < frames; f++)
            {
                for (var i = 0; i < all.Length; i++)
                {
                    all[i].localPosition = restPosition[i];
                    all[i].localRotation = restRotation[i];
                    all[i].localScale = restScale[i];
                }
                clip.SampleAnimation(target, clip.length * f / (frames - 1));
                for (var b = 0; b < bones; b++)
                {
                    var pose = mapped[b] != null ? mapped[b] : skeleton[b];
                    positions[b] = pose.localPosition;
                    rotations[b] = pose.localRotation;
                    scales[b] = SnapScale(pose.localScale);
                    if (f > 0 && Quaternion.Dot(rotations[b], previous[b]) < 0f)
                        rotations[b] = Negate(rotations[b]);
                    previous[b] = rotations[b];
                }
                if (f > 0)
                    json.Append(',');
                json.Append("{\"p\":[");
                for (var b = 0; b < bones; b++)
                {
                    if (b > 0)
                        json.Append(',');
                    json.Append('[').Append(F(positions[b].x)).Append(',').Append(F(positions[b].y)).Append(',').Append(F(positions[b].z)).Append(']');
                }
                json.Append("],\"q\":[");
                for (var b = 0; b < bones; b++)
                {
                    if (b > 0)
                        json.Append(',');
                    json.Append('[').Append(F(rotations[b].x)).Append(',').Append(F(rotations[b].y)).Append(',').Append(F(rotations[b].z)).Append(',').Append(F(rotations[b].w)).Append(']');
                }
                json.Append("],\"s\":[");
                for (var b = 0; b < bones; b++)
                {
                    if (b > 0)
                        json.Append(',');
                    json.Append('[').Append(F(scales[b].x)).Append(',').Append(F(scales[b].y)).Append(',').Append(F(scales[b].z)).Append(']');
                }
                Vector3 rootPosition;
                Quaternion rootRotation;
                if (useBody)
                {
                    rootPosition = animator.bodyPosition;
                    rootRotation = animator.bodyRotation;
                }
                else
                {
                    rootPosition = target.transform.position;
                    rootRotation = target.transform.rotation;
                }
                json.Append("],\"rootP\":[").Append(F(rootPosition.x)).Append(',').Append(F(rootPosition.y)).Append(',').Append(F(rootPosition.z)).Append(']');
                json.Append(",\"rootQ\":[").Append(F(rootRotation.x)).Append(',').Append(F(rootRotation.y)).Append(',').Append(F(rootRotation.z)).Append(',').Append(F(rootRotation.w)).Append("]}");
            }
            json.Append("]}");
            var relative = Sanitize(rig.RigName) + "/" + Sanitize(clip.name) + ".json";
            var path = Path.Combine(root, "Temp/sa/c08", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json.ToString());
            Debug.Log("SA: " + clip.name + ": wrote " + ("Temp/sa/c08/" + relative) + " with " + frames + " samples at " + F(rate) + " Hz. Open " + ManifestPath + " to review the slice.", clip);
            return relative;
        }

        static Vector3 SnapScale(Vector3 scale)
        {
            return new Vector3(SnapUnit(scale.x), SnapUnit(scale.y), SnapUnit(scale.z));
        }

        static float SnapUnit(float value)
        {
            return Mathf.Abs(value - 1f) <= UnitScaleTolerance ? 1f : value;
        }

        static Quaternion Negate(Quaternion rotation)
        {
            return new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
        }

        static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        static string DisplayName(string assetPath)
        {
            return Path.GetFileNameWithoutExtension(assetPath);
        }

        static string Sanitize(string name)
        {
            foreach (var bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            return name;
        }

        static string Quote(string text)
        {
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        static string JsonBool(bool value)
        {
            return value ? "true" : "false";
        }

        static string F(float value)
        {
            return value.ToString("G9", CultureInfo.InvariantCulture);
        }
    }
}
