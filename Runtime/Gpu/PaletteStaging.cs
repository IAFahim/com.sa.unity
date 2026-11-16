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
        int skins;

        struct StartOrder : IComparer<PaletteRange>
        {
            public int Compare(PaletteRange a, PaletteRange b) => a.Start.CompareTo(b.Start);
        }

        public PaletteStaging()
        {
            Ranges = new NativeList<PaletteRange>(64, Allocator.Persistent);
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
            var total = 0;
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

        public int Upload(UnityEngine.GraphicsBuffer target)
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
