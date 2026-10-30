using System;
using System.Collections.Generic;
using System.IO;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

public class Benchmark : MonoBehaviour
{
    public int Count = 10000;
    public int WarmupFrames = 120;
    public int MeasureFrames = 300;
    public bool RenderCrowd = true;

    [Serializable]
    class Report
    {
        public int Instances, Frames, EntityCount, ChunkCount;
        public float FrameMsAvg, FrameMsP95, SaMainMsAvg, SaMainMsP95, SaGpuMsAvg, SaGpuMsP95;
        public float RegisterMs, LayoutMs, FillMs, UploadMs;
        public string Cpu, Gpu, GraphicsApi;
        public bool GpuTimingSupported;
    }

    static readonly string[] MarkerNames = { "SA.Register", "SA.Layout", "SA.Fill", "SA.Upload" };
    readonly List<float> frameMs = new List<float>();
    readonly List<float> mainMs = new List<float>();
    readonly List<float> gpuMs = new List<float>();
    readonly float[] markerSums = new float[4];
    ProfilerRecorder[] recorders;
    Recorder gpuRecorder;
    int stage;
    int frames;

    void Update()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
            return;
        var manager = world.EntityManager;
        if (stage == 0)
        {
            using var crowds = manager.CreateEntityQuery(typeof(Crowd));
            if (crowds.IsEmpty)
                return;
            var crowd = crowds.GetSingleton<Crowd>();
            crowd.Count = Count;
            crowds.SetSingleton(crowd);
            if (!RenderCrowd)
                foreach (var camera in Camera.allCameras)
                    camera.enabled = false;
            stage = 1;
            return;
        }
        if (stage == 1)
        {
            using var members = manager.CreateEntityQuery(typeof(CrowdMember), typeof(SA.Rig));
            if (members.CalculateEntityCount() < Count)
                return;
            if (++frames < WarmupFrames)
                return;
            recorders = new ProfilerRecorder[MarkerNames.Length];
            for (var i = 0; i < recorders.Length; i++)
                recorders[i] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, MarkerNames[i], 1);
            gpuRecorder = Recorder.Get("SA.GPU");
            gpuRecorder.enabled = true;
            frames = 0;
            stage = 2;
            return;
        }
        if (stage != 2)
            return;
        frameMs.Add(Time.unscaledDeltaTime * 1000f);
        var main = 0f;
        for (var i = 0; i < recorders.Length; i++)
        {
            var ms = recorders[i].LastValue / 1e6f;
            markerSums[i] += ms;
            main += ms;
        }
        mainMs.Add(main);
        gpuMs.Add(gpuRecorder.gpuElapsedNanoseconds / 1e6f);
        if (++frames < MeasureFrames)
            return;
        Write(manager);
        foreach (var recorder in recorders)
            recorder.Dispose();
        stage = 3;
    }

    static float Average(List<float> values)
    {
        var sum = 0f;
        foreach (var value in values)
            sum += value;
        return values.Count > 0 ? sum / values.Count : 0f;
    }

    static float Percentile95(List<float> values)
    {
        var sorted = new List<float>(values);
        sorted.Sort();
        return sorted.Count > 0 ? sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))] : 0f;
    }

    void Write(EntityManager manager)
    {
        using var all = manager.UniversalQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
        var report = new Report
        {
            Instances = Count,
            Frames = frames,
            EntityCount = all.Length,
            ChunkCount = manager.UniversalQuery.CalculateChunkCountWithoutFiltering(),
            FrameMsAvg = Average(frameMs),
            FrameMsP95 = Percentile95(frameMs),
            SaMainMsAvg = Average(mainMs),
            SaMainMsP95 = Percentile95(mainMs),
            SaGpuMsAvg = Average(gpuMs),
            SaGpuMsP95 = Percentile95(gpuMs),
            RegisterMs = markerSums[0] / frames,
            LayoutMs = markerSums[1] / frames,
            FillMs = markerSums[2] / frames,
            UploadMs = markerSums[3] / frames,
            Cpu = SystemInfo.processorType,
            Gpu = SystemInfo.graphicsDeviceName,
            GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            GpuTimingSupported = SystemInfo.supportsGpuRecorder
        };
        Directory.CreateDirectory("Temp/sa");
        File.WriteAllText($"Temp/sa/benchmark_{Count}{(RenderCrowd ? "_rendered" : "_compute")}.json", JsonUtility.ToJson(report, true));
    }
}
