using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using SA.Animation;
using SA.Animation.Content;
using SA.Motion.Numerics;
using Pose = SA.Motion.Numerics.Pose;
using Quaternion = SA.Motion.Numerics.Quaternion;

namespace SA.Editor
{
    public readonly struct ClipSweepLimits
    {
        public readonly float Degrees;
        public readonly float Metres;
        public readonly float RootMetres;
        public readonly float RootDegrees;

        public ClipSweepLimits(float degrees, float metres, float rootMetres, float rootDegrees)
        {
            Degrees = degrees;
            Metres = metres;
            RootMetres = rootMetres;
            RootDegrees = rootDegrees;
        }

        public static ClipSweepLimits Default => new ClipSweepLimits(1f, 0.01f, 0.01f, 1f);
    }

    public sealed class ClipSweepRow
    {
        public string Clip = string.Empty;
        public string Take = string.Empty;
        public string Character = string.Empty;
        public string Status = string.Empty;
        public string WorstBone = string.Empty;
        public string Reason = string.Empty;
        public string Flags = string.Empty;
        public string MidWorst = string.Empty;
        public int Frames;
        public float Hz;
        public float Degrees;
        public float Centimetres;
        public float RootCentimetres;
        public float RootDegrees;
        public float MidDegrees;
        public float MidCentimetres;
        public float UnityRootCentimetres;
        public double Length;
        public double EncodedDuration;
        public bool Loop;
        public bool EncodedLoop;
        public int Bytes;
        public float Seconds;
        public readonly Dictionary<string, float> BoneDegrees = new Dictionary<string, float>(StringComparer.Ordinal);
    }

    public static class FamilyClipSweep
    {
        public const string Pass = "PASS";
        public const string Fail = "FAIL";
        public const string NonHumanoid = "NONHUMANOID";
        public const string Header = "clip,take,character,frames,hz,status,worst_bone,degrees,cm,root_cm,root_deg,mid_worst,mid_degrees,mid_cm,unity_root_cm,length_s,encoded_s,loop,encoded_loop,bytes,seconds,flags,reason";

        static readonly int[] TrackedPositions = { 1, 6, 13, 32, 50, 54 };

        public static float ClipRate(AnimationClip clip)
        {
            if (clip == null)
                return FamilyClips.DefaultSampleRate;
            var rate = clip.frameRate;
            return rate >= 1f && rate <= 240f && float.IsFinite(rate) ? rate : FamilyClips.DefaultSampleRate;
        }

        public static List<string> FindClipAssets(string[] folders)
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", folders))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    found.Add(path);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Model", folders))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                    continue;
                foreach (var asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                    if (asset is AnimationClip)
                    {
                        found.Add(path);
                        break;
                    }
            }
            return new List<string>(found);
        }

        public static List<AnimationClip> Takes(string path)
        {
            var takes = new List<AnimationClip>();
            if (path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip != null)
                    takes.Add(clip);
                return takes;
            }
            foreach (var asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                    takes.Add(clip);
            takes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return takes;
        }

        public static string NonHumanoidProof(string path, AnimationClip clip)
        {
            var proof = new StringBuilder();
            proof.Append("humanMotion=").Append(clip.humanMotion).Append(" legacy=").Append(clip.legacy);
            if (AssetImporter.GetAtPath(path) is ModelImporter importer)
            {
                proof.Append(" animationType=").Append(importer.animationType);
                var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(path);
                if (avatar == null && importer.sourceAvatar != null)
                    avatar = importer.sourceAvatar;
                proof.Append(" avatar.isHuman=").Append(avatar != null && avatar.isHuman);
            }
            else
            {
                var muscles = 0;
                var transforms = 0;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type == typeof(Animator))
                        muscles++;
                    else if (binding.type == typeof(Transform))
                        transforms++;
                }
                proof.Append(" muscleCurves=").Append(muscles).Append(" transformCurves=").Append(transforms);
            }
            return proof.ToString();
        }

        public static string Describe(string path, AnimationClip clip)
        {
            var flags = new List<string>();
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.loopTime)
                flags.Add("loop");
            if (settings.mirror)
                flags.Add("mirror");
            if (settings.hasAdditiveReferencePose)
                flags.Add("additive");
            if (settings.loopBlendOrientation)
                flags.Add("bakeRotation");
            if (settings.loopBlendPositionY)
                flags.Add("bakeY");
            if (settings.loopBlendPositionXZ)
                flags.Add("bakeXZ");
            var speed = clip.averageSpeed;
            if (speed.magnitude * clip.length > 0.05f || Math.Abs(clip.averageAngularSpeed) * clip.length > 5f)
                flags.Add("rootMotion");
            else
                flags.Add("inPlace");
            if (clip.frameRate >= 59f)
                flags.Add("fps" + Mathf.RoundToInt(clip.frameRate).ToString(CultureInfo.InvariantCulture));
            if (clip.length < 0.2f)
                flags.Add("veryShort");
            if (clip.length > 30f)
                flags.Add("veryLong");
            if (AssetImporter.GetAtPath(path) is ModelImporter importer)
            {
                var clips = importer.clipAnimations;
                if (clips == null || clips.Length == 0)
                    clips = importer.defaultClipAnimations;
                foreach (var take in clips)
                {
                    if (take.name != clip.name)
                        continue;
                    foreach (var info in importer.importedTakeInfos)
                    {
                        if (info.name != take.takeName)
                            continue;
                        var first = info.startTime * info.sampleRate;
                        var last = info.stopTime * info.sampleRate;
                        if (take.firstFrame > first + 0.5f || take.lastFrame < last - 0.5f)
                            flags.Add("subRange");
                    }
                    if (take.hasAdditiveReferencePose && !flags.Contains("additive"))
                        flags.Add("additive");
                }
                if (Math.Abs(importer.globalScale - 1f) > 1e-4f || !importer.useFileScale)
                    flags.Add("importScale");
            }
            var fingers = false;
            var face = false;
            var toes = false;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(Animator))
                    continue;
                var name = binding.propertyName;
                if (name.Contains("Index") || name.Contains("Thumb") || name.Contains("Middle") || name.Contains("Ring") || name.Contains("Little"))
                    fingers = true;
                if (name.Contains("Jaw") || name.Contains("Eye"))
                    face = true;
                if (name.Contains("Toes"))
                    toes = true;
            }
            flags.Add(fingers ? "fingerCurves" : "noFingerCurves");
            if (face)
                flags.Add("faceCurves");
            if (toes)
                flags.Add("toeCurves");
            return string.Join(";", flags);
        }

        public static bool TryMeasure(FamilyRigPlan plan, AnimationClip clip, float hz, float scale, in ClipSweepLimits limits, ClipSweepRow row, out string error)
        {
            error = string.Empty;
            var started = System.Diagnostics.Stopwatch.StartNew();
            row.Hz = hz;
            row.Length = clip.length;
            row.Loop = clip.isLooping;
            if (!FamilyClips.TrySample(plan, clip, hz, out var canonical, out var root, out var start, out var frames, out error))
                return false;
            row.Frames = frames;
            var duration = FamilyClips.EncodedDuration(clip, hz);
            if (!FamilyClips.TryEncode(plan, canonical, root, duration, clip.isLooping, false, out var bytes, out error))
                return false;
            row.Bytes = bytes.Length;
            var reference = SharedFamilyBake.CanonicalReferencePoses();
            if (SharedClipReader.Load(bytes, plan.Family, reference, out var loaded) != AnimationError.None || loaded == null)
            {
                error = "encoded clip failed to reload.";
                return false;
            }
            row.EncodedDuration = loaded.Duration;
            row.EncodedLoop = loaded.Loop;
            if (loaded.SampleCount != frames)
            {
                error = "encoded clip has " + loaded.SampleCount + " samples for " + frames + " frames.";
                return false;
            }
            var copy = UnityEngine.Object.Instantiate(plan.character);
            copy.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                if (!Measure(plan, clip, copy, scale, loaded, reference, start, frames, row, out error))
                    return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
            var reasons = new List<string>();
            if (row.Degrees > limits.Degrees)
                reasons.Add("rotation " + F(row.Degrees) + " deg on " + row.WorstBone);
            if (row.Centimetres > limits.Metres * 100f)
                reasons.Add("position " + F(row.Centimetres) + " cm");
            if (row.RootCentimetres > limits.RootMetres * 100f)
                reasons.Add("root path " + F(row.RootCentimetres) + " cm");
            if (row.RootDegrees > limits.RootDegrees)
                reasons.Add("heading " + F(row.RootDegrees) + " deg");
            if (Math.Abs(row.EncodedDuration - duration) > 1e-6)
                reasons.Add("duration " + row.EncodedDuration.ToString("0.######", CultureInfo.InvariantCulture) + " s for " + duration.ToString("0.######", CultureInfo.InvariantCulture) + " s");
            if (row.EncodedLoop != row.Loop)
                reasons.Add("loop flag " + row.EncodedLoop + " for " + row.Loop);
            if (!float.IsFinite(row.Degrees) || !float.IsFinite(row.Centimetres) || !float.IsFinite(row.RootCentimetres) || !float.IsFinite(row.RootDegrees))
                reasons.Add("non-finite result");
            row.Status = reasons.Count == 0 ? Pass : Fail;
            row.Reason = string.Join("; ", reasons);
            row.Seconds = (float)started.Elapsed.TotalSeconds;
            return true;
        }

        static bool Measure(FamilyRigPlan plan, AnimationClip clip, GameObject copy, float scale, SharedClip loaded, Pose[] reference, RigidPose start,
            int frames, ClipSweepRow row, out string error)
        {
            error = string.Empty;
            var count = plan.bones.Length;
            var top = copy.transform;
            top.localPosition = Vector3.zero;
            top.localRotation = UnityEngine.Quaternion.identity;
            top.localScale = new Vector3(scale, scale, scale);
            var all = copy.GetComponentsInChildren<Transform>(true);
            var restPositions = new Vector3[all.Length];
            var restRotations = new UnityEngine.Quaternion[all.Length];
            var restScales = new Vector3[all.Length];
            for (var i = 0; i < all.Length; i++)
            {
                restPositions[i] = all[i].localPosition;
                restRotations[i] = all[i].localRotation;
                restScales[i] = all[i].localScale;
            }
            var mapped = new Transform[count];
            mapped[0] = top;
            for (var b = 1; b < count; b++)
            {
                mapped[b] = top.Find(plan.paths[b]);
                if (mapped[b] == null)
                {
                    error = "bone '" + plan.paths[b] + "' is missing on the ground-truth copy.";
                    return false;
                }
            }
            var animator = copy.GetComponentInChildren<Animator>(true);
            var driven = animator != null ? animator.gameObject : copy;
            var canonicalFrame = new Pose[FamilyClips.CanonicalBones];
            var placedRoot = new RigidPose[1];
            var model = new RigidPose[count];
            var truthModel = new RigidPose[count];
            var truthLocal = new Pose[count];
            var rootTruth = new RigidPose[frames];
            var rootLocals = new Pose[frames * count];
            var steps = 2 * frames - 1;
            var duration = loaded.Duration;
            for (var s = 0; s < steps; s++)
            {
                var mid = (s & 1) != 0;
                var frame = s >> 1;
                var seconds = mid ? duration * (frame + 0.5) / (frames - 1) : duration * frame / (frames - 1);
                var clipSeconds = clip.length > 0f ? Math.Min(clip.length, seconds) : 0.0;
                if (loaded.Loop && frame == frames - 1)
                    seconds = duration * (1.0 - 1e-9);
                for (var i = 0; i < all.Length; i++)
                {
                    all[i].localPosition = restPositions[i];
                    all[i].localRotation = restRotations[i];
                    all[i].localScale = restScales[i];
                }
                top.localScale = new Vector3(scale, scale, scale);
                clip.SampleAnimation(driven, (float)clipSeconds);
                if (!mid && frame == frames - 1)
                    row.UnityRootCentimetres = top.localPosition.magnitude * 100f;
                for (var b = 0; b < count; b++)
                {
                    var position = mapped[b].position / scale;
                    var rotation = mapped[b].rotation;
                    truthModel[b] = new RigidPose(new Float3(position.x, position.y, position.z),
                        Quaternion.Normalize(new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w)));
                }
                if (loaded.Sample(seconds, reference, canonicalFrame) != AnimationError.None)
                {
                    error = "decode failed at " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s.";
                    return false;
                }
                if (!RootAt(loaded, seconds, out var rootPose) || !RigidMath.TryCompose(start, rootPose, out placedRoot[0]))
                {
                    error = "root is invalid at " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s.";
                    return false;
                }
                if (!FamilyClips.TryApply(plan, canonicalFrame, placedRoot, out var locals, out error))
                    return false;
                if (RigidMath.Model(plan.parents, locals, model) != AnimationError.None)
                {
                    error = "applied pose failed to compose at " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s.";
                    return false;
                }
                for (var b = 1; b < count; b++)
                {
                    var c = plan.canonicalOfBone[b];
                    if (c <= 0)
                        continue;
                    var degrees = Degrees(truthModel[b].Rotation, model[b].Rotation);
                    if (!float.IsFinite(degrees))
                        degrees = float.PositiveInfinity;
                    if (mid)
                    {
                        if (degrees > row.MidDegrees || float.IsInfinity(degrees))
                        {
                            row.MidDegrees = degrees;
                            row.MidWorst = SharedFamilyBake.CanonicalNames[c] + "@" + frame.ToString(CultureInfo.InvariantCulture) + ".5";
                        }
                        continue;
                    }
                    var name = SharedFamilyBake.CanonicalNames[c];
                    if (!row.BoneDegrees.TryGetValue(name, out var worst) || degrees > worst)
                        row.BoneDegrees[name] = degrees;
                    if (degrees > row.Degrees || float.IsInfinity(degrees))
                    {
                        row.Degrees = degrees;
                        row.WorstBone = name + "@" + frame.ToString(CultureInfo.InvariantCulture);
                    }
                }
                foreach (var c in TrackedPositions)
                {
                    var b = plan.boneOfCanonical[c];
                    if (b < 0)
                        continue;
                    var centimetres = SharedFamilyBake.Distance(truthModel[b].Position, model[b].Position) * 100f * scale;
                    if (!float.IsFinite(centimetres))
                        centimetres = float.PositiveInfinity;
                    if (mid)
                        row.MidCentimetres = Math.Max(row.MidCentimetres, centimetres);
                    else
                        row.Centimetres = Math.Max(row.Centimetres, centimetres);
                }
                if (mid)
                    continue;
                rootTruth[frame] = placedRoot[0];
                if (!TruthLocals(plan, truthModel, truthLocal))
                {
                    error = "ground truth is not a rigid pose at frame " + frame + ".";
                    return false;
                }
                Array.Copy(truthLocal, 0, rootLocals, frame * count, count);
            }
            if (!FamilyClips.TryCanonical(plan, rootLocals, frames, out _, out var truthRoot, out var truthStart, out error))
                return false;
            for (var f = 0; f < frames; f++)
            {
                if (!RigidMath.TryCompose(truthStart, truthRoot[f], out var expected))
                {
                    error = "ground-truth root is invalid at frame " + f + ".";
                    return false;
                }
                var centimetres = SharedFamilyBake.Distance(expected.Position, rootTruth[f].Position) * 100f * scale;
                var degrees = Degrees(expected.Rotation, rootTruth[f].Rotation);
                row.RootCentimetres = Math.Max(row.RootCentimetres, float.IsFinite(centimetres) ? centimetres : float.PositiveInfinity);
                row.RootDegrees = Math.Max(row.RootDegrees, float.IsFinite(degrees) ? degrees : float.PositiveInfinity);
            }
            return true;
        }

        static bool TruthLocals(FamilyRigPlan plan, RigidPose[] truthModel, Pose[] truthLocal)
        {
            truthLocal[0] = Pose.Identity;
            for (var b = 1; b < truthModel.Length; b++)
            {
                var parent = plan.parents[b] <= 0 ? RigidPose.Identity : truthModel[plan.parents[b]];
                if (!RigidMath.TryInverse(parent, out var inverse) || !RigidMath.TryCompose(inverse, truthModel[b], out var local))
                    return false;
                truthLocal[b] = new Pose { Position = local.Position, Rotation = Quaternion.Normalize(local.Rotation), Scale = Float3.One };
            }
            return true;
        }

        public static bool RootAt(SharedClip clip, double seconds, out RigidPose root)
        {
            root = RigidPose.Identity;
            if (clip == null || clip.Root.Length == 0)
                return false;
            if (SampleLocator.Locate(clip.Duration, clip.SampleCount, seconds, false, out var window) != AnimationError.None)
                return false;
            var a = clip.Root[window.First];
            var b = clip.Root[window.Second];
            var t = window.Alpha;
            var qa = new UnityEngine.Quaternion(a.Rotation.X, a.Rotation.Y, a.Rotation.Z, a.Rotation.W);
            var qb = new UnityEngine.Quaternion(b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W);
            var q = UnityEngine.Quaternion.Slerp(qa, qb, t);
            root = new RigidPose(a.Position + (b.Position - a.Position) * t, Quaternion.Normalize(new Quaternion(q.x, q.y, q.z, q.w)));
            return true;
        }

        public static float Degrees(Quaternion a, Quaternion b)
        {
            var x = (double)a.X;
            var y = (double)a.Y;
            var z = (double)a.Z;
            var w = (double)a.W;
            var la = Math.Sqrt(x * x + y * y + z * z + w * w);
            var lb = Math.Sqrt((double)b.X * b.X + (double)b.Y * b.Y + (double)b.Z * b.Z + (double)b.W * b.W);
            var rw = (w * b.W + x * b.X + y * b.Y + z * b.Z) / (la * lb);
            var rx = (w * b.X - x * b.W - y * b.Z + z * b.Y) / (la * lb);
            var ry = (w * b.Y + x * b.Z - y * b.W - z * b.X) / (la * lb);
            var rz = (w * b.Z - x * b.Y + y * b.X - z * b.W) / (la * lb);
            return (float)(2.0 * Math.Atan2(Math.Sqrt(rx * rx + ry * ry + rz * rz), Math.Abs(rw)) * 180.0 / Math.PI);
        }

        public static string ToCsv(ClipSweepRow row)
        {
            var text = new StringBuilder();
            text.Append(Quote(row.Clip)).Append(',').Append(Quote(row.Take)).Append(',').Append(Quote(row.Character)).Append(',')
                .Append(row.Frames.ToString(CultureInfo.InvariantCulture)).Append(',').Append(F(row.Hz)).Append(',').Append(row.Status).Append(',')
                .Append(Quote(row.WorstBone)).Append(',').Append(F(row.Degrees)).Append(',').Append(F(row.Centimetres)).Append(',')
                .Append(F(row.RootCentimetres)).Append(',').Append(F(row.RootDegrees)).Append(',').Append(Quote(row.MidWorst)).Append(',').Append(F(row.MidDegrees)).Append(',')
                .Append(F(row.MidCentimetres)).Append(',').Append(F(row.UnityRootCentimetres)).Append(',')
                .Append(row.Length.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.EncodedDuration.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Loop ? '1' : '0').Append(',').Append(row.EncodedLoop ? '1' : '0').Append(',')
                .Append(row.Bytes.ToString(CultureInfo.InvariantCulture)).Append(',').Append(F(row.Seconds)).Append(',')
                .Append(Quote(row.Flags)).Append(',').Append(Quote(row.Reason));
            return text.ToString();
        }

        public static string F(float value) => float.IsFinite(value) ? value.ToString("0.#####", CultureInfo.InvariantCulture) : "inf";

        static string Quote(string value)
        {
            value ??= string.Empty;
            return "\"" + value.Replace("\"", "\"\"").Replace("\n", " ") + "\"";
        }

        public static string Run(string[] characters, float[] scales, string[] folders, string csvPath, int first, int count, bool append)
        {
            var limits = ClipSweepLimits.Default;
            var plans = new List<FamilyRigPlan>();
            var labels = new List<string>();
            var planScales = new List<float>();
            var instances = new List<GameObject>();
            var report = new StringBuilder();
            try
            {
                for (var i = 0; i < characters.Length; i++)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(characters[i]);
                    if (asset == null)
                    {
                        report.Append("character missing: ").Append(characters[i]).Append('\n');
                        continue;
                    }
                    var instance = UnityEngine.Object.Instantiate(asset);
                    instance.name = asset.name;
                    instance.hideFlags = HideFlags.HideAndDontSave;
                    instances.Add(instance);
                    if (!FamilyClips.TryPlan(instance, out var plan, out var error))
                    {
                        report.Append("plan failed for ").Append(characters[i]).Append(": ").Append(error).Append('\n');
                        continue;
                    }
                    plans.Add(plan);
                    var scale = scales != null && i < scales.Length && scales[i] > 0f ? scales[i] : 1f;
                    planScales.Add(scale);
                    labels.Add(asset.name + (Math.Abs(scale - 1f) > 1e-6f ? "@x" + scale.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty));
                }
                var assets = FindClipAssets(folders);
                Directory.CreateDirectory(Path.GetDirectoryName(csvPath));
                using var writer = new StreamWriter(csvPath, append);
                if (!append)
                    writer.WriteLine(Header);
                var last = Math.Min(assets.Count, first + count);
                var rows = 0;
                for (var a = first; a < last; a++)
                {
                    var path = assets[a];
                    foreach (var clip in Takes(path))
                    {
                        if (!clip.humanMotion)
                        {
                            var skip = new ClipSweepRow { Clip = path, Take = clip.name, Character = "-", Status = NonHumanoid, Length = clip.length, Loop = clip.isLooping };
                            skip.Reason = NonHumanoidProof(path, clip);
                            writer.WriteLine(ToCsv(skip));
                            rows++;
                            continue;
                        }
                        var flags = Describe(path, clip);
                        for (var p = 0; p < plans.Count; p++)
                        {
                            var row = new ClipSweepRow { Clip = path, Take = clip.name, Character = labels[p], Flags = flags };
                            try
                            {
                                if (!TryMeasure(plans[p], clip, ClipRate(clip), planScales[p], limits, row, out var error))
                                {
                                    row.Status = Fail;
                                    row.Reason = "error: " + error;
                                }
                            }
                            catch (Exception exception)
                            {
                                row.Status = Fail;
                                row.Reason = "exception: " + exception.GetType().Name + " " + exception.Message;
                            }
                            writer.WriteLine(ToCsv(row));
                            rows++;
                        }
                    }
                    writer.Flush();
                }
                report.Append("assets ").Append(first).Append("..").Append(last).Append(" of ").Append(assets.Count).Append(", rows ").Append(rows).Append('\n');
            }
            finally
            {
                foreach (var instance in instances)
                    if (instance != null)
                        UnityEngine.Object.DestroyImmediate(instance);
            }
            return report.ToString();
        }
    }
}
