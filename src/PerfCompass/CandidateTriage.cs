using System.Net;
using System.Text.RegularExpressions;

namespace PerfCompass;

public record CandidateTriageRow(string Id, string Name, string State, string Specifications, string Quote,
    string[] Reasons, string[] Issues, string[] Pending, string Sources)
{
    public string ScenarioKey { get; init; } = "";
    public string ReasonText => string.Join("；", Reasons);
    public string ComparisonReason => string.Join("；", Reasons.Take(2)) + (Reasons.Length > 2 ? $"；另 {Reasons.Length - 2} 项见依据" : "");
    public string IssueText => string.Join("；", Issues);
    public string PendingText => string.Join("；", Pending);
}
public record CandidateTriageCase(string Label, string Notice, CandidateTriageRow[] Candidates)
{
    public string ScenarioKey { get; init; } = "";
    public string Source { get; init; } = "";
}
public record CandidateTriageReport(string Scope, string Notice, CandidateTriageCase[] Cases);

/// <summary>Read-only order for comparing concrete candidates, never a suitability verdict.</summary>
public static class CandidateTriage
{
    public const string Notice = "初筛列比较顺序，不是购机达标结论。有价方案按有出处报价安排核对，无价方案按观测路线比较；供货、可用内存与实际任务仍需核对，报价出处不等于成交认证。";

    public static CandidateTriageReport Build(PurchaseWorkspace workspace, AnalysisReport report)
    {
        CandidateComparison.Validate(workspace);
        var outdated = CandidateComparison.OutdatedSemanticCriteria(workspace, report);
        bool Reviewed(PurchaseCriterion c) => c.Reviewed && c.ReviewStamp == CandidateComparison.CriterionStamp(c) && !outdated.Contains(c);
        var goals = CandidateComparison.Drafts(report);
        // A reviewed manual goal adds a constraint, rather than silently erasing
        // the confirmed source requirement. Unreviewed edits cannot become facts.
        foreach (var c in workspace.Criteria.Where(Reviewed))
            if (!goals.Any(g => g.Metric == c.Metric && g.Direction == c.Direction && g.Target == c.Target
                && g.Unit == c.Unit && g.Condition == c.Condition && g.OriginId == c.OriginId)) goals.Add(c);
        var storage = StorageCapacityPreview.From(report)?.Evidence;
        var memory = MemoryCapacityPreview.From(report)?.Evidence;
        var gpu = GpuPurchasePlan.Build(report);
        string scope = $"所选 {report.WindowDays} 天 · {report.SelectedScene ?? "全部场景"} · {report.Generated.LocalDateTime:yyyy-MM-dd HH:mm}";
        string notice = Notice;
        if (workspace.Criteria.Any(c => !Reviewed(c))) notice += " 候选页有未核对、已修改或过期来源的条件，它们尚未作为额外约束使用。";
        if (report.HistoryQuality.HasIssues) notice += " " + report.HistoryQuality.Message;
        if (report.ExperienceQuality.HasIssues) notice += " " + report.ExperienceQuality.Message;
        if (report.SemanticUseNotice.Length > 0) notice += " " + report.SemanticUseNotice;
        var cases = CandidateComparison.ConfigurationScenarios(goals).Select(scenario =>
        {
            var rows = workspace.Candidates.Select(candidate => Assess(candidate, scenario)).ToArray();
            return new CandidateTriageCase(scenario.Label, string.Join(" ", scenario.Conflicts.Select(c => c.Message)),
                rows.OrderBy(r => r.Conflict).ThenBy(r => r.Failure).ThenBy(r => r.Footprint)
                    .ThenBy(r => r.HasQuote ? 0 : 1).ThenBy(r => r.Price).ThenBy(r => r.Route).ThenBy(r => r.Input).Select(r => r.Row).ToArray())
            { ScenarioKey = scenario.Key, Source = string.Join("\n", scenario.Criteria.Select(c => c.Name + "：" + c.Evidence).Distinct()) };
        }).ToArray();
        return new(scope, notice, cases);

        (CandidateTriageRow Row, int Conflict, int Failure, int Footprint, int Route, bool HasQuote, double Price, int Input) Assess(PurchaseCandidate c, ConfigurationScenario scenario)
        {
            bool specs = !string.IsNullOrWhiteSpace(c.SpecificationSource) || !string.IsNullOrWhiteSpace(c.Source);
            bool quote = c.PriceCny.HasValue && !string.IsNullOrWhiteSpace(c.Source);
            var reasons = new List<string>(); var issues = new List<string>(); var pending = new List<string>();
            foreach (var goal in scenario.Criteria.Where(g => g.Metric is "price" or "memory" or "vram" or "storage"))
            {
                if (!goal.Target.HasValue) { pending.Add("目标数值待核对：" + goal.Name); continue; }
                double? value = goal.Metric switch { "price" => c.PriceCny, "memory" => c.MemoryGb, "vram" => c.VramGb, _ => c.StorageGb };
                string target = $"{CandidateComparison.Metrics.Single(m => m.Key == goal.Metric).Label} {CandidateComparison.DirectionLabel(goal.Direction)} {goal.Target:G} {goal.Unit}";
                if (value == null || !(goal.Metric == "price" ? quote : specs)) pending.Add("待补读数或来源：" + target);
                else if (!CandidateComparison.Meets(value.Value, goal.Target.Value, goal.Direction)) issues.Add($"{target}；该候选 {value:G} {goal.Unit}");
                else reasons.Add("资料数值符合：" + target);
            }
            int footprint = 0;
            if (storage != null)
            {
                double occupiedGB = storage.OccupiedGiB * (1073741824d / 1_000_000_000);
                if (specs && c.StorageGb.HasValue)
                {
                    if (c.StorageGb < occupiedGB)
                    { footprint = 1; issues.Add($"存储 {c.StorageGb:0.##} GB 小于已记录卷合计占用约 {occupiedGB:0.##} GB，若保留这些占用需释放、外迁或换容量"); }
                    else reasons.Add($"存储名义容量高于已记录占用约 {occupiedGB:0.##} GB；尚未扣系统预留与增长");
                }
                else pending.Add("保留现有文件所需的存储容量与来源待补");
                pending.Add("先逐卷核对迁移清单；合计读数可能含系统、临时文件或重复挂载");
                pending.Add($"容量读数：{storage.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {storage.Scope}");
            }
            if (memory != null)
            {
                if (specs && c.MemoryGb.HasValue && c.MemoryGb.Value / (1073741824d / 1_000_000_000) < memory.PeakGiB)
                {
                    footprint++;
                    issues.Add($"内存字段 {c.MemoryGb:0.##} GB（十进制名义容量约 {c.MemoryGb.Value / (1073741824d / 1_000_000_000):0.##} GiB）低于已测物理峰值 {memory.PeakGiB:0.0} GiB；需缩减当时占用或核对换页影响");
                }
                pending.Add($"内存物理峰值 {memory.PeakGiB:0.0} GiB、当前可用 {memory.MaximumObservedCapacityGiB:0.0} GiB；商品单位需核对，名义容量高于峰值也不能证明实际可用量或任务达标");
                pending.Add($"内存观测：{memory.Scope} · {memory.FirstRecord.LocalDateTime:MM/dd HH:mm}–{memory.LastRecord.LocalDateTime:MM/dd HH:mm}");
            }
            string kind = specs ? ExplicitGraphicsKind(c.Gpu) : "unknown";
            if (kind == "integrated" && scenario.Criteria.Any(g => g.Metric == "vram" && g.Target >= 0
                && (g.Direction == "gt" || g.Direction == "min" && g.Target > 0)))
                issues.Add("核显共享内存不能充当本情景要求的独立显存");
            int route = 0;
            if (gpu.Mode == "light")
            {
                route = kind == "integrated" ? 0 : kind == "discrete" ? 1 : 2;
                string measuredScope = report.Cards.FirstOrDefault(card => card.Key == "gpu")?.Observation?.Scope ?? gpu.Scope;
                reasons.Add(kind == "integrated" ? "已测核心/3D轻载，核显路线值得比较；是否省钱还看整机报价"
                    : kind == "discrete" ? "已测核心/3D轻载，按整机报价与实际任务比较；不单为独显型号加预算" : "显卡路线未明确，不按型号名猜测核显或独显");
                pending.Add("显卡顺序依据：" + measuredScope);
            }
            else pending.Add(gpu.Title);
            if (!specs) pending.Add("规格字段尚缺资料来源，不能据此判定条件不符");
            if (string.IsNullOrWhiteSpace(c.Cpu)) pending.Add("CPU 型号待补");
            pending.Add("CPU、GPU 及软件功能按实际任务核对；不按型号或旧机利用率推算新机表现");
            if (!quote) pending.Add(c.PriceCny.HasValue ? "报价已有金额，尚缺报价来源" : "报价待补，不能认定比有价候选更贵");
            if (scenario.Criteria.Any(g => g.Metric is "task" or "check")) pending.Add("任务与功能目标及其结果请在下方逐项核对");
            string state = scenario.Conflicts.Length > 0 ? "先核对情景条件" : issues.Count > footprint ? "规格或报价与条件不符"
                : footprint > 0 ? "保持已测占用需调整容量" : !specs ? "补齐规格来源后比较" : "可进入任务比较";
            string quoteText = quote ? $"报价 ¥{c.PriceCny:0.##}（有出处，待核实）" : c.PriceCny.HasValue ? $"¥{c.PriceCny:0.##} · 来源待补" : "报价未知";
            string sources = "规格资料：" + (string.IsNullOrWhiteSpace(c.SpecificationSource) ? c.Source : c.SpecificationSource)
                + "\n报价来源：" + (string.IsNullOrWhiteSpace(c.Source) ? "未填写" : c.Source);
            var row = new CandidateTriageRow(c.Id, c.Name, state, $"CPU：{Known(c.Cpu)} · GPU：{Known(c.Gpu)}\n内存 {Number(c.MemoryGb)} GB · 独立显存 {Number(c.VramGb)} GB · 存储 {Number(c.StorageGb)} GB",
                quoteText, reasons.Distinct().ToArray(), issues.Distinct().ToArray(), pending.Distinct().ToArray(), sources) { ScenarioKey = scenario.Key };
            return (row, scenario.Conflicts.Length > 0 ? 1 : 0, Math.Max(0, issues.Count - footprint), footprint, route, quote, quote ? c.PriceCny!.Value : 0, workspace.Candidates.IndexOf(c));
        }
    }

    public static string ExplicitGraphicsKind(string text)
    {
        string label = (text ?? "").Trim();
        if (Regex.IsMatch(label, @"^(集成显卡|核芯显卡|核显)(?:$|\s*[·:/（(])", RegexOptions.CultureInvariant)) return "integrated";
        if (Regex.IsMatch(label, @"^(独立显卡|独显)(?:$|\s*[·:/（(])", RegexOptions.CultureInvariant)) return "discrete";
        return "unknown";
    }
    private static string Known(string value) => string.IsNullOrWhiteSpace(value) ? "未填写" : value;
    private static string Number(double? value) => value.HasValue ? $"{value:0.##}" : "未知";
    public static string Html(CandidateTriageReport report)
    {
        string E(string text) => WebUtility.HtmlEncode(text);
        string cases = string.Join("", report.Cases.Select(c => "<section><h2>" + E(c.Label) + "</h2><p>" + E(c.Notice) + "</p><details><summary>需求与条件依据</summary><pre>" + E(c.Source) + "</pre></details>"
            + string.Join("", c.Candidates.Select(r => $"<article><h3>{E(r.Name)} · {E(r.State)}</h3><p>{E(r.Quote)}</p><p>{E(r.Specifications)}</p><p>{E(r.ReasonText)}</p><p class='issue'>{E(r.IssueText)}</p><details><summary>待核对项与资料来源</summary><p>{E(r.PendingText)}</p><pre>{E(r.Sources)}</pre></details></article>")) + "</section>"));
        return "<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>候选自动初筛</title><style>body{font:15px/1.7 system-ui,sans-serif;max-width:960px;margin:32px auto;padding:20px;color:#192a36;background:#f4f7f8}section,article{background:white;padding:18px;margin:16px 0;border:1px solid #e3eaec;border-radius:10px}p,pre{white-space:pre-wrap;overflow-wrap:anywhere}pre{font:inherit}.issue{color:#98631d}</style><h1>候选自动初筛</h1><p>"
            + E(report.Scope) + "</p><p>" + E(report.Notice) + "</p>" + cases + "</html>";
    }
}
