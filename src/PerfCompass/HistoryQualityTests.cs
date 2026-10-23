using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunHistoryQualityTests(Action<string, Action> check, Action<bool, string> assert, Func<List<Sample>> fixture)
    {
        (LocalStore Store, Settings Settings, string Today) Setup()
        {
            var store = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-history-quality-" + Guid.NewGuid().ToString("N")));
            var settings = WorkloadCoverage.Select(new(), true, "test-machine");
            var samples = fixture();
            foreach (var group in samples.GroupBy(s => s.Time.LocalDateTime.Date))
                File.WriteAllLines(Path.Combine(store.Root, $"samples-{group.Key:yyyy-MM-dd}.jsonl"), group.Select(s => JsonSerializer.Serialize(s)), new UTF8Encoding(false));
            return (store, settings, Path.Combine(store.Root, $"samples-{samples.Max(s => s.Time).LocalDateTime:yyyy-MM-dd}.jsonl"));
        }
        check("History quality keeps clean evidence and excludes known unrelated dates and hardware", () =>
        {
            var t = Setup(); File.WriteAllText(Path.Combine(t.Store.Root, $"samples-{DateTime.Today.AddDays(-80):yyyy-MM-dd}.jsonl"), "{old broken");
            File.AppendAllText(t.Today, JsonSerializer.Serialize(new { Time = DateTimeOffset.Now.AddSeconds(-1), HardwareId = "other", Seconds = "invalid", Active = true }) + "\n");
            var report = HistoryAnalysis.Analyze(t.Store, t.Settings, 7, hardwareId: "test-machine");
            assert(report.Ready && report.SampleCount == 3600 && !report.HistoryQuality.HasIssues && report.Cards.Single(c => c.Key == "memory").Status == "surplus", "clean scoped evidence lost");
        });
        check("Malformed history reports exact locations and cannot silently establish surplus", () =>
        {
            var t = Setup(); byte[] original = File.ReadAllBytes(t.Today);
            long expectedLine = original.LongCount(b => b == (byte)'\n') + 1;
            string time = DateTimeOffset.Now.AddSeconds(-1).ToString("O");
            string prefix = "{\"Time\":\"" + time + "\",\"HardwareId\":\"test-machine\",\"Seconds\":10,\"Active\":true";
            foreach (string bad in new[] { "{truncated", "null", "{}", prefix + ",\"Gpu\":0,\"Gpu\":100}", prefix.Replace("\"Seconds\":10", "\"Seconds\":20") + "}", prefix.Replace("\"Active\":true", "\"Active\":\"yes\"") + "}", prefix.Replace("\"HardwareId\":\"test-machine\"", "\"HardwareId\":\"\"") + "}" })
            {
                File.WriteAllBytes(t.Today, original.Concat(Encoding.UTF8.GetBytes(bad)).ToArray()); var before = File.ReadAllBytes(t.Today);
                var report = HistoryAnalysis.Analyze(t.Store, t.Settings, 7, hardwareId: "test-machine");
                var issue = report.HistoryQuality.Issues.Single();
                assert(!report.Ready && report.SampleCount == 3600 && report.ActiveHours == 10 && report.HistoryQuality.IssueCount == 1 && issue.Line == expectedLine
                    && issue.File == Path.GetFileName(t.Today) && report.Cards.All(c => c.Status is not ("surplus" or "review" or "changed"))
                    && report.DemandTrends.All(tr => tr.Status == "insufficient") && File.ReadAllBytes(t.Today).SequenceEqual(before), "corrupt row disappeared, wrong line location, or source mutated");
            }
        });
        check("Invalid UTF8 and oversized lines preserve later valid records with bounded issue details", () =>
        {
            var s = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-history-bytes-" + Guid.NewGuid().ToString("N")));
            string path = Path.Combine(s.Root, $"samples-{DateTime.Today:yyyy-MM-dd}.jsonl"); var row = fixture()[^1];
            using (var file = File.Create(path))
            {
                file.Write(new byte[] { 255, (byte)'\n' });
                file.Write(Enumerable.Repeat((byte)'x', LocalStore.MaximumHistoryLineBytes + 1).ToArray()); file.WriteByte((byte)'\n');
                for (int i = 0; i < 60; i++) file.Write(Encoding.UTF8.GetBytes("{bad\n"));
                file.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row)));
            }
            var report = HistoryAnalysis.Analyze(s, new(), 7, hardwareId: "test-machine");
            assert(report.SampleCount == 1 && report.HistoryQuality.IssueCount == 62 && report.HistoryQuality.Issues.Length == 50
                && report.HistoryQuality.Issues[0].Reason.Contains("UTF-8") && report.HistoryQuality.Issues[1].Reason.Contains("4 MiB"), "invalid bytes became replacement text or oversized line lost following records");
        });
        check("Committed prefix reads ignore concurrent appends until the next independent scan", () =>
        {
            var s = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-history-prefix-" + Guid.NewGuid().ToString("N")));
            var row = fixture()[^1]; s.Append(row); s.Append(row with { Time = row.Time.AddSeconds(-1) });
            using var diagnostics = new HistoryDiagnostics(7);
            using var e = s.ReadChecked(7, "test-machine", DateTimeOffset.Now, diagnostics).GetEnumerator(); assert(e.MoveNext(), "no first record");
            string file = Directory.GetFiles(s.Root, "samples-*.jsonl").Single(); File.AppendAllText(file, "{unfinished concurrent append");
            int count = 1; while (e.MoveNext()) count++;
            assert(count == 2 && !diagnostics.Build().HasIssues, "snapshot read crossed its committed prefix");
            assert(HistoryAnalysis.Analyze(s, new(), 7, hardwareId: "test-machine").HistoryQuality.IssueCount == 1, "persisted incomplete tail remained invisible on next scan");
        });
        check("Unreadable history is explicit and cannot reuse a previous ready result", () =>
        {
            var t = Setup(); var clean = HistoryAnalysis.Analyze(t.Store, t.Settings, 7, hardwareId: "test-machine");
            assert(clean.Ready, "fixture not ready"); int excluded = File.ReadLines(t.Today).Count();
            using (var held = new FileStream(t.Today, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var report = HistoryAnalysis.Analyze(t.Store, t.Settings, 7, hardwareId: "test-machine");
                assert(!report.Ready && report.SampleCount == clean.SampleCount - excluded && report.HistoryQuality.Issues.Single().Reason.Contains("无法读取")
                    && !report.HistoryQuality.Issues.Single().Label.Contains("第 0 行"), "locked file silently omitted, stale result reused, or a nonexistent line reported");
            }
            assert(HistoryAnalysis.Analyze(t.Store, t.Settings, 7, hardwareId: "test-machine").Ready, "unlocked history did not recover");
        });
        check("History anomaly signatures invalidate comparison coverage while preserving pressure and report escaping", () =>
        {
            var report = AnalysisEngine.Analyze(fixture().Select(s => s with { Cpu = 99, CoreMax = 100 }), WorkloadCoverage.Select(new(), true, "test-machine"), 7);
            using var d = new HistoryDiagnostics(60); d.Add("samples-<unsafe>.jsonl", 7, "<script>bad</script>");
            var quality = d.Build(); var damaged = HistoryAnalysis.Apply(report, quality);
            assert(damaged.Cards.Single(c => c.Key == "cpu").Status == "pressure" && damaged.Summary.Contains("历史记录")
                && CandidateComparison.Stamp(damaged) != CandidateComparison.Stamp(report), "observed pressure lost or old coverage inherited");
            string html = ReportExporter.Html(damaged, null); assert(html.Contains("历史记录异常") && html.Contains("&lt;script&gt;bad&lt;/script&gt;") && !html.Contains("<script>bad"), "history report lost diagnostics or failed escaping");
            d.Add("samples-<unsafe>.jsonl", 8, "different"); assert(CandidateComparison.Stamp(HistoryAnalysis.Apply(report, d.Build())) != CandidateComparison.Stamp(damaged), "changed anomalies retained the same context");
            using var a = new HistoryDiagnostics(60); using var b = new HistoryDiagnostics(60);
            a.Add("same-file", 1, "same-reason", [1]); b.Add("same-file", 1, "same-reason", [2]);
            assert(a.Build().Signature != b.Build().Signature, "different damaged bytes at the same location retained old coverage");
        });
        check("BOM whitespace unknown fields and a complete unterminated record remain readable", () =>
        {
            var s = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-history-compatible-" + Guid.NewGuid().ToString("N")));
            string json = JsonSerializer.Serialize(fixture()[^1]); json = json[..^1] + ",\"FutureField\":42}";
            File.WriteAllText(Path.Combine(s.Root, $"samples-{DateTime.Today:yyyy-MM-dd}.jsonl"), " \t\r\n" + json, new UTF8Encoding(true));
            var report = HistoryAnalysis.Analyze(s, new(), 7, hardwareId: "test-machine");
            assert(report.SampleCount == 1 && !report.HistoryQuality.HasIssues, "compatible complete record falsely declared corrupt");
        });
        check("Damaged comparison context cannot leave an increase narrative on genuine pressure evidence", () =>
        {
            var t = Setup(); var recent = fixture().Select(s => s with { Cpu = 99, CoreMax = 100 }).ToList();
            var baseline = recent.Take(180).SelectMany(s => new[] { s with { Time = s.Time.AddDays(-8), Cpu = 1 }, s with { Time = s.Time.AddDays(-9), Cpu = 1 } });
            foreach (var group in recent.Concat(baseline).GroupBy(s => s.Time.LocalDateTime.Date))
                File.WriteAllLines(Path.Combine(t.Store.Root, $"samples-{group.Key:yyyy-MM-dd}.jsonl"), group.Select(s => JsonSerializer.Serialize(s)));
            var clean = HistoryAnalysis.Analyze(t.Store, t.Settings, 7, hardwareId: "test-machine");
            assert(clean.Cards.Single(c => c.Key == "cpu").Title.Contains("增加"), "fixture did not establish the comparison narrative");
            File.AppendAllText(t.Today, "{truncated"); var damaged = HistoryAnalysis.Analyze(t.Store, t.Settings, 7, hardwareId: "test-machine");
            var cpu = damaged.Cards.Single(c => c.Key == "cpu");
            assert(cpu.Status == "pressure" && !cpu.Title.Contains("增加") && !cpu.Evidence.Contains("此前") && damaged.DemandTrends.All(tr => tr.Status == "insufficient"), "invalid period comparison survived on a pressure card");
        });
        check("Slow valid polls flush before the history interval limit without dropping observation time", () =>
        {
            var source = Enumerable.Range(0, 9).Select(i => fixture()[0] with { Time = DateTimeOffset.Now.AddSeconds(-90 + i * 5), Seconds = 5 }).ToArray();
            var batch = new List<Sample>(); var saved = new List<Sample>();
            foreach (var next in source) { if (SampleBatch.MustFlushBefore(batch, next)) { saved.Add(SampleBatch.Merge(batch)); batch.Clear(); } batch.Add(next); }
            if (batch.Count > 0) saved.Add(SampleBatch.Merge(batch));
            assert(saved.Count == 3 && saved.All(s => s.Seconds == 15) && saved.Sum(s => s.Seconds) == 45
                && AnalysisEngine.Analyze(saved, new(), 7).ActiveHours == 45d / 3600, "slow poll history exceeded the analyzer limit or lost time");
            bool refused = false; try { _ = SampleBatch.Merge(source.Take(4).ToArray()); } catch (ArgumentException) { refused = true; }
            assert(refused, "oversized interval was silently emitted");
        });
        check("History diagnostics follow actual daily file boundaries at midnight morning and night", () =>
        {
            foreach (int hour in new[] { 0, 2, 12, 23 })
            {
                var now = new DateTimeOffset(DateTime.Today.AddHours(hour).AddMinutes(15), DateTimeOffset.Now.Offset);
                var s = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-history-clock-" + Guid.NewGuid().ToString("N")));
                var prototype = fixture()[0]; var samples = Enumerable.Range(0, 3600).Select(i => prototype with
                    { Time = now.AddDays(-(i / 1200)).AddSeconds(-(i % 1200) * 10) }).ToArray();
                foreach (var group in samples.GroupBy(p => p.Time.LocalDateTime.Date))
                    File.WriteAllLines(Path.Combine(s.Root, $"samples-{group.Key:yyyy-MM-dd}.jsonl"), group.Select(p => JsonSerializer.Serialize(p)));
                var settings = WorkloadCoverage.Select(new(), true, "test-machine");
                assert(HistoryAnalysis.Analyze(s, settings, 7, asOf: now, hardwareId: "test-machine").Ready, "clock fixture not ready");
                string path = Path.Combine(s.Root, $"samples-{now.LocalDateTime:yyyy-MM-dd}.jsonl"); byte[] original = File.ReadAllBytes(path);
                long line = original.LongCount(b => b == (byte)'\n') + 1;
                File.AppendAllText(path, "{bad"); var damaged = HistoryAnalysis.Analyze(s, settings, 7, asOf: now, hardwareId: "test-machine");
                assert(damaged.HistoryQuality.Issues.Single().Line == line && damaged.SampleCount == 3600 && !damaged.Ready, "daily corruption location depends on wall-clock hour");
                File.WriteAllBytes(path, original); int excluded = File.ReadLines(path).Count();
                using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var locked = HistoryAnalysis.Analyze(s, settings, 7, asOf: now, hardwareId: "test-machine");
                assert(locked.SampleCount == 3600 - excluded && locked.HistoryQuality.IssueCount == 1 && !locked.Ready, "locked file exclusion assumes a fixed daily row count");
            }
        });
        check("Thirty-day history file scan keeps complete evidence without retaining raw samples", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-history-benchmark-" + Guid.NewGuid().ToString("N"));
            var store = new LocalStore(root); var now = DateTimeOffset.Now; var prototype = fixture()[0];
            try
            {
                StreamWriter? writer = null; string? currentFile = null; var writing = Stopwatch.StartNew();
                try
                {
                    for (int i = 0; i < 259200; i++)
                    {
                        var sample = prototype with { Time = now.AddDays(-30).AddSeconds(i * 10) };
                        string file = Path.Combine(root, $"samples-{sample.Time.LocalDateTime:yyyy-MM-dd}.jsonl");
                        if (file != currentFile) { writer?.Dispose(); writer = new StreamWriter(file, false, new UTF8Encoding(false)); currentFile = file; }
                        writer!.WriteLine(JsonSerializer.Serialize(sample));
                    }
                }
                finally { writer?.Dispose(); }
                writing.Stop(); long bytes = Directory.GetFiles(root, "samples-*.jsonl").Sum(f => new FileInfo(f).Length);
                var reading = Stopwatch.StartNew(); var report = HistoryAnalysis.Analyze(store, WorkloadCoverage.Select(new(), true, "test-machine"), 30, asOf: now, hardwareId: "test-machine"); reading.Stop();
                assert(report.SampleCount == 259200 && report.History.Count <= 722 && !report.HistoryQuality.HasIssues, "full history scan lost evidence or retained raw chart points");
                File.WriteAllText("artifacts/history-read-benchmark.json", JsonSerializer.Serialize(new
                { Source = "synthetic local history files", Samples = 259200, Bytes = bytes, WriteSeconds = writing.Elapsed.TotalSeconds,
                    ReadAndAnalyzeSeconds = reading.Elapsed.TotalSeconds, ChartPoints = report.History.Count,
                    Scope = "One local read and analysis, including validation; not continuous 30-day operation or a cold-cache comparison" }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                // Only files created in this uniquely named benchmark directory are removed.
                foreach (string file in Directory.GetFiles(root, "samples-*.jsonl")) File.Delete(file);
                Directory.Delete(root);
            }
        });
    }
}
