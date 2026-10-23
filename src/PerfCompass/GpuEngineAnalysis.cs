namespace PerfCompass;

/// <summary>
/// Keeps named execution engines separate from the legacy core/3D metric. A missing
/// category remains unknown; a complete category is not whole-GPU coverage.
/// </summary>
internal sealed class GpuEngineAnalysis(DateTimeOffset now, int windowDays)
{
    private sealed class Engine(DateTimeOffset now)
    {
        internal readonly WeightedSeries Observations = new();
        internal readonly TemporalDemandAnalyzer Temporal = new(now);
        internal readonly Dictionary<string, int> Sources = new(StringComparer.Ordinal);
        internal readonly HashSet<string> SourceScopes = new(StringComparer.Ordinal);
        internal readonly HashSet<string> WeekSources = new(StringComparer.Ordinal), PreviousWeekSources = new(StringComparer.Ordinal),
            MonthSources = new(StringComparer.Ordinal), PreviousMonthSources = new(StringComparer.Ordinal);
        internal double CompleteSeconds, HeavySeconds;
        internal double? Peak;
        internal bool Seen;

        internal bool ComparableSources(int days)
        {
            var recent = days == 7 ? WeekSources : MonthSources;
            var previous = days == 7 ? PreviousWeekSources : PreviousMonthSources;
            return recent.Count == 1 && previous.Count == 1 && recent.SetEquals(previous);
        }
    }

    private readonly Dictionary<string, Engine> engines = GpuEngineEvidence.Kinds.ToDictionary(k => k, _ => new Engine(now), StringComparer.Ordinal);
    private readonly HashSet<string> hardwareIds = new(StringComparer.Ordinal);

    internal void Add(Sample sample)
    {
        double age = (now - sample.Time).TotalDays;
        if (!sample.Active || !double.IsFinite(sample.Seconds) || sample.Seconds is <= 0 or > 15 || age < 0 || age > Math.Max(windowDays, 60)) return;
        hardwareIds.Add(sample.HardwareId ?? "");
        bool valid = GpuEngineEvidence.Valid(sample);
        foreach (var (kind, state) in engines)
        {
            var reading = valid ? sample.GpuEngines!.SingleOrDefault(e => e.Kind == kind) : null;
            double observed = reading == null ? 0 : GpuEngineEvidence.Observed(sample, reading);
            double covered = reading == null ? 0 : GpuEngineEvidence.Covered(sample, reading);
            double heavy = reading == null ? 0 : GpuEngineEvidence.Heavy(sample, reading);
            double high = reading == null ? 0 : GpuEngineEvidence.High(sample, reading);
            if (reading != null)
            {
                state.Seen = true;
                if (age <= windowDays)
                {
                    foreach (var group in reading.Sources.GroupBy(s => s, StringComparer.Ordinal))
                        state.Sources[group.Key] = Math.Max(state.Sources.GetValueOrDefault(group.Key), group.Count());
                    state.SourceScopes.Add(System.Text.Json.JsonSerializer.Serialize(new { sample.GpuEngineVersion, Sources = reading.Sources.Order(StringComparer.Ordinal) }));
                    state.Observations.Add(reading.Load, observed, max: 100);
                    state.CompleteSeconds += covered;
                    state.HeavySeconds += heavy;
                    if (observed > 0) state.Peak = Math.Max(state.Peak ?? 0, reading.Peak ?? reading.Load!.Value);
                }
            }

            // A persisted batch can mix complete and partial category readings. Its
            // mean cannot be split back into complete/partial means, so that batch
            // cannot supply a complete-category trend or sustained-pressure value.
            bool comparableReading = observed > 0 && covered >= observed - 1e-7;
            state.Temporal.Add(new Sample
            {
                Time = sample.Time, Seconds = sample.Seconds, Active = true, Scene = sample.Scene,
                HardwareId = sample.HardwareId ?? "", Gpu = comparableReading ? reading!.Load : null,
                GpuEngineVersion = sample.GpuEngineVersion, GpuMetricSource = "gpu-engine/" + kind,
                GpuCoverage = comparableReading ? covered : 0,
                GpuHeavySeconds = comparableReading ? Math.Min(heavy, covered) : 0,
                GpuHighSeconds = comparableReading ? Math.Min(high, covered) : 0
            });
            // Source names identify the exposed category scope. If that scope
            // changes, the difference cannot be attributed to changed demand.
            if (comparableReading && age < 60)
            {
                string signature = sample.GpuEngineVersion + "\n" + string.Join("\n", reading!.Sources.Order(StringComparer.Ordinal));
                if (age < 7) state.WeekSources.Add(signature);
                else if (age < 14) state.PreviousWeekSources.Add(signature);
                if (age < 30) state.MonthSources.Add(signature);
                else state.PreviousMonthSources.Add(signature);
            }
        }
    }

    internal AnalysisReport Apply(AnalysisReport report)
    {
        // The surrounding analyzer normally enforces this already. Keep the same
        // protection here so this extension cannot pool engines across machines.
        if (string.IsNullOrWhiteSpace(report.HardwareId) || hardwareIds.Any(id => id != report.HardwareId))
            return report with { GpuEngineProfiles = [], DemandTrends = report.DemandTrends.Where(t => !t.Key.StartsWith("gpu-engine/", StringComparison.Ordinal)).ToList() };

        var profiles = engines.Select(pair => new GpuEngineProfile(pair.Key, Name(pair.Key),
            pair.Value.Observations.Seconds / 3600, pair.Value.CompleteSeconds / 3600,
            pair.Value.SourceScopes.Count == 1 ? pair.Value.Observations.Quantile(.95) : null, pair.Value.Peak, pair.Value.HeavySeconds / 60,
            pair.Value.Sources.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => Enumerable.Repeat(p.Key, p.Value)).ToArray())
            { SourceScopes = pair.Value.SourceScopes.Order(StringComparer.Ordinal).ToArray() }).ToList();
        var trends = engines.Where(pair => pair.Value.Seen).SelectMany(pair => pair.Value.Temporal.Build()
            .Where(trend => trend.Key == "gpu").Select(trend => Rename(trend, pair.Key, pair.Value))).ToList();
        var pressured = engines.Where(pair => pair.Value.Temporal.HasRecentPressure("gpu") && pair.Value.WeekSources.Count == 1)
            .Select(pair => Name(pair.Key)).ToArray();
        var increase = report.HistoryQuality.HasIssues ? null : trends.Where(t => t.Status == "increased").OrderBy(t => t.Days).FirstOrDefault();
        var heavyProfiles = profiles.Where(p => p.HeavyMinutes > 0 || p.Peak >= 80).ToArray();
        var known = profiles.Where(p => p.Sources.Length > 0).ToArray();
        string evidence = known.Length == 0 ? "尚无分引擎记录，历史核心/3D读数不能补成计算、编码等引擎的零负载。"
            : "分引擎观测：" + string.Join("；", known.Select(p => $"{p.Name} P95 {Percent(p.P95)}，峰值 {Percent(p.Peak)}，已观察 {DurationText.FromHours(p.ObservedHours)} / 类别完整 {DurationText.FromHours(p.CompleteHours)}，≥80% 累计 {p.HeavyMinutes:0.0} 分钟" + (p.HasMixedSources ? "；" + p.ScopeNotice : ""))) + "。";
        const string limits = "分引擎 P95 按已取得读数的时长加权，部分读数只代表已知节点；类别完整仅指已暴露的同类传感器读数完整，不证明该卡所有引擎均被观测或驱动读数一定新鲜。缺少类别保留未知，不同引擎占用不能相加；低占用不能证明独显过剩。";
        var cards = report.Cards.Select(card =>
        {
            if (card.Key != "gpu") return card;
            var result = card with { Evidence = card.Evidence + " " + evidence, Limits = card.Limits + " " + limits };
            if (pressured.Length > 0)
                return result with
                {
                    Status = "pressure", Title = $"近期{string.Join("、", pressured)}存在持续高负载",
                    Suggestion = "核对对应任务的耗时、帧率、画质和功能要求。已观察到的引擎重负载不能被长期核心低占用抵消；高占用本身仍不证明瓶颈或升级收益。"
                };
            if (increase != null)
                return result with
                {
                    Status = card.Status == "pressure" ? "pressure" : "changed",
                    Title = card.Status == "pressure" ? card.Title : $"近期{increase.Name}负载增加，核对新任务需求",
                    Evidence = result.Evidence + $" 最近 {increase.Days} 天 / 此前 {increase.Days} 天 P95：{Percent(increase.RecentP95)} / {Percent(increase.PreviousP95)}。",
                    Suggestion = increase.Suggestion
                };
            if (heavyProfiles.Length > 0 && card.Status is "review" or "surplus" or "fit" or "unknown")
            {
                bool intermittent = heavyProfiles.All(p => p.P95 < 30);
                return result with
                {
                    Status = report.Ready && card.Status is ("review" or "surplus" or "fit") ? "fit" : "unknown",
                    Title = intermittent ? "存在偶发引擎重负载，保留该任务需求" : "已观察到引擎重负载，先核对任务需求",
                    Suggestion = $"已观察到{string.Join("、", heavyProfiles.Select(p => p.Name))}重负载；部分读数或时长不足时不能断言持续压力。确认这些任务是否仍需运行，并在候选显卡上验证表现后再讨论降档。"
                };
            }
            if (card.Status == "review")
                return result with
                {
                    Title = profiles.Any(p => p.Kind is not ("core" or "3d") && p.ObservedHours > 0)
                        ? "核心/3D负载偏低，结合其他引擎核对" : "已采核心/3D负载偏低，其他引擎待核对",
                    Suggestion = "这只是待核对的降档方向。继续核对计算、编码、游戏帧率、显存容量及软件兼容性；只有候选显卡通过实际任务验证，才有降低配置的依据。"
                };
            return result;
        }).ToList();
        return report with
        {
            Cards = cards, GpuEngineProfiles = profiles,
            DemandTrends = report.DemandTrends.Where(t => !t.Key.StartsWith("gpu-engine/", StringComparison.Ordinal)).Concat(trends).ToList()
        };
    }

    private static DemandTrend Rename(DemandTrend trend, string kind, Engine state)
    {
        string name = Name(kind);
        bool comparableSources = state.ComparableSources(trend.Days);
        bool mixedRecent = (trend.Days == 7 ? state.WeekSources : state.MonthSources).Count > 1;
        bool mixedPrevious = (trend.Days == 7 ? state.PreviousWeekSources : state.PreviousMonthSources).Count > 1;
        return trend with
        {
            Key = "gpu-engine/" + kind, Name = name,
            RecentP95 = mixedRecent ? null : trend.RecentP95, PreviousP95 = mixedPrevious ? null : trend.PreviousP95,
            RecentBusyMinutes = mixedRecent ? 0 : trend.RecentBusyMinutes, PreviousBusyMinutes = mixedPrevious ? 0 : trend.PreviousBusyMinutes,
            RecentHeavyScenes = mixedRecent ? [] : trend.RecentHeavyScenes,
            StatisticsUnavailableReason = mixedRecent || mixedPrevious ? "时期内部来源或规则混合，未合并该期 P95，负载比例暂不解释。" : trend.StatisticsUnavailableReason,
            Status = comparableSources ? trend.Status : "insufficient",
            Summary = !comparableSources && trend.RecentHours > 0 && trend.PreviousHours > 0
                ? $"{name}采集规则或传感器来源范围发生变化，暂不判断需求增减"
                : trend.Summary.Replace("显卡", name, StringComparison.Ordinal),
            Suggestion = trend.Suggestion + " 该对照只使用同类别、同来源且读数完整的观测；旧记录没有该引擎数据时保留未知。"
        };
    }

    private static string Name(string kind) => "GPU " + GpuEngineEvidence.Label(kind);
    private static string Percent(double? value) => value.HasValue ? $"{value:0.0}%" : "缺失";
}
