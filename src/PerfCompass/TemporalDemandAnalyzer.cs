namespace PerfCompass;

/// <summary>Comparable adjacent periods plus a recent-workload floor; thresholds are transparent heuristics.</summary>
public sealed class TemporalDemandAnalyzer(DateTimeOffset now)
{
    private sealed class Period
    {
        public readonly WeightedSeries Cpu = new(), Core = new(), Gpu = new(), Memory = new(), MemoryLoad = new(), Commit = new();
        public readonly HashSet<DateTime> CpuDays = [], CoreDays = [], GpuDays = [], MemoryDays = [], CommitDays = [], ActiveDays = [];
        public double ActiveSeconds;
        public double CpuHighSeconds, GpuHeavySeconds, GpuHighSeconds, MemoryHighSeconds;
        public readonly Dictionary<string, double> GpuHeavyScenes = [], CpuHeavyScenes = [];
        public void Add(Sample s)
        {
            if (!s.Active || s.Seconds is <= 0 or > 15) return;
            ActiveSeconds += s.Seconds; ActiveDays.Add(s.Time.LocalDateTime.Date);
            double C(double? measured) => Math.Clamp(measured ?? s.Seconds, 0, s.Seconds);
            double cpuSeconds = C(s.CpuCoverage), gpuSeconds = C(s.GpuCoverage), memorySeconds = C(s.MemoryCoverage);
            double coreSeconds = C(s.CoreCoverage), commitSeconds = C(s.CommitCoverage);
            Cpu.Add(s.Cpu, cpuSeconds, max: 100); Core.Add(s.CoreMax, coreSeconds, max: 100);
            Gpu.Add(s.Gpu, gpuSeconds, max: 100); Memory.Add(s.MemoryUsedGb, memorySeconds); Commit.Add(s.CommitGb, commitSeconds);
            if (s.MemoryTotalGb is > 0) MemoryLoad.Add(s.MemoryUsedGb / s.MemoryTotalGb * 100, memorySeconds, max: 100);
            double Exposure(double? measured, bool high, double covered) => Math.Clamp(measured ?? (high ? covered : 0), 0, covered);
            CpuHighSeconds += Exposure(s.CpuHighSeconds, s.Cpu >= 90, cpuSeconds);
            GpuHeavySeconds += Exposure(s.GpuHeavySeconds, s.Gpu >= 80, gpuSeconds);
            GpuHighSeconds += Exposure(s.GpuHighSeconds, s.Gpu >= 95, gpuSeconds);
            MemoryHighSeconds += Exposure(s.MemoryHighSeconds, s.MemoryTotalGb is > 0 && s.MemoryUsedGb / s.MemoryTotalGb >= .9, memorySeconds);
            if (s.Cpu.HasValue && cpuSeconds > 0) CpuDays.Add(s.Time.LocalDateTime.Date);
            if (s.Gpu.HasValue && gpuSeconds > 0) GpuDays.Add(s.Time.LocalDateTime.Date);
            if (s.MemoryUsedGb.HasValue && memorySeconds > 0) MemoryDays.Add(s.Time.LocalDateTime.Date);
            if (s.CoreMax.HasValue && coreSeconds > 0) CoreDays.Add(s.Time.LocalDateTime.Date);
            if (s.CommitGb.HasValue && commitSeconds > 0) CommitDays.Add(s.Time.LocalDateTime.Date);
            if (s.Cpu >= 80 && cpuSeconds > 0) CpuHeavyScenes[s.Scene] = CpuHeavyScenes.GetValueOrDefault(s.Scene) + cpuSeconds;
            double gpuHeavy = Exposure(s.GpuHeavySeconds, s.Gpu >= 80, gpuSeconds);
            if (gpuHeavy > 0) GpuHeavyScenes[s.Scene] = GpuHeavyScenes.GetValueOrDefault(s.Scene) + gpuHeavy;
        }
    }
    private readonly Period week = new(), previousWeek = new(), month = new(), previousMonth = new();
    public double FreshHours => week.ActiveSeconds / 3600;
    public int FreshDays => week.ActiveDays.Count;
    public bool FreshObservationReady => week.ActiveSeconds >= 1800 && week.ActiveDays.Count >= 2;
    public bool HasFreshEvidence(string key) => key switch
    {
        "cpu" => week.Cpu.Seconds >= 1800 && week.CpuDays.Count >= 2 && week.Core.Seconds >= 1800 && week.CoreDays.Count >= 2,
        "memory" => week.Memory.Seconds >= 1800 && week.MemoryDays.Count >= 2 && week.Commit.Seconds >= 1800 && week.CommitDays.Count >= 2,
        "gpu" => week.Gpu.Seconds >= 1800 && week.GpuDays.Count >= 2,
        _ => FreshObservationReady
    };
    public bool HasRecentPressure(string key)
    {
        var metric = key switch { "cpu" => week.Cpu, "gpu" => week.Gpu, "memory" => week.MemoryLoad, _ => null };
        if (metric == null || metric.Seconds < 1800 || metric.Seconds < week.ActiveSeconds * .9) return false;
        double high = key switch { "cpu" => week.CpuHighSeconds, "gpu" => week.GpuHighSeconds, _ => week.MemoryHighSeconds };
        return high / metric.Seconds >= (key == "memory" ? .05 : .1);
    }
    public void Add(Sample s)
    {
        double age = (now - s.Time).TotalDays;
        if (age < 0 || age >= 60) return;
        if (age < 7) week.Add(s); else if (age < 14) previousWeek.Add(s);
        if (age < 30) month.Add(s); else previousMonth.Add(s);
    }
    public List<DemandTrend> Build()
    {
        var result = new List<DemandTrend>();
        Compare(week, previousWeek, 7, result);
        Compare(month, previousMonth, 30, result);
        return result;
    }
    private static void Compare(Period recent, Period previous, int days, List<DemandTrend> result)
    {
        result.Add(Trend("gpu", "显卡", "%", days, recent.Gpu, previous.Gpu, recent.GpuDays.Count, previous.GpuDays.Count, recent.GpuHeavyScenes, recent.ActiveSeconds, previous.ActiveSeconds, recent.GpuHeavySeconds, previous.GpuHeavySeconds));
        result.Add(Trend("cpu", "处理器", "%", days, recent.Cpu, previous.Cpu, recent.CpuDays.Count, previous.CpuDays.Count, recent.CpuHeavyScenes, recent.ActiveSeconds, previous.ActiveSeconds, recent.Cpu.TimeAbove(80), previous.Cpu.TimeAbove(80)));
        result.Add(Trend("memory", "内存", " GiB", days, recent.Memory, previous.Memory, recent.MemoryDays.Count, previous.MemoryDays.Count, [], recent.ActiveSeconds, previous.ActiveSeconds, 0, 0));
    }
    private static DemandTrend Trend(string key, string name, string unit, int days, WeightedSeries recent, WeightedSeries previous,
        int recentDays, int previousDays, Dictionary<string, double> heavyScenes, double recentObserved, double previousObserved, double busy, double priorBusy)
    {
        double? current = recent.Quantile(.95), before = previous.Quantile(.95);
        double minHours = days == 7 ? .5 : 3; int minDays = days == 7 ? 2 : 3;
        bool comparable = recent.Seconds >= minHours * 3600 && previous.Seconds >= minHours * 3600 && recentDays >= minDays && previousDays >= minDays
            && recent.Seconds >= recentObserved * .9 && previous.Seconds >= previousObserved * .9;
        double share = busy / Math.Max(1, recent.Seconds), priorShare = priorBusy / Math.Max(1, previous.Seconds);
        bool increase = false, decrease = false;
        if (comparable && current.HasValue && before.HasValue)
        {
            if (key == "memory")
            {
                increase = current >= before + Math.Max(2, before.Value * .25);
                decrease = before >= current + Math.Max(2, current.Value * .25);
            }
            else
            {
                increase = current >= 60 && current - before >= 25 || share >= .1 && share - priorShare >= .08 && busy >= 600;
                decrease = before >= 60 && before - current >= 25 || priorShare >= .1 && priorShare - share >= .08 && priorBusy >= 600;
            }
        }
        string status = !comparable ? "insufficient" : increase ? "increased" : decrease ? "decreased" : "similar";
        string summary = status switch
        {
            "increased" => $"最近 {days} 天的{name}负载较此前增加",
            "decreased" => $"最近 {days} 天的{name}负载较此前减少",
            "similar" => $"两个时期的{name}需求接近",
            _ => recent.Seconds < minHours * 3600 || recentDays < minDays ? $"继续积累最近 {days} 天的{name}任务记录" : $"缺少此前 {days} 天的{name}对照记录"
        };
        string suggestion = status switch
        {
            "increased" => "核对近期新增的任务、画质或并发设置，以及异常后台占用。若这些任务今后仍会继续使用，购机时优先满足它们，并验证候选机器的实际表现。",
            "decreased" => "确认过去的重负载任务是否已经停用；近期没再运行，还不足以说明以后不再需要。",
            "similar" => "结合当前压力与余量继续比较候选配置。",
            _ => $"每个时期至少需要 {minHours:0.#} 小时、{minDays} 天的有效观测，且传感器有效覆盖达到 90%，再进行趋势对照。"
        };
        return new(key, name, unit, days, status, current, before, recent.Seconds / 3600, previous.Seconds / 3600,
            busy / 60, priorBusy / 60, recentDays, previousDays, heavyScenes.OrderByDescending(p => p.Value).Select(p => p.Key).Take(3).ToArray(), summary, suggestion);
    }
    public static List<Advice> PrioritizeRecent(List<Advice> cards, IReadOnlyList<DemandTrend> trends)
    {
        return cards.Select(card =>
        {
            var increase = trends.Where(t => t.Key == card.Key && t.Status == "increased").OrderBy(t => t.Days).FirstOrDefault();
            if (increase == null) return card;
            string sceneText = increase.RecentHeavyScenes.Length > 0 ? $"重负载场景：{string.Join("、", increase.RecentHeavyScenes)}。" : "";
            return card with
            {
                Status = card.Status is "surplus" or "fit" or "unknown" or "review" ? "changed" : card.Status,
                Title = $"近期{card.Name}负载增加，核对新任务需求",
                Evidence = card.Evidence + $" 最近 {increase.Days} 天 / 此前 {increase.Days} 天 P95：{increase.RecentP95:0.0}{increase.Unit} / {increase.PreviousP95:0.0}{increase.Unit}。" + sceneText,
                Suggestion = increase.Suggestion
            };
        }).ToList();
    }
}
