namespace PerfCompass;

/// <summary>Time-weighted empirical quantiles; missing and zero-duration readings carry no evidence.</summary>
public sealed class WeightedSeries
{
    private readonly List<(double Value, double Seconds)> readings = [];
    public double Seconds { get; private set; }
    public double? Maximum { get; private set; }
    public void Add(double? value, double seconds, double min = 0, double max = double.MaxValue)
    {
        if (value is not double v || !double.IsFinite(v) || !double.IsFinite(seconds) || seconds <= 0 || v < min || v > max) return;
        readings.Add((v, seconds)); Seconds += seconds; Maximum = Math.Max(Maximum ?? v, v);
    }
    public double TimeAbove(double threshold) => readings.Where(r => r.Value >= threshold).Sum(r => r.Seconds);
    public double? Quantile(double p)
    {
        if (readings.Count == 0) return null;
        if (p < 0 || p > 1 || !double.IsFinite(p)) throw new ArgumentOutOfRangeException(nameof(p));
        readings.Sort((a, b) => a.Value.CompareTo(b.Value));
        double target = Seconds * p, elapsed = 0;
        foreach (var reading in readings) { elapsed += reading.Seconds; if (elapsed >= target) return reading.Value; }
        return readings[^1].Value;
    }
}
