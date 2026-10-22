using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PerfCompass;

namespace PerfCompass.ModelProbe;

internal record ReviewNote(string Id, string CaseSha256, string Rating, string[] Issues);
internal record ReviewNotes(string Version, string CorpusSha256, string Reviewer, ReviewNote[] Cases, string ExperimentSha256 = "");
internal record ReviewRow(string Id, string Source, string Hash, string Outcome, double? Seconds, SemanticDraft? Draft, string[] Validation, ReviewNote? Review);

internal static class SemanticReview
{
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Read(string path, int maximum)
    {
        if (new FileInfo(path).Length > maximum) throw new InvalidDataException("Review input too large.");
        return File.ReadAllText(path, Encoding.UTF8);
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static List<ReviewRow> Load(string corpusPath, string caseDirectory, string? notesPath)
    {
        using var corpus = JsonDocument.Parse(Read(corpusPath, 2 * 1024 * 1024));
        Require(corpus.RootElement.ValueKind == JsonValueKind.Array && corpus.RootElement.GetArrayLength() is > 0 and <= 1000, "Invalid review corpus.");
        ReviewNotes? notes = notesPath == null ? null : JsonSerializer.Deserialize<ReviewNotes>(Read(notesPath, 1024 * 1024));
        if (notesPath != null)
        {
            Require(notes != null && notes.Version == "semantic-review-1.0" && string.Equals(notes.CorpusSha256, Hash(corpusPath), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(notes.Reviewer)
                && notes.Cases != null && notes.Cases.All(n => n != null && n.Issues != null && n.Issues.All(i => i != null && i.Length <= 3000) && n.Rating is "complete" or "incomplete" or "incorrect" or "failed")
                && notes.Cases.Select(n => n.Id).Distinct().Count() == notes.Cases.Length, "Review notes are invalid, duplicated, or bound to a different corpus.");
            string manifest = Path.Combine(caseDirectory, "manifest.json");
            Require(File.Exists(manifest) ? string.Equals(notes.ExperimentSha256, Hash(manifest), StringComparison.OrdinalIgnoreCase) : string.IsNullOrEmpty(notes.ExperimentSha256), "Review notes are bound to a different experiment manifest.");
        }
        var rows = new List<ReviewRow>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in corpus.RootElement.EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!; string source = item.GetProperty("text").GetString()!;
            Require(id != null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,50}$") && seen.Add(id) && source != null && source.Length <= 2000, "Corpus identifiers or sources invalid.");
            string path = Path.Combine(caseDirectory, "case-" + id + ".json");
            string outcome = "missing", hash = ""; SemanticDraft? draft = null; double? seconds = null; var validation = new List<string>();
            if (File.Exists(path))
            {
                using var result = JsonDocument.Parse(Read(path, 262144)); var root = result.RootElement;
                Require(root.GetProperty("Id").GetString() == id && root.GetProperty("Source").GetString() == source, "Result source or identifier differs from corpus.");
                outcome = root.GetProperty("Outcome").GetString()!;
                Require(outcome is "received" or "failed", "Result outcome invalid.");
                seconds = root.GetProperty("Seconds").GetDouble(); Require(double.IsFinite(seconds.Value) && seconds.Value >= 0, "Result duration invalid.");
                if (outcome == "received")
                {
                    draft = root.GetProperty("Draft").Deserialize<SemanticDraft>();
                    Require(draft != null && draft.Source == source && draft.Facts != null && draft.Facts.Count <= 20 && draft.Facts.All(f => f != null) && draft.Questions != null, "Received draft invalid.");
                    for (int i = 0; i < draft!.Facts.Count; i++)
                    {
                        try { SemanticRequirements.Validate(source, draft.Facts[i]); }
                        catch (ArgumentException e) { validation.Add($"事项 {i + 1}：{e.Message}"); }
                    }
                }
                hash = Hash(path);
            }
            var note = notes?.Cases.SingleOrDefault(n => n.Id == id);
            if (note != null)
            {
                Require(hash.Length > 0 && string.Equals(hash, note.CaseSha256, StringComparison.OrdinalIgnoreCase), "Review notes are stale: case " + id + " changed or is missing.");
                Require(note.Rating != "complete" || outcome == "received" && validation.Count == 0, "A protocol or grounding failure cannot be reviewed as complete.");
                Require(note.Rating != "failed" || outcome == "failed", "Failed review contradicts received result.");
                Require(note.Rating is "complete" or "failed" || note.Issues.Length > 0, "Incomplete or incorrect reviews need a concrete issue.");
            }
            rows.Add(new(id!, source!, hash, outcome, seconds, draft, validation.ToArray(), note));
        }
        Require(notes == null || notes.Cases.All(n => rows.Any(r => r.Id == n.Id)), "Review notes refer to an unknown case.");
        return rows;
    }
    internal static string Html(List<ReviewRow> rows, string reviewer, string identity = "")
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string Rating(string? s) => s switch { "complete" => "本例未发现遗漏或错误", "incomplete" => "有重要遗漏", "incorrect" => "有语义错误", "failed" => "调用或格式失败", _ => "尚未审阅" };
        var html = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>模型语义审阅</title><style>body{font:15px/1.85 system-ui,'Microsoft YaHei';max-width:1040px;margin:32px auto;padding:20px;color:#192a36;background:#f4f7f8}section{background:white;border:1px solid #e0e7e9;border-radius:12px;padding:24px;margin:24px 0}article{border-top:1px solid #dce6e7;padding:12px 0}blockquote{border-left:3px solid #bed8d4;margin:12px 0;padding-left:14px;color:#526775}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#edf3f4;padding:14px}small{color:#677a87;overflow-wrap:anywhere}.issue{color:#98551d}h2,h3{margin:0 0 10px}</style><h1>模型语义审阅</h1>");
        html.Append($"<p>{rows.Count} 条开发诊断输入 · 收到候选 {rows.Count(r => r.Outcome == "received")} · 调用失败 {rows.Count(r => r.Outcome == "failed")} · 未完成 {rows.Count(r => r.Outcome == "missing")}</p>");
        foreach (string rating in new[] { "complete", "incomplete", "incorrect", "failed", "unreviewed" }) html.Append(E(Rating(rating)) + " " + rows.Count(r => (r.Review?.Rating ?? "unreviewed") == rating) + " · ");
        html.Append("<p>以上为逐例开发审阅，不是模型通用准确率。格式与原文定位不证明语义正确。所有候选未确认，没有生成购买建议；须独立人工复核和真实使用验证。</p><p>审阅者：" + E(reviewer) + "</p>");
        html.Append("<pre>" + E(identity.Length == 0 ? "未提供实验身份，不据此比较模型。" : identity) + "</pre>");
        foreach (var row in rows)
        {
            html.Append("<section id='case-" + E(row.Id) + "'><h2>用例 " + E(row.Id) + " · " + E(Rating(row.Review?.Rating)) + "</h2><pre>" + E(row.Source) + "</pre><small>状态：" + E(row.Outcome) + " · 请求 " + row.Seconds?.ToString("0.0") + " 秒 · 文件 SHA256 " + E(row.Hash) + "</small>");
            if (row.Review != null) html.Append("<ul class='issue'>" + string.Join("", row.Review.Issues.Select(i => "<li>" + E(i) + "</li>")) + "</ul>");
            html.Append(string.Join("", row.Validation.Select(v => "<p class='issue'>引用/字段检查：" + E(v) + "</p>")));
            if (row.Draft is { } draft)
            {
                foreach (var f in draft.Facts)
                {
                    html.Append("<article><h3>" + E(f.Label) + "</h3><p>" + E(SemanticRequirements.Label(f.Kind) + "；" + f.Detail) + "</p><blockquote>" + E(f.Evidence) + "</blockquote>");
                    var goals = QuantityGoals.Compile(f);
                    html.Append("<small>当前版本的数量候选（未确认）：" + (goals.Count == 0 ? "逐项核对" : string.Join("；", goals.Select(g => E(g.Metric + " " + CandidateComparison.DirectionLabel(g.Direction) + $" {g.Target:G} {g.Unit}")))) + "</small></article>");
                }
                html.Append("<h3>模型提出的问题</h3><ul>" + string.Join("", draft.Questions.Select(q => "<li>" + E(q) + "</li>")) + "</ul>");
            }
            html.Append("</section>");
        }
        return html.Append("</html>").ToString();
    }
    internal static int Run(string[] args)
    {
        if (args.Length is not (4 or 5)) { Console.Error.WriteLine("Usage: --review <corpus-json> <case-directory> <new-output-html> [review-notes-json]"); return 2; }
        try
        {
            var rows = Load(args[1], args[2], args.Length == 5 ? args[4] : null);
            string reviewer = args.Length == 5 ? JsonSerializer.Deserialize<ReviewNotes>(Read(args[4], 1024 * 1024))!.Reviewer : "未提供逐例审阅标注";
            string identity = "", manifestPath = Path.Combine(args[2], "manifest.json");
            if (File.Exists(manifestPath))
            {
                using var manifest = JsonDocument.Parse(Read(manifestPath, 65536));
                identity = string.Join("\n", new[] { "Model", "ModelSha256", "RunnerSha256", "PromptSha256", "SchemaSha256", "CorpusSha256", "Threads", "GpuLayers", "Context", "ProductCommit", "WorkingTreeDirty", "ProbeSha256", "ProductAssemblySha256", "Outcome", "ServerExited", "ServerPeakWorkingSetBytes", "ServerCpuSeconds", "LogicalProcessors" }
                    .Where(key => manifest.RootElement.TryGetProperty(key, out _)).Select(key => key + ": " + manifest.RootElement.GetProperty(key).ToString()));
            }
            if (!args[3].EndsWith(".html", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be HTML.");
            using var file = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(false)); writer.Write(Html(rows, reviewer, identity));
            Console.WriteLine($"Wrote {rows.Count} cases; {rows.Count(r => r.Review != null)} reviewed. No purchase recommendations generated."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("Review generation failed: " + e.GetType().Name); return 1; }
    }
}
