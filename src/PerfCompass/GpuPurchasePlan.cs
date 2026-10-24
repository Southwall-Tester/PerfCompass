namespace PerfCompass;

public record GpuPurchaseRoute(string Name, string Order, string Check);
public record GpuPurchasePlan(string Mode, string Title, string NextStep, string Scope,
    string Evidence, string[] Needs, GpuPurchaseRoute[] Routes)
{
    public const string Notice = "比较顺序不是达标结论。核显也有不同性能与媒体功能；独显档位不能代替具体整机的功耗、散热、显存和软件兼容性。";

    public static GpuPurchasePlan Build(AnalysisReport report)
    {
        var card = report.Cards.FirstOrDefault(c => c.Key == "gpu");
        string scope = $"所选 {report.WindowDays} 天 · {report.SelectedScene ?? "全部场景"}";
        var needs = (report.SemanticRequirements?.Facts?.Where(f => f != null && SemanticUse.Relevant(f))
            .Select(f => $"{f.Subject} · {f.DecisionUseLabel}；{SemanticRequirements.Label(f.Timeframe)}"
                + (string.IsNullOrWhiteSpace(f.Condition) ? "" : "；条件：" + f.Condition)
                + "；原文：" + f.Evidence) ?? [])
            .Concat(report.ConfirmedRequirements?.Facts?.Where(f => f != null && !f.Excluded)
                .Select(f => $"{f.Label}；原文：{f.Evidence}") ?? []).ToArray();
        var heavy = report.GpuEngineProfiles.Where(p => p.ObservedHours > 0 && (p.Peak >= 80 || p.HeavyMinutes > 0)).ToArray();
        bool invalid = card == null || string.IsNullOrWhiteSpace(report.HardwareId)
            || report.HistoryQuality.HasIssues || report.ExperienceQuality.HasIssues
            || report.GpuHistoryHasMixedSources || report.GpuMetricScopes.Distinct(StringComparer.Ordinal).Skip(1).Any()
            || report.GpuEngineProfiles.Any(p => p.HasMixedSources);
        bool protectedNeed = report.SemanticUseNotice.Length > 0
            || report.SemanticRequirements?.Facts?.Any(f => f != null && SemanticUse.Relevant(f) && SemanticUse.Conditional(f)) == true
            || card?.Status is "requirements" or "constraints" or "meaning" or "experience" or "experience-data" or "changed";
        string mode = invalid ? "unknown" : protectedNeed ? "requirements"
            : card!.RetainedWorkload != null ? "workload"
            : heavy.Length > 0 && heavy.All(p => p.Kind is "encode" or "decode" or "processing" or "video" or "media")
                ? "media"
            : heavy.Length > 0 || card.Status == "pressure" || card.Observation?.State == "busy" ? "workload"
            : card.Status is "review" or "surplus" || card.Observation?.State == "light" ? "light" : "unknown";
        // This is a shopping order, never a new suitability/coverage assessment.
        // Missing engines stay unknown; media activity does not erase core tasks.
        string title = mode switch
        {
            "light" => "先比较核显整机，再看较低档独显",
            "media" => "先买对媒体功能，再比较显卡档位",
            "workload" => "以重任务表现作基准，再比较降档收益",
            "requirements" => "先按需求筛选，保留不同条件下的方案",
            _ => "先用一个最重要的任务缩小显卡选择"
        };
        string next = mode switch
        {
            "light" => "首轮同时选一台核显整机和一台较低档独显整机，比较最重要任务的表现与整机报价；通过目标后，再决定是否值得为当前级别独显加预算。",
            "media" => "先按软件、编解码格式、位深、分辨率和并发路数筛选核显或独显，再比较同素材、同质量设置下的导出耗时或播放丢帧；媒体引擎重载不直接决定图形性能档位。",
            "workload" => "先记录最重任务的可接受帧率或耗时，以当前整机作基准；比较较低档候选的同任务结果。只有当前体验未达目标且候选有收益时，再为更强显卡加预算。",
            "requirements" => "先把已确认的软件功能、性能目标和触发条件用于筛选；分别比较满足这些条件的核显与独显整机，不用历史轻载覆盖新任务。",
            _ => "选一个换机后必须做好的任务，确定可接受帧率、耗时或功能；带着同一任务比较核显和独显整机。记录异常单独核对，不把缺失读数当成高配需求。"
        };
        string order = mode == "light" ? "首轮比较" : mode == "media" ? "先查媒体功能" : "按任务目标筛选";
        var routes = new[]
        {
            new GpuPurchaseRoute("核显整机", order, mode == "media"
                ? "查所需格式、位深、分辨率、路数及软件是否调用该媒体引擎；再跑同一素材。"
                : "查软件功能、显示输出及共享内存影响，再核对最重任务的实际体验。"),
            new GpuPurchaseRoute("较低档独显整机", order, "查接口与显存容量，用同版本、同输入及同设置比较帧率或耗时，并保留整机报价来源。"),
            new GpuPurchaseRoute("当前级别或更强独显整机", mode == "light" ? "通过较低价方案后再考虑" : "任务表现基准与升级对照",
                "只有目标任务需要其功能或表现，才为它加预算；同名显卡仍需核对整机功耗和散热。")
        };
        if (mode == "workload") routes = [routes[2], routes[1], routes[0]];
        string evidence = card == null ? "本范围尚无显卡观测。" : card.Evidence;
        if (card?.Observation is { } o) evidence += " " + o.Evidence + " " + o.Scope;
        if (card?.RetainedWorkload is { } retained) evidence += " " + retained.Evidence + " " + retained.Scope;
        if (mode == "media") evidence += " 重载类别：" + string.Join("、", heavy.Select(p => p.Name)) + "；范围为所选窗口，其他未观测任务仍待核对。";
        return new(mode, title, next, scope, evidence, needs, routes);
    }
}
