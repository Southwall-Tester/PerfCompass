namespace PerfCompass;

/// <summary>Review metadata, independent of the provider's eight-field source extraction contract.</summary>
public static class SemanticUse
{
    public const string PlanVersion = "semantic-plan-1.1";
    public static readonly SemanticChoice[] Choices = [new("require", "仍需满足或验证"), new("avoid", "必须避免"), new("omit", "不作为购机条件"), new("clarify", "用途待核对")];
    public static readonly SemanticChoice[] SourceRelations = [new("required", "肯定陈述"), new("excluded", "否定陈述"), new("conditional", "条件陈述"), new("uncertain", "尚不确定")];
    public static string SourceRelation(SemanticFact fact) => SourceRelations.FirstOrDefault(c => c.Key == fact.Polarity)?.Label ?? "关系待核对";
    public static string Role(SemanticFact fact) => fact.DecisionUse ?? (fact.Timeframe == "past" || fact.Polarity == "excluded" ? "clarify" : "require");
    public static string Label(SemanticFact fact) => Choices.FirstOrDefault(c => c.Key == Role(fact))?.Label ?? "用途待核对";
    public static bool KnownPlan(string version) => version is PlanVersion or SemanticRequirements.Version;
    public static bool Relevant(SemanticFact fact) => Role(fact) != "omit";
    public static bool PendingUse(SemanticFact fact) => Relevant(fact) && Role(fact) == "clarify";
    public static bool Conditional(SemanticFact fact) => Relevant(fact) && (PendingUse(fact) || fact.Polarity is "conditional" or "uncertain" || fact.Timeframe == "unspecified" || fact.Kind == "conflict");
    public static bool UnconditionalGoal(SemanticFact fact) => Relevant(fact) && !Conditional(fact);
    // Preserve the source identity of ordinary current/future positive goals.
    // Ambiguous old interpretations get a distinct identity and cannot retain
    // the old goal's review or its unconditional trigger by accident.
    internal static SemanticFact SourceIdentity(SemanticFact fact) => Role(fact) == "require" && fact.Timeframe != "past" && fact.Polarity != "excluded"
        ? fact with { DecisionUse = null } : fact;
    internal static string IdentitySuffix(SemanticFact fact) => fact.Timeframe == "past" || fact.Polarity == "excluded" || Role(fact) != "require" ? ":use:" + Role(fact) : "";
}

public static partial class SemanticRequirements
{
    private static bool WouldSuggestDowngrade(Advice c) => c.Status is "surplus" or "review" || c.Status is ("fit" or "unknown") && c.Observation?.State == "light" && !c.UsesRetainedWorkload;
    private static AnalysisReport PendingUse(AnalysisReport report, string notice, SemanticPlan plan)
        => report with { Ready = false, SemanticUseNotice = notice, SemanticRequirements = null,
            SemanticUseSignature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plan))),
            Cards = report.Cards.Select(c => WouldSuggestDowngrade(c)
            ? c with { Status = "meaning", Title = "先核对需求的购机用途", Evidence = c.Evidence + " " + notice, Suggestion = "到记录设置核对事项原文及购机用途，再比较降档方向。" } : c).ToList() };

    private static AnalysisReport ApplyUses(AnalysisReport report, Settings settings)
    {
        var plan = settings.SemanticRequirements;
        if (plan == null || plan.Source != settings.FutureNeeds.Trim()) return report;
        if (!SemanticUse.KnownPlan(plan.ContractVersion) || plan.Facts == null || plan.Facts.Count is < 1 or > 20 || plan.Facts.Any(f => f == null))
            return PendingUse(report, "已保存的需求协议尚不能读取，请核对原文后重新保存事项。", plan);
        try { foreach (var fact in plan.Facts) Validate(plan.Source, fact); }
        catch (ArgumentException) { return PendingUse(report, "已保存事项存在无效字段或原文依据，请重新核对。", plan); }
        if (plan.ContractVersion == Version && plan.Facts.Any(f => f.DecisionUse != null))
            return PendingUse(report, "旧协议含有新增购机用途，请核对后按新版确认协议保存。", plan);
        if (plan.ContractVersion == SemanticUse.PlanVersion && plan.Facts.Any(f => f.DecisionUse == null))
            return PendingUse(report, "新版事项缺少购机用途，请核对原文后重新保存。", plan);
        var relevant = plan.Facts.Where(SemanticUse.Relevant).ToList();
        var active = relevant.Where(SemanticUse.UnconditionalGoal).ToList();
        bool futureWork = active.Any(f => f.Kind == "workload" && SemanticUse.Role(f) == "require" && f.Timeframe is ("past" or "future"));
        bool pending = relevant.Any(SemanticUse.Conditional), pendingUse = relevant.Any(SemanticUse.PendingUse);
        var cards = report.Cards.Select(c =>
        {
            bool resource = active.Any(f => f.Kind == "experience" || f.Kind == "resource" && MatchesResource(f, c.Key));
            bool protects = c.Key is "cpu" or "gpu" or "memory" && (futureWork || resource);
            bool replace = protects && (WouldSuggestDowngrade(c) || c.Status is ("unknown" or "fit") && c.Observation == null && !c.UsesRetainedWorkload);
            bool clarify = !replace && pendingUse && WouldSuggestDowngrade(c);
            if (!protects && !pending) return c;
            return c with
            {
                Status = replace ? "requirements" : clarify ? "meaning" : c.Status,
                Title = replace ? "先验证已确认的任务与目标，再评估降档" : clarify ? "先核对需求的购机用途" : c.Title,
                Evidence = c.Evidence + " 原文时间与购机用途分别核对；必需满足、必须避免及不作为条件的事项见购机条件。",
                Suggestion = replace ? "先在候选配置上验证已选用的任务与体验目标，再判断余量。" : clarify ? "到记录设置核对这些事项是仍需满足、必须避免，还是只保留背景记录。"
                    : c.Suggestion + (pending ? " 另外核对用途待定或条件触发时的需求。" : ""),
                Observation = !replace && !clarify && pending && c.Observation is { } observation ? observation with { Action = observation.Action + " 另外分别比较条件需求成立与不成立时的配置，并核对用途待定的事项。" } : c.Observation,
                RetainedWorkload = !replace && !clarify && pending && c.RetainedWorkload is { } retained ? retained with { Action = retained.Action + " 另外分别比较条件需求成立与不成立时的配置，并核对用途待定的事项。" } : c.RetainedWorkload
            };
        }).ToList();
        if (relevant.Count > 0) cards.Add(new("constraints", "购机条件", "constraints", pendingUse ? "核对购机用途与条件分支" : pending ? "分开核对确定需求与条件需求" : "按已确认条件验证候选电脑",
            string.Join("\n", plan.Facts.Select(f => $"{f.Subject} · 原文{SemanticUse.SourceRelation(f)} · 原文{Label(f.Timeframe)} · 购机用途：{SemanticUse.Label(f)}；{f.Detail}；原文：{f.Evidence}")),
            "必需满足与必须避免的内容逐项核对；不作为条件的内容保留原文。用途待定及触发条件分别确认。", ""));
        return report with { Cards = cards, Ready = report.Ready && !pendingUse, SemanticRequirements = plan,
            SemanticUseNotice = pendingUse ? "部分事项的购机用途尚待核对，不能仅凭原文的过去或否定标签判定它已不再需要。" : "" };
    }
}
