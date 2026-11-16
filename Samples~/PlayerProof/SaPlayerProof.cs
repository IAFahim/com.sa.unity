using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using SA.Motion.Numerics;
using SA.Shape.Unity;

namespace SA.PlayerProof
{
    public sealed class SaPlayerProof : MonoBehaviour
    {
        public const string Argument = "-saPlayerProof";
        public const string DataFolder = "SA.PlayerProof";
        public const string SharedScene = "Shared.entities";
        public const string SharedSkinScene = "SharedSkins.entities";
        public const string ShapeScene = "Shape.entities";
        public const string ShapeSkinScene = "ShapeSkins.entities";
        public const string SharedTrace = "SharedFamilyTrace.bytes";
        public const string SharedPalette = "SharedFamilyPalette.bytes";
        public const string ShapeTrace = "ShapeTrace.bytes";
        public const string ShapePalette = "ShapePalette.bytes";

        struct Result
        {
            public string Name;
            public int Values;
            public int Expected;
            public int Exact;
            public float Worst;
            public int WorstIndex;
            public int Rigs;
            public int Native;
        }

        string reportPath = string.Empty;
        int frames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Begin()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < arguments.Length; i++)
            {
                if (arguments[i] != Argument)
                    continue;
                var host = new GameObject("SA Player Proof");
                DontDestroyOnLoad(host);
                host.AddComponent<SaPlayerProof>().reportPath = arguments[i + 1];
                return;
            }
        }

        void Update()
        {
            if (++frames < 3)
                return;
            enabled = false;
            var failures = 1;
            string report;
            try
            {
                report = Run(out failures);
            }
            catch (Exception e)
            {
                report = "{\n  \"failures\": 1,\n  \"error\": \"" + e.GetType().Name + ": " + e.Message.Replace("\\", "/").Replace("\"", "'").Replace("\n", " ") + "\"\n}\n";
            }
            File.WriteAllText(reportPath, report);
            Debug.Log("SA player proof: " + report);
            Application.Quit(failures == 0 ? 0 : 1);
        }

        static string Run(out int failures)
        {
            var folder = Path.Combine(Application.streamingAssetsPath, DataFolder);
            var results = new[]
            {
                SharedPose(folder),
                SharedPalettes(folder),
                ShapePose(folder),
                ShapePalettes(folder)
            };
            failures = 0;
            var text = new StringBuilder();
            text.Append("{\n  \"scenes\": [\n");
            for (var i = 0; i < results.Length; i++)
            {
                var result = results[i];
                var passed = result.Values == result.Expected && result.Exact == result.Expected && result.Native == result.Rigs && result.Values > 0;
                if (!passed)
                    failures++;
                text.Append("    { \"name\": \"").Append(result.Name)
                    .Append("\", \"values\": ").Append(result.Values)
                    .Append(", \"expected\": ").Append(result.Expected)
                    .Append(", \"exact\": ").Append(result.Exact)
                    .Append(", \"worst\": ").Append(result.Worst.ToString("R", CultureInfo.InvariantCulture))
                    .Append(", \"worstIndex\": ").Append(result.WorstIndex)
                    .Append(", \"rigs\": ").Append(result.Rigs)
                    .Append(", \"rigsOnBurst\": ").Append(result.Native)
                    .Append(", \"passed\": ").Append(passed ? "true" : "false")
                    .Append(i + 1 < results.Length ? " },\n" : " }\n");
            }
            text.Append("  ],\n  \"failures\": ").Append(failures)
                .Append(",\n  \"platform\": \"").Append(Application.platform)
                .Append("\",\n  \"unity\": \"").Append(Application.unityVersion)
                .Append("\",\n  \"graphics\": \"").Append(SystemInfo.graphicsDeviceName).Append(" / ").Append(SystemInfo.graphicsDeviceType)
                .Append("\",\n  \"processor\": \"").Append(SystemInfo.processorType.Trim())
                .Append("\"\n}\n");
            return text.ToString();
        }

        static unsafe World Load(string file)
        {
            var world = new World("SA.PlayerProof");
            var bytes = File.ReadAllBytes(file);
            fixed (byte* content = bytes)
            {
                using (var reader = new MemoryBinaryReader(content, bytes.Length))
                {
                    var transaction = world.EntityManager.BeginExclusiveEntityTransaction();
                    SerializeUtility.DeserializeWorld(transaction, reader);
                    world.EntityManager.EndExclusiveEntityTransaction();
                }
            }
            return world;
        }

        static float[] Expected(string file)
        {
            var stored = File.ReadAllBytes(file);
            var values = new float[stored.Length / 4];
            Buffer.BlockCopy(stored, 0, values, 0, values.Length * 4);
            return values;
        }

        static Result Compare(string name, float[] expected, List<float> actual, int rigs, int native)
        {
            var result = new Result { Name = name, Values = actual.Count, Expected = expected.Length, WorstIndex = -1, Rigs = rigs, Native = native };
            var count = Math.Min(expected.Length, actual.Count);
            for (var i = 0; i < count; i++)
            {
                var gap = Math.Abs(expected[i] - actual[i]);
                if (gap == 0f || float.IsNaN(expected[i]) && float.IsNaN(actual[i]))
                {
                    result.Exact++;
                    continue;
                }
                if (float.IsNaN(gap))
                    gap = float.MaxValue;
                if (gap > result.Worst)
                {
                    result.Worst = gap;
                    result.WorstIndex = i;
                }
            }
            return result;
        }

        static Entity[] Rigs(EntityManager manager)
        {
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<SharedTimeline>(), ComponentType.ReadOnly<SharedRig>());
            using var found = query.ToEntityArray(Allocator.Temp);
            var rigs = found.ToArray();
            Array.Sort(rigs, (a, b) => manager.GetComponentData<SharedRig>(a).NameHash.CompareTo(manager.GetComponentData<SharedRig>(b).NameHash));
            return rigs;
        }

        static Entity[] Skins(EntityManager manager)
        {
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<Skin>(), ComponentType.ReadOnly<SkinBinding>());
            using var found = query.ToEntityArray(Allocator.Temp);
            var skins = found.ToArray();
            Array.Sort(skins, (a, b) => manager.GetComponentData<Skin>(a).Value.Value.Id.Value.x.CompareTo(manager.GetComponentData<Skin>(b).Value.Value.Id.Value.x));
            return skins;
        }

        static void Step(World world, ref double elapsed, float seconds, SharedFamilySystem shared, ShapeTimelineSystem shape)
        {
            elapsed += seconds;
            world.SetTime(new TimeData(elapsed, seconds));
            shared.Update();
            world.EntityManager.CompleteAllTrackedJobs();
            if (shape == null)
                return;
            shape.Update();
            world.EntityManager.CompleteAllTrackedJobs();
        }

        static int SharedNative(EntityManager manager, Entity[] rigs)
        {
            var native = 0;
            foreach (var rig in rigs)
                if (manager.HasComponent<SharedPoseState>(rig) && manager.GetComponentData<SharedPoseState>(rig).Native == 1)
                    native++;
            return native;
        }

        static int ShapeNative(EntityManager manager, Entity[] rigs)
        {
            var native = 0;
            foreach (var rig in rigs)
                if (manager.HasComponent<ShapePoseState>(rig) && manager.GetComponentData<ShapePoseState>(rig).Native == 1)
                    native++;
            return native;
        }

        static void Weights(EntityManager manager, Entity rig, float a, float b)
        {
            var contributions = manager.GetBuffer<SharedContribution>(rig);
            var first = contributions[0];
            var second = contributions[1];
            first.Weight = a;
            second.Weight = b;
            contributions[0] = first;
            contributions[1] = second;
        }

        static void Seek(EntityManager manager, Entity rig, double seconds)
        {
            var clock = manager.GetComponentData<SharedClock>(rig);
            SharedFamilyControl.Seek(ref clock, seconds);
            manager.SetComponentData(rig, clock);
        }

        static void Pause(EntityManager manager, Entity rig, bool paused)
        {
            var clock = manager.GetComponentData<SharedClock>(rig);
            SharedFamilyControl.SetPaused(ref clock, paused);
            manager.SetComponentData(rig, clock);
        }

        static void PlaySingle(EntityManager manager, Entity rig, int clip)
        {
            var contributions = manager.GetBuffer<SharedContribution>(rig);
            SharedFamilyControl.PlaySingle(ref contributions, clip);
        }

        static void Request(EntityManager manager, Entity rig, uint id, float value)
        {
            ShapeControl.SetRequested(manager.GetBuffer<ShapeChannelState>(rig), id, value);
        }

        static void Bones(EntityManager manager, Entity rig, List<float> trace, bool post)
        {
            var bones = manager.GetBuffer<BoneEntity>(rig).ToNativeArray(Allocator.Temp);
            foreach (var bone in bones)
            {
                var local = manager.GetComponentData<LocalTransform>(bone.Value);
                trace.Add(local.Position.x);
                trace.Add(local.Position.y);
                trace.Add(local.Position.z);
                trace.Add(local.Rotation.value.x);
                trace.Add(local.Rotation.value.y);
                trace.Add(local.Rotation.value.z);
                trace.Add(local.Rotation.value.w);
                trace.Add(local.Scale);
                if (!post || !manager.HasComponent<PostTransformMatrix>(bone.Value))
                    continue;
                var matrix = manager.GetComponentData<PostTransformMatrix>(bone.Value).Value;
                trace.Add(matrix.c0.x);
                trace.Add(matrix.c1.y);
                trace.Add(matrix.c2.z);
            }
            bones.Dispose();
        }

        static void Palettes(EntityManager manager, Entity[] skins, SkinningSystem skinning, List<float> trace)
        {
            foreach (var entity in skins)
            {
                var skin = manager.GetComponentData<Skin>(entity);
                var binding = manager.GetComponentData<SkinBinding>(entity);
                var count = skin.Value.Value.Bones.Length;
                var matrices = new float4x4[count];
                if (binding.MatrixBase > 0)
                    skinning.SkinMatrixBuffer.GetData(matrices, 0, (int)binding.MatrixBase, count);
                foreach (var matrix in matrices)
                    for (var column = 0; column < 4; column++)
                        for (var row = 0; row < 4; row++)
                            trace.Add(matrix[column][row]);
            }
        }

        static Result SharedPose(string folder)
        {
            var trace = new List<float>();
            int rigCount;
            int native;
            using (var world = Load(Path.Combine(folder, SharedScene)))
            {
                var manager = world.EntityManager;
                var rigs = Rigs(manager);
                var shared = world.GetOrCreateSystemManaged<SharedFamilySystem>();
                var elapsed = 0.0;
                for (var step = 0; step < 36; step++)
                {
                    if (step == 10)
                    {
                        PlaySingle(manager, rigs[0], 1);
                        Weights(manager, rigs[2], 0.2f, 0.8f);
                    }
                    if (step == 22 || step == 23)
                        Seek(manager, rigs[1], 1.5);
                    if (step == 27 || step == 29)
                        Pause(manager, rigs[1], step == 27);
                    if (step == 29)
                    {
                        var contributions = manager.GetBuffer<SharedContribution>(rigs[2]);
                        var layered = contributions[2];
                        layered.Weight = 0.5f;
                        layered.Layer = 1;
                        contributions[2] = layered;
                    }
                    var seconds = step < 2 || step == 22 || step == 23 ? 0f : step == 19 ? 0.3f : 1f / 60f;
                    Step(world, ref elapsed, seconds, shared, null);
                    foreach (var rig in rigs)
                    {
                        var clock = manager.GetComponentData<SharedClock>(rig);
                        trace.Add((float)clock.Seconds);
                        trace.Add(clock.Tick);
                        trace.Add(clock.Continuity);
                        var contributions = manager.GetBuffer<SharedContribution>(rig);
                        trace.Add((float)contributions[0].Sample);
                        trace.Add((float)contributions[1].Sample);
                        Bones(manager, rig, trace, false);
                    }
                }
                rigCount = rigs.Length;
                native = SharedNative(manager, rigs);
            }
            return Compare("shared-family poses", Expected(Path.Combine(folder, SharedTrace)), trace, rigCount, native);
        }

        static Result SharedPalettes(string folder)
        {
            var trace = new List<float>();
            int rigCount;
            int native;
            using (var world = Load(Path.Combine(folder, SharedSkinScene)))
            {
                var manager = world.EntityManager;
                var rigs = Rigs(manager);
                var skins = Skins(manager);
                var shared = world.GetOrCreateSystemManaged<SharedFamilySystem>();
                var skinning = world.GetOrCreateSystemManaged<SkinningSystem>();
                var palette = world.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
                var elapsed = 0.0;
                for (var step = 0; step < 14; step++)
                {
                    if (step == 6)
                        PlaySingle(manager, rigs[0], 1);
                    Step(world, ref elapsed, step == 0 ? 0f : 1f / 60f, shared, null);
                    skinning.Update();
                    palette.Update();
                    manager.CompleteAllTrackedJobs();
                    Palettes(manager, skins, skinning, trace);
                }
                rigCount = rigs.Length;
                native = SharedNative(manager, rigs);
            }
            return Compare("shared-family palettes", Expected(Path.Combine(folder, SharedPalette)), trace, rigCount, native);
        }

        static void ShapeAct(EntityManager manager, Entity[] rigs, int step)
        {
            if (step == 4)
                Request(manager, rigs[0], 7u, 0.8f);
            if (step == 9)
                PlaySingle(manager, rigs[0], 1);
            if (step == 12)
            {
                Request(manager, rigs[1], 11u, -0.6f);
                Request(manager, rigs[1], 12u, 1.7f);
                Weights(manager, rigs[1], 0.3f, 0.7f);
            }
            if (step == 16)
                Request(manager, rigs[2], 7u, 0.5f);
            if (step == 21)
                Request(manager, rigs[0], 7u, 0f);
            if (step == 24 || step == 25)
                Seek(manager, rigs[1], 1.5);
            if (step == 28 || step == 30)
                Pause(manager, rigs[1], step == 28);
            if (step == 31)
            {
                var physical = manager.GetComponentData<ShapePhysical>(rigs[2]);
                physical.LivePhysical = 0;
                manager.SetComponentData(rigs[2], physical);
                Request(manager, rigs[2], 7u, 0.5f);
            }
            if (step == 33)
                Request(manager, rigs[1], 11u, 0.9f);
        }

        static void Add(List<float> trace, Float3 value)
        {
            trace.Add(value.X);
            trace.Add(value.Y);
            trace.Add(value.Z);
        }

        static void ShapeRecord(EntityManager manager, Entity[] rigs, List<float> trace)
        {
            foreach (var rig in rigs)
            {
                var clock = manager.GetComponentData<SharedClock>(rig);
                trace.Add((float)clock.Seconds);
                trace.Add(clock.Tick);
                trace.Add(clock.Continuity);
                trace.Add(clock.Seek);
                var contributions = manager.GetBuffer<SharedContribution>(rig);
                trace.Add((float)contributions[0].Sample);
                trace.Add((float)contributions[1].Sample);
                var channels = manager.GetBuffer<ShapeChannelState>(rig);
                trace.Add(channels.Length);
                for (var i = 0; i < channels.Length; i++)
                {
                    trace.Add(channels[i].Requested);
                    trace.Add(channels[i].Value);
                    trace.Add(channels[i].Velocity);
                }
                trace.Add(manager.GetComponentData<ShapeState>(rig).Revision);
                Bones(manager, rig, trace, true);
                var deformations = manager.GetBuffer<ShapeSkinDeformation>(rig);
                trace.Add(deformations.Length);
                for (var i = 0; i < deformations.Length; i++)
                {
                    Add(trace, deformations[i].Value.C0);
                    Add(trace, deformations[i].Value.C1);
                    Add(trace, deformations[i].Value.C2);
                    Add(trace, deformations[i].Value.C3);
                }
                var morphs = manager.GetBuffer<ShapeMorphWeight>(rig);
                trace.Add(morphs.Length);
                for (var i = 0; i < morphs.Length; i++)
                    trace.Add(morphs[i].Value);
                var contact = manager.GetComponentData<ShapeContactState>(rig);
                Add(trace, contact.SoleA);
                Add(trace, contact.SoleB);
                trace.Add(contact.HipHeight);
                trace.Add(contact.GroundY);
                trace.Add(contact.Revision);
                if (!manager.HasComponent<ShapePhysical>(rig))
                    continue;
                var physical = manager.GetComponentData<ShapePhysical>(rig);
                trace.Add(physical.LivePhysical);
                trace.Add(physical.PendingRebuild);
                trace.Add(physical.Diagnostic.Length);
            }
        }

        static Result ShapePose(string folder)
        {
            var trace = new List<float>();
            int rigCount;
            int native;
            using (var world = Load(Path.Combine(folder, ShapeScene)))
            {
                var manager = world.EntityManager;
                var rigs = Rigs(manager);
                var shared = world.GetOrCreateSystemManaged<SharedFamilySystem>();
                var shape = world.GetOrCreateSystemManaged<ShapeTimelineSystem>();
                var elapsed = 0.0;
                for (var step = 0; step < 40; step++)
                {
                    ShapeAct(manager, rigs, step);
                    var seconds = step < 2 || step == 24 || step == 25 ? 0f : step == 19 ? 0.3f : 1f / 60f;
                    Step(world, ref elapsed, seconds, shared, shape);
                    ShapeRecord(manager, rigs, trace);
                }
                rigCount = rigs.Length;
                native = ShapeNative(manager, rigs);
            }
            return Compare("shape poses and state", Expected(Path.Combine(folder, ShapeTrace)), trace, rigCount, native);
        }

        static Result ShapePalettes(string folder)
        {
            var trace = new List<float>();
            int rigCount;
            int native;
            using (var world = Load(Path.Combine(folder, ShapeSkinScene)))
            {
                var manager = world.EntityManager;
                var rigs = Rigs(manager);
                var skins = Skins(manager);
                var shared = world.GetOrCreateSystemManaged<SharedFamilySystem>();
                var shape = world.GetOrCreateSystemManaged<ShapeTimelineSystem>();
                var skinning = world.GetOrCreateSystemManaged<SkinningSystem>();
                var palette = world.GetOrCreateSystemManaged<SharedFamilyPaletteSystem>();
                var shapePalette = world.GetOrCreateSystemManaged<ShapeSkinPaletteSystem>();
                var elapsed = 0.0;
                for (var step = 0; step < 16; step++)
                {
                    if (step == 3)
                    {
                        Request(manager, rigs[0], 7u, 0.8f);
                        Request(manager, rigs[1], 11u, -0.6f);
                        Request(manager, rigs[1], 12u, 1.7f);
                    }
                    if (step == 8)
                        PlaySingle(manager, rigs[1], 1);
                    Step(world, ref elapsed, step == 0 ? 0f : 1f / 60f, shared, shape);
                    skinning.Update();
                    palette.Update();
                    shapePalette.Update();
                    manager.CompleteAllTrackedJobs();
                    Palettes(manager, skins, skinning, trace);
                }
                rigCount = rigs.Length;
                native = ShapeNative(manager, rigs);
            }
            return Compare("shape palettes", Expected(Path.Combine(folder, ShapePalette)), trace, rigCount, native);
        }
    }
}
