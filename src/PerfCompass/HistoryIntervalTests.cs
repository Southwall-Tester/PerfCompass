using System.IO;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunHistoryIntervalTests(Action<string, Action> check, Action<bool, string> assert, Func<List<Sample>> fixture)
    {
        LocalStore Store(IEnumerable<Sample> samples, int copies = 1)
        {
            var s = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-intervals-" + Guid.NewGuid().ToString("N")));
            foreach (var group in samples.GroupBy(p => p.Time.LocalDateTime.Date))
                File.WriteAllLines(Path.Combine(s.Root, $"samples-{group.Key:yyyy-MM-dd}.jsonl"), group.SelectMany(p => Enumerable.Repeat(JsonSerializer.Serialize(p), copies)), new UTF8Encoding(false));
            return s;
        }
        var settings = WorkloadCoverage.Select(new(), true, "test-machine");
        check("Identical history copies cannot turn five observed hours into sufficient evidence", () =>
        {
            var samples = fixture().Where((_, i) => i % 2 == 0).ToArray();
            assert(AnalysisEngine.Analyze(samples.SelectMany(s => Enumerable.Repeat(s, 3)), settings, 7).Ready, "fixture did not reproduce duplicate inflation");
            var store = Store(samples, 3); var before = Directory.GetFiles(store.Root, "samples-*.jsonl").ToDictionary(f => f, File.ReadAllBytes);
            var report = HistoryAnalysis.Analyze(store, settings, 7, hardwareId: "test-machine");
            assert(report.ActiveHours == 5 && !report.Ready && report.SampleCount == 1800 && report.HistoryQuality.DuplicateCount == 3600
                && !report.HistoryQuality.HasIssues && report.Cards.All(c => c.Status is not ("surplus" or "pressure")), "duplicate copies manufactured duration or buying evidence");
            assert(before.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "analysis rewrote original copies");
        });
        check("Deduplication preserves genuine sufficient evidence and the same candidate context", () =>
        {
            var samples = fixture(); var clean = HistoryAnalysis.Analyze(Store(samples), settings, 7, hardwareId: "test-machine");
            var copied = HistoryAnalysis.Analyze(Store(samples, 2), settings, 7, hardwareId: "test-machine");
            assert(copied.Ready && copied.ActiveHours == 10 && copied.SampleCount == clean.SampleCount && copied.HistoryQuality.DuplicateCount == 3600
                && copied.HistoryQuality.HasNotices && !copied.HistoryQuality.HasIssues && CandidateComparison.Stamp(copied) == CandidateComparison.Stamp(clean), "harmless copies invalidated otherwise valid evidence");
            string html = ReportExporter.Html(copied, null); assert(html.Contains("历史重复记录已排除") && html.Contains("3600"), "report hid duplicate accounting");
        });
        check("Duplicate short high readings cannot manufacture sustained CPU pressure", () =>
        {
            var samples = fixture().Where((_, i) => i % 1200 == 0).Select(s => s with { Cpu = 100, CoreMax = 100 }).ToArray();
            var report = HistoryAnalysis.Analyze(Store(samples, 1500), settings, 7, hardwareId: "test-machine");
            assert(report.ActiveHours == 30d / 3600 && report.SampleCount == 3 && report.Cards.Single(c => c.Key == "cpu").Status == "unknown", "repetition became sustained pressure");
        });
        check("Interval union is independent of insertion order and handles nesting adjacency and bridges", () =>
        {
            var source = new[] { (0L, 10L), (5L, 15L), (15L, 20L), (7L, 8L), (30L, 40L), (19L, 35L) };
            foreach (var rows in new[] { source, source.Reverse().ToArray(), source.OrderBy(p => p.Item2 - p.Item1).ToArray() })
            {
                var intervals = new HistoryIntervals(); long total = rows.Sum(r => intervals.Add(r.Item1, r.Item2));
                assert(total == 40 && intervals.Add(0, 40) == 0 && intervals.Add(40, 50) == 10, "interval order inflated covered time");
            }
            var disjoint = new HistoryIntervals(); for (int i = 0; i < 10000; i++) assert(disjoint.Add(i * 3, i * 3 + 1) == 1, "disjoint interval lost");
            assert(disjoint.Add(0, 30000) == 20000, "bridging many ranges double-counted gaps");
        });
        check("Nonidentical overlapping samples count unique time and cannot assert historical load shares", () =>
        {
            var now = DateTimeOffset.Now.AddMinutes(-1); var prototype = fixture()[^1];
            var rows = new[] { prototype with { Time = now, Cpu = 0, Gpu = 0 }, prototype with { Time = now.AddSeconds(5), Cpu = 100, Gpu = 100 } };
            foreach (var ordered in new[] { rows, rows.Reverse().ToArray() })
            {
                var report = HistoryAnalysis.Analyze(Store(ordered), settings, 7, hardwareId: "test-machine");
                assert(report.ActiveHours == 15d / 3600 && report.HistoryQuality.HasIntervalConflicts && report.HistoryQuality.IssueCount == 1
                    && report.Cards.All(c => c.Status == "unknown") && report.History.Count == 0 && report.SceneProfiles.Single().ActiveHours == 15d / 3600
                    && report.SceneProfiles.Single().CpuP95 == null && report.DemandTrends.All(t => t.RecentP95 == null && !t.LoadStatisticsAvailable
                        && t.RecentHeavyScenes.Length == 0 && t.SceneLabel == "" && !t.BusyLabel.Contains("0.0%")), "ambiguous overlap retained pressure or double-counted time");
            }
        });
        check("Overlapping scene labels use unique overall time and cannot be added together", () =>
        {
            var row = fixture()[^1] with { Time = DateTimeOffset.Now.AddMinutes(-1) };
            var store = Store([row with { Scene = "A" }, row with { Scene = "B" }]);
            var all = HistoryAnalysis.Analyze(store, settings, 7, hardwareId: "test-machine");
            var scene = HistoryAnalysis.Analyze(store, settings, 7, "B", hardwareId: "test-machine");
            assert(all.ActiveHours == 10d / 3600 && all.SceneProfiles.Sum(p => p.ActiveHours) == 20d / 3600 && scene.ActiveHours == 10d / 3600
                && scene.HistoryQuality.HasIntervalConflicts && ReportExporter.Html(all, null).Contains("不能相加"), "scene labels created extra overall observation");
        });
        check("Different hardware scopes and harmless outer whitespace do not create interval conflicts", () =>
        {
            var row = fixture()[^1]; var s = Store([row, row with { HardwareId = "other" }]);
            string path = Directory.GetFiles(s.Root, "samples-*.jsonl").Single();
            File.AppendAllText(path, " \t" + JsonSerializer.Serialize(row) + "\r\n");
            var scoped = HistoryAnalysis.Analyze(s, settings, 7, hardwareId: "test-machine");
            assert(scoped.SampleCount == 1 && scoped.HistoryQuality.DuplicateCount == 1 && !scoped.HistoryQuality.HasIntervalConflicts, "unrelated hardware or outer whitespace created a conflict");
        });
        check("Fractional tick rounding does not erase valid contiguous observation", () =>
        {
            var now = DateTimeOffset.Now.AddMinutes(-2); var row = fixture()[^1];
            double duration = Enumerable.Repeat(.1234567, 5).Sum(); long ticks = (long)Math.Round(duration * TimeSpan.TicksPerSecond);
            var records = Enumerable.Range(0, 100).Select(i => row with { Time = now.AddTicks(i * ticks), Seconds = duration }).ToArray();
            var report = HistoryAnalysis.Analyze(Store(records), settings, 7, hardwareId: "test-machine");
            assert(report.SampleCount == 100 && !report.HistoryQuality.HasIssues && Math.Abs(report.ActiveHours * 3600 - duration * 100) < .00001, $"floating point rounding became missing time or overlap: hours={report.ActiveHours:R}, expectedSeconds={duration * 100:R}, count={report.SampleCount}, issues={report.HistoryQuality.Details}");
        });
    }
}
