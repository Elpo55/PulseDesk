namespace PulseDesk.Core.Metrics;

/// <summary>
/// Reduces the number of points drawn by charts without hiding peaks.
/// </summary>
public static class Downsampler
{
    /// <summary>
    /// Largest-Triangle-Three-Buckets downsampling (Steinarsson, 2013). Keeps the first and last
    /// samples and, in each bucket, the sample that best preserves the visual shape of the curve.
    /// </summary>
    /// <param name="data">Samples ordered by time.</param>
    /// <param name="threshold">Maximum number of samples to return (at least 3 to have an effect).</param>
    public static MetricSample[] Lttb(ReadOnlySpan<MetricSample> data, int threshold)
    {
        if (threshold >= data.Length || threshold < 3)
        {
            return data.ToArray();
        }

        var origin = data[0].Timestamp;
        double X(in MetricSample s) => (s.Timestamp - origin).TotalSeconds;

        var sampled = new MetricSample[threshold];
        var sampledIndex = 0;
        var bucketSize = (double)(data.Length - 2) / (threshold - 2);
        var a = 0;

        sampled[sampledIndex++] = data[a];

        for (var i = 0; i < threshold - 2; i++)
        {
            // Average point of the next bucket, used as the third vertex of the triangle.
            var avgStart = (int)Math.Floor((i + 1) * bucketSize) + 1;
            var avgEnd = Math.Min((int)Math.Floor((i + 2) * bucketSize) + 1, data.Length);
            double avgX = 0, avgY = 0;
            for (var j = avgStart; j < avgEnd; j++)
            {
                avgX += X(data[j]);
                avgY += data[j].Value;
            }

            var avgLength = avgEnd - avgStart;
            avgX /= avgLength;
            avgY /= avgLength;

            // Pick the point of the current bucket forming the largest triangle.
            var rangeStart = (int)Math.Floor(i * bucketSize) + 1;
            var rangeEnd = (int)Math.Floor((i + 1) * bucketSize) + 1;
            var pointAx = X(data[a]);
            var pointAy = data[a].Value;
            var maxArea = -1.0;
            var next = rangeStart;

            for (var j = rangeStart; j < rangeEnd; j++)
            {
                var area = Math.Abs(
                    ((pointAx - avgX) * (data[j].Value - pointAy)) -
                    ((pointAx - X(data[j])) * (avgY - pointAy)));
                if (area > maxArea)
                {
                    maxArea = area;
                    next = j;
                }
            }

            sampled[sampledIndex++] = data[next];
            a = next;
        }

        sampled[sampledIndex] = data[^1];
        return sampled;
    }
}
