using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace SA
{
    internal struct PaletteRange
    {
        public int Start;
        public int Count;
        public int Index;
    }

    internal sealed class PaletteStaging : IDisposable
    {
        public NativeArray<float4x4> Matrices;
        public NativeArray<int> Offsets;
        public NativeArray<byte> Built;
        public NativeList<PaletteRange> Ranges;
        public int DirectLimit = 8;
        NativeList<PaletteRun> runs;
        GpuArray<float4x4> staged;
        GpuArray<PaletteRun> stagedRuns;
        int skins;
        int total;

        struct StartOrder : IComparer<PaletteRange>
        {
            public int Compare(PaletteRange a, PaletteRange b) => a.Start.CompareTo(b.Start);
        }

        public PaletteStaging()
        {
            Ranges = new NativeList<PaletteRange>(64, Allocator.Persistent);
            runs = new NativeList<PaletteRun>(64, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (Matrices.IsCreated)
                Matrices.Dispose();
            if (Offsets.IsCreated)
                Offsets.Dispose();
            if (Built.IsCreated)
                Built.Dispose();
            if (Ranges.IsCreated)
                Ranges.Dispose();
            if (runs.IsCreated)
                runs.Dispose();
            staged?.Dispose();
            stagedRuns?.Dispose();
        }

        public void Begin(int skinCount)
        {
            skins = skinCount;
            Ranges.Clear();
            if (Ranges.Capacity < skinCount)
                Ranges.Capacity = skinCount;
            if (Offsets.IsCreated && Offsets.Length >= skinCount)
                return;
            var capacity = math.max(skinCount, Offsets.IsCreated ? Offsets.Length * 2 : 64);
            if (Offsets.IsCreated)
                Offsets.Dispose();
            if (Built.IsCreated)
                Built.Dispose();
            Offsets = new NativeArray<int>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Built = new NativeArray<byte>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        public int Arrange()
        {
            for (var i = 0; i < skins; i++)
            {
                Offsets[i] = -1;
                Built[i] = 0;
            }
            Ranges.Sort(new StartOrder());
            total = 0;
            for (var i = 0; i < Ranges.Length; i++)
            {
                var range = Ranges[i];
                Offsets[range.Index] = total;
                total += range.Count;
            }
            if (total > 0 && (!Matrices.IsCreated || Matrices.Length < total))
            {
                var capacity = math.max(total, Matrices.IsCreated ? Matrices.Length * 2 : 0);
                if (Matrices.IsCreated)
                    Matrices.Dispose();
                Matrices = new NativeArray<float4x4>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            return total;
        }

        public int Upload(GpuPipeline pipeline)
        {
            var needed = 0;
            var end = -1;
            runs.Clear();
            for (var i = 0; i < Ranges.Length; i++)
            {
                var range = Ranges[i];
                if (Built[range.Index] == 0)
                {
                    end = -1;
                    continue;
                }
                if (range.Start != end)
                    needed++;
                end = range.Start + range.Count;
                runs.Add(new PaletteRun { Source = (uint)Offsets[range.Index], Target = (uint)range.Start, Count = (uint)range.Count });
            }
            if (needed <= DirectLimit)
                return Direct(pipeline.SkinMatrices.Buffer);
            if (staged == null)
            {
                staged = new GpuArray<float4x4>();
                stagedRuns = new GpuArray<PaletteRun>();
            }
            staged.Reserve(total);
            stagedRuns.Reserve(runs.Length);
            staged.Buffer.SetData(Matrices, 0, 0, total);
            stagedRuns.Buffer.SetData(runs.AsArray(), 0, 0, runs.Length);
            pipeline.Scatter(staged.Buffer, stagedRuns.Buffer, runs.Length);
            return 2;
        }

        int Direct(UnityEngine.GraphicsBuffer target)
        {
            var calls = 0;
            var source = 0;
            var start = 0;
            var length = 0;
            for (var i = 0; i < Ranges.Length; i++)
            {
                var range = Ranges[i];
                var built = Built[range.Index] != 0;
                if (built && length > 0 && range.Start == start + length)
                {
                    length += range.Count;
                    continue;
                }
                if (length > 0)
                {
                    target.SetData(Matrices, source, start, length);
                    calls++;
                }
                length = 0;
                if (!built)
                    continue;
                source = Offsets[range.Index];
                start = range.Start;
                length = range.Count;
            }
            if (length > 0)
            {
                target.SetData(Matrices, source, start, length);
                calls++;
            }
            return calls;
        }
    }
}
