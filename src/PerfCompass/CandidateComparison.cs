using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    public string Version { get; set; } = "comparison-1.1";
    public string ContextStamp { get; set; } = "";
    public bool CoverageConfirmed { get; set; }
    public List<PurchaseCandidate> Candidates { get; set; } = [];
    public List<PurchaseCriterion> Criteria { get; set; } = [];
    public List<PurchaseObservation> Observations { get; set; } = [];
}
public record PurchaseCheck(string Name, string State, string Reading, string Evidence, string Condition);
public record PurchaseAssessment(string Id, string Name, string State, double? PriceCny, List<PurchaseCheck> Checks);
public record PurchaseComparison(string Summary, List<PurchaseAssessment> Candidates, string[] Context, string ContextStamp)
{
    public string[] Tradeoffs { get; init; } = [];
    public string[] ScenarioSummaries { get; init; } = [];
}

public static class CandidateComparison
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static string CriterionStamp(PurchaseCriterion c)
    {
        string legacy = Hash(new { c.Name, c.Metric, c.Direction, c.Target, c.Unit, c.Condition, c.Evidence, c.Procedure });
        return string.IsNullOrEmpty(c.MeasurementProtocol) ? legacy : Hash(new { Goal = legacy, c.MeasurementProtocol });
    }
    public static string ObservationStamp(PurchaseCandidate candidate, PurchaseCriterion criterion) => Hash(new { candidate.Id, candidate.Cpu, candidate.Gpu, candidate.MemoryGb, candidate.VramGb, candidate.StorageGb, Criterion = CriterionStamp(criterion) });
    public static readonly SemanticChoice[] Metrics = [new("check", "逐项核对"), new("price", "整机报价 / 元"), new("memory", "内存 / GB"), new("vram", "独立显存 / GB"), new("storage", "存储 / GB"), new("task", "实际任务测量")];
    public static readonly SemanticChoice[] Directions = [new("min", "至少"), new("max", "不超过")];
    public static readonly SemanticChoice[] Verdicts = [new("unknown", "待核对"), new("pass", "满足"), new("fail", "不满足")];
    public static string Stamp(AnalysisReport report) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        report.FutureNeeds, report.SemanticRequirements, report.ConfirmedRequirements, report.SelectedScene, report.WindowDays, report.MethodVersion,
        Scenes = report.Scenes.Order().ToArray(),
        Trends = report.DemandTrends.Where(t => t.Status == "increased").Select(t => t.Key + ":" + t.Days + ":" + string.Join("/", t.RecentHeavyScenes.Order())).Order().ToArray(),
        Experiences = report.ExperienceEvents.Where(e => !e.Resolved).Select(e => e.Id).Order().ToArray(),
        Signals = report.Cards.Where(c => c.Key is "cpu" or "gpu" or "memory").Select(c => c.Key + ":" + c.Status + ":" + c.Title).Order().ToArray()
    }))));
    public static List<PurchaseCriterion> Drafts(AnalysisReport report)
    {
        var result = new List<PurchaseCriterion>();
        if (report.SemanticRequirements is { } plan)
            foreach (var f in plan.Facts.Where(f => f.Timeframe != "past"))
            {
                var c = new PurchaseCriterion { OriginId = "semantic:" + Hash(f), Name = f.Label + "：" + f.Detail, Evidence = f.Evidence,
                    Condition = f.Polarity is "conditional" or "uncertain" || f.Timeframe == "unspecified" ? (string.IsNullOrWhiteSpace(f.Condition) ? "需求尚未确定：" + f.Subject : f.Condition) : "" };
                // An exclusion is a checklist of the entire negative statement, never a positive capacity floor.
                if (f.Polarity == "required" && f.Timeframe is "current" or "future") TryNumeric(f, c);
                result.Add(c);
            }
        else if (report.ConfirmedRequirements is { } rules)
            result.Add(new() { OriginId = "rules:" + Hash(rules), Name = "验证已确认需求：" + rules.Summary, Evidence = rules.Source });
        else if (!string.IsNullOrWhiteSpace(report.FutureNeeds))
            result.Add(new() { OriginId = "raw:" + Hash(report.FutureNeeds), Name = "核对尚未结构化的需求全文", Evidence = report.FutureNeeds });
        foreach (var scene in report.SceneProfiles.Where(s => s.ActiveHours > 0 && (report.SelectedScene == null || report.SelectedScene == s.Name)))
            result.Add(new() { OriginId = "scene:" + scene.Name, Name = "验证已有场景：" + scene.Name, Evidence = "已有观测 " + scene.DurationLabel + "；请包含重要和偶发任务，按候选电脑实际表现核对。" });
        foreach (var trend in report.DemandTrends.Where(t => t.Status == "increased").GroupBy(t => t.Key).Select(g => g.First()))
            result.Add(new() { OriginId = "trend:" + trend.Key, Name = "验证近期增加的" + trend.Name + "任务", Evidence = trend.Summary + "；" + string.Join("、", trend.RecentHeavyScenes) });
        foreach (var e in report.ExperienceEvents.Where(e => !e.Resolved))
            result.Add(new() { OriginId = "experience:" + e.Id, Name = "验证体验已改善：" + e.Title, Evidence = e.Note });
        return result;
    }
    private static void TryNumeric(SemanticFact fact, PurchaseCriterion criterion)
    {
        var m = Regex.Match(fact.Value.Trim(), @"^(至少|不低于|>=|≥|最多|不超过|<=|≤)?\s*(\d+(?:\.\d+)?)\s*(GB|TB|MB|万元|元)?\s*(以内|以下|以上|起步)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return;
        string prefix = m.Groups[1].Value, suffix = m.Groups[4].Value;
        bool lower = prefix is "至少" or "不低于" or ">=" or "≥" || suffix is "以上" or "起步";
        bool upper = prefix is "最多" or "不超过" or "<=" or "≤" || suffix is "以内" or "以下";
        if (lower && upper || !lower && !upper && fact.Kind != "budget") return;
        string unit = m.Groups[3].Success ? m.Groups[3].Value : fact.Unit.Trim();
        string subject = fact.Subject;
        bool vram = Regex.IsMatch(subject, @"显存|(?i:\bVRAM\b)"), memory = Regex.IsMatch(subject, @"内存|(?i:\bRAM\b)"), storage = Regex.IsMatch(subject, @"存储|硬盘|(?i:\bSSD\b)");
        if (new[] { vram, memory, storage }.Count(v => v) > 1 || memory && Regex.IsMatch(subject, @"显卡|(?i:\bGPU\b)")) return;
        // Component budgets cannot be tested against the whole-computer quotation.
        bool wholeBudget = Regex.IsMatch(subject, @"^(整机|总|购机|电脑)?预算(上限)?$");
        string metric = fact.Kind == "budget" && wholeBudget && (unit is "元" or "万元") ? "price"
            : fact.Kind == "resource" && vram ? "vram"
            : fact.Kind == "resource" && memory ? "memory"
            : fact.Kind == "resource" && storage ? "storage" : "";
        if (metric.Length == 0 || metric != "price" && !new[] { "GB", "TB", "MB" }.Contains(unit, StringComparer.OrdinalIgnoreCase)) return;
        double scale = unit.Equals("TB", StringComparison.OrdinalIgnoreCase) ? 1000 : unit.Equals("MB", StringComparison.OrdinalIgnoreCase) ? 1d / 1000 : unit == "万元" ? 10000 : 1;
        criterion.Target = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * scale;
        criterion.Metric = metric; criterion.Unit = metric == "price" ? "元" : "GB";
        criterion.Direction = lower ? "min" : "max";
    }
    public static void Validate(PurchaseWorkspace workspace)
    {
        void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new ArgumentException(message); }
        bool Text(string? text, int max) => text != null && text.Length <= max;
        bool Number(double? value) => value == null || double.IsFinite(value.Value) && value >= 0 && value <= 1e12;
        Need(workspace.Version is "comparison-1.0" or "comparison-1.1" && workspace.Candidates != null && workspace.Criteria != null && workspace.Observations != null, "比较文件版本或内容无效。");
        Need(workspace.Candidates!.Count <= 3 && workspace.Criteria!.Count <= 40 && workspace.Observations!.Count <= 120, "最多比较三套配置、40 项条件。");
        Need(workspace.Candidates.All(c => c != null && Text(c.Id, 100) && !string.IsNullOrWhiteSpace(c.Id) && Text(c.Name, 150) && !string.IsNullOrWhiteSpace(c.Name) && Text(c.Cpu, 200) && Text(c.Gpu, 200) && Text(c.Source, 2000) && new[] { c.PriceCny, c.MemoryGb, c.VramGb, c.StorageGb }.All(Number)), "候选名称、容量、价格或来源无效；数值须非负且有限。");
        Need(workspace.Candidates.Select(c => c.Id).Distinct().Count() == workspace.Candidates.Count, "候选编号重复。");
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
        string stamp = Stamp(report);
        bool current = workspace.ContextStamp == stamp;
        var assessments = new List<PurchaseAssessment>();
        foreach (var candidate in workspace.Candidates)
        {
            var checks = new List<PurchaseCheck>();
            foreach (var c in workspace.Criteria)
            {
                var o = workspace.Observations.SingleOrDefault(o => o.CandidateId == candidate.Id && o.CriterionId == c.Id);
                double? value = c.Metric switch { "price" => candidate.PriceCny, "memory" => candidate.MemoryGb, "vram" => candidate.VramGb, "storage" => candidate.StorageGb, _ => o?.Value };
                string source = c.Metric is "check" or "task" ? o?.Evidence ?? "" : candidate.Source;
                bool evidence = !string.IsNullOrWhiteSpace(source);
                string state = "unknown", reading = "待补充结果与来源";
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
                        (state, reading) = o?.Measurement == null ? ("unknown", "此目标已绑定测量协议，请导入匹配的原始测量文件") : TaskMeasurements.Assess(o.Measurement, c, value);
                    else
                    {
                        state = (c.Direction == "min" ? value >= c.Target : value <= c.Target) ? "pass" : "fail";
                        reading = $"{value:0.###} {c.Unit}；目标{(c.Direction == "min" ? "至少" : "不超过")} {c.Target:0.###} {c.Unit}";
                    }
                }
                checks.Add(new(c.Name, state, reading, source, c.Condition));
            }
            var required = checks.Where(c => string.IsNullOrWhiteSpace(c.Condition)).ToArray();
            string overall = required.Any(c => c.State == "fail") ? "fail" : !current || !workspace.CoverageConfirmed || required.Length == 0 || required.Any(c => c.State != "pass") ? "unknown" : "pass";
            assessments.Add(new(candidate.Id, candidate.Name, overall, candidate.PriceCny, checks));
        }
        var passing = assessments.Where(a => a.State == "pass" && a.PriceCny.HasValue).OrderBy(a => a.PriceCny).ToArray();
        string summary = !current ? "需求或近期任务已改变，请重新核对条件覆盖范围。" : !workspace.CoverageConfirmed ? "请先核对是否覆盖重要任务、新需求和近期体验问题。"
            : passing.Length == 0 ? "当前没有同时通过必需条件且有报价的候选。调整不满足条件的候选，或补齐待验证项。"
            : "必需条件已通过且有报价的候选中，最低报价为 " + string.Join("、", passing.Where(p => p.PriceCny == passing[0].PriceCny).Select(p => p.Name)) + $"（{passing[0].PriceCny:0.##} 元）。";
        if (passing.Length > 0)
        {
            var max = passing.Max(p => p.PriceCny)!.Value;
            if (max > passing[0].PriceCny) summary += $" 与通过条件的最高报价相比可少花 {max - passing[0].PriceCny:0.##} 元；更高价格的收益需看具体任务结果。";
        }
        if (workspace.Criteria.Any(c => !string.IsNullOrWhiteSpace(c.Condition))) summary += " 条件需求单列；触发时还需通过对应项目。";
        var context = new[] { $"分析范围：最近 {report.WindowDays} 天 / {report.SelectedScene ?? "全部场景"}；已观测 {report.DurationLabel}。比较结论覆盖已核对的条件，条件需求按触发情况另行判断。" }
            .Concat(report.Cards.Where(c => c.Key is "cpu" or "gpu" or "memory").Select(c => c.Name + "：" + c.Title + "；" + c.Suggestion)).ToArray();
        var tradeoffs = new List<string>();
        if (passing.Length > 0)
        {
            var baseline = workspace.Candidates.Single(c => c.Id == passing[0].Id);
            foreach (var candidate in workspace.Candidates.Where(c => passing.Any(p => p.Id == c.Id) && c.PriceCny > baseline.PriceCny))
            {
                var changes = new List<string>();
                void Capacity(string name, double? before, double? after) { if (before.HasValue && after.HasValue && before != after) changes.Add($"{name} {before:0.###} → {after:0.###} GB"); }
                Capacity("内存", baseline.MemoryGb, candidate.MemoryGb); Capacity("独立显存", baseline.VramGb, candidate.VramGb); Capacity("存储", baseline.StorageGb, candidate.StorageGb);
                foreach (var goal in workspace.Criteria.Where(c => c.Metric == "task"))
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
        foreach (string condition in workspace.Criteria.Select(c => c.Condition.Trim()).Where(c => c.Length > 0).Distinct())
        {
            var qualified = assessments.Where(a => a.State == "pass" && a.Checks.Where(c => c.Condition.Trim() == condition).All(c => c.State == "pass") && a.PriceCny.HasValue).OrderBy(a => a.PriceCny).ToArray();
            scenarios.Add("情景「" + condition + "」成立时：" + (qualified.Length == 0 ? "当前没有通过必需条件及此情景全部条件、且有报价的候选。请检查不满足或待验证项。" : "最低已核对报价为 " + string.Join("、", qualified.Where(a => a.PriceCny == qualified[0].PriceCny).Select(a => a.Name)) + $"（{qualified[0].PriceCny:0.##} 元）。") + "多个条件同时成立时，还需一起核对所有相关项目。");
        }
        return new(summary, assessments, context, stamp) { Tradeoffs = tradeoffs.ToArray(), ScenarioSummaries = scenarios.ToArray() };
    }
    public static PurchaseWorkspace Load(string root)
    {
        var path = Path.Combine(root, "comparison.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 8388608) throw new InvalidDataException("比较文件过大。");
        var workspace = JsonSerializer.Deserialize<PurchaseWorkspace>(File.ReadAllText(path, Encoding.UTF8)) ?? throw new InvalidDataException("比较文件为空。");
        Validate(workspace); workspace.Version = "comparison-1.1"; return workspace;
    }
    public static void Save(string root, PurchaseWorkspace workspace)
    {
        Validate(workspace); workspace.Version = "comparison-1.1"; string path = Path.Combine(root, "comparison.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(workspace), new UTF8Encoding(false)); File.Move(path + ".tmp", path, true);
    }
    public static string Html(PurchaseWorkspace workspace, PurchaseComparison comparison)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string State(string s) => s switch { "pass" => "满足已核对条件", "fail" => "有不满足项", _ => "待验证" };
        return "<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>候选配置比较</title><style>body{font:15px/1.8 system-ui,'Microsoft YaHei';background:#f4f7f8;color:#192a36;margin:30px auto;max-width:1000px;padding:20px}section{background:white;padding:24px;margin:20px 0;border-radius:12px}td,th{text-align:left;padding:12px;border-bottom:1px solid #ddd;vertical-align:top;overflow-wrap:anywhere;box-sizing:border-box}table{width:100%;border-collapse:collapse;table-layout:fixed}th:nth-child(1){width:30%}th:nth-child(2){width:28%}th:nth-child(3){width:42%}small{color:#677a87}</style><h1>候选配置比较</h1><p>" + E(comparison.Summary) + "</p><small>" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm") + " · comparison-1.1 · 报价与测量由用户提供；未独立验证其真实性。GB 容量按 1 TB = 1000 GB 归一化，GiB/TiB 请先换算，购买前核对厂商口径。</small>"
            + string.Join("", comparison.ScenarioSummaries.Select(t => "<section><h2>条件情景</h2><p>" + E(t) + "</p></section>"))
            + string.Join("", comparison.Tradeoffs.Select(t => "<section><p>" + E(t) + "</p></section>"))
            + string.Join("", comparison.Candidates.Select(a => { var candidate = workspace.Candidates.Single(c => c.Id == a.Id); return "<section><h2>" + E(a.Name) + " · 必需条件：" + State(a.State) + "</h2><p>" + E(candidate.Cpu) + " / " + E(candidate.Gpu) + "；报价 " + E(a.PriceCny?.ToString("0.##")) + " 元</p><p>配置/报价来源：" + E(candidate.Source) + "</p><table><tr><th>条件</th><th>结果</th><th>依据</th></tr>" + string.Join("", a.Checks.Select(c => "<tr><td>" + E(c.Name) + (c.Condition.Length > 0 ? "<br>条件：" + E(c.Condition) : "") + "</td><td>" + State(c.State) + "<br>" + E(c.Reading) + "</td><td>" + E(c.Evidence) + "</td></tr>")) + "</table></section>"; }))
            + "<section><h2>历史与近期任务依据</h2>" + string.Join("", comparison.Context.Select(c => "<p>" + E(c) + "</p>")) + "</section><section><h2>目标、条件与测试步骤</h2>" + string.Join("", workspace.Criteria.Select(c => "<p><b>" + E(c.Name) + "</b><br>需求依据：" + E(c.Evidence) + "<br>测试步骤：" + E(c.Procedure) + "</p>")) + "</section></html>";
    }
}
