using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SA.Editor;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SA.Conformance
{
    [Serializable]
    public sealed class ConformanceManifest
    {
        public string Folder = string.Empty;
        public List<string> Characters = new List<string>();
        public List<string> ClipPaths = new List<string>();
        public List<string> ClipNames = new List<string>();

        public string Path => Folder + "/conformance.json";

        public static ConformanceManifest Load(string folder)
        {
            var path = folder + "/conformance.json";
            return File.Exists(path) ? JsonUtility.FromJson<ConformanceManifest>(File.ReadAllText(path)) : null;
        }

        public List<AnimationClip> Clips()
        {
            var clips = new List<AnimationClip>();
            for (var i = 0; i < ClipPaths.Count; i++)
                clips.Add(FindClip(ClipPaths[i], ClipNames[i]));
            return clips;
        }

        public static AnimationClip FindClip(string path, string name)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                if (asset is AnimationClip clip && clip.name == name)
                    return clip;
            var main = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            return main != null && main.name == name ? main : null;
        }

        public static string Label(string character) => System.IO.Path.GetFileNameWithoutExtension(character);
    }

    public sealed class DrivenRig
    {
        public string Driver = string.Empty;
        public string Character = string.Empty;
        public Entity Entity;
        public readonly Dictionary<string, Entity> Bones = new Dictionary<string, Entity>(StringComparer.Ordinal);
        public object State;
    }

    public interface IConformanceDriver
    {
        string Name { get; }
        bool RootRelative { get; }
        bool Discover(World world, ConformanceManifest manifest, List<GameObject> truths, List<DrivenRig> rigs, out string error);
        bool Pose(World world, DrivenRig rig, int clip, AnimationClip asset, double seconds, out string reason);
    }

    public sealed class ConformanceRow
    {
        public string Driver = string.Empty;
        public string Character = string.Empty;
        public string Clip = string.Empty;
        public string Take = string.Empty;
        public string Status = string.Empty;
        public string Worst = string.Empty;
        public string Reason = string.Empty;
        public int Frames;
        public float Degrees;
        public float Centimetres;
        public float DegreesOutsideTwist;
        public string WorstOutsideTwist = string.Empty;
    }

    public static class PlayModeConformance
    {
        public const string DefaultFolder = "Assets/zz_conformance";
        public const string SaDriver = "SA";

        public static readonly HumanBodyBones[] TwistBones =
        {
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg
        };

        static readonly HumanBodyBones[] PositionBones =
            { HumanBodyBones.Hips, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };

        public static readonly List<Action<GameObject, string, int, List<AnimationClip>, Scene>> ExtraRigs = new List<Action<GameObject, string, int, List<AnimationClip>, Scene>>();

        public static Vector3 Spot(int index) => new Vector3(2f * (index + 1), 0f, 0f);

        public static int SpotIndex(World world, Entity entity)
        {
            var x = Read(world, entity).pos.x;
            var index = (int)Math.Round(x / 2f) - 1;
            return index >= 0 && Math.Abs(x - Spot(index).x) < 0.01f ? index : -1;
        }

        public static readonly List<IConformanceDriver> ExtraDrivers = new List<IConformanceDriver>();

        public static string RigName(string driver, string character) => driver + "|" + ConformanceManifest.Label(character);

        public static string BuildScene(string folder, string[] characters, List<AnimationClip> clips)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return "leave Play mode first.";
            Directory.CreateDirectory(folder);
            var manifest = new ConformanceManifest { Folder = folder };
            manifest.Characters.AddRange(characters);
            foreach (var clip in clips)
            {
                manifest.ClipPaths.Add(AssetDatabase.GetAssetPath(clip));
                manifest.ClipNames.Add(clip.name);
            }
            File.WriteAllText(manifest.Path, JsonUtility.ToJson(manifest, true));
            var mainPath = folder + "/Conformance.unity";
            var subPath = folder + "/ConformanceSub.unity";
            var main = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(main, mainPath);
            var sub = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var report = new StringBuilder();
            for (var index = 0; index < characters.Length; index++)
            {
                var character = characters[index];
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(character);
                if (asset == null)
                {
                    report.Append("missing ").Append(character).Append('\n');
                    continue;
                }
                var rig = Place(asset, RigName(SaDriver, character), sub);
                var rigAuthoring = rig.AddComponent<RigAuthoring>();
                rigAuthoring.DiscoverClips = false;
                rigAuthoring.Engine = Engine.Cpu;
                var family = rig.AddComponent<SharedFamilyAuthoring>();
                family.Rig = rigAuthoring;
                family.Clips = new List<AnimationClip>(clips);
                family.Autoplay = clips.Count > 0 ? clips[0] : null;
                foreach (var extra in ExtraRigs)
                    extra(asset, character, index, clips, sub);
                report.Append("placed ").Append(ConformanceManifest.Label(character)).Append('\n');
            }
            EditorSceneManager.SaveScene(sub, subPath);
            EditorSceneManager.CloseScene(sub, true);
            AssetDatabase.ImportAsset(subPath);
            var holder = new GameObject("Conformance SubScene");
            SceneManager.MoveGameObjectToScene(holder, main);
            var subScene = holder.AddComponent<SubScene>();
            subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(subPath);
            subScene.AutoLoadScene = true;
            EditorSceneManager.SaveScene(main, mainPath);
            report.Append("scene ").Append(mainPath).Append(", subscene ").Append(subPath).Append(", clips ").Append(clips.Count);
            return report.ToString();
        }

        public static GameObject Place(GameObject asset, string name, Scene scene)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
            if (PrefabUtility.IsPartOfPrefabInstance(instance))
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = name;
            instance.transform.SetPositionAndRotation(Vector3.zero, UnityEngine.Quaternion.identity);
            instance.transform.localScale = Vector3.one;
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                UnityEngine.Object.DestroyImmediate(renderer);
            var animator = instance.GetComponentInChildren<Animator>(true);
            var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (hips != null)
            {
                var strays = new List<GameObject>();
                foreach (Transform child in instance.transform)
                    if (!hips.IsChildOf(child))
                        strays.Add(child.gameObject);
                foreach (var stray in strays)
                    UnityEngine.Object.DestroyImmediate(stray);
            }
            return instance;
        }

        public static string Measure(string folder, string csvPath, int maxFramesPerClip)
        {
            if (!EditorApplication.isPlaying)
                return "enter Play mode first.";
            var manifest = ConformanceManifest.Load(folder);
            if (manifest == null)
                return "no manifest in " + folder + "; build the scene first.";
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return "not ready: no default world.";
            var drivers = new List<IConformanceDriver> { new SharedFamilyDriver() };
            drivers.AddRange(ExtraDrivers);
            var clips = manifest.Clips();
            var truths = new List<GameObject>();
            try
            {
                foreach (var character in manifest.Characters)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(character);
                    var truth = UnityEngine.Object.Instantiate(asset);
                    truth.name = ConformanceManifest.Label(character);
                    truth.hideFlags = HideFlags.HideAndDontSave;
                    truth.transform.SetPositionAndRotation(Vector3.zero, UnityEngine.Quaternion.identity);
                    truths.Add(truth);
                }
                var rigs = new List<DrivenRig>();
                foreach (var driver in drivers)
                    if (!driver.Discover(world, manifest, truths, rigs, out var error))
                        return "not ready: " + driver.Name + ": " + error;
                var timeSystem = world.GetExistingSystemManaged<UpdateWorldTimeSystem>();
                var timeWasEnabled = timeSystem != null && timeSystem.Enabled;
                if (timeSystem != null)
                    timeSystem.Enabled = false;
                var rows = new List<ConformanceRow>();
                try
                {
                    for (var c = 0; c < truths.Count; c++)
                        MeasureCharacter(world, drivers, rigs, truths[c], clips, manifest, maxFramesPerClip, rows);
                }
                finally
                {
                    if (timeSystem != null)
                        timeSystem.Enabled = timeWasEnabled;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(csvPath));
                var csv = new StringBuilder("driver,character,clip,take,frames,status,worst,degrees,cm,worst_outside_twist,degrees_outside_twist,reason\n");
                foreach (var row in rows)
                    csv.Append(row.Driver).Append(',').Append(row.Character).Append(",\"").Append(row.Clip).Append("\",\"").Append(row.Take).Append("\",")
                        .Append(row.Frames).Append(',').Append(row.Status).Append(",\"").Append(row.Worst).Append("\",")
                        .Append(F(row.Degrees)).Append(',').Append(F(row.Centimetres)).Append(",\"").Append(row.WorstOutsideTwist).Append("\",")
                        .Append(F(row.DegreesOutsideTwist)).Append(",\"").Append(row.Reason.Replace("\"", "'")).Append("\"\n");
                File.WriteAllText(csvPath, csv.ToString());
                return Summary(rows);
            }
            finally
            {
                foreach (var truth in truths)
                    if (truth != null)
                        UnityEngine.Object.DestroyImmediate(truth);
            }
        }

        static string F(float value) => float.IsFinite(value) ? value.ToString("0.#####", CultureInfo.InvariantCulture) : "inf";

        public static string Summary(List<ConformanceRow> rows)
        {
            var groups = new SortedDictionary<string, (int pass, int fail, int skip, float deg, float cm, float other)>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var key = row.Driver + " " + row.Character;
                groups.TryGetValue(key, out var g);
                if (row.Status == "PASS")
                    g.pass++;
                else if (row.Status == "FAIL")
                    g.fail++;
                else
                    g.skip++;
                if (row.Status != "SKIP")
                {
                    g.deg = Math.Max(g.deg, row.Degrees);
                    g.cm = Math.Max(g.cm, row.Centimetres);
                    g.other = Math.Max(g.other, row.DegreesOutsideTwist);
                }
                groups[key] = g;
            }
            var text = new StringBuilder();
            foreach (var pair in groups)
                text.Append(pair.Key).Append(": pass ").Append(pair.Value.pass).Append(", fail ").Append(pair.Value.fail).Append(", cannot play ").Append(pair.Value.skip)
                    .Append(", worst ").Append(F(pair.Value.deg)).Append(" deg ").Append(F(pair.Value.cm)).Append(" cm, outside the twist bones ").Append(F(pair.Value.other)).Append(" deg\n");
            return text.ToString();
        }

        sealed class Accumulator
        {
            public readonly ConformanceRow Row = new ConformanceRow();

            public void Rotation(float degrees, string where, bool twist)
            {
                if (!float.IsFinite(degrees))
                    degrees = float.PositiveInfinity;
                if (!twist && degrees > Row.DegreesOutsideTwist)
                {
                    Row.DegreesOutsideTwist = degrees;
                    Row.WorstOutsideTwist = where;
                }
                if (degrees > Row.Degrees)
                {
                    Row.Degrees = degrees;
                    Row.Worst = where;
                }
            }

            public void Position(float centimetres)
            {
                Row.Centimetres = Math.Max(Row.Centimetres, float.IsFinite(centimetres) ? centimetres : float.PositiveInfinity);
            }

            public void Finish()
            {
                if (Row.Status == "SKIP")
                    return;
                var reasons = new List<string>();
                if (Row.Degrees > 1f)
                    reasons.Add("rotation " + F(Row.Degrees) + " deg on " + Row.Worst);
                if (Row.Centimetres > 1f)
                    reasons.Add("position " + F(Row.Centimetres) + " cm");
                Row.Status = reasons.Count == 0 ? "PASS" : "FAIL";
                if (reasons.Count > 0)
                    Row.Reason = string.Join("; ", reasons);
            }
        }

        static void MeasureCharacter(World world, List<IConformanceDriver> drivers, List<DrivenRig> rigs, GameObject truth, List<AnimationClip> clips,
            ConformanceManifest manifest, int maxFramesPerClip, List<ConformanceRow> rows)
        {
            var label = truth.name;
            var mine = rigs.FindAll(r => r.Character == label);
            var animator = truth.GetComponentInChildren<Animator>(true);
            var driven = animator != null ? animator.gameObject : truth;
            var all = truth.GetComponentsInChildren<Transform>(true);
            var restPositions = new Vector3[all.Length];
            var restRotations = new UnityEngine.Quaternion[all.Length];
            var restScales = new Vector3[all.Length];
            for (var i = 0; i < all.Length; i++)
            {
                restPositions[i] = all[i].localPosition;
                restRotations[i] = all[i].localRotation;
                restScales[i] = all[i].localScale;
            }
            var human = new List<(string name, Transform bone, bool position, bool twist)>();
            for (var h = 0; h < (int)HumanBodyBones.LastBone; h++)
            {
                var bone = animator != null ? animator.GetBoneTransform((HumanBodyBones)h) : null;
                if (bone != null)
                    human.Add((bone.name, bone, Array.IndexOf(PositionBones, (HumanBodyBones)h) >= 0, Array.IndexOf(TwistBones, (HumanBodyBones)h) >= 0));
            }
            for (var c = 0; c < clips.Count; c++)
            {
                var clip = clips[c];
                var accumulators = new Dictionary<DrivenRig, Accumulator>();
                var pairs = new Dictionary<DrivenRig, Accumulator>();
                foreach (var rig in mine)
                {
                    accumulators[rig] = NewAccumulator(rig.Driver, label, manifest, c);
                    if (rig.Driver != SaDriver)
                        pairs[rig] = NewAccumulator(SaDriver + "-vs-" + rig.Driver, label, manifest, c);
                }
                if (clip == null)
                {
                    foreach (var a in accumulators.Values)
                    {
                        a.Row.Status = "SKIP";
                        a.Row.Reason = "clip asset missing";
                    }
                }
                else
                {
                    var rate = FamilyClips.ClipRate(clip);
                    var frames = FamilyClips.SampleCount(clip, rate);
                    var picks = Math.Min(frames, Math.Max(2, maxFramesPerClip));
                    var sa = mine.Find(r => r.Driver == SaDriver);
                    var poses = new Dictionary<DrivenRig, Dictionary<string, RigidTransform>>();
                    for (var k = 0; k < picks; k++)
                    {
                        var frame = picks == frames ? k : (int)Math.Round(k * (frames - 1) / (double)(picks - 1));
                        var seconds = FamilyClips.SampleSeconds(clip, frames, frame);
                        var drive = clip.isLooping && frame == frames - 1 ? seconds * (1.0 - 1e-6) : seconds;
                        var active = new List<DrivenRig>();
                        foreach (var rig in mine)
                        {
                            var acc = accumulators[rig];
                            if (acc.Row.Status == "SKIP")
                                continue;
                            var driver = drivers.Find(d => d.Name == rig.Driver);
                            if (!driver.Pose(world, rig, c, clip, drive, out var reason))
                            {
                                acc.Row.Status = "SKIP";
                                acc.Row.Reason = reason;
                                continue;
                            }
                            active.Add(rig);
                        }
                        if (active.Count == 0)
                            break;
                        Step(world);
                        foreach (var rig in active)
                            drivers.Find(d => d.Name == rig.Driver).Pose(world, rig, c, clip, drive, out _);
                        Step(world);
                        for (var i = 0; i < all.Length; i++)
                        {
                            all[i].localPosition = restPositions[i];
                            all[i].localRotation = restRotations[i];
                            all[i].localScale = restScales[i];
                        }
                        clip.SampleAnimation(driven, (float)seconds);
                        var rootInverse = math.inverse(new RigidTransform(truth.transform.rotation, truth.transform.position));
                        poses.Clear();
                        foreach (var rig in active)
                        {
                            var driver = drivers.Find(d => d.Name == rig.Driver);
                            var rigWorld = Read(world, rig.Entity);
                            var rigInverse = math.inverse(rigWorld);
                            var bones = new Dictionary<string, RigidTransform>(StringComparer.Ordinal);
                            foreach (var (name, _, _, _) in human)
                                if (rig.Bones.TryGetValue(name, out var entity))
                                    bones[name] = math.mul(rigInverse, Read(world, entity));
                            poses[rig] = bones;
                            var acc = accumulators[rig];
                            foreach (var (name, bone, position, twist) in human)
                            {
                                if (!bones.TryGetValue(name, out var pose))
                                {
                                    acc.Rotation(float.PositiveInfinity, name + " has no entity", twist);
                                    continue;
                                }
                                var expected = new RigidTransform(bone.rotation, bone.position);
                                if (driver.RootRelative)
                                    expected = math.mul(rootInverse, expected);
                                acc.Rotation(Angle(expected.rot, pose.rot), name + "@" + seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s", twist);
                                if (position)
                                    acc.Position(math.distance(expected.pos, pose.pos) * 100f);
                            }
                            acc.Row.Frames++;
                        }
                        if (sa == null || !poses.TryGetValue(sa, out var saBones))
                            continue;
                        foreach (var rig in active)
                        {
                            if (rig == sa || !poses.TryGetValue(rig, out var other))
                                continue;
                            var driver = drivers.Find(d => d.Name == rig.Driver);
                            var pair = pairs[rig];
                            foreach (var (name, _, position, twist) in human)
                            {
                                if (!saBones.TryGetValue(name, out var a) || !other.TryGetValue(name, out var b))
                                    continue;
                                if (driver.RootRelative)
                                    a = math.mul(rootInverse, a);
                                pair.Rotation(Angle(a.rot, b.rot), name + "@" + seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s", twist);
                                if (position)
                                    pair.Position(math.distance(a.pos, b.pos) * 100f);
                            }
                            pair.Row.Frames++;
                        }
                    }
                    foreach (var pair in pairs)
                        if (accumulators[pair.Key].Row.Status == "SKIP" || sa == null || accumulators[sa].Row.Status == "SKIP")
                        {
                            pair.Value.Row.Status = "SKIP";
                            pair.Value.Row.Reason = accumulators[pair.Key].Row.Status == "SKIP" ? pair.Key.Driver + " cannot play the clip" : "SA cannot play the clip";
                        }
                }
                foreach (var a in accumulators.Values)
                {
                    a.Finish();
                    rows.Add(a.Row);
                }
                foreach (var a in pairs.Values)
                {
                    a.Finish();
                    rows.Add(a.Row);
                }
            }
        }

        static Accumulator NewAccumulator(string driver, string character, ConformanceManifest manifest, int clip)
        {
            var a = new Accumulator();
            a.Row.Driver = driver;
            a.Row.Character = character;
            a.Row.Clip = manifest.ClipPaths[clip];
            a.Row.Take = manifest.ClipNames[clip];
            return a;
        }

        static float Angle(quaternion a, quaternion b)
        {
            var r = math.mul(math.inverse(math.normalize(a)), math.normalize(b)).value;
            return (float)(2.0 * Math.Atan2(Math.Sqrt((double)r.x * r.x + (double)r.y * r.y + (double)r.z * r.z), Math.Abs((double)r.w)) * 180.0 / Math.PI);
        }

        static double elapsed;

        static void Step(World world)
        {
            elapsed += 1.0;
            world.SetTime(new TimeData(elapsed, 0f));
            world.Update();
        }

        public static RigidTransform Read(World world, Entity entity)
        {
            var manager = world.EntityManager;
            if (manager.HasComponent<LocalToWorld>(entity))
            {
                var matrix = manager.GetComponentData<LocalToWorld>(entity).Value;
                return new RigidTransform(new quaternion(math.orthonormalize(new float3x3(matrix))), matrix.c3.xyz);
            }
            var local = manager.HasComponent<LocalTransform>(entity) ? manager.GetComponentData<LocalTransform>(entity) : LocalTransform.Identity;
            return new RigidTransform(local.Rotation, local.Position);
        }

        sealed class SharedFamilyDriver : IConformanceDriver
        {
            public string Name => SaDriver;
            public bool RootRelative => false;

            public bool Discover(World world, ConformanceManifest manifest, List<GameObject> truths, List<DrivenRig> rigs, out string error)
            {
                error = string.Empty;
                var manager = world.EntityManager;
                using var query = manager.CreateEntityQuery(typeof(SharedRig), typeof(SharedClipAsset), typeof(SharedContribution), typeof(SharedClock), typeof(Rig), typeof(BoneEntity));
                using var entities = query.ToEntityArray(Allocator.Temp);
                foreach (var truth in truths)
                {
                    var hash = RigBuilder.Hash(SaDriver + "|" + truth.name);
                    var found = Entity.Null;
                    foreach (var entity in entities)
                        if (manager.GetComponentData<SharedRig>(entity).NameHash == hash)
                            found = entity;
                    if (found == Entity.Null)
                    {
                        error = "no baked SA rig for " + truth.name + " (" + entities.Length + " shared rigs in the world)";
                        return false;
                    }
                    if (manager.GetBuffer<SharedClipAsset>(found).Length != manifest.ClipPaths.Count)
                    {
                        error = truth.name + " baked " + manager.GetBuffer<SharedClipAsset>(found).Length + " clips for " + manifest.ClipPaths.Count;
                        return false;
                    }
                    var rig = new DrivenRig { Driver = SaDriver, Character = truth.name, Entity = found };
                    var byHash = new Dictionary<uint, string>();
                    foreach (var t in truth.GetComponentsInChildren<Transform>(true))
                        byHash[RigBuilder.Hash(t.name)] = t.name;
                    var blob = manager.GetComponentData<Rig>(found).Value;
                    ref var bones = ref blob.Value.Bones;
                    var boneEntities = manager.GetBuffer<BoneEntity>(found);
                    for (var b = 0; b < bones.Length && b < boneEntities.Length; b++)
                        if (byHash.TryGetValue(bones[b].NameHash, out var name) && !rig.Bones.ContainsKey(name))
                            rig.Bones[name] = boneEntities[b].Value;
                    rigs.Add(rig);
                }
                return true;
            }

            public bool Pose(World world, DrivenRig rig, int clip, AnimationClip asset, double seconds, out string reason)
            {
                reason = string.Empty;
                var manager = world.EntityManager;
                manager.CompleteAllTrackedJobs();
                var contributions = manager.GetBuffer<SharedContribution>(rig.Entity);
                var index = -1;
                for (var i = 0; i < contributions.Length; i++)
                    if (contributions[i].Clip == clip)
                        index = i;
                if (index < 0)
                {
                    reason = "no contribution for clip " + clip;
                    return false;
                }
                SharedFamilyControl.PlaySingle(ref contributions, clip);
                var clock = manager.GetComponentData<SharedClock>(rig.Entity);
                SharedFamilyControl.Seek(ref clock, seconds);
                clock.Paused = 0;
                manager.SetComponentData(rig.Entity, clock);
                return true;
            }
        }
    }
}
