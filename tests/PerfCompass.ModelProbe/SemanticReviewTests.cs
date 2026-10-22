using System.Text.Json;
using System.IO;
using PerfCompass;

namespace PerfCompass.ModelProbe;

internal static class SemanticReviewTests
{
    internal static int Run(string output)
    {
        try
        {
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Choose an empty test directory.");
            Directory.CreateDirectory(output);
            string source = "明年用<工具>处理天文数据 <script>";
            string corpus = Path.Combine(output, "corpus.json"), cases = Path.Combine(output, "cases"); Directory.CreateDirectory(cases);
            File.WriteAllText(corpus, JsonSerializer.Serialize(new[] { new { id = "a", text = source }, new { id = "missing", text = "未完成" } }));
            var draft = new SemanticDraft(source, "synthetic", "loopback", [new("<工具>", "workload", "required", "future", "处理天文数据", "", "", source)], []);
            string casePath = Path.Combine(cases, "case-a.json");
            File.WriteAllText(casePath, JsonSerializer.Serialize(new { Id = "a", Source = source, Seconds = 1.0, Outcome = "received", Draft = draft }));
            var rows = SemanticReview.Load(corpus, cases, null);
            if (rows.Count != 2 || rows[0].Review != null || rows[1].Outcome != "missing") throw new Exception("Missing or unreviewed rows promoted.");
            string html = SemanticReview.Html(rows, "<审阅者>");
            if (!html.Contains("&lt;script&gt;") || html.Contains("数据 <script>") || !html.Contains("&lt;审阅者&gt;")) throw new Exception("HTML escaping failed.");
            Console.WriteLine("PASS review report preserves missing and unreviewed states and escapes all text");
            string notes = Path.Combine(output, "notes.json");
            var annotations = new ReviewNotes("semantic-review-1.0", SemanticReview.Hash(corpus), "synthetic test", [new("a", SemanticReview.Hash(casePath), "complete", [])]);
            File.WriteAllText(notes, JsonSerializer.Serialize(annotations));
            if (SemanticReview.Load(corpus, cases, notes)[0].Review?.Rating != "complete") throw new Exception("Valid annotation rejected.");
            void Reject(Action operation)
            {
                try { operation(); } catch (InvalidDataException) { return; }
                throw new Exception("Invalid review accepted.");
            }
            File.AppendAllText(casePath, " "); Reject(() => SemanticReview.Load(corpus, cases, notes));
            Console.WriteLine("PASS changed case bytes invalidate previous review annotations");
            annotations = annotations with { Cases = [new("a", SemanticReview.Hash(casePath), "incorrect", [])] };
            File.WriteAllText(notes, JsonSerializer.Serialize(annotations)); Reject(() => SemanticReview.Load(corpus, cases, notes));
            annotations = annotations with { Cases = [new("a", SemanticReview.Hash(casePath), "incorrect", ["具体语义问题"]), new("a", SemanticReview.Hash(casePath), "complete", [])] };
            File.WriteAllText(notes, JsonSerializer.Serialize(annotations)); Reject(() => SemanticReview.Load(corpus, cases, notes));
            Console.WriteLine("PASS duplicate review entries and unexplained negative classifications are rejected");
            File.WriteAllText(casePath, JsonSerializer.Serialize(new { Id = "a", Source = "不同原文", Seconds = 1.0, Outcome = "received", Draft = draft }));
            Reject(() => SemanticReview.Load(corpus, cases, null));
            Console.WriteLine("PASS result sources must match the reviewed corpus");
            File.WriteAllText(casePath, JsonSerializer.Serialize(new { Id = "a", Source = source, Seconds = 1.0, Outcome = "received", Draft = draft with { Facts = [draft.Facts[0] with { Evidence = "不存在的引用" }] } }));
            annotations = annotations with { Cases = [new("a", SemanticReview.Hash(casePath), "complete", [])] };
            File.WriteAllText(notes, JsonSerializer.Serialize(annotations)); Reject(() => SemanticReview.Load(corpus, cases, notes));
            Console.WriteLine("PASS invalid grounding cannot be annotated as a complete case");
            string manifest = Path.Combine(cases, "manifest.json"); File.WriteAllText(manifest, "{\"Model\":\"fixture\"}");
            annotations = annotations with { ExperimentSha256 = SemanticReview.Hash(manifest), Cases = [new("a", SemanticReview.Hash(casePath), "incorrect", ["引用不在原文"])] };
            File.WriteAllText(notes, JsonSerializer.Serialize(annotations));
            _ = SemanticReview.Load(corpus, cases, notes);
            File.WriteAllText(manifest, "{\"Model\":\"different-model\"}"); Reject(() => SemanticReview.Load(corpus, cases, notes));
            Console.WriteLine("PASS experiment identity changes invalidate prior case classifications");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Review regression failed: " + error.Message); return 1; }
    }
}
