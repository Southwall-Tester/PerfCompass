namespace PerfCompass;

internal static class GpuEngineEvidence
{
    internal const string Version = GpuLoadObservations.Version;
    internal static readonly string[] Kinds = ["core", "3d", "compute", "video", "encode", "decode", "processing", "copy"];
    internal static string Label(string kind) => kind switch
    {
        "core" => "核心", "3d" => "3D", "compute" => "计算", "video" => "视频引擎（合并）",
        "encode" => "编码", "decode" => "解码", "processing" => "视频处理", "copy" => "复制", _ => "未知"
    };
    private static bool Percent(double? value) => value == null || value is >= 0 and <= 100 && double.IsFinite(value.Value);
    private static bool Duration(double? value, double seconds) => value == null || value >= 0 && value <= seconds + 1e-7 && double.IsFinite(value.Value);
    internal static bool Valid(Sample sample)
    {
        if (sample.GpuEngineVersion != Version || sample.GpuEngines == null || sample.GpuEngines.Count > Kinds.Length
            || !double.IsFinite(sample.Seconds) || sample.Seconds is < 0 or > 15) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var engine in sample.GpuEngines)
        {
            if (engine == null || !Kinds.Contains(engine.Kind) || !seen.Add(engine.Kind) || engine.Sources == null
                || engine.Sources.Length is < 1 or > 256 || engine.Sources.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 160)
                || !Percent(engine.Load) || !Percent(engine.Peak) || engine.Peak < engine.Load
                || engine.Complete && !engine.Load.HasValue || !Duration(engine.ObservedSeconds, sample.Seconds)
                || !Duration(engine.CoverageSeconds, sample.Seconds) || !Duration(engine.HeavySeconds, sample.Seconds) || !Duration(engine.HighSeconds, sample.Seconds)) return false;
            var classified = GpuLoadObservations.Normalize(engine.Sources.Select(s => (s, (double?)0)));
            if (classified.Count != 1 || classified[0].Kind != engine.Kind || classified[0].Sources.Length != engine.Sources.Length) return false;
            double observed = Observed(sample, engine), covered = Covered(sample, engine);
            if (engine.CoverageSeconds > observed + 1e-7 || engine.HeavySeconds > observed + 1e-7 || engine.HighSeconds > observed + 1e-7
                || !engine.Load.HasValue && (engine.Peak.HasValue || engine.ObservedSeconds > 0 || engine.CoverageSeconds > 0 || engine.HeavySeconds > 0 || engine.HighSeconds > 0)
                || High(sample, engine) > Heavy(sample, engine) + 1e-7 || engine.Complete && covered + 1e-7 < observed
                || Heavy(sample, engine) > 0 && (engine.Peak ?? engine.Load) < 80
                || High(sample, engine) > 0 && (engine.Peak ?? engine.Load) < 95) return false;
        }
        return true;
    }
    internal static double Observed(Sample s, GpuEngineSample e) => e.Load.HasValue ? Math.Clamp(e.ObservedSeconds ?? s.Seconds, 0, Math.Max(0, s.Seconds)) : 0;
    internal static double Covered(Sample s, GpuEngineSample e) => Math.Clamp(e.CoverageSeconds ?? (e.Complete ? Observed(s, e) : 0), 0, Observed(s, e));
    internal static double Heavy(Sample s, GpuEngineSample e) => Math.Clamp(e.HeavySeconds ?? (e.Load >= 80 ? Observed(s, e) : 0), 0, Observed(s, e));
    internal static double High(Sample s, GpuEngineSample e) => Math.Clamp(e.HighSeconds ?? (e.Load >= 95 ? Observed(s, e) : 0), 0, Observed(s, e));
    internal static List<GpuEngineSample> Current(IReadOnlyList<GpuLoadObservation> values) => values
        .Where(v => v.Sources.Length is > 0 and <= 256 && v.Sources.All(s => s.Length <= 160)).Select(v =>
        new GpuEngineSample(v.Kind, v.Sources, v.Load) { Complete = v.Complete }).ToList();

    internal static List<GpuEngineSample> Merge(IReadOnlyList<Sample> source)
    {
        double duration = source.Where(s => s.Active && s.Seconds is > 0 and <= 15).Sum(s => s.Seconds);
        if (source.Count == 1 && source[0].Seconds == 0 && Valid(source[0]))
            return source[0].GpuEngines!.Select(e => e with { ObservedSeconds = 0, CoverageSeconds = 0, HeavySeconds = 0, HighSeconds = 0 }).ToList();
        var measured = source.Where(s => (duration == 0 || s.Active) && s.Seconds is > 0 and <= 15 && Valid(s)).ToArray();
        var result = new List<GpuEngineSample>();
        foreach (string kind in Kinds)
        {
            var readings = measured.SelectMany(s => s.GpuEngines!.Where(e => e.Kind == kind).Select(e => (Sample: s, Engine: e))).ToArray();
            if (readings.Length == 0) continue;
            double observed = readings.Sum(r => Observed(r.Sample, r.Engine));
            bool sameSources = readings.Select(r => string.Join("\n", r.Engine.Sources.Order(StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Count() == 1;
            double covered = sameSources ? readings.Sum(r => Covered(r.Sample, r.Engine)) : 0;
            var values = readings.Where(r => Observed(r.Sample, r.Engine) > 0).ToArray();
            // One category may expose different node names over time. Preserve every
            // observed name; neither their count nor completeness proves GPU capability.
            var sources = readings.SelectMany(r => r.Engine.Sources.GroupBy(s => s, StringComparer.Ordinal).Select(g => (Name: g.Key, Count: g.Count())))
                .GroupBy(s => s.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => Enumerable.Repeat(g.Key, g.Max(s => s.Count))).ToArray();
            // The bounded history format cannot safely preserve an oversized scope.
            // Keep that category unknown rather than write a truncated complete scope.
            if (sources.Length > 256) continue;
            result.Add(new(kind, sources, observed > 0 ? values.Sum(r => r.Engine.Load!.Value * Observed(r.Sample, r.Engine)) / observed : null)
            {
                Complete = observed > 0 && sameSources && readings.All(r => r.Engine.Complete) && covered >= (duration > 0 ? duration : source.Sum(s => s.Seconds)) - 1e-7,
                Peak = values.Length > 0 ? values.Max(r => r.Engine.Peak ?? r.Engine.Load) : null,
                ObservedSeconds = duration > 0 ? observed : 0, CoverageSeconds = duration > 0 ? covered : 0,
                HeavySeconds = duration > 0 ? readings.Sum(r => Heavy(r.Sample, r.Engine)) : 0, HighSeconds = duration > 0 ? readings.Sum(r => High(r.Sample, r.Engine)) : 0
            });
        }
        return result;
    }
    internal static string Describe(Sample sample)
    {
        if (!Valid(sample)) return "尚无分引擎记录；缺失不表示负载为零或不支持该功能。";
        return (sample.GpuEngines!.Count == 0 ? "本次未取得可分类的引擎读数。" : string.Join("\n", sample.GpuEngines.Select(e => $"{Label(e.Kind)}：{(e.Load.HasValue ? $"{e.Load:0.0}%" : "未取得")}{(e.Complete ? "" : "（部分读数缺失）")} · {string.Join("、", e.Sources)}")))
            + "\n仅列出已取得的传感器类别；缺少类别不表示不支持该任务。不同引擎占用不能相加，也不能由低占用直接判断独显多余。";
    }
}
