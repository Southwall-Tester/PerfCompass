using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public sealed class PurchaseCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新候选";
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public double? PriceCny { get; set; }
    public double? MemoryGb { get; set; }
    public double? VramGb { get; set; }
    public double? StorageGb { get; set; }
    public string Source { get; set; } = "";
    public string SpecificationSource { get; set; } = "";
}
public sealed class PurchaseCriterion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginId { get; set; } = "";
    public string Name { get; set; } = "新条件";
    public string Metric { get; set; } = "check";
    public string Direction { get; set; } = "min";
    public double? Target { get; set; }
    public string Unit { get; set; } = "";
    public string Condition { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Procedure { get; set; } = "";
    public string MeasurementProtocol { get; set; } = "";
    private bool reviewed;
    public bool Reviewed { get => reviewed; set { reviewed = value; if (value) ReviewStamp = CandidateComparison.CriterionStamp(this); } }
    public string ReviewStamp { get; set; } = "";
}
public sealed class PurchaseObservation
{
    public string CandidateId { get; set; } = "";
    public string CriterionId { get; set; } = "";
    public string CandidateName { get; set; } = "";
    public string CriterionName { get; set; } = "";
    public double? Value { get; set; }
    public string Verdict { get; set; } = "unknown";
    public string Evidence { get; set; } = "";
    public TaskMeasurementResult? Measurement { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string>? SignatureFactory { get; set; }
    private bool comparable;
    public bool Comparable { get => comparable; set { comparable = value; if (value && SignatureFactory != null) ProofStamp = SignatureFactory(); } }
    public string ProofStamp { get; set; } = "";
}
public sealed class PurchaseWorkspace
{
    public string Version { get; set; } = "comparison-1.4";
    public string ContextStamp { get; set; } = "";
    public bool CoverageConfirmed { get; set; }
    public List<PurchaseCandidate> Candidates { get; set; } = [];
    public List<PurchaseCriterion> Criteria { get; set; } = [];
    public List<PurchaseObservation> Observations { get; set; } = [];
    public SpecificationDraft SpecificationDraft { get; set; } = new();
}
public record PurchaseCheck(string Name, string State, string Reading, string Evidence, string Condition)
{
    public string ScenarioKey { get; init; } = "";
    public string EvidenceKind { get; init; } = "none";
    public string EvidenceLabel { get; init; } = "待补依据";
}
public record PurchaseAssessment(string Id, string Name, string State, double? PriceCny, List<PurchaseCheck> Checks);
public record PurchaseConflict(string Metric, string Condition, string[] CriterionIds, string Message)
{
    public string ScenarioKey { get; init; } = "";
}
public record PurchaseComparison(string Summary, List<PurchaseAssessment> Candidates, string[] Context, string ContextStamp)
{
    public string[] Tradeoffs { get; init; } = [];
    public string[] ScenarioSummaries { get; init; } = [];
    public PurchaseConflict[] Conflicts { get; init; } = [];
    public PurchaseShortlist[] Shortlists { get; init; } = [];
}

public static partial class CandidateComparison
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static bool HasQuoteEvidence(PurchaseCandidate candidate) => candidate.PriceCny.HasValue && !string.IsNullOrWhiteSpace(candidate.Source);
    public static string CriterionStamp(PurchaseCriterion c)
    {
        string legacy = Hash(new { c.Name, c.Metric, c.Direction, c.Target, c.Unit, c.Condition, c.Evidence, c.Procedure });
        if (IsUnresolvedCondition(c)) legacy = Hash(new { Goal = legacy, Scenario = ScenarioKey(c) });
        return string.IsNullOrEmpty(c.MeasurementProtocol) ? legacy : Hash(new { Goal = legacy, c.MeasurementProtocol });
    }
    public static string ObservationStamp(PurchaseCandidate candidate, PurchaseCriterion criterion) => Hash(new { candidate.Id, candidate.Cpu, candidate.Gpu, candidate.MemoryGb, candidate.VramGb, candidate.StorageGb, Criterion = CriterionStamp(criterion) });
    public static readonly SemanticChoice[] Metrics = [new("check", "逐项核对"), new("price", "整机报价 / 元"), new("memory", "内存 / GB"), new("vram", "独立显存 / GB"), new("storage", "存储 / GB"), new("task", "实际任务测量")];
    public static readonly SemanticChoice[] Directions = [new("min", "至少"), new("max", "不超过"), new("gt", "严格大于"), new("lt", "严格小于")];
    public static bool Meets(double value, double target, string direction) => direction switch { "min" => value >= target, "max" => value <= target, "gt" => value > target, "lt" => value < target, _ => false };
    public static string DirectionLabel(string direction) => Directions.FirstOrDefault(d => d.Key == direction)?.Label ?? "方向无效";
    public static readonly SemanticChoice[] Verdicts = [new("unknown", "待核对"), new("pass", "满足"), new("fail", "不满足")];
    public static string Stamp(AnalysisReport report) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        ComparisonScopeRules = "uncertainty-1",
        report.FutureNeeds, report.SemanticRequirements, report.ConfirmedRequirements, report.SelectedScene, report.WindowDays, report.MethodVersion,
        report.HardwareId, report.RepresentativeConfirmed,
        GpuMetricScopes = report.GpuMetricScopes.Order(StringComparer.Ordinal).ToArray(),
        Scenes = report.Scenes.Order().ToArray(),
        // Bind changes in observed scope and task clues, not every new percentage or
        // elapsed second. A second busy engine can be hidden by an unchanged GPU card.
        GpuEngines = report.GpuEngineProfiles.OrderBy(p => p.Kind, StringComparer.Ordinal).Select(p => new
        {
            p.Kind, Sources = p.Sources.Order(StringComparer.Ordinal).ToArray(),
            SourceScopes = (p.SourceScopes.Length > 0 ? p.SourceScopes : [JsonSerializer.Serialize(p.Sources.Order(StringComparer.Ordinal).ToArray())])
                .Order(StringComparer.Ordinal).ToArray(),
            Coverage = p.ObservedHours <= 0 ? "missing"
                : p.CompleteHours + 1e-7 < Math.Max(p.ObservedHours, report.ActiveHours) ? "partial" : "complete",
            Heavy = p.HeavyMinutes > 0 || p.Peak >= 80
        }).ToArray(),
        Trends = report.DemandTrends.Where(t => t.Status == "increased").Select(t => t.Key + ":" + t.Days + ":" + string.Join("/", t.RecentHeavyScenes.Order())).Order().ToArray(),
        Experiences = report.ExperienceEvents.Where(e => !e.Resolved).Select(e => Hash(new { e.Id, e.Time, e.HardwareId, e.Scene, e.Symptom, e.Note, e.Clues })).Order().ToArray(),
        Signals = report.Cards.Where(c => c.Key is "cpu" or "gpu" or "memory").Select(c => c.Key + ":" + c.Status + ":" + c.Title
            + (c.Observation is { } observation ? ":observation:" + Hash(new { observation.State, observation.Scope }) : "")
            + (c.RetainedWorkload is { } retained ? ":retained:" + Hash(new { retained.State, retained.Scope }) : "")).Order().ToArray()
    })))) + (report.HistoryQuality.HasIssues ? ":history:" + report.HistoryQuality.Signature : "")
        + (report.ExperienceQuality.HasIssues ? ":experience:" + report.ExperienceQuality.Signature : "")
        + (report.SemanticUseNotice.Length > 0 ? ":meaning:" + Hash(report.SemanticUseNotice) : "")
        + (report.SemanticUseSignature.Length > 0 ? ":meaning-data:" + report.SemanticUseSignature : "");
    public static List<PurchaseCriterion> Drafts(AnalysisReport report)
    {
        var result = new List<PurchaseCriterion>();
        if (report.SemanticRequirements is { } plan)
            foreach (var f in plan.Facts.Where(SemanticUse.Relevant))
            {
                var c = new PurchaseCriterion { OriginId = "semantic:" + Hash(SemanticUse.SourceIdentity(f)) + SemanticUse.IdentitySuffix(f), Name = f.Subject + " · " + SemanticUse.Label(f) + " · 原文" + SemanticRequirements.Label(f.Timeframe) + "：" + f.Detail, Evidence = f.Evidence,
                    Condition = SemanticUse.PendingUse(f) ? "需求尚未确定：" + f.Subject + (string.IsNullOrWhiteSpace(f.Condition) ? "" : "（用途待核对；原触发条件：" + f.Condition + "）")
                        : SemanticUse.Conditional(f) ? (string.IsNullOrWhiteSpace(f.Condition) ? "需求尚未确定：" + f.Subject : f.Condition) : "" };
                // An exclusion is a checklist of the entire negative statement, never a positive capacity floor.
                var quantities = QuantityGoals.Compile(f);
                if (quantities.Count == 0) result.Add(c);
                else foreach (var q in quantities)
                {
                    result.Add(new() { OriginId = c.OriginId + (quantities.Count > 1 ? ":" + q.Direction : ""),
                        Name = c.Name + (quantities.Count > 1 ? "（" + DirectionLabel(q.Direction) + "）" : ""),
                        Condition = c.Condition, Evidence = c.Evidence, Metric = q.Metric, Direction = q.Direction, Target = q.Target, Unit = q.Unit });
                }
            }
        else if (report.ConfirmedRequirements is { } rules)
            result.Add(new() { OriginId = "rules:" + Hash(rules), Name = "验证已确认需求：" + rules.Summary, Evidence = rules.Source });
        else if (!string.IsNullOrWhiteSpace(report.FutureNeeds))
            result.Add(new() { OriginId = "raw:" + Hash(report.FutureNeeds), Name = "核对尚未结构化的需求全文", Evidence = report.FutureNeeds });
        foreach (var scene in report.SceneProfiles.Where(s => s.ActiveHours > 0 && (report.SelectedScene == null || report.SelectedScene == s.Name)))
            result.Add(new() { OriginId = "scene:" + scene.Name, Name = "验证已有场景：" + scene.Name, Evidence = "已有观测 " + scene.DurationLabel + "；请包含重要和偶发任务，按候选电脑实际表现核对。" });
        if (report.Cards.SingleOrDefault(c => c.Key == "gpu")?.RetainedWorkload is { } retained)
            result.Add(new() { OriginId = "retained:gpu-core", Name = "核对所选范围已观测的核心/3D重负载任务",
                Evidence = retained.Scope + "；" + retained.Evidence + " " + retained.Action });
        foreach (var engine in report.GpuEngineProfiles.Where(p => p.ObservedHours > 0 && (p.HeavyMinutes > 0 || p.Peak >= 80)))
        {
            // A generic scene may contain several engine tasks. Keep a stable checklist
            // origin for each busy category without inventing a capacity or speed target.
            var sources = engine.Sources.Order(StringComparer.Ordinal).ToArray();
            string nodes = sources.Length == 0 ? "来源未取得" : string.Join("、", sources.Take(8));
            if (sources.Length > 8) nodes += $"；另 {sources.Length - 8} 个节点未在此展开，完整来源见分引擎历史";
            result.Add(new()
            {
                OriginId = "gpu-engine:" + engine.Kind, Name = "核对已观测的" + engine.Name + "重负载任务",
                Evidence = $"类别：{engine.Name}；已暴露节点：{nodes}；峰值 {(engine.Peak.HasValue ? $"{engine.Peak:0.##}%" : "未取得")}；负载≥80% 累计 {engine.HeavyMinutes:G6} 分钟。"
                    + "已知节点的重负载线索不证明瓶颈或升级收益，也不证明所有引擎覆盖完整。请确认对应任务是否仍需运行，并在候选电脑核对功能与实际表现。"
            });
        }
        foreach (var trend in report.DemandTrends.Where(t => t.Status == "increased").GroupBy(t => t.Key).Select(g => g.First()))
            result.Add(new() { OriginId = "trend:" + trend.Key, Name = "验证近期增加的" + trend.Name + "任务", Evidence = trend.Summary + "；" + string.Join("、", trend.RecentHeavyScenes) });
        foreach (var e in report.ExperienceEvents.Where(e => !e.Resolved))
            result.Add(new() { OriginId = "experience:" + e.Id, Name = "验证体验已改善：" + e.Title, Evidence = e.Note });
        return result;
    }
    public static void Validate(PurchaseWorkspace workspace)
    {
        void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new ArgumentException(message); }
        bool Text(string? text, int max) => text != null && text.Length <= max;
        bool Number(double? value) => value == null || double.IsFinite(value.Value) && value >= 0 && value <= 1e12;
        Need(workspace.Version is "comparison-1.0" or "comparison-1.1" or "comparison-1.2" or "comparison-1.3" or "comparison-1.4" && workspace.Candidates != null && workspace.Criteria != null && workspace.Observations != null, "比较文件版本或内容无效。");
        Need(workspace.Candidates!.Count <= 3 && workspace.Criteria!.Count <= 40 && workspace.Observations!.Count <= 120, "最多比较三套配置、40 项条件。");
        Need(workspace.Candidates.All(c => c != null && Text(c.Id, 100) && !string.IsNullOrWhiteSpace(c.Id) && Text(c.Name, 150) && !string.IsNullOrWhiteSpace(c.Name) && Text(c.Cpu, 200) && Text(c.Gpu, 200) && Text(c.Source, 2000) && new[] { c.PriceCny, c.MemoryGb, c.VramGb, c.StorageGb }.All(Number)), "候选名称、容量、价格或来源无效；数值须非负且有限。");
        Need(workspace.Candidates.Select(c => c.Id).Distinct().Count() == workspace.Candidates.Count, "候选编号重复。");
        Need(workspace.Candidates.All(c => Text(c.SpecificationSource, 12000)), "规格来源过长或缺失。");
        var draft = workspace.SpecificationDraft;
        Need(draft != null && Text(draft.Source, 1000) && Text(draft.Text, 8000) && Text(draft.Name, 150) && Text(draft.Cpu, 200) && Text(draft.Gpu, 200)
            && new[] { draft.Memory, draft.Vram, draft.Storage }.All(s => Text(s, 100)) && Text(draft.ParsedStamp, 64)
            && (draft.RecordedAt == null || draft.RecordedAt >= DateTimeOffset.UnixEpoch), "规格摘录草稿字段无效。");
        Need(workspace.Candidates.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == workspace.Candidates.Count, "请为每套候选填写不同名称。");
        Need(workspace.Criteria.All(c => c != null && Text(c.Id, 100) && !string.IsNullOrWhiteSpace(c.Id) && Text(c.OriginId, 200) && Text(c.Name, 2000) && !string.IsNullOrWhiteSpace(c.Name) && Metrics.Any(m => m.Key == c.Metric) && Directions.Any(d => d.Key == c.Direction) && Number(c.Target) && Text(c.Unit, 50) && Text(c.Condition, 500) && Text(c.Evidence, 3000) && Text(c.Procedure, 2000)), "条件名称、类型、数值或依据无效。");
        Need(workspace.Criteria.Select(c => c.Id).Distinct().Count() == workspace.Criteria.Count, "条件编号重复。");
        Need(workspace.Criteria.All(c => c.Metric is "check" or "task" || c.Unit == (c.Metric == "price" ? "元" : "GB")), "规格比较单位固定为 GB，报价固定为人民币元；请先换算数值。");
        Need(workspace.Observations.All(o => o != null && workspace.Candidates.Any(c => c.Id == o.CandidateId) && workspace.Criteria.Any(c => c.Id == o.CriterionId) && Number(o.Value) && Verdicts.Any(v => v.Key == o.Verdict) && Text(o.Evidence, 3000)), "测量记录、来源或关联条件无效。");
        Need(workspace.Observations.Select(o => (o.CandidateId, o.CriterionId)).Distinct().Count() == workspace.Observations.Count, "同一候选与条件存在重复测量，请合并核对。");
        Need(workspace.Criteria.All(c => c.MeasurementProtocol != null && (c.MeasurementProtocol.Length == 0 || c.MeasurementProtocol.Length == 64 && c.MeasurementProtocol.All(Uri.IsHexDigit))), "测量协议校验值无效。");
        foreach (var observation in workspace.Observations.Where(o => o.Measurement != null)) TaskMeasurements.Validate(observation.Measurement!);
    }
    public static PurchaseComparison Evaluate(PurchaseWorkspace workspace, AnalysisReport report)
    {
        Validate(workspace);
        if (OutdatedSemanticCriteria(workspace, report).Count > 0) throw new ArgumentException("部分条件的源需求或购机用途需要重新核对。请删除这些条件，或选中后保留为独立目标并重新核对。");
        string stamp = Stamp(report);
        bool current = workspace.ContextStamp == stamp;
        var conflicts = FindConflicts(workspace.Criteria);
        bool requiredConflict = conflicts.Any(c => c.Condition.Length == 0);
        var assessments = new List<PurchaseAssessment>();
        foreach (var candidate in workspace.Candidates)
        {
            var checks = new List<PurchaseCheck>();
            foreach (var c in workspace.Criteria)
            {
                var o = workspace.Observations.SingleOrDefault(o => o.CandidateId == candidate.Id && o.CriterionId == c.Id);
                double? value = c.Metric switch { "price" => candidate.PriceCny, "memory" => candidate.MemoryGb, "vram" => candidate.VramGb, "storage" => candidate.StorageGb, _ => o?.Value };
                string source = c.Metric is "check" or "task" ? o?.Evidence ?? ""
                    : c.Metric == "price" || string.IsNullOrWhiteSpace(candidate.SpecificationSource) ? candidate.Source : candidate.SpecificationSource;
                bool evidence = !string.IsNullOrWhiteSpace(source);
                string sourceDisplay = source;
                if (c.Metric is "memory" or "vram" or "storage" && !string.IsNullOrWhiteSpace(candidate.SpecificationSource))
                {
                    int rawStart = source.IndexOf("\n原文：\n", StringComparison.Ordinal);
                    if (rawStart >= 0) sourceDisplay = source[..rawStart] + "\n完整原文见本候选的独立规格资料。";
                }
                string state = "unknown", reading = "待补充结果与来源";
                string evidenceKind = c.Metric switch
                {
                    "check" => "manual-check",
                    "task" when o?.Measurement != null => "repeated-measurement",
                    "task" when c.MeasurementProtocol.Length > 0 => "none",
                    "task" => "manual-measurement",
                    _ => "specification"
                };
                bool repeatedAssessed = false;
                bool proofCurrent = o?.Comparable == true && o.ProofStamp == ObservationStamp(candidate, c);
                if (!c.Reviewed || c.ReviewStamp != CriterionStamp(c)) reading = "条件已修改或尚未核对，请重新勾选已核对";
                else if (c.Metric == "check")
                {
                    if (evidence && proofCurrent) { state = o!.Verdict; reading = Verdicts.First(v => v.Key == state).Label; }
                    else reading = "核对完整条件并填写依据，确认测试可比";
                }
                else if (value.HasValue && c.Target.HasValue && evidence && (c.Metric != "task" || proofCurrent && !string.IsNullOrWhiteSpace(c.Procedure) && !string.IsNullOrWhiteSpace(c.Unit)))
                {
                    if (c.Metric == "task" && (o?.Measurement != null || c.MeasurementProtocol.Length > 0))
                    {
                        (state, reading) = o?.Measurement == null ? ("unknown", "此目标已绑定测量协议，请导入匹配的原始测量文件") : TaskMeasurements.Assess(o.Measurement, c, value);
                        repeatedAssessed = o?.Measurement != null;
                    }
                    else
                    {
                        state = Meets(value.Value, c.Target.Value, c.Direction) ? "pass" : "fail";
                        reading = $"{value:G} {c.Unit}；目标{DirectionLabel(c.Direction)} {c.Target:G} {c.Unit}";
                    }
                }
                string evidenceLabel = evidenceKind switch
                {
                    "specification" when c.Metric is "memory" or "vram" or "storage" && !string.IsNullOrWhiteSpace(candidate.SpecificationSource) => "独立规格资料",
                    "specification" => "规格 / 报价资料",
                    "manual-check" => "人工核对",
                    "manual-measurement" => "手填任务数值",
                    "repeated-measurement" when repeatedAssessed && state == "pass" => "重复耗时测量达标",
                    "repeated-measurement" when repeatedAssessed && state == "fail" => "重复耗时测量未达标",
                    "repeated-measurement" => "重复耗时测量待核对",
                    _ => "尚缺原始重复测量"
                };
                checks.Add(new(c.Name, state, reading, sourceDisplay, c.Condition) { ScenarioKey = ScenarioKey(c), EvidenceKind = evidenceKind, EvidenceLabel = evidenceLabel });
            }
            var required = checks.Where(c => string.IsNullOrWhiteSpace(c.Condition)).ToArray();
            string overall = requiredConflict ? "unknown" : required.Any(c => c.State == "fail") ? "fail" : !current || !workspace.CoverageConfirmed || required.Length == 0 || required.Any(c => c.State != "pass") ? "unknown" : "pass";
            assessments.Add(new(candidate.Id, candidate.Name, overall, candidate.PriceCny, checks));
        }
        // Task evidence establishes its own target result; it cannot establish
        // where a separately entered quotation came from.
        var evidencedQuotes = workspace.Candidates.Where(HasQuoteEvidence).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var passing = assessments.Where(a => a.State == "pass" && evidencedQuotes.Contains(a.Id)).OrderBy(a => a.PriceCny).ToArray();
        string MissingQuoteSourceNotice(IEnumerable<PurchaseAssessment> candidates)
        {
            var missing = candidates.Where(a => a.PriceCny.HasValue && !evidencedQuotes.Contains(a.Id)).Select(a => a.Name).ToArray();
            return missing.Length == 0 ? "" : " 待补报价来源：" + string.Join("、", missing) + "；这些金额暂不参与最低报价、价差或节省计算。";
        }
        string summary = requiredConflict ? "必需条件存在数值冲突。请先核对冲突项目的方向、数值和触发条件，再比较候选电脑。" : !current ? "比较范围已改变或尚未核对，请重新核对条件覆盖范围。" : !workspace.CoverageConfirmed ? "请先核对是否覆盖重要任务、新需求和近期体验问题。"
            : assessments.Count == 1 ? $"候选「{assessments[0].Name}」：" + (assessments[0].State == "pass" ? "必需条件已通过。" : assessments[0].State == "fail" ? "存在不满足的必需条件。" : "仍有待验证的必需条件。")
                + (assessments[0].PriceCny.HasValue ? $" 已记录整机报价 {assessments[0].PriceCny:0.##} 元；单台验证不计算候选价差。" : " 报价未填写，任务与规格目标可单独判断。")
            : passing.Length == 0 ? assessments.Any(a => a.State == "pass")
                ? $"已有 {assessments.Count(a => a.State == "pass")} 套候选通过必需条件；补齐报价和来源后比较价格。"
                : "当前没有通过必需条件的候选。调整不满足条件的配置，或补齐待验证项。"
            : "必需条件已通过且已填写报价来源的候选中，最低报价为 " + string.Join("、", passing.Where(p => p.PriceCny == passing[0].PriceCny).Select(p => p.Name)) + $"（{passing[0].PriceCny:0.##} 元）。";
        if (passing.Length > 1)
        {
            var max = passing.Max(p => p.PriceCny)!.Value;
            if (max > passing[0].PriceCny) summary += $" 与通过条件的最高报价相比可少花 {max - passing[0].PriceCny:0.##} 元；更高价格的收益需看具体任务结果。";
        }
        summary += MissingQuoteSourceNotice(assessments);
        if (workspace.Criteria.Any(c => !string.IsNullOrWhiteSpace(c.Condition))) summary += " 条件需求单列；触发时还需通过对应项目。";
        var context = new[] { $"分析范围：最近 {report.WindowDays} 天 / {report.SelectedScene ?? "全部场景"}；已观测 {report.DurationLabel}。比较结论覆盖已核对的条件，条件需求按触发情况另行判断。",
                "证据类型逐项列出。重复耗时测量达标仅表示所附原始正式轮次全部达到该目标；机器对应关系与实际产物仍由用户核对。" }
            .Concat(report.HistoryQuality.HasIssues ? new[] { report.HistoryQuality.Message + " 候选结果仅限已逐项核对的条件，不覆盖无法读取的历史任务。", report.HistoryQuality.Details } : [])
            .Concat(report.ExperienceQuality.HasIssues ? new[] { report.ExperienceQuality.Message + " 候选结果仅限已核对条件，不覆盖无法读取的体验。", report.ExperienceQuality.Details } : [])
            .Concat(report.SemanticUseNotice.Length > 0 ? new[] { report.SemanticUseNotice } : [])
            .Concat(report.Cards.Where(c => c.Key is "cpu" or "gpu" or "memory").Select(c => c.Name + "：" + c.DisplayTitle + "；" + c.DisplaySuggestion)).ToArray();
        var tradeoffs = new List<string>();
        if (passing.Length > 0)
        {
            var baseline = workspace.Candidates.Single(c => c.Id == passing[0].Id);
            foreach (var candidate in workspace.Candidates.Where(c => passing.Any(p => p.Id == c.Id) && c.PriceCny > baseline.PriceCny))
            {
                var changes = new List<string>();
                void Capacity(string name, double? before, double? after) { if (before.HasValue && after.HasValue && before != after) changes.Add($"{name} {before:0.###} → {after:0.###} GB"); }
                Capacity("内存", baseline.MemoryGb, candidate.MemoryGb); Capacity("独立显存", baseline.VramGb, candidate.VramGb); Capacity("存储", baseline.StorageGb, candidate.StorageGb);
                foreach (var goal in workspace.Criteria.Where(c => c.Metric == "task" && c.Reviewed && c.ReviewStamp == CriterionStamp(c)))
                {
                    var before = workspace.Observations.SingleOrDefault(o => o.CandidateId == baseline.Id && o.CriterionId == goal.Id);
                    var after = workspace.Observations.SingleOrDefault(o => o.CandidateId == candidate.Id && o.CriterionId == goal.Id);
                    bool ValidMeasurement(PurchaseObservation observation) => observation.Measurement == null ? goal.MeasurementProtocol.Length == 0 : TaskMeasurements.Assess(observation.Measurement, goal, observation.Value).State != "unknown";
                    if (before?.Value != null && after?.Value != null && before.Comparable && after.Comparable && before.ProofStamp == ObservationStamp(baseline, goal) && after.ProofStamp == ObservationStamp(candidate, goal) && !string.IsNullOrWhiteSpace(before.Evidence) && !string.IsNullOrWhiteSpace(after.Evidence) && ValidMeasurement(before) && ValidMeasurement(after))
                        changes.Add($"{goal.Name}：{before.Value:0.###} → {after.Value:0.###} {goal.Unit}" + (goal.Condition.Length > 0 ? "（条件：" + goal.Condition + "）" : ""));
                }
                tradeoffs.Add($"{candidate.Name} 比 {baseline.Name} 多花 {candidate.PriceCny - baseline.PriceCny:0.##} 元。" + (changes.Count > 0 ? string.Join("；", changes) + "。" : "当前没有记录可量化的容量差异或任务收益。") + "超出必需条件的部分是否值得，由任务收益和条件需求决定。");
            }
        }
        var scenarios = new List<string>();
        foreach (var branch in workspace.Criteria.Where(c => !string.IsNullOrWhiteSpace(c.Condition)).GroupBy(ScenarioKey))
        {
            string condition = ScenarioLabel(branch);
            var targetsPassed = assessments.Where(a => a.State == "pass" && a.Checks.Where(c => c.ScenarioKey == branch.Key).All(c => c.State == "pass")).ToArray();
            var qualified = targetsPassed.Where(a => evidencedQuotes.Contains(a.Id)).OrderBy(a => a.PriceCny).ToArray();
            string conflict = requiredConflict ? "必需条件存在数值冲突，请先修正。" : conflicts.Any(c => c.ScenarioKey == branch.Key) ? "本情景存在数值冲突，请核对冲突项目。" : "";
            scenarios.Add("情景「" + condition + "」成立时：" + (conflict.Length > 0 ? conflict : qualified.Length == 0 ? "当前没有通过必需条件及此情景全部条件、且报价依据齐全的候选。请检查不满足或待验证项。" : "最低已核对报价为 " + string.Join("、", qualified.Where(a => a.PriceCny == qualified[0].PriceCny).Select(a => a.Name)) + $"（{qualified[0].PriceCny:0.##} 元）。")
                + (conflict.Length > 0 ? "" : MissingQuoteSourceNotice(targetsPassed)) + "多个条件同时成立时，还需一起核对所有相关项目。");
        }
        return new(summary, assessments, context, stamp) { Tradeoffs = tradeoffs.ToArray(), ScenarioSummaries = scenarios.ToArray(), Conflicts = conflicts,
            Shortlists = BuildShortlists(workspace, assessments, conflicts) };
    }
    public static PurchaseWorkspace Load(string root)
    {
        var path = Path.Combine(root, "comparison.json");
        if (!File.Exists(path) && !Directory.Exists(path)) return new();
        return LoadFile(path);
    }
    public static PurchaseWorkspace LoadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8388608) throw new InvalidDataException("比较文件过大。");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var workspace = JsonSerializer.Deserialize<PurchaseWorkspace>(reader.ReadToEnd()) ?? throw new InvalidDataException("比较文件为空。");
        Validate(workspace);
        if (workspace.Version is not ("comparison-1.3" or "comparison-1.4") && workspace.Criteria.Any(IsUnresolvedCondition))
        {
            workspace.CoverageConfirmed = false;
            foreach (var criterion in workspace.Criteria.Where(IsUnresolvedCondition)) criterion.Reviewed = false;
        }
        workspace.Version = "comparison-1.4"; return workspace;
    }
    public static void Save(string root, PurchaseWorkspace workspace) => SaveFile(Path.Combine(root, "comparison.json"), workspace);
    public static void SaveFile(string path, PurchaseWorkspace workspace)
    {
        Validate(workspace); workspace.Version = "comparison-1.4";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(workspace);
        if (bytes.Length > 8388608) throw new InvalidDataException("比较文件过大，请减少原始测量或拆分比较后再保存。");
        string target = Path.GetFullPath(path);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Html(PurchaseWorkspace workspace, PurchaseComparison comparison)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string State(string s) => s switch { "pass" => "满足已核对条件", "fail" => "有不满足项", _ => "待验证" };
        string Specification(PurchaseCandidate c) => string.IsNullOrWhiteSpace(c.SpecificationSource) ? ""
            : "<details><summary>独立规格资料与摘录原文</summary><pre style='white-space:pre-wrap'>" + E(c.SpecificationSource) + "</pre></details>";
        return "<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>候选配置比较</title><style>body{font:15px/1.8 system-ui,'Microsoft YaHei';background:#f4f7f8;color:#192a36;margin:30px auto;max-width:1000px;padding:20px}section{background:white;padding:24px;margin:20px 0;border-radius:12px}td,th{text-align:left;padding:12px;border-bottom:1px solid #ddd;vertical-align:top;overflow-wrap:anywhere;box-sizing:border-box}table{width:100%;border-collapse:collapse;table-layout:fixed}th:nth-child(1){width:30%}th:nth-child(2){width:28%}th:nth-child(3){width:42%}small{color:#677a87}</style><h1>候选配置比较</h1><p>" + E(comparison.Summary) + "</p><small>" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm") + " · comparison-1.4 · 报价与测量由用户提供；未独立验证其真实性。GB 容量按 1 TB = 1000 GB 归一化，GiB/TiB 请先换算，购买前核对厂商口径。</small>"
            + string.Join("", comparison.Shortlists.Select(s => "<section><h2>购机建议与加价取舍</h2><p>" + E(s.Scope) + "</p><h3>" + E(s.Summary) + "</h3>"
                + string.Join("", s.Options.Select(o => "<h4>" + E(o.Name) + " · " + E(o.Label) + "</h4><p>" + E(o.Detail) + "</p>")) + "<small>" + E(s.Notice) + "</small></section>"))
            + string.Join("", comparison.Conflicts.Select(c => "<section><h2>条件数值冲突</h2><p>" + E(c.Message) + "</p></section>"))
            + string.Join("", comparison.ScenarioSummaries.Select(t => "<section><h2>条件情景</h2><p>" + E(t) + "</p></section>"))
            + string.Join("", comparison.Tradeoffs.Select(t => "<section><p>" + E(t) + "</p></section>"))
            + string.Join("", comparison.Candidates.Select(a => { var candidate = workspace.Candidates.Single(c => c.Id == a.Id); return "<section><h2>" + E(a.Name) + " · 必需条件：" + State(a.State) + "</h2><p>" + E(candidate.Cpu) + " / " + E(candidate.Gpu) + "；报价 " + (a.PriceCny.HasValue ? E(a.PriceCny.Value.ToString("0.##")) + " 元" : "未填写") + "</p><p>报价/旧合并来源：" + E(string.IsNullOrWhiteSpace(candidate.Source) ? a.PriceCny.HasValue ? "待补报价来源，此金额不参与价格比较。" : "未填写" : candidate.Source) + "</p>" + Specification(candidate) + "<table><tr><th>条件</th><th>结果</th><th>依据类型与记录</th></tr>" + string.Join("", a.Checks.Select(c => "<tr><td>" + E(c.Name) + (c.Condition.Length > 0 ? "<br>条件：" + E(c.Condition) : "") + "</td><td>" + State(c.State) + "<br>" + E(c.Reading) + "</td><td><b>" + E(c.EvidenceLabel) + "</b><br>" + E(c.Evidence) + "</td></tr>")) + "</table></section>"; }))
            + "<section><h2>历史与近期任务依据</h2>" + string.Join("", comparison.Context.Select(c => "<p>" + E(c) + "</p>")) + "</section><section><h2>目标、条件与测试步骤</h2>" + string.Join("", workspace.Criteria.Select(c => "<p><b>" + E(c.Name) + "</b><br>需求依据：" + E(c.Evidence) + "<br>测试步骤：" + E(c.Procedure) + "</p>")) + "</section></html>";
    }
}
