namespace PerfCompass;

public static class AnalysisEngine
{
    public static string NormalizeScene(string? scene) => string.IsNullOrWhiteSpace(scene) ? "未标记" : scene;
    public static double? Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return null;
        values.Sort();
        double index = (values.Count - 1) * p;
        int low = (int)index, high = (int)Math.Ceiling(index);
        return values[low] + (values[high] - values[low]) * (index - low);
    }
    public static AnalysisReport Analyze(IEnumerable<Sample> samples, Settings settings, int windowDays, string? selectedScene = null, DateTimeOffset? asOf = null)
    {
        var now = asOf ?? DateTimeOffset.Now;
        var temporal = new TemporalDemandAnalyzer(now);
        var cpu = new WeightedSeries(); var core = new WeightedSeries(); var memory = new WeightedSeries();
        var commit = new WeightedSeries(); var gpu = new WeightedSeries(); var vram = new WeightedSeries();
        var sceneStats = new Dictionary<string, SceneAccumulator>(StringComparer.Ordinal);
        var dates = new HashSet<DateTime>(); var scenes = new HashSet<string>();
        // Only keep one hourly aggregate, not raw snapshots, in the report.
        var bins = new SortedDictionary<long, (double C, int Cn, double M, int Mn, double G, int Gn)>();
        double seconds = 0, cpuSeconds = 0, cpuHigh = 0, memorySeconds = 0, gpuSeconds = 0, coreSeconds = 0, commitSeconds = 0;
        double diskPeak = 0; double? memoryPeak = null, gpuPeak = null; int count = 0; Sample? latest = null;
        foreach (var raw in samples)
        {
            var s = Sanitize(raw);
            if (s.Time > now) continue;
            if (selectedScene == null || s.Scene == selectedScene) temporal.Add(s);
            if (s.Time < now.AddDays(-windowDays)) continue;
            if (!sceneStats.TryGetValue(s.Scene, out var scene)) sceneStats[s.Scene] = scene = new SceneAccumulator(s.Scene);
            scene.Add(s);
            if (selectedScene != null && s.Scene != selectedScene) continue;
            count++; if (latest == null || s.Time >= latest.Time) latest = s;
            long bin = s.Time.ToUnixTimeSeconds() / 3600;
            bins.TryGetValue(bin, out var b);
            if (s.Cpu is double c) { b.C += c; b.Cn++; }
            if (s.MemoryUsedGb is double m && s.MemoryTotalGb is > 0) { b.M += m / s.MemoryTotalGb.Value * 100; b.Mn++; }
            if (s.Gpu is double g) { b.G += g; b.Gn++; }
            bins[bin] = b;
            diskPeak = Math.Max(diskPeak, s.MaxDiskPercent);
            if (!s.Active || s.Seconds <= 0) continue;
            double dt = Math.Min(s.Seconds, 15);
            seconds += dt; dates.Add(s.Time.LocalDateTime.Date); scenes.Add(s.Scene);
            double Coverage(double? measured) => Math.Clamp(measured ?? dt, 0, dt);
            if (s.Cpu is double cv) { cpu.Add(cv, Coverage(s.CpuCoverage)); cpuSeconds += Coverage(s.CpuCoverage); cpuHigh += Math.Clamp(s.CpuHighSeconds ?? (cv >= 90 ? Coverage(s.CpuCoverage) : 0), 0, Coverage(s.CpuCoverage)); }
            if (s.CoreMax is double co) { core.Add(co, Coverage(s.CoreCoverage)); coreSeconds += Coverage(s.CoreCoverage); }
            if (s.MemoryUsedGb is double mv && s.MemoryTotalGb is > 0 && Coverage(s.MemoryCoverage) > 0) { memory.Add(mv, Coverage(s.MemoryCoverage)); memoryPeak = Math.Max(memoryPeak ?? 0, s.MemoryPeakGb ?? mv); memorySeconds += Coverage(s.MemoryCoverage); }
            if (s.CommitGb is double cm) { commit.Add(cm, Coverage(s.CommitCoverage)); commitSeconds += Coverage(s.CommitCoverage); }
            if (s.Gpu is double gv) { gpu.Add(gv, Coverage(s.GpuCoverage)); gpuSeconds += Coverage(s.GpuCoverage); if (Coverage(s.GpuCoverage) > 0) gpuPeak = Math.Max(gpuPeak ?? 0, s.GpuPeak ?? gv); }
            if (s.VramUsedGb is double vr) vram.Add(vr, Coverage(s.GpuCoverage));
        }
        bool ready = seconds >= 36000 && dates.Count >= 3 && settings.Representative && temporal.FreshObservationReady;
        var cards = new List<Advice>();
        string State(bool enough) => enough ? "fit" : "unknown";
        string F(double? x, string unit = "%") => x.HasValue ? $"{x:0.0}{unit}" : "缺失";
        var c95 = cpu.Quantile(.95); var core95 = core.Quantile(.95);
        bool cpuCoverage = cpuSeconds >= seconds * .95 && coreSeconds >= seconds * .95 && seconds > 0;
        string cs = temporal.HasRecentPressure("cpu") ? "pressure" : ready && cpuCoverage && temporal.HasFreshEvidence("cpu") && c95 < 25 && core.Maximum < 70 ? "surplus" : State(ready && cpuCoverage && temporal.HasFreshEvidence("cpu"));
        cards.Add(new("cpu", "处理器", cs,
            cs == "surplus" ? "多核算力疑似有较大余量" : cs == "pressure" ? "存在持续高负载" : "继续核对单核与多核需求",
            $"有效时段 CPU P95 {F(c95)}；最忙逻辑核心 P95 {F(core95)}；CPU ≥90% 累计 {cpuHigh / 60:0.0} 分钟。",
            cs == "surplus" ? "下次可优先比较较少核心、单核性能相近的 CPU，再用实际任务验证。" : "结合编译、渲染耗时或卡顿确认收益。高占用也可能只是正常、高效地完成任务。",
            "跨代性能、大小核和线程迁移无法按占用率线性换算。不能把 20% 占用解释成只需五分之一性能。"));
        var m95 = memory.Quantile(.95); double? peak = memoryPeak;
        double? peakCommit = commit.Maximum;
        int[] tiers = [8,16,24,32,48,64,96,128,192,256];
        int candidate = peak.HasValue && peakCommit.HasValue ? tiers.FirstOrDefault(t => peak <= t * .70 && peakCommit <= t * .90) : 0;
        bool memCoverage = memorySeconds >= seconds * .95 && commitSeconds >= seconds * .95 && seconds > 0;
        string ms = temporal.HasRecentPressure("memory") ? "pressure" : ready && memCoverage && temporal.HasFreshEvidence("memory") && candidate > 0 && candidate < latest?.MemoryTotalGb * .9 ? "surplus" : State(ready && memCoverage && temporal.HasFreshEvidence("memory"));
        cards.Add(new("memory", "内存", ms,
            ms == "surplus" ? $"可评估 {candidate} GB，当前容量可能偏高" : ms == "pressure" ? "内存余量曾持续偏低" : "保留峰值，再评估容量",
            $"当前 {F(latest?.MemoryTotalGb, " GB")}；有效时段使用量 P95 {F(m95, " GB")}，峰值 {F(peak, " GB")}；提交量峰值 {F(peakCommit, " GB")}。",
            ms == "surplus" ? $"{candidate} GB 是下次购机的验证候选；先在该容量机器上重跑最重的多任务组合。" : "覆盖虚拟机、大工程和多任务。余量偏低时，检查分页与卡顿是否同时发生，再决定是否升级。",
            "降档候选保留至少 30% 物理容量余量，并用提交量作保守约束。提交量不等于物理需求；本版未测硬缺页，不能证明内存瓶颈。"));
        var g95 = gpu.Quantile(.95);
        bool rareGpuWork = gpuPeak >= 90 && g95 < 30;
        string gs = temporal.HasRecentPressure("gpu") ? "pressure" : ready && gpuSeconds >= seconds * .9 && temporal.HasFreshEvidence("gpu") && g95 < 30 && !rareGpuWork ? "review" : State(ready && gpuSeconds >= seconds * .9 && temporal.HasFreshEvidence("gpu") && gpu.Maximum.HasValue);
        cards.Add(new("gpu", "显卡", gs,
            !gpu.Maximum.HasValue ? "GPU 负载数据尚不可用" : gs == "review" ? "负载偏低，降档仍需验证" : gs == "pressure" ? "GPU 存在持续高负载" : rareGpuWork ? "存在偶发重负载，保留该任务需求" : "观察图形与计算任务需求",
            $"覆盖 {gpuSeconds / Math.Max(seconds, 1) * 100:0}% 有效时段；GPU P95 {F(g95)}，峰值 {F(gpuPeak)}；显存分配峰值 {F(vram.Maximum, " GB")}。",
            rareGpuWork ? "在场景明细中找出重负载任务，再用候选显卡验证该任务的帧率、耗时与功能。" : "核对目标分辨率、帧率、编码、CUDA/AI 和多屏需求。只有低档候选卡也满足这些条件，才有降档依据。",
            "本版没有帧时间、编码引擎与候选卡基准测试。低利用率可能来自锁帧或 CPU 瓶颈，不直接判定独显过剩。多显卡仅分析界面标明的卡。"));
        cards.Add(new("disk", "存储", diskPeak >= 90 ? "pressure" : State(ready),
            diskPeak >= 90 ? "至少一个分区空间曾接近用满" : "容量和速度需要分别评估",
            $"当前固定卷合计已用 {latest?.DiskUsedGb:0} / {latest?.DiskTotalGb:0} GB；观测到最高分区使用率 {diskPeak:0.0}%。",
            "下次选容量时逐卷核对数据、安装空间与增长量；判断 SSD 性能档位时，需要比较实际任务耗时。",
            "分区不等于物理硬盘。低读写吞吐不能证明高端 SSD 无用；本版不据此推荐降速或具体型号。"));
        if (count == 0)
            cards = cards.Select(c => c with { Status = "unknown", Title = "等待真实使用记录", Evidence = "当前时间范围内尚无记录。" }).ToList();
        var trends = temporal.Build();
        cards = TemporalDemandAnalyzer.PrioritizeRecent(cards, trends);
        return new(now, windowDays, count, seconds / 3600, dates.Count, ready, scenes.Order().ToArray(), cards,
            bins.Select(kv => new HistoryPoint(DateTimeOffset.FromUnixTimeSeconds(kv.Key * 3600), kv.Value.Cn > 0 ? kv.Value.C / kv.Value.Cn : null,
                kv.Value.Mn > 0 ? kv.Value.M / kv.Value.Mn : null, kv.Value.Gn > 0 ? kv.Value.G / kv.Value.Gn : null)).ToList(), settings.FutureNeeds)
            { SelectedScene = selectedScene, DemandTrends = trends, FreshActiveHours = temporal.FreshHours, FreshActiveDays = temporal.FreshDays,
                SceneProfiles = sceneStats.Values.Select(s => s.Profile()).OrderByDescending(p => p.ActiveHours).ThenBy(p => p.Name).ToList() };
    }

    private static Sample Sanitize(Sample sample)
    {
        double? Nonnegative(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
        double? Percent(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value) ? value : null;
        double? total = Nonnegative(sample.MemoryTotalGb), used = Nonnegative(sample.MemoryUsedGb), peak = Nonnegative(sample.MemoryPeakGb);
        if (total is not > 0 || used > total) used = null;
        if (peak < used || peak > total) peak = null;
        double? gpu = Percent(sample.Gpu), gpuPeak = Percent(sample.GpuPeak);
        if (gpuPeak < gpu) gpuPeak = gpu;
        double? Coverage(double? value) => value.HasValue && (!double.IsFinite(value.Value) || value < 0) ? 0 : value;
        return sample with
        {
            Scene = NormalizeScene(sample.Scene),
            Seconds = double.IsFinite(sample.Seconds) && sample.Seconds is > 0 and <= 15 ? sample.Seconds : 0,
            Cpu = Percent(sample.Cpu), CoreMax = Percent(sample.CoreMax), Gpu = gpu, GpuPeak = gpuPeak,
            MemoryTotalGb = total, MemoryUsedGb = used, MemoryPeakGb = peak, CommitGb = Nonnegative(sample.CommitGb), VramUsedGb = Nonnegative(sample.VramUsedGb),
            CpuCoverage = Coverage(sample.CpuCoverage), CoreCoverage = Coverage(sample.CoreCoverage), MemoryCoverage = Coverage(sample.MemoryCoverage),
            CommitCoverage = Coverage(sample.CommitCoverage), GpuCoverage = Coverage(sample.GpuCoverage),
            CpuHighSeconds = Exposure(sample.CpuHighSeconds), GpuHeavySeconds = Exposure(sample.GpuHeavySeconds), GpuHighSeconds = Exposure(sample.GpuHighSeconds), MemoryHighSeconds = Exposure(sample.MemoryHighSeconds),
            MaxDiskPercent = Percent(sample.MaxDiskPercent) ?? 0
        };
        double? Exposure(double? value) => value.HasValue && double.IsFinite(value.Value) && value >= 0 && value <= sample.Seconds ? value : null;
    }

    private sealed class SceneAccumulator(string name)
    {
        private readonly WeightedSeries cpu = new(), core = new(), gpu = new();
        private int count; private double seconds; private double? memoryPeak, gpuPeak;
        public void Add(Sample s)
        {
            count++;
            if (!s.Active || s.Seconds <= 0) return;
            seconds += s.Seconds;
            double Coverage(double? value) => Math.Clamp(value ?? s.Seconds, 0, s.Seconds);
            cpu.Add(s.Cpu, Coverage(s.CpuCoverage)); core.Add(s.CoreMax, Coverage(s.CoreCoverage)); gpu.Add(s.Gpu, Coverage(s.GpuCoverage));
            if (s.Gpu.HasValue && Coverage(s.GpuCoverage) > 0) gpuPeak = Math.Max(gpuPeak ?? 0, s.GpuPeak ?? s.Gpu.Value);
            if (s.MemoryUsedGb.HasValue && Coverage(s.MemoryCoverage) > 0) memoryPeak = Math.Max(memoryPeak ?? 0, s.MemoryPeakGb ?? s.MemoryUsedGb.Value);
        }
        public SceneProfile Profile() => new(name, count, seconds / 3600, cpu.Quantile(.95), core.Quantile(.95), memoryPeak, gpu.Quantile(.95), gpuPeak,
            seconds > 0 ? cpu.Seconds / seconds : 0, seconds > 0 ? gpu.Seconds / seconds : 0);
    }
}
