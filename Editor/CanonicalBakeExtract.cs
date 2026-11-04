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
                if (!EnsureTakeSettings(rig.RigPath))
                {
                    Debug.LogWarning("SA: " + rig.RigName + ": take settings were reapplied from " + rig.RigPath + ".meta and the model was reimported. Run " + NextControl + " again to bake the corrected takes.", model);
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
                    rig.Clips.RemoveAll(clip => clip == null);
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
                {
                    Debug.LogWarning("SA: " + model.name + ": no animation takes found at " + path + ". Check the model import settings, then run " + NextControl + " again.", model);
                    entries.Add(new SliceEntry { RigPath = path, RigName = DisplayName(path), Clip = null });
                }
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
            if (animator == null)
            {
                animator = preview.AddComponent<Animator>();
                var avatar = FindAvatar(rig.RigPath);
                if (avatar != null)
                    animator.avatar = avatar;
            }
            else if (animator.avatar == null)
            {
                var avatar = FindAvatar(rig.RigPath);
                if (avatar != null)
                    animator.avatar = avatar;
            }
            target = animator != null ? animator.gameObject : preview;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long clipId);
            var sampledP = new Vector3[bones * frames];
            var sampledQ = new Quaternion[bones * frames];
            var sampledS = new Vector3[bones * frames];
            var sampledRootP = new Vector3[frames];
            var sampledRootQ = new Quaternion[frames];
            var previous = new Quaternion[bones];
            for (var b = 0; b < bones; b++)
                previous[b] = Quaternion.identity;
            var previousRoot = Quaternion.identity;
            var worstScale = 0f;
            var worstBone = string.Empty;
            var scaleNormalized = false;
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
                    var position = pose.localPosition;
                    var rotation = pose.localRotation;
                    var raw = pose.localScale;
                    var gap = Math.Max(Math.Abs(raw.x - 1f), Math.Max(Math.Abs(raw.y - 1f), Math.Abs(raw.z - 1f)));
                    if (gap > worstScale)
                    {
                        worstScale = (float)gap;
                        worstBone = AnimationUtility.CalculateTransformPath(skeleton[b], preview.transform);
                    }
                    var scale = SnapScale(raw);
                    if (Math.Abs(scale.x - 1f) > UnitScaleTolerance || Math.Abs(scale.y - 1f) > UnitScaleTolerance || Math.Abs(scale.z - 1f) > UnitScaleTolerance)
                    {
                        scale = Vector3.one;
                        scaleNormalized = true;
                    }
                    if (f > 0 && Quaternion.Dot(rotation, previous[b]) < 0f)
                        rotation = Negate(rotation);
                    previous[b] = rotation;
                    sampledP[f * bones + b] = position;
                    sampledQ[f * bones + b] = rotation;
                    sampledS[f * bones + b] = scale;
                }
                var rootRotation = target.transform.rotation;
                if (f > 0 && Quaternion.Dot(rootRotation, previousRoot) < 0f)
                    rootRotation = Negate(rootRotation);
                previousRoot = rootRotation;
                sampledRootP[f] = target.transform.position;
                sampledRootQ[f] = rootRotation;
            }
            var baseP = sampledRootP[0];
            var baseQi = Quaternion.Inverse(sampledRootQ[0]);
            for (var f = 0; f < frames; f++)
            {
                sampledRootP[f] = baseQi * (sampledRootP[f] - baseP);
                sampledRootQ[f] = baseQi * sampledRootQ[f];
            }
            previousRoot = sampledRootQ[0];
            for (var f = 1; f < frames; f++)
            {
                if (Quaternion.Dot(sampledRootQ[f], previousRoot) < 0f)
                    sampledRootQ[f] = Negate(sampledRootQ[f]);
                previousRoot = sampledRootQ[f];
            }
            var synthetic = !RigBuilder.IsIdentityRoot(sampledP[0], sampledQ[0]);
            var outBones = synthetic ? bones + 1 : bones;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var maskRoot = ReadMaskRootWeight(rig.RigPath, clip.name);
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
            json.Append(",\"loopTime\":").Append(JsonBool(settings.loopTime));
            json.Append(",\"maskRoot\":").Append(maskRoot.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"syntheticRoot\":").Append(JsonBool(synthetic));
            json.Append(",\"scaleNormalized\":").Append(JsonBool(scaleNormalized));
            json.Append(",\"worstScaleGap\":").Append(F(worstScale));
            json.Append(",\"worstScaleBone\":").Append(Quote(worstBone));
            json.Append(",\"avatar\":").Append(Quote(avatarName));
            json.Append(",\"avatarIsHuman\":").Append(JsonBool(isHuman));
            json.Append(",\"avatarIsValid\":").Append(JsonBool(isValid));
            json.Append(",\"bones\":[");
            for (var b = 0; b < outBones; b++)
            {
                if (b > 0)
                    json.Append(',');
                if (synthetic && b == 0)
                {
                    json.Append("{\"path\":").Append(Quote(RigBuilder.SyntheticRootName)).Append(",\"parent\":-1}");
                    continue;
                }
                var source = synthetic ? b - 1 : b;
                var parent = -1;
                var hit = skeleton[source].parent != null && order.TryGetValue(skeleton[source].parent, out parent);
                if (synthetic)
                    parent = hit ? parent + 1 : 0;
                json.Append("{\"path\":").Append(Quote(AnimationUtility.CalculateTransformPath(skeleton[source], preview.transform)));
                json.Append(",\"parent\":").Append(parent.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            json.Append("],\"frames\":[");
            for (var f = 0; f < frames; f++)
            {
                if (f > 0)
                    json.Append(',');
                json.Append("{\"p\":[");
                for (var b = 0; b < outBones; b++)
                {
                    if (b > 0)
                        json.Append(',');
                    var position = synthetic && b == 0 ? Vector3.zero : sampledP[f * bones + (synthetic ? b - 1 : b)];
                    json.Append('[').Append(F(position.x)).Append(',').Append(F(position.y)).Append(',').Append(F(position.z)).Append(']');
                }
                json.Append("],\"q\":[");
                for (var b = 0; b < outBones; b++)
                {
                    if (b > 0)
                        json.Append(',');
                    var rotation = synthetic && b == 0 ? Quaternion.identity : sampledQ[f * bones + (synthetic ? b - 1 : b)];
                    json.Append('[').Append(F(rotation.x)).Append(',').Append(F(rotation.y)).Append(',').Append(F(rotation.z)).Append(',').Append(F(rotation.w)).Append(']');
                }
                json.Append("],\"s\":[");
                for (var b = 0; b < outBones; b++)
                {
                    if (b > 0)
                        json.Append(',');
                    var scale = synthetic && b == 0 ? Vector3.one : sampledS[f * bones + (synthetic ? b - 1 : b)];
                    json.Append('[').Append(F(scale.x)).Append(',').Append(F(scale.y)).Append(',').Append(F(scale.z)).Append(']');
                }
                json.Append("],\"rootP\":[").Append(F(sampledRootP[f].x)).Append(',').Append(F(sampledRootP[f].y)).Append(',').Append(F(sampledRootP[f].z)).Append(']');
                json.Append(",\"rootQ\":[").Append(F(sampledRootQ[f].x)).Append(',').Append(F(sampledRootQ[f].y)).Append(',').Append(F(sampledRootQ[f].z)).Append(',').Append(F(sampledRootQ[f].w)).Append("]}");
            }
            json.Append("]}");
            var relative = Sanitize(rig.RigName) + "/" + Sanitize(clip.name) + ".json";
            var path = Path.Combine(root, "Temp/sa/c08", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json.ToString());
            var travel = (sampledRootP[frames - 1] - sampledRootP[0]).magnitude;
            Debug.Log("SA: " + clip.name + ": wrote " + ("Temp/sa/c08/" + relative) + " with " + frames + " samples at " + F(rate) + " Hz"
                + " syntheticRoot=" + JsonBool(synthetic) + " loopTime=" + JsonBool(settings.loopTime) + " maskRoot=" + maskRoot
                + " scaleNormalized=" + JsonBool(scaleNormalized) + " travel=" + travel.ToString("G6", CultureInfo.InvariantCulture) + "m."
                + " Open " + ManifestPath + " to review the slice.", clip);
            return relative;
        }

        [MenuItem("Tools/SA/Animation Library/Reapply Take Settings From Meta")]
        static void ReapplyTakeSettings()
        {
            var entries = CollectEntries();
            if (entries.Count == 0)
            {
                Debug.LogWarning("SA: Reapply Take Settings From Meta: no animations found. Select a model or animation asset, then run Tools/SA/Animation Library/Reapply Take Settings From Meta again.");
                return;
            }
            var rigs = GroupByRig(entries);
            var corrected = 0;
            foreach (var rig in rigs)
                if (!EnsureTakeSettings(rig.RigPath))
                    corrected++;
            Debug.Log("SA: Reapply Take Settings From Meta: reimported " + corrected + " of " + rigs.Count + " rigs. Run Tools/SA/Animation Library/Extract Canonical Samples again to bake the corrected takes.");
        }

        static bool EnsureTakeSettings(string rigPath)
        {
            var importer = AssetImporter.GetAtPath(rigPath) as ModelImporter;
            if (importer == null)
                return true;
            var changed = false;
            if (importer.avatarSetup == ModelImporterAvatarSetup.CopyFromOther && importer.sourceAvatar == null)
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                changed = true;
            }
            var takes = ParseMetaTakes(rigPath);
            if (takes == null || takes.Count == 0)
                return !changed ? true : ReimportAndRetry(importer);
            var defaults = importer.defaultClipAnimations;
            var basis = defaults.Length > 0 ? defaults : importer.clipAnimations;
            if (basis.Length == 0)
            {
                if (importer.clipAnimations.Length > 0)
                {
                    importer.clipAnimations = new ModelImporterClipAnimation[0];
                    importer.SaveAndReimport();
                    return false;
                }
                return true;
            }
            var current = new Dictionary<string, ModelImporterClipAnimation>(StringComparer.Ordinal);
            foreach (var existing in importer.clipAnimations)
                current[existing.name] = existing;
            TakeSettings single = null;
            if (takes.Count == 1)
                foreach (var pair in takes)
                    single = pair.Value;
            var masks = new Dictionary<string, AvatarMask>(StringComparer.Ordinal);
            foreach (var fallback in basis)
            {
                TakeSettings wanted = null;
                TakeSettings named;
                if (takes.TryGetValue(fallback.name, out named))
                    wanted = named;
                else if (single != null && basis.Length == 1)
                    wanted = single;
                if (wanted == null || !wanted.HasMask)
                    continue;
                ModelImporterClipAnimation probe;
                var probeEntry = current.TryGetValue(fallback.name, out probe) ? probe : fallback;
                if (probeEntry.maskSource != null)
                    continue;
                var built = BuildTakeMask(rigPath, fallback.name, wanted);
                if (built == null)
                    return false;
                masks[fallback.name] = built;
            }
            var next = new List<ModelImporterClipAnimation>(basis.Length);
            foreach (var fallback in basis)
            {
                var entry = fallback;
                ModelImporterClipAnimation overrideEntry;
                if (current.TryGetValue(fallback.name, out overrideEntry))
                    entry = overrideEntry;
                TakeSettings wanted = null;
                TakeSettings named;
                if (takes.TryGetValue(fallback.name, out named))
                    wanted = named;
                else if (single != null && basis.Length == 1)
                    wanted = single;
                if (wanted == null)
                {
                    next.Add(entry);
                    continue;
                }
                if (entry.loopTime != wanted.LoopTime)
                {
                    entry.loopTime = wanted.LoopTime;
                    changed = true;
                }
                AvatarMask built;
                if (masks.TryGetValue(fallback.name, out built))
                {
                    entry.maskType = ClipAnimationMaskType.CopyFromOther;
                    entry.maskSource = built;
                    changed = true;
                }
                next.Add(entry);
            }
            if (!changed)
                return true;
            importer.clipAnimations = next.ToArray();
            return ReimportAndRetry(importer);
        }

        static bool ReimportAndRetry(ModelImporter importer)
        {
            importer.SaveAndReimport();
            return false;
        }

        static AvatarMask BuildTakeMask(string rigPath, string takeName, TakeSettings wanted)
        {
            var mask = new AvatarMask();
            mask.transformCount = wanted.MaskPaths.Count;
            for (var i = 0; i < wanted.MaskPaths.Count; i++)
            {
                mask.SetTransformPath(i, wanted.MaskPaths[i]);
                mask.SetTransformActive(i, wanted.MaskActive[i]);
            }
            var directory = Path.GetDirectoryName(rigPath);
            var maskPath = directory + "/" + Sanitize(DisplayName(rigPath) + "_" + takeName) + ".mask";
            AssetDatabase.DeleteAsset(maskPath);
            AssetDatabase.CreateAsset(mask, maskPath);
            var stored = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
            if (stored == null)
                Debug.LogError("SA: " + takeName + " at " + rigPath + ": take mask asset was not persisted at " + maskPath + ". Fix: run Tools/SA/Animation Library/Reapply Take Settings From Meta again, then rebake.");
            return stored;
        }

        static int ReadMaskRootWeight(string rigPath, string takeName)
        {
            var importer = AssetImporter.GetAtPath(rigPath) as ModelImporter;
            if (importer == null)
                return -1;
            var list = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
            foreach (var take in list)
            {
                if (take.name != takeName)
                    continue;
                var mask = take.maskSource;
                if (mask == null)
                    return -1;
                for (var i = 0; i < mask.transformCount; i++)
                    if (mask.GetTransformPath(i) == "root")
                        return mask.GetTransformActive(i) ? 1 : 0;
                return -1;
            }
            return -1;
        }

        sealed class TakeSettings
        {
            public string Name;
            public bool LoopTime;
            public readonly List<string> MaskPaths = new List<string>();
            public readonly List<bool> MaskActive = new List<bool>();
            public bool HasMask => MaskPaths.Count > 0;
        }

        static Dictionary<string, TakeSettings> ParseMetaTakes(string rigPath)
        {
            var metaPath = Path.Combine(ProjectRoot(), rigPath + ".meta");
            if (!File.Exists(metaPath))
                return null;
            var takes = new Dictionary<string, TakeSettings>(StringComparer.Ordinal);
            TakeSettings take = null;
            string pendingPath = null;
            var inClips = false;
            var inMask = false;
            foreach (var raw in File.ReadAllLines(metaPath))
            {
                var indent = 0;
                while (indent < raw.Length && raw[indent] == ' ')
                    indent++;
                var line = raw.Trim();
                if (!inClips)
                {
                    if (line == "clipAnimations:")
                        inClips = true;
                    continue;
                }
                if (indent < 4)
                    break;
                if (line.StartsWith("- serializedVersion:", StringComparison.Ordinal))
                {
                    if (take != null && take.Name != null)
                        takes[take.Name] = take;
                    take = new TakeSettings();
                    pendingPath = null;
                    inMask = false;
                    continue;
                }
                if (take == null)
                    continue;
                if (line.StartsWith("name: ", StringComparison.Ordinal) && take.Name == null)
                {
                    take.Name = line.Substring("name: ".Length).Trim();
                    continue;
                }
                if (line.StartsWith("loopTime: ", StringComparison.Ordinal))
                {
                    take.LoopTime = line.Substring("loopTime: ".Length).Trim() != "0";
                    continue;
                }
                if (line == "transformMask:")
                {
                    inMask = true;
                    pendingPath = null;
                    continue;
                }
                if (line.StartsWith("maskType:", StringComparison.Ordinal))
                {
                    inMask = false;
                    pendingPath = null;
                    continue;
                }
                if (!inMask)
                    continue;
                if (line.StartsWith("- path:", StringComparison.Ordinal))
                {
                    pendingPath = line.Substring("- path:".Length).Trim();
                    continue;
                }
                if (line.StartsWith("weight:", StringComparison.Ordinal) && pendingPath != null)
                {
                    take.MaskPaths.Add(pendingPath);
                    take.MaskActive.Add(line.Substring("weight:".Length).Trim() != "0");
                    pendingPath = null;
                }
            }
            if (take != null && take.Name != null)
                takes[take.Name] = take;
            return takes;
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
