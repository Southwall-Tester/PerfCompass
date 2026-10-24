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
    public static AnalysisReport Analyze(IEnumerable<Sample> samples, Settings settings, int windowDays, string? selectedScene = null, DateTimeOffset? asOf = null, string? hardwareId = null, Func<HistoryQuality>? readQuality = null)
    {
        var now = asOf ?? DateTimeOffset.Now;
        var temporal = new TemporalDemandAnalyzer(now);
        var gpuEngines = new GpuEngineAnalysis(now, windowDays);
        var cpu = new WeightedSeries(); var core = new WeightedSeries(); var memory = new WeightedSeries();
        var commit = new WeightedSeries(); var gpu = new WeightedSeries(); var vram = new WeightedSeries();
        var sceneStats = new Dictionary<string, SceneAccumulator>(StringComparer.Ordinal);
        var dates = new HashSet<DateTime>(); var scenes = new HashSet<string>();
        var hardwareIds = new HashSet<string>(StringComparer.Ordinal);
        var gpuMetricScopes = new HashSet<string>(StringComparer.Ordinal);
        var gpuHistoryScopes = new HashSet<string>(StringComparer.Ordinal);
        // Only keep one hourly aggregate, not raw snapshots, in the report.
        var bins = new SortedDictionary<long, HourAccumulator>();
        double seconds = 0, cpuSeconds = 0, cpuHigh = 0, memorySeconds = 0, gpuSeconds = 0, coreSeconds = 0, commitSeconds = 0;
        double diskPeak = 0; double? memoryPeak = null, gpuPeak = null; int count = 0, samplingGaps = 0; Sample? latest = null;
        foreach (var raw in samples)
        {
            var s = Sanitize(raw);
            if (s.Time > now) continue;
            if (!string.IsNullOrWhiteSpace(hardwareId) && s.HardwareId != hardwareId) continue;
            if (s.Time >= now.AddDays(-Math.Max(windowDays, 60))) hardwareIds.Add(s.HardwareId ?? "");
            var measured = s with { Seconds = (selectedScene == null ? s.UniqueObservationSeconds : s.UniqueSceneSeconds) ?? s.Seconds };
            if (selectedScene == null || s.Scene == selectedScene) { temporal.Add(measured); gpuEngines.Add(measured); }
            if (s.Time < now.AddDays(-windowDays)) continue;
            if (!sceneStats.TryGetValue(s.Scene, out var scene)) sceneStats[s.Scene] = scene = new SceneAccumulator(s.Scene);
            scene.Add(s);
            if (selectedScene != null && s.Scene != selectedScene) continue;
            temporal.AddSelected(measured);
            if (s.Seconds == 0 && SamplingClock.KnownIssue(s.SamplingIssue)) samplingGaps++;
            count++; if (latest == null || s.Time >= latest.Time) latest = s;
            if (s.Seconds > 0)
            {
                // History includes recorded idle intervals. Purchase statistics
                // below retain their separate active-only gate. Split the recorded
                // interval, not its end timestamp, across hours and the time window.
                long end = s.Time.UtcTicks;
                long duration = (long)Math.Round(s.Seconds * TimeSpan.TicksPerSecond);
                long start = Math.Max(end - duration, now.AddDays(-windowDays).UtcTicks);
                if (duration > 0 && start < end)
                {
                    double C(double? value) => Math.Clamp(value ?? s.Seconds, 0, s.Seconds);
                    double cpuCovered = C(s.CpuCoverage), memoryCovered = C(s.MemoryCoverage), gpuCovered = C(s.GpuCoverage);
                    if (s.Gpu.HasValue && gpuCovered > 0) gpuHistoryScopes.Add(GpuEngineEvidence.MetricScope(s));
                    for (long cursor = start; cursor < end;)
                    {
                        long bin = cursor - cursor % TimeSpan.TicksPerHour;
                        long stop = Math.Min(end, bin + TimeSpan.TicksPerHour);
                        double share = (stop - cursor) / (double)duration;
                        if (!bins.TryGetValue(bin, out var b)) bins[bin] = b = new();
                        bool portion = stop - cursor < duration;
                        // Coverage records a total, not the positions of successful
                        // reads. Proportional allocation is explicitly an estimate
                        // when only part of a partially covered interval is assigned.
                        b.Cpu.Add(s.Cpu, cpuCovered * share, portion && cpuCovered + 1e-7 < s.Seconds);
                        b.Memory.Add(s.MemoryTotalGb is > 0 ? s.MemoryUsedGb / s.MemoryTotalGb * 100 : null,
                            memoryCovered * share, portion && memoryCovered + 1e-7 < s.Seconds);
                        b.Gpu.Add(s.Gpu, gpuCovered * share, portion && gpuCovered + 1e-7 < s.Seconds);
                        cursor = stop;
                    }
                }
            }
            diskPeak = Math.Max(diskPeak, s.MaxDiskPercent);
            if (!s.Active || s.Seconds <= 0) continue;
            double dt = Math.Min((selectedScene == null ? s.UniqueObservationSeconds : s.UniqueSceneSeconds) ?? s.Seconds, 15);
            seconds += dt; dates.Add(s.Time.LocalDateTime.Date); scenes.Add(s.Scene);
            double Coverage(double? measured) => Math.Clamp(measured ?? dt, 0, dt);
            if (s.Cpu is double cv) { cpu.Add(cv, Coverage(s.CpuCoverage)); cpuSeconds += Coverage(s.CpuCoverage); cpuHigh += Math.Clamp(s.CpuHighSeconds ?? (cv >= 90 ? Coverage(s.CpuCoverage) : 0), 0, Coverage(s.CpuCoverage)); }
            if (s.CoreMax is double co) { core.Add(co, Coverage(s.CoreCoverage)); coreSeconds += Coverage(s.CoreCoverage); }
            if (s.MemoryUsedGb is double mv && s.MemoryTotalGb is > 0 && Coverage(s.MemoryCoverage) > 0) { memory.Add(mv, Coverage(s.MemoryCoverage)); memoryPeak = Math.Max(memoryPeak ?? 0, s.MemoryPeakGb ?? mv); memorySeconds += Coverage(s.MemoryCoverage); }
            if (s.CommitGb is double cm) { commit.Add(cm, Coverage(s.CommitCoverage)); commitSeconds += Coverage(s.CommitCoverage); }
            if (s.Gpu is double gv)
            {
                gpu.Add(gv, Coverage(s.GpuCoverage)); gpuSeconds += Coverage(s.GpuCoverage);
                if (Coverage(s.GpuCoverage) > 0) { gpuPeak = Math.Max(gpuPeak ?? 0, s.GpuPeak ?? gv); gpuMetricScopes.Add(GpuEngineEvidence.MetricScope(s)); }
            }
            if (s.VramUsedGb is double vr) vram.Add(vr, Coverage(s.GpuCoverage));
        }
        string scopeHardwareId = !string.IsNullOrWhiteSpace(hardwareId) ? hardwareId : hardwareIds.Count == 1 ? hardwareIds.Single() : "";
        var coverage = WorkloadCoverage.For(settings, scopeHardwareId);
        var quality = readQuality?.Invoke() ?? HistoryQuality.Unchecked;
        bool ready = !quality.HasIssues && seconds >= 36000 && dates.Count >= 3 && coverage.Confirmed && temporal.FreshObservationReady;
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
            ms == "surplus" ? $"可评估 {candidate} GiB，当前容量可能偏高" : ms == "pressure" ? "内存余量曾持续偏低" : "保留峰值，再评估容量",
            $"当前 {F(latest?.MemoryTotalGb, " GiB")}；有效时段使用量 P95 {F(m95, " GiB")}，峰值 {F(peak, " GiB")}；提交量峰值 {F(peakCommit, " GiB")}。",
            ms == "surplus" ? $"{candidate} GiB 是下次购机的验证候选；先在该容量机器上重跑最重的多任务组合。" : "覆盖虚拟机、大工程和多任务。余量偏低时，检查分页与卡顿是否同时发生，再决定是否升级。",
            "降档候选保留至少 30% 物理容量余量，并用提交量作保守约束。提交量不等于物理需求；本版未测硬缺页，不能证明内存瓶颈。"));
        bool gpuScopeComparable = gpuMetricScopes.Count == 1;
        var g95 = gpuScopeComparable ? gpu.Quantile(.95) : null;
        bool observedHeavyGpuWork = gpuPeak >= 90;
        bool rareGpuWork = observedHeavyGpuWork && g95 < 30;
        var sceneProfiles = sceneStats.Values.Select(s => s.Profile()).OrderByDescending(p => p.ActiveHours).ThenBy(p => p.Name).ToList();
        var mixedScenes = sceneProfiles.Where(p => p.GpuHasMixedSources).Select(p => p.Name).ToArray();
        string gpuScopeNotice = gpuMetricScopes.Count > 1 ? "本范围核心/3D来源或规则混合，未合并 P95；峰值仅为不同来源中已观察到的负载线索。" : "";
        if (mixedScenes.Length > 0) gpuScopeNotice += " 场景“" + string.Join("、", mixedScenes) + "”的核心/3D来源或规则混合，场景 P95 未合并。";
        string gs = temporal.HasRecentPressure("gpu") ? "pressure" : ready && gpuScopeComparable && gpuSeconds >= seconds * .9 && temporal.HasFreshEvidence("gpu") && g95 < 30 && !rareGpuWork ? "review" : State(ready && gpuScopeComparable && gpuSeconds >= seconds * .9 && temporal.HasFreshEvidence("gpu") && gpu.Maximum.HasValue);
        cards.Add(new("gpu", "显卡", gs,
            !gpu.Maximum.HasValue ? "GPU 核心/3D负载数据尚不可用" : gs == "review" ? "核心/3D负载偏低，降档仍需验证" : gs == "pressure" ? "GPU 核心/3D存在持续高负载" : rareGpuWork ? "存在偶发重负载，保留该任务需求" : observedHeavyGpuWork ? "已观察到核心/3D重负载，先核对任务需求" : "观察图形与计算任务需求",
            $"核心/3D读数覆盖 {gpuSeconds / Math.Max(seconds, 1) * 100:0}% 有效时段；P95 {F(g95)}，峰值 {F(gpuPeak)}；显存分配峰值 {F(vram.Maximum, " GiB")}。" + gpuScopeNotice,
            observedHeavyGpuWork ? "在场景明细中找出重负载任务，再用候选显卡验证该任务的帧率、耗时与功能。" : "核对目标分辨率、帧率、编码、CUDA/AI 和多屏需求。只有低档候选卡也满足这些条件，才有降档依据。",
            "核心/3D为既有记录口径，分引擎观测另列。本版没有帧时间与候选卡基准测试。低利用率可能来自锁帧或 CPU 瓶颈，不直接判定独显过剩。多显卡仅分析界面标明的卡。"));
        cards.Add(new("disk", "存储", diskPeak >= 90 ? "pressure" : State(ready),
            diskPeak >= 90 ? "至少一个分区空间曾接近用满" : "容量和速度需要分别评估",
            $"当前固定卷合计已用 {latest?.DiskUsedGb:0} / {latest?.DiskTotalGb:0} GiB；观测到最高分区使用率 {diskPeak:0.0}%。",
            "下次选容量时逐卷核对数据、安装空间与增长量；判断 SSD 性能档位时，需要比较实际任务耗时。",
            "分区不等于物理硬盘。低读写吞吐不能证明高端 SSD 无用；本版不据此推荐降速或具体型号。"));
        if (count == 0)
            cards = cards.Select(c => c with { Status = "unknown", Title = "等待真实使用记录", Evidence = "当前时间范围内尚无记录。" }).ToList();
        var trends = temporal.Build().Select(t => t.Key == "gpu" ? t with
            { Name = "GPU 核心/3D", Summary = t.Summary.Replace("显卡", "GPU 核心/3D", StringComparison.Ordinal) } : t).ToList();
        if (!quality.HasIssues) cards = TemporalDemandAnalyzer.PrioritizeRecent(cards, trends);
        cards = cards.Select(c => c.Key == "gpu" ? c with { Title = c.Title.Replace("近期显卡", "近期GPU 核心/3D", StringComparison.Ordinal) } : c).ToList();
        if (hardwareIds.Count > 0 && string.IsNullOrWhiteSpace(scopeHardwareId))
        {
            cards = cards.Select(c => c with { Status = "unknown", Title = "请先隔离同一硬件配置的记录", Suggestion = coverage.Message }).ToList();
            trends.Clear();
        }
        if (count > 0 && !string.IsNullOrWhiteSpace(scopeHardwareId))
            cards = cards.Select(card =>
            {
                var observation = temporal.Observe(card.Key, selectedScene);
                // Recent readings support a scoped observation, not an exception
                // to history integrity or the existing purchase-eligibility gates.
                if (quality.HasIntervalConflicts || observation?.State == "light"
                    && (quality.HasIssues || card.Key == "gpu" && gpuMetricScopes.Count > 1)) observation = null;
                // A selected window may include an important task before the
                // recent observation period. Keep its positive evidence separate
                // so recent quiet readings cannot erase an observed task.
                ResourceObservation? retained = card.Key == "gpu" && observedHeavyGpuWork && gpuSeconds > 0 && !quality.HasIssues && !quality.HasIntervalConflicts
                    ? new("busy", "所选范围曾有核心/3D重负载，保留该任务",
                        $"所选窗口已测核心/3D峰值 {F(gpuPeak)}；核心/3D有效读数 {DurationText.FromHours(gpuSeconds / 3600)}。" + gpuScopeNotice,
                        "用曾出现重负载的任务核对候选显卡的帧率、耗时和功能；若该任务已不再需要，分别比较保留与取消它的方案。",
                        $"所选 {windowDays} 天 · {selectedScene ?? "全部场景"} · 已测核心/3D",
                        gpuSeconds / 3600, Math.Clamp(gpuSeconds / Math.Max(seconds, double.Epsilon), 0, 1)) : null;
                return card with { Observation = observation, RetainedWorkload = retained,
                    Evidence = card.Evidence + (retained == null ? "" : " 保留任务范围：" + retained.Scope + "。") };
            }).ToList();
        var result = new AnalysisReport(now, windowDays, count, seconds / 3600, dates.Count, ready, scenes.Order().ToArray(), cards,
            bins.Select(kv => new HistoryPoint(new DateTimeOffset(kv.Key, TimeSpan.Zero), kv.Value.Cpu.Mean,
                kv.Value.Memory.Mean, gpuHistoryScopes.Count == 1 ? kv.Value.Gpu.Mean : null)
                {
                    GpuScope = gpuHistoryScopes.Count == 1 ? gpuHistoryScopes.Single() : "",
                    CpuObservedSeconds = kv.Value.Cpu.Seconds, MemoryObservedSeconds = kv.Value.Memory.Seconds,
                    GpuObservedSeconds = gpuHistoryScopes.Count > 1 ? null : kv.Value.Gpu.Seconds,
                    CpuCoverageEstimated = kv.Value.Cpu.Estimated, MemoryCoverageEstimated = kv.Value.Memory.Estimated,
                    GpuCoverageEstimated = gpuHistoryScopes.Count == 1 && kv.Value.Gpu.Estimated
                }).ToList(), settings.FutureNeeds)
            { HardwareId = scopeHardwareId, RepresentativeConfirmed = coverage.Confirmed, CoverageMessage = coverage.Message, SamplingGapCount = samplingGaps, HistoryQuality = quality,
                SelectedMemoryCapacity = temporal.SelectedCapacity($"所选 {windowDays} 天 · {selectedScene ?? "全部场景"} · 已测物理容量"),
                GpuMetricScopes = string.IsNullOrWhiteSpace(scopeHardwareId) ? [] : gpuMetricScopes.Order(StringComparer.Ordinal).ToArray(),
                GpuHistoryHasMixedSources = gpuHistoryScopes.Count > 1,
                SelectedScene = selectedScene, DemandTrends = trends, FreshActiveHours = temporal.FreshHours, FreshActiveDays = temporal.FreshDays,
                SceneProfiles = sceneProfiles };
        return HistoryAnalysis.Apply(gpuEngines.Apply(result), quality);
    }

    private sealed class HourAccumulator
    {
        internal readonly HourMetric Cpu = new(), Memory = new(), Gpu = new();
    }
    private sealed class HourMetric
    {
        private double weighted, minimum, maximum;
        internal double Seconds { get; private set; }
        internal bool Estimated { get; private set; }
        internal double? Mean => Seconds > 0 ? Math.Clamp(weighted / Seconds, minimum, maximum) : null;
        internal void Add(double? value, double seconds, bool estimated)
        {
            if (value is not double reading || !double.IsFinite(reading) || seconds <= 0 || !double.IsFinite(seconds)) return;
            minimum = Seconds > 0 ? Math.Min(minimum, reading) : reading;
            maximum = Seconds > 0 ? Math.Max(maximum, reading) : reading;
            weighted += reading * seconds; Seconds += seconds; Estimated |= estimated;
        }
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
        double? Unique(double? value) => value.HasValue ? double.IsFinite(value.Value) && value >= 0 && value <= sample.Seconds + 1d / TimeSpan.TicksPerSecond ? Math.Min(value.Value, sample.Seconds) : 0 : null;
        return sample with
        {
            Scene = NormalizeScene(sample.Scene),
            Seconds = double.IsFinite(sample.Seconds) && sample.Seconds is > 0 and <= 15 ? sample.Seconds : 0,
            UniqueObservationSeconds = Unique(sample.UniqueObservationSeconds), UniqueSceneSeconds = Unique(sample.UniqueSceneSeconds),
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
        private readonly HashSet<string> gpuScopes = new(StringComparer.Ordinal);
        private int count; private double seconds; private double? memoryPeak, gpuPeak;
        public void Add(Sample s)
        {
            count++;
            if (!s.Active || s.Seconds <= 0) return;
            double duration = s.UniqueSceneSeconds ?? s.Seconds;
            seconds += duration;
            double Coverage(double? value) => Math.Clamp(value ?? duration, 0, duration);
            cpu.Add(s.Cpu, Coverage(s.CpuCoverage)); core.Add(s.CoreMax, Coverage(s.CoreCoverage)); gpu.Add(s.Gpu, Coverage(s.GpuCoverage));
            if (s.Gpu.HasValue && Coverage(s.GpuCoverage) > 0)
            { gpuPeak = Math.Max(gpuPeak ?? 0, s.GpuPeak ?? s.Gpu.Value); gpuScopes.Add(GpuEngineEvidence.MetricScope(s)); }
            if (s.MemoryUsedGb.HasValue && Coverage(s.MemoryCoverage) > 0) memoryPeak = Math.Max(memoryPeak ?? 0, s.MemoryPeakGb ?? s.MemoryUsedGb.Value);
        }
        public SceneProfile Profile() => new(name, count, seconds / 3600, cpu.Quantile(.95), core.Quantile(.95), memoryPeak,
            gpuScopes.Count == 1 ? gpu.Quantile(.95) : null, gpuPeak,
            seconds > 0 ? cpu.Seconds / seconds : 0, seconds > 0 ? gpu.Seconds / seconds : 0)
            { GpuMetricScopes = gpuScopes.Order(StringComparer.Ordinal).ToArray() };
    }
}
