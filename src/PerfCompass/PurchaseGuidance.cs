namespace PerfCompass;

public record ConfigurationDecision(string Key, string Name, string Direction, string Reason,
    string NextStep, string Scope, string Level)
{
    public int Priority { get; init; }
}

/// <summary>
/// Turns the final, already assessed cards into purchasing actions. This layer
/// neither estimates new hardware performance nor creates another evidence gate.
/// </summary>
public static class PurchaseGuidance
{
    public static IReadOnlyList<ConfigurationDecision> Build(AnalysisReport report)
    {
        var cards = report.Cards.ToList();
        // The normal analyzer supplies these four cards even for an empty window.
        // Keep a useful entry point when an empty report has no cards at all.
        foreach (var (key, name) in new[] { ("cpu", "处理器"), ("memory", "内存"), ("gpu", "显卡"), ("disk", "存储") })
            if (!cards.Any(c => c.Key == key))
                cards.Add(new(key, name, "unknown", "", "本范围尚无该项分析结果。", "", ""));
        return cards.Select(card => Decide(report, card)).OrderBy(d => d.Priority).ToList();
    }

    private static ConfigurationDecision Decide(AnalysisReport report, Advice card)
    {
        string scope = $"所选 {report.WindowDays} 天 · {report.SelectedScene ?? "全部场景"}";
        ConfigurationDecision Result(string direction, string reason, string next, string range, string level, int priority)
            => new(card.Key, card.Name, direction, reason, next, range, level) { Priority = priority };
        ConfigurationDecision Final(string direction, int priority)
            => Result(direction, card.Evidence, card.DisplaySuggestion, scope, card.StatusLabel, priority);

        // A retained observation is a fact, not permission to override the final
        // needs, experience or pressure assessment. These statuses use their own
        // evidence and selected scope rather than a possibly older light label.
        switch (card.Status)
        {
            case "meaning": return Final("核对需求的购机用途，再决定配置取舍", 0);
            case "experience-data": return Final("核对体验记录后，再决定配置取舍", 0);
            case "experience": return Final("先核对体验问题，再决定配置取舍", 0);
            case "requirements": return Final("先验证已确认需求，再决定配置取舍", 1);
            case "constraints": return Final("先按已确认条件筛选候选电脑", 2);
            case "changed": return Final("优先核对近期增加的任务需求", 3);
            case "pressure": return Final(card.Key switch
            {
                "cpu" => "优先核对繁忙任务的处理器性能",
                "memory" => "优先核对峰值任务所需内存容量",
                "gpu" => "保留重任务需求，优先核对候选显卡表现",
                "disk" => "优先核对空间需求与增长量",
                _ => "优先核对任务压力与候选配置表现"
            }, 4);
        }

        if (report.SampleCount == 0 && card.Observation == null && card.RetainedWorkload == null)
            return Result(UnmeasuredDirection(card.Key), card.Evidence,
                JoinStep(UnmeasuredStep(card.Key), card.DisplaySuggestion), scope, "待补测", 40);
        if (string.IsNullOrWhiteSpace(report.HardwareId))
            return Result("先隔离当前硬件配置的记录", report.CoverageMessage.Length > 0 ? report.CoverageMessage : "当前分析尚未绑定单一硬件配置。",
                "选择当前硬件配置后，" + UnmeasuredStep(card.Key), scope, "待核对", 10);
        if (report.HistoryQuality.HasIntervalConflicts)
            return HistoryIssue();

        bool observationApplies = card.Status is "unknown" or "fit";
        var observation = observationApplies ? card.Observation : null;
        // Independently observed heavy work remains useful when another history
        // row is damaged. Overlapping intervals were excluded above.
        if (observation?.State == "busy")
            return Result(card.Key switch
            {
                "cpu" => "优先核对繁忙任务的处理器性能",
                "memory" => "按峰值任务筛选容量，核对内存余量",
                "gpu" => "保留已测重任务，核对候选显卡表现",
                _ => "保留已测繁忙任务，核对候选表现"
            }, observation.Evidence, card.DisplaySuggestion, observation.Scope, "已测线索", 15);
        if (report.HistoryQuality.HasIssues)
            return HistoryIssue();
        if (report.ExperienceQuality.HasIssues && card.UsesRetainedWorkload)
            return Result("保留已测重任务，核对体验记录", card.RetainedWorkload!.Evidence + " " + report.ExperienceQuality.Message,
                "保留所选时段的重要任务，先核对体验记录再比较候选配置。", card.RetainedWorkload.Scope, "已测线索", 15);
        if (report.ExperienceQuality.HasIssues)
            return Result("核对体验记录后，再决定配置取舍", report.ExperienceQuality.Message,
                "在历史趋势页查看异常位置，核对原文件与备份。", scope, "体验记录待核对", 16);
        if (card.UsesRetainedWorkload)
        {
            var retained = card.RetainedWorkload!;
            return Result("保留所选时段的重任务，再核对较低档候选", retained.Evidence,
                card.DisplaySuggestion, retained.Scope, "已测线索", 16);
        }
        if (card.Key == "gpu" && (report.GpuMetricScopes.Distinct(StringComparer.Ordinal).Skip(1).Any()
            || report.GpuEngineProfiles.Any(p => p.HasMixedSources)))
            return Result("先按同一采集来源核对显卡任务", card.Evidence,
                "保留不同来源的原始记录，使用同一采集方式重跑目标图形、计算或编码任务，再比较候选显卡。",
                scope, "待核对", 20);

        if (card.Status is "surplus" or "review")
            return Final(card.Key switch
            {
                "cpu" => "优先比较较少核心、单核性能相近的处理器",
                "memory" => "比较较低容量候选，保留最重任务余量",
                "gpu" => "优先比较较低档显卡",
                _ => "按已测需求比较配置档位"
            }, 25);
        if (observation != null && observation.State is "light" or "observed")
            return Result(card.Key switch
            {
                "cpu" when observation.State == "light" => "优先比较较少核心、单核性能相近的处理器",
                "cpu" => "用主要任务耗时比较处理器",
                "memory" when observation.State == "light" => "比较较低容量，保留峰值任务余量",
                "memory" => "先保留当前内存容量作比较基线",
                "gpu" when observation.State == "light" => "优先比较较低档显卡",
                "gpu" => "按目标任务比较显卡表现",
                _ => "按已测任务比较候选配置"
            }, observation.Evidence, card.DisplaySuggestion, observation.Scope, "已测线索", observation.State == "light" ? 26 : 30);

        // Disk fill and speed have different meanings. Neither a default zero nor
        // low I/O throughput supplies a reason to choose a lower SSD performance tier.
        if (card.Key == "disk" && report.SampleCount > 0)
            return Final("按文件与增长量选容量，按任务耗时选速度", 35);
        return Result(UnmeasuredDirection(card.Key), card.Evidence,
            JoinStep(UnmeasuredStep(card.Key), card.DisplaySuggestion), scope, "待补测", 40);

        ConfigurationDecision HistoryIssue() => Result("先核对历史异常，再判断配置余量", report.HistoryQuality.Message,
            "查看异常的文件和行号，核对记录来源；修复或隔离后重新分析。", scope, "待核对", 10);
    }

    private static string JoinStep(string step, string finalSuggestion)
        => string.IsNullOrWhiteSpace(finalSuggestion) || finalSuggestion == step ? step : step + " " + finalSuggestion;

    private static string UnmeasuredDirection(string key) => key switch
    {
        "cpu" => "先记录主要任务耗时与最忙单核",
        "memory" => "先测最重多任务组合的内存峰值",
        "gpu" => "先记录目标图形、计算或编码任务",
        "disk" => "先核对文件容量、增长量与存储任务",
        _ => "先核对这项需求的实际任务"
    };

    private static string UnmeasuredStep(string key) => key switch
    {
        "cpu" => "运行常用的重任务，记录完成时间，并同时观察整机与最忙逻辑核心负载。",
        "memory" => "同时打开最重的一组应用，记录物理使用峰值、提交量及是否发生卡顿。",
        "gpu" => "运行实际需要的游戏、计算或编码任务，记录画质、帧率、耗时、显存和所需软件功能。",
        "disk" => "逐卷核对现有文件与预计增长量；用实际打开、复制或处理文件的耗时比较速度。",
        _ => "列出需要保留的任务与可接受目标，再在候选机器上验证。"
    };
}
