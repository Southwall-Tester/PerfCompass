using System.Text.RegularExpressions;

namespace PerfCompass;

public record RequirementFact(string Key, string Label, string Evidence, bool Excluded, bool Future);
public record RequirementsPlan(string Source, string RuleVersion, List<RequirementFact> Facts, string AiMode)
{
    public string AiModeLabel => !Facts.Any(f => f.Key == "ai" && !f.Excluded) ? "不涉及 AI 任务" : AiMode switch { "local" => "本地运行、训练或微调", "cloud" => "在线服务或云端 API", "both" => "本地与云端都会使用", _ => "尚未明确" };
    public bool NeedsAiChoice => Facts.Any(f => f.Key == "ai" && !f.Excluded) && AiMode == "unspecified";
    public string[] UnparsedClauses => Regex.Split(Source, @"[，,。；;！!？?\r\n]|但是|不过|但|而是")
        .Select(s => s.Trim()).Where(s => s.Length > 0 && !Facts.Any(f => f.Evidence == s)).ToArray();
    public string Summary => string.Join("；", Facts.Select(f => $"{(f.Excluded ? "不纳入" : f.Future ? "未来需求" : "任务线索")}：{f.Label}").Concat(UnparsedClauses.Select(s => "未解析：" + s)));
}

/// <summary>Bounded local extraction. Every fact keeps its source clause; no free-form model inference.</summary>
public static partial class RequirementParser
{
    public const string Version = "rules-1.0";
    private static readonly (string Key, string Label, string Pattern)[] Tasks =
    [
        ("ai", "AI 任务", @"(?i)(?<![a-z])AI(?![a-z])|人工智能|大模型|本地模型|机器学习|深度学习"),
        ("compile", "开发编译", "编译|写代码|开发程序|程序开发|软件开发"),
        ("game", "游戏", "游戏|电竞|3A大作"),
        ("video", "视频剪辑与渲染", "剪辑|视频渲染|三维渲染|3D渲染"),
        ("vm", "虚拟机", "虚拟机|虚拟化"),
        ("office", "办公", "办公|文档|表格|上网课")
    ];
    public static RequirementsPlan Parse(string? source)
    {
        string text = (source ?? "").Trim(); if (text.Length > 2000) text = text[..2000];
        var facts = new List<RequirementFact>();
        // Negation and intent are evaluated per clause, never from the presence of a keyword elsewhere.
        foreach (string raw in Regex.Split(text, @"[，,。；;！!？?\r\n]|但是|不过|但|而是"))
        {
            string clause = raw.Trim(); if (clause.Length == 0) continue;
            bool future = Regex.IsMatch(clause, "准备|计划|打算|以后|将来|未来|想要|想学|要开始|将要|下台|下一台");
            foreach (var task in Tasks)
            {
                var match = Regex.Match(clause, task.Pattern); if (!match.Success) continue;
                string prefix = clause[..match.Index];
                // '不只是游戏' is inclusive. Ambiguous distant negations remain unconfirmed in the UI.
                bool excluded = Regex.IsMatch(prefix, @"(?:不玩|不做|不学|不用|不需要|无需|不再|从不|没有|不想|不打算|不准备|不计划|暂不)[^，。；]{0,8}$")
                    && !Regex.IsMatch(prefix, "不只|不仅|不光");
                facts.Add(new(task.Key, task.Label, clause, excluded, future));
            }
            if (Regex.IsMatch(clause, @"(?i)4\s*K|3840\s*[x×*]\s*2160"))
                facts.Add(new("display", "4K 显示目标（核对接口与任务表现）", clause, Regex.IsMatch(clause, "不需要|不用|不要"), future));
            var fps = Regex.Match(clause, @"(?i)(?:目标|希望|至少|达到|稳定|想要|要求)\s*(\d{2,3})\s*(?:fps|帧)");
            if (fps.Success) facts.Add(new("fps", $"目标帧率 {fps.Groups[1].Value} FPS", clause, false, future));
            if (Regex.IsMatch(clause, "很慢|卡顿|等待过久|耗时过长|太慢"))
                facts.Add(new("latency", "体验问题线索（尚未确定部件原因）", clause, false, future));
        }
        string mode = "unspecified";
        var positiveAi = facts.Where(f => f.Key == "ai" && !f.Excluded).ToList();
        bool local = positiveAi.Any(f => Regex.IsMatch(f.Evidence, "本地|离线|自己电脑|训练|微调"));
        bool cloud = positiveAi.Any(f => Regex.IsMatch(f.Evidence, "在线|云端|网页|调用.*API|调用.*接口"));
        if (local != cloud) mode = local ? "local" : "cloud";
        return new(text, Version, facts.DistinctBy(f => (f.Key, f.Evidence, f.Excluded)).ToList(), mode);
    }
    public static RequirementsPlan Confirm(RequirementsPlan parsed, string aiMode)
    {
        if (parsed.Facts.Any(f => f.Key == "ai" && !f.Excluded) && aiMode is not ("local" or "cloud" or "both")) throw new ArgumentException("请明确 AI 使用方式。");
        string mode = parsed.Facts.Any(f => f.Key == "ai" && !f.Excluded) ? aiMode : "unspecified";
        if (parsed.Facts.Count == 0 && parsed.Source.Length > 0) throw new ArgumentException("未识别到支持的任务，请补充或改写描述。");
        return parsed with { AiMode = mode };
    }
    public static AnalysisReport Apply(AnalysisReport report, Settings settings)
    {
        var plan = settings.Requirements;
        // An edited source invalidates previously confirmed constraints.
        if (plan == null || plan.Source != settings.FutureNeeds.Trim() || plan.RuleVersion != Version) return report;
        var positive = plan.Facts.Where(f => !f.Excluded).ToList();
        bool localAi = positive.Any(f => f.Key == "ai") && plan.AiMode is "local" or "both";
        var cards = report.Cards.Select(c =>
        {
            string[] keys = c.Key switch
            {
                "cpu" => ["compile", "game", "video", "vm"],
                "memory" => ["compile", "video", "vm"],
                "gpu" => ["game", "video", "fps", "display"],
                _ => []
            };
            var relevant = positive.Where(f => keys.Contains(f.Key) || (localAi && (c.Key is "gpu" or "memory") && f.Key == "ai")).ToList();
            if (relevant.Count == 0) return c;
            bool protect = relevant.Any(f => f.Future || f.Key is "ai" or "display" or "fps");
            string evidence = " 已确认需求：" + string.Join("；", relevant.Select(f => $"{f.Label}（原文：{f.Evidence}）")) + "。";
            return c with
            {
                Status = protect && c.Status is "surplus" or "review" ? "requirements" : c.Status,
                Title = protect && c.Status is "surplus" or "review" ? "先验证已确认的新需求，再评估降档" : c.Title,
                Evidence = c.Evidence + evidence,
                Suggestion = c.Suggestion + (protect ? " 已确认的新任务不能由历史闲置证明可降档。请补充任务规模与目标，在候选配置上重跑验证。" : " 用已确认任务核对上述判断。")
            };
        }).ToList();
        return report with { Cards = cards, ConfirmedRequirements = plan };
    }
}
