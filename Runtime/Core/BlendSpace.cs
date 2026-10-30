using System;
using Unity.Mathematics;

namespace SA
{
    public static class BlendSpace
    {
        public static void Linear(ReadOnlySpan<float> thresholds, float value, Span<float> weights)
        {
            var count = math.min(thresholds.Length, weights.Length);
            for (var i = 0; i < weights.Length; i++)
                weights[i] = 0f;
            if (count == 0)
                return;
            value = Playback.Finite(value);
            int below = -1, above = -1;
            for (var i = 0; i < count; i++)
            {
                var t = thresholds[i];
                if (t <= value && (below < 0 || t > thresholds[below]))
                    below = i;
                if (t >= value && (above < 0 || t < thresholds[above]))
                    above = i;
            }
            if (below < 0 && above < 0)
            {
                weights[0] = 1f;
                return;
            }
            if (below < 0 || above < 0 || below == above || thresholds[above] <= thresholds[below])
            {
                weights[below < 0 ? above : below] = 1f;
                return;
            }
            var x = (value - thresholds[below]) / (thresholds[above] - thresholds[below]);
            weights[below] = 1f - x;
            weights[above] = x;
        }

        public static void Cartesian(ReadOnlySpan<float2> points, float2 value, Span<float> weights)
        {
            var count = math.min(points.Length, weights.Length);
            for (var i = 0; i < count; i++)
            {
                var weight = 1f;
                for (var j = 0; j < count && weight > 0f; j++)
                {
                    var edge = points[j] - points[i];
                    var length = math.lengthsq(edge);
                    if (j != i && length > 0f)
                        weight = math.min(weight, math.max(0f, 1f - math.dot(value - points[i], edge) / length));
                }
                weights[i] = weight;
            }
            Normalize(points, value, weights, count);
        }

        public static void Directional(ReadOnlySpan<float2> points, float2 value, Span<float> weights)
        {
            var count = math.min(points.Length, weights.Length);
            var magnitude = math.length(value);
            for (var i = 0; i < count; i++)
            {
                var weight = 1f;
                var mi = math.length(points[i]);
                for (var j = 0; j < count && weight > 0f; j++)
                {
                    if (j == i)
                        continue;
                    var mj = math.length(points[j]);
                    var middle = (mi + mj) * 0.5f;
                    var scale = middle > 0f ? 1f / middle : 0f;
                    var angles = AngleWeights(points[i], points[j], value);
                    var toValue = new float2((magnitude - mi) * scale, angles.y);
                    var edge = new float2((mj - mi) * scale, angles.x);
                    var length = math.lengthsq(edge);
                    if (length > 0f)
                        weight = math.min(weight, math.saturate(1f - math.dot(toValue, edge) / length));
                }
                weights[i] = weight;
            }
            Normalize(points, value, weights, count);
        }

        static float Angle(float2 a, float2 b) => math.atan2(a.x * b.y - a.y * b.x, math.dot(a, b));

        static float2 AngleWeights(float2 i, float2 j, float2 value)
        {
            if (!math.any(i != 0f))
                return new float2(Angle(j, value), 0f);
            if (!math.any(j != 0f))
                return new float2(Angle(i, value), Angle(i, value));
            var edge = Angle(i, j);
            return new float2(edge, math.any(value != 0f) ? Angle(i, value) : edge);
        }

        static void Normalize(ReadOnlySpan<float2> points, float2 value, Span<float> weights, int count)
        {
            var sum = 0f;
            for (var i = 0; i < count; i++)
                sum += weights[i];
            if (sum > 0f)
            {
                for (var i = 0; i < count; i++)
                    weights[i] /= sum;
                return;
            }
            var nearest = 0;
            for (var i = 1; i < count; i++)
                if (math.distancesq(points[i], value) < math.distancesq(points[nearest], value))
                    nearest = i;
            if (count > 0)
                weights[nearest] = 1f;
        }
    }
}
