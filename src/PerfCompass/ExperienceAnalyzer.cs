namespace PerfCompass;

public record ResourceClue(string Key, string Name, double? Peak, double HighSeconds, double CoveredSeconds)
{
    public string Label => CoveredSeconds > 0 ? $"{Name}峰值 {Peak:0.0}% · 高负载 {HighSeconds:0.#} / 已采样 {CoveredSeconds:0.#} 秒" : $"{Name}：无可用采样";
}
public record ExperienceEvent(string Id, DateTimeOffset Time, string HardwareId, string Scene, string Symptom,
    string Note, List<ResourceClue> Clues, bool Resolved = false)
{
    public string Title => $"{Time.LocalDateTime:MM-dd HH:mm:ss} · {Scene} · {Symptom}";
    public string StateLabel => Resolved ? "已处理" : "待核对";
    public string EvidenceLabel => string.Join("\n", Clues.Select(c => c.Label));
    public string ParsedDescription => RequirementParser.Parse(Note).Summary;
    public string Conclusion => Clues.All(c => c.CoveredSeconds == 0) ? "没有近期有效采样，仅保留体验描述。"
        : Clues.Any(c => c.HighSeconds >= 2) ? "采样中同时出现资源高负载，请复现任务核对；同时出现不代表因果关系。"
        : "此前两分钟未采到明显资源饱和；仍需核对网络、锁帧、驱动、应用等待与采样间隙。";
}

public static class ExperienceAnalyzer
{
    public static ExperienceEvent Create(DateTimeOffset now, string hardwareId, string scene, string symptom, string? note, IEnumerable<Sample> samples)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) throw new ArgumentException("Hardware identity is required.");
        scene = AnalysisEngine.NormalizeScene(scene);
        if (symptom is not ("操作卡顿" or "任务等待过久")) throw new ArgumentException("Unsupported experience type.");
        var readings = samples.Where(s => s.HardwareId == hardwareId && AnalysisEngine.NormalizeScene(s.Scene) == scene
            && s.Time <= now && s.Time > now.AddMinutes(-2) && double.IsFinite(s.Seconds) && s.Seconds is > 0 and <= 15)
            .OrderBy(s => s.Time).ToList();
        // A stopped collector must not lend stale readings to a newly reported symptom.
        if (readings.Count == 0 || now - readings[^1].Time > TimeSpan.FromSeconds(6)) readings.Clear();
        var keys = new[] { "cpu", "core", "memory", "gpu", "disk" };
        var names = new[] { "CPU", "最忙核心", "内存", "GPU", "磁盘活动" };
        var peaks = new double?[5]; var high = new double[5]; var covered = new double[5];
        var lastEnd = now.AddMinutes(-2);
        foreach (var s in readings)
        {
            var start = s.Time.AddSeconds(-s.Seconds);
            if (start < lastEnd) start = lastEnd;
            double elapsed = Math.Max(0, (s.Time - start).TotalSeconds); lastEnd = s.Time;
            double? memory = s.MemoryTotalGb is > 0 ? s.MemoryUsedGb / s.MemoryTotalGb * 100 : null;
            double?[] values = [s.Cpu, s.CoreMax, memory, s.Gpu, s.DiskBusy];
            double?[] coverage = [s.CpuCoverage, s.CoreCoverage, s.MemoryCoverage, s.GpuCoverage, null];
            for (int i = 0; i < 5; i++)
            {
                if (values[i] is not double value || !double.IsFinite(value) || value is < 0 or > 100) continue;
                double measured = coverage[i] ?? s.Seconds;
                if (!double.IsFinite(measured) || measured < 0 || measured > s.Seconds) continue;
                double dt = elapsed * measured / s.Seconds;
                if (dt <= 0) continue;
                peaks[i] = Math.Max(peaks[i] ?? 0, value); covered[i] += dt;
                if (value >= (i == 3 ? 95 : 90)) high[i] += dt;
            }
        }
        string text = (note ?? "").Trim(); if (text.Length > 500) text = text[..500];
        return new(Guid.NewGuid().ToString("N"), now, hardwareId, scene, symptom, text,
            keys.Select((key, i) => new ResourceClue(key, names[i], peaks[i], high[i], covered[i])).ToList());
    }

    public static AnalysisReport Attach(AnalysisReport report, IEnumerable<ExperienceEvent> events)
    {
        var selected = events.Where(e => e.Time <= report.Generated && e.Time >= report.Generated.AddDays(-report.WindowDays)
            && (report.SelectedScene == null || e.Scene == report.SelectedScene)).OrderByDescending(e => e.Time).ToList();
        bool pending = selected.Any(e => !e.Resolved && e.Time >= report.Generated.AddDays(-7));
        var cards = report.Cards.Select(c => pending && (c.Status is "surplus" or "review" || c.Status is ("unknown" or "fit")
            && (c.Observation?.State == "light" || c.UsesRetainedWorkload)) ? c with
        {
            Status = "experience", Title = "先核对近期体验，再判断配置余量",
            Evidence = c.Evidence + " 最近 7 天有尚未处理的卡顿或等待记录。",
            Suggestion = "在相同任务中复现并核对体验记录；处理后可标为已处理，再比较降档候选。低占用不能证明体验达标，也不能证明某个部件是原因。"
        } : c).ToList();
        return report with { Cards = cards, ExperienceEvents = selected };
    }
}
