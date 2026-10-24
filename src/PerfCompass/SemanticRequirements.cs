using System.Text.Json;

namespace PerfCompass;

public record SemanticFact(string Subject, string Kind, string Polarity, string Timeframe, string Value, string Unit, string Condition, string Evidence)
{
    public string Label => $"{Subject} · {SemanticRequirements.Label(Polarity)} · {SemanticRequirements.Label(Timeframe)}";
    public string Detail => string.Join("；", new[] { Value, Unit, string.IsNullOrWhiteSpace(Condition) ? "" : "条件：" + Condition }.Where(s => !string.IsNullOrWhiteSpace(s)));
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DecisionUse { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DecisionUseLabel => SemanticUse.Label(this);
}
public record SemanticPlan(string Source, string Model, string Provider, string ContractVersion, List<SemanticFact> Facts, DateTimeOffset ConfirmedAt);
public record SemanticDraft(string Source, string Model, string Provider, List<SemanticFact> Facts, string[] Questions);
public record SemanticChoice(string Key, string Label);

public sealed class SemanticFactEditor : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public Action? Changed { get; set; }
    private bool include;
    private string subject = "", kind = "other", polarity = "uncertain", timeframe = "unspecified", value = "", unit = "", condition = "", evidence = "";
    private string decisionUse = "clarify";
    private void Set<T>(ref T field, T next, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, next))
        {
            field = next;
            if (name is nameof(Subject) or nameof(Kind) or nameof(Polarity) or nameof(Timeframe) or nameof(Value) or nameof(Unit) or nameof(Condition) or nameof(Evidence))
            { decisionUse = "clarify"; PropertyChanged?.Invoke(this, new(nameof(DecisionUse))); }
            PropertyChanged?.Invoke(this, new(name)); PropertyChanged?.Invoke(this, new(nameof(QuantityPreview))); Changed?.Invoke();
        }
    }
    public bool Include { get => include; set => Set(ref include, value); }
    public string Subject { get => subject; set => Set(ref subject, value); }
    public string Kind { get => kind; set => Set(ref kind, value); }
    public string Polarity { get => polarity; set => Set(ref polarity, value); }
    public string DecisionUse { get => decisionUse; set => Set(ref decisionUse, value); }
    public string Timeframe { get => timeframe; set => Set(ref timeframe, value); }
    public string Value { get => value; set => Set(ref this.value, value); }
    public string Unit { get => unit; set => Set(ref unit, value); }
    public string Condition { get => condition; set => Set(ref condition, value); }
    public string Evidence { get => evidence; set => Set(ref evidence, value); }
    public SemanticChoice[] Kinds => SemanticRequirements.Kinds;
    public SemanticChoice[] Polarities => SemanticUse.SourceRelations;
    public SemanticChoice[] Timeframes => SemanticRequirements.Timeframes;
    public SemanticChoice[] DecisionUses => SemanticUse.Choices;
    public SemanticFactEditor() { }
    public SemanticFactEditor(SemanticFact fact) { Subject = fact.Subject; Kind = fact.Kind; Polarity = fact.Polarity; Timeframe = fact.Timeframe; Value = fact.Value; Unit = fact.Unit; Condition = fact.Condition; Evidence = fact.Evidence; DecisionUse = SemanticUse.Role(fact); }
    public SemanticFact ToFact() => new(Subject.Trim(), Kind, Polarity, Timeframe, Value.Trim(), Unit.Trim(), Condition.Trim(), Evidence.Trim()) { DecisionUse = DecisionUse };
    public string QuantityPreview
    {
        get
        {
            var fact = ToFact();
            if (DecisionUse == "omit") return "本次不作为购机条件，原文保留。";
            if (DecisionUse == "clarify") return "请先核对这项内容怎样影响选机。";
            if (DecisionUse == "avoid") return "候选比较方式：核对完整的禁止事项，不自动反转数值方向。";
            var goals = QuantityGoals.Compile(fact);
            return goals.Count == 0 ? "候选比较方式：逐项核对" : "候选比较目标：" + string.Join(" 且 ", goals.Select(g => CandidateComparison.DirectionLabel(g.Direction) + $" {g.Target:G} {g.Unit}")) + (Polarity == "conditional" ? "（条件成立时）" : "");
        }
    }
}

public static partial class SemanticRequirements
{
    public const string Version = "semantic-1.0";
    public static readonly SemanticChoice[] Kinds = [new("workload", "任务或软件"), new("resource", "容量与规模"), new("experience", "体验目标"), new("budget", "预算与费用范围"), new("portability", "便携与续航"), new("noise", "噪声"), new("power", "功耗"), new("compatibility", "兼容性"), new("frequency", "使用频率"), new("schedule", "时间安排"), new("conflict", "冲突"), new("other", "其他条件")];
    public static readonly SemanticChoice[] Polarities = [new("required", "明确需要"), new("excluded", "明确排除"), new("conditional", "条件成立时需要"), new("uncertain", "尚未确定")];
    public static readonly SemanticChoice[] Timeframes = [new("past", "过去"), new("current", "现在"), new("future", "未来"), new("unspecified", "时间未明确")];
    public static string Label(string key) => Kinds.Concat(Polarities).Concat(Timeframes).FirstOrDefault(c => c.Key == key)?.Label ?? key;
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new ArgumentException(message); }
    public static void Validate(string source, SemanticFact fact, bool requireGrounding = true)
    {
        Require(source.Length is > 0 and <= 2000, "需求原文应为 1–2000 字符。");
        Require(!string.IsNullOrWhiteSpace(fact.Subject) && fact.Subject.Length <= 150, "请填写事项名称（最多 150 字符）。");
        Require(Kinds.Any(c => c.Key == fact.Kind) && Polarities.Any(c => c.Key == fact.Polarity) && Timeframes.Any(c => c.Key == fact.Timeframe), "事项类型、肯否关系或时间无效。");
        Require(fact.DecisionUse == null || SemanticUse.Choices.Any(c => c.Key == fact.DecisionUse), "购机用途无效，请重新核对。");
        Require(fact.Value != null && fact.Unit != null && fact.Condition != null && fact.Evidence != null, "事项字段不能缺失。");
        Require(fact.Value.Length <= 500 && fact.Unit.Length <= 50 && fact.Condition.Length <= 500, "事项字段过长。");
        if (!requireGrounding) return;
        int position = source.IndexOf(fact.Evidence, StringComparison.Ordinal);
        Require(fact.Evidence.Length > 0 && position >= 0, "原文依据必须逐字出现在需求描述中。");
        Require(source.IndexOf(fact.Evidence, position + 1, StringComparison.Ordinal) < 0, "原文依据有重复，请补充相邻文字以明确位置。");
        bool conditionPresent = !string.IsNullOrWhiteSpace(fact.Condition) && !new[] { "无", "none", "n/a", "不适用" }.Contains(fact.Condition.Trim(), StringComparer.OrdinalIgnoreCase);
        Require(fact.Polarity != "conditional" || conditionPresent, "条件事项需要填写实际触发条件。");
        Require(!conditionPresent || fact.Polarity is "conditional" or "uncertain", "存在触发条件，请将事项改为条件需求或尚未确定，不能作为无条件要求。");
    }
    public static SemanticDraft Parse(string source, string model, string provider, string content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        Require(root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 2 && root.EnumerateObject().All(p => p.Name is "facts" or "questions"), "模型返回了不支持或重复的字段。");
        Require(root.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array && facts.GetArrayLength() <= 8, "模型事项列表无效。");
        Require(root.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() <= 4, "模型问题列表无效。");
        var result = new List<SemanticFact>();
        foreach (var item in facts.EnumerateArray())
        {
            string[] fields = ["subject", "kind", "polarity", "timeframe", "value", "unit", "condition", "evidence"];
            Require(item.ValueKind == JsonValueKind.Object && item.EnumerateObject().Count() == fields.Length && item.EnumerateObject().All(p => fields.Contains(p.Name)), "模型事项字段不完整或重复。");
            string Read(string name) { Require(item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String, "模型事项字段类型无效。"); return value.GetString()!; }
            var fact = new SemanticFact(Read("subject"), Read("kind"), Read("polarity"), Read("timeframe"), Read("value"), Read("unit"), Read("condition"), Read("evidence"));
            Validate(source, fact, false); result.Add(fact);
        }
        var qs = questions.EnumerateArray().Select(q => { Require(q.ValueKind == JsonValueKind.String && q.GetString()!.Length <= 500, "模型问题无效。"); return q.GetString()!; }).ToArray();
        return new(source, model, provider, result, qs);
    }
    public static SemanticPlan Confirm(SemanticDraft draft, IEnumerable<SemanticFactEditor> editors)
    {
        var facts = editors.Where(e => e.Include).Select(e => e.ToFact()).ToList();
        Require(facts.Count is > 0 and <= 20, "请至少核对并选择一项需求，最多 20 项。");
        foreach (var fact in facts) Validate(draft.Source, fact);
        Require(facts.All(f => f.DecisionUse != null), "请核对每项内容的购机用途。");
        return new(draft.Source, draft.Model, draft.Provider, SemanticUse.PlanVersion, facts, DateTimeOffset.Now);
    }
    public static AnalysisReport Apply(AnalysisReport report, Settings settings) => ApplyUses(report, settings);
    private static bool MatchesResource(SemanticFact fact, string component)
    {
        string text = fact.Subject + " " + fact.Evidence;
        bool gpu = System.Text.RegularExpressions.Regex.IsMatch(text, "显存|显卡|(?i:GPU|VRAM)");
        return component switch
        {
            "gpu" => gpu,
            "memory" => System.Text.RegularExpressions.Regex.IsMatch(text, "内存|(?i:\\bRAM\\b)") || !gpu && System.Text.RegularExpressions.Regex.IsMatch(text, "(?i:memory)"),
            "cpu" => System.Text.RegularExpressions.Regex.IsMatch(text, "处理器|核心|线程|(?i:CPU)"),
            _ => false
        };
    }
}
