using System.IO;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunHistoryAggregationTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var anchor = now.AddDays(-1).AddMinutes(-30);
        const string hardware = "history-aggregation-synthetic-machine";
        Sample Row(double end, double seconds, double? cpu = 0, double? memory = 0, double? gpu = 0) => new()
        {
            Time = anchor.AddSeconds(end), Seconds = seconds, Active = true, HardwareId = hardware,
            Scene = "合成加权任务", ObservationVersion = ActivityRules.Version,
            Cpu = cpu, CoreMax = cpu, MemoryUsedGb = memory, MemoryTotalGb = 100, CommitGb = memory,
            Gpu = gpu, GpuMetricSource = "GPU Core"
        };
        AnalysisReport Analyze(IEnumerable<Sample> rows, int days = 7, string? scene = null, string id = hardware)
            => AnalysisEngine.Analyze(rows, new(), days, scene, now, id);
        static bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < 1e-7;
        void InStore(IEnumerable<Sample> rows, Action<LocalStore> action)
        {
            string directory = Path.Combine(Path.GetTempPath(), "PerfCompass-history-aggregation-" + Guid.NewGuid().ToString("N"));
            using var lease = DataRootLease.Acquire(directory) ?? throw new IOException("Cannot lease isolated hourly aggregation test directory.");
            var store = new LocalStore(directory);
            foreach (var row in rows) store.Append(row);
            action(store);
        }
        AnalysisReport History(LocalStore store, int days = 7, string? scene = null)
            => HistoryAnalysis.Analyze(store, new(), days, scene, now, hardware);

        check("Actual history hours weight unequal records by component seconds instead of row count", () =>
        {
            Sample[] rows = [Row(1, 1, 100, 100, 100), Row(10, 9, 0, 0, 0)];
            InStore(rows, store =>
            {
                var report = History(store); var point = report.History.Single();
                assert(!report.HistoryQuality.HasIssues && Near(point.Cpu, 10) && Near(point.Memory, 10) && Near(point.Gpu, 10),
                    "one short high reading and nine measured low seconds still produced a 50-percent row-count average");
                assert(Near(point.CpuObservedSeconds, 10) && Near(point.MemoryObservedSeconds, 10) && Near(point.GpuObservedSeconds, 10)
                    && !point.CpuCoverageEstimated && !point.MemoryCoverageEstimated && !point.GpuCoverageEstimated,
                    "hourly means lost their actual component denominators or mislabeled unsplit full readings as estimated");
            });
        });
        check("Splitting equal-load intervals preserves every hourly mean and covered duration", () =>
        {
            var grouped = Analyze([Row(1, 1, 100, 100, 100), Row(10, 9)]).History.Single();
            var divided = Analyze(new[] { Row(1, 1, 100, 100, 100) }.Concat(Enumerable.Range(2, 9).Select(i => Row(i, 1)))).History.Single();
            assert(grouped == divided, "changing batch size changed the hourly observation despite identical elapsed load");
        });
        check("Independent CPU memory and GPU coverage supply distinct weighted denominators", () =>
        {
            var first = Row(10, 10, 100, 20, 100) with { CpuCoverage = 1, MemoryCoverage = 2, GpuCoverage = 4 };
            var second = Row(20, 10, 0, 80, 0) with { CpuCoverage = 9, MemoryCoverage = 8, GpuCoverage = 6 };
            var point = Analyze([first, second]).History.Single();
            assert(Near(point.Cpu, 10) && Near(point.Memory, 68) && Near(point.Gpu, 40)
                && Near(point.CpuObservedSeconds, 10) && Near(point.MemoryObservedSeconds, 10) && Near(point.GpuObservedSeconds, 10),
                "total active duration or another component's coverage replaced an actual sensor denominator");
            assert(!point.CpuCoverageEstimated && !point.MemoryCoverageEstimated && !point.GpuCoverageEstimated,
                "a whole partially-covered record wholly inside one hour needs no estimated location allocation");
        });
        check("Zero or invalid coverage cannot add percentages, measured seconds or GPU provenance", () =>
        {
            var ignored = Row(10, 10, 100, 100, 100) with
            { CpuCoverage = 0, MemoryCoverage = 0, GpuCoverage = 0, GpuMetricSource = "D3D 3D" };
            var point = Analyze([ignored, Row(20, 10, 10, 20, 30)]).History.Single();
            assert(Near(point.Cpu, 10) && Near(point.Memory, 20) && Near(point.Gpu, 30)
                && Near(point.CpuObservedSeconds, 10) && Near(point.MemoryObservedSeconds, 10) && Near(point.GpuObservedSeconds, 10)
                && point.GpuScope.EndsWith("/GPU Core", StringComparison.Ordinal),
                "zero-covered raw values polluted a mean, denominator or source scope");
            var absent = Analyze([ignored]).History.Single();
            assert(absent.Cpu == null && absent.Memory == null && absent.Gpu == null && absent.CpuObservedSeconds == 0
                && absent.MemoryObservedSeconds == 0 && absent.GpuObservedSeconds == 0,
                "zero sensor coverage became a zero-load reading or an unqualified known mean");
            var invalid = Analyze([ignored with { CpuCoverage = double.NaN, MemoryCoverage = -1, GpuCoverage = double.PositiveInfinity }]).History.Single();
            assert(invalid.Cpu == null && invalid.Memory == null && invalid.Gpu == null, "invalid coverage survived sanitization into hourly evidence");
        });
        check("Full measured intervals cross UTC hour boundaries by elapsed overlap and exact endpoints stay in the preceding hour", () =>
        {
            var boundary = now.AddDays(-1);
            var frame = Row(0, 10, 80, 40, 60) with { Time = boundary.AddSeconds(2).ToOffset(TimeSpan.FromHours(8)) };
            InStore([frame], store =>
            {
                var report = History(store); var points = report.History;
                assert(!report.HistoryQuality.HasIssues && points.Count == 2 && points[0].Time == boundary.AddHours(-1) && points[1].Time == boundary,
                    "a crossing record was put only into its ending hour or binned by a mismatched offset");
                assert(Near(points[0].CpuObservedSeconds, 8) && Near(points[1].CpuObservedSeconds, 2)
                    && Near(points.Sum(p => p.MemoryObservedSeconds ?? 0), 10) && Near(points.Sum(p => p.GpuObservedSeconds ?? 0), 10)
                    && points.All(p => Near(p.Cpu, 80) && Near(p.Memory, 40) && Near(p.Gpu, 60)
                        && !p.CpuCoverageEstimated && !p.MemoryCoverageEstimated && !p.GpuCoverageEstimated),
                    "full crossing coverage was lost, duplicated or confused with partially observed coverage");
            });
            var exact = Analyze([frame with { Time = boundary }]).History;
            assert(exact.Count == 1 && exact[0].Time == boundary.AddHours(-1) && Near(exact[0].CpuObservedSeconds, 10),
                "an end-exclusive interval ending at the hour boundary created a later-hour reading");
        });
        check("Partial crossing coverage is proportionally allocated and explicitly estimated per component", () =>
        {
            var frame = Row(0, 10, 80, 40, 60) with
            { Time = now.AddDays(-1).AddSeconds(2), CpuCoverage = 4, MemoryCoverage = 6, GpuCoverage = 10 };
            var points = Analyze([frame]).History;
            assert(points.Count == 2 && Near(points[0].CpuObservedSeconds, 3.2) && Near(points[1].CpuObservedSeconds, .8)
                && Near(points[0].MemoryObservedSeconds, 4.8) && Near(points[1].MemoryObservedSeconds, 1.2)
                && Near(points[0].GpuObservedSeconds, 8) && Near(points[1].GpuObservedSeconds, 2),
                "partial coverage was assigned to invented first/last sample locations or multiplied by each hour");
            assert(points.All(p => p.CpuCoverageEstimated && p.MemoryCoverageEstimated && !p.GpuCoverageEstimated),
                "estimated partial placement was hidden or spread to an independently fully-covered metric");
        });
        check("The report cutoff clips hourly time and scales unknown coverage locations without reading before the window", () =>
        {
            var cutoff = now.AddDays(-1);
            var frame = Row(0, 10, 80, 40, 60) with { Time = cutoff.AddSeconds(2), CpuCoverage = 6, MemoryCoverage = 10, GpuCoverage = 4 };
            var point = Analyze([frame], days: 1).History.Single();
            assert(point.Time == cutoff && Near(point.CpuObservedSeconds, 1.2) && Near(point.MemoryObservedSeconds, 2)
                && Near(point.GpuObservedSeconds, .8) && point.CpuCoverageEstimated && !point.MemoryCoverageEstimated && point.GpuCoverageEstimated,
                "the crossing window boundary kept pre-window seconds or invented where partial readings occurred");
            assert(Analyze([frame with { Time = cutoff }], days: 1).History.Count == 0,
                "an interval ending at the cutoff created in-window load evidence");
        });
        check("Recorded idle time remains in history while purchase statistics continue to use active time only", () =>
        {
            var idle = Row(9, 9) with { Active = false };
            var active = Row(10, 1, 100, 100, 100);
            InStore([idle, active], store =>
            {
                var report = History(store); var point = report.History.Single();
                assert(!report.HistoryQuality.HasIssues && Near(point.Cpu, 10) && Near(point.CpuObservedSeconds, 10)
                    && Near(report.ActiveHours * 3600, 1) && Near(report.SceneProfiles.Single().ActiveHours * 3600, 1),
                    "history dropped legitimate idle recordings or the chart's full duration leaked into purchasing evidence");
                assert(report.SceneProfiles.Single().CpuP95 == 100, "idle chart records diluted active-scene CPU demand");
            });
        });
        check("Actual history duplicate and overlap safeguards also govern the new hourly duration fields", () =>
        {
            var frame = Row(10, 10, 40, 40, 40);
            InStore([frame, frame], store =>
            {
                var report = History(store);
                assert(report.HistoryQuality.DuplicateCount == 1 && report.History.Count == 1
                    && Near(report.History.Single().CpuObservedSeconds, 10), "byte-identical copies inflated an hourly sensor denominator");
            });
            InStore([frame, Row(15, 10, 80, 80, 80)], store =>
            {
                var report = History(store);
                assert(report.HistoryQuality.HasIntervalConflicts && report.History.Count == 0,
                    "overlapping records retained means or split-hour duration claims with unknown load ownership");
            });
        });
        check("Mixed GPU scopes withhold GPU hourly values and durations independently of CPU and memory", () =>
        {
            var first = Row(10, 10, 10, 20, 30);
            var second = Row(20, 10, 30, 40, 50) with { GpuMetricSource = "D3D 3D" };
            var report = Analyze([first, second]); var point = report.History.Single();
            assert(report.GpuHistoryHasMixedSources && point.Gpu == null && point.GpuObservedSeconds == null && point.GpuScope.Length == 0
                && Near(point.Cpu, 20) && Near(point.Memory, 30) && Near(point.CpuObservedSeconds, 20),
                "GPU source mixing exposed a combined GPU mean/denominator or erased independent component readings");
        });
        check("Explicit hardware and scene filters bound hourly weights and missing hours retain unknown readings", () =>
        {
            var own = Row(10, 10, 20, 30, 40);
            var otherHardware = own with { HardwareId = "other-synthetic-machine", Cpu = 100 };
            var otherScene = Row(20, 10, 80, 90, 100) with { Scene = "另一合成任务" };
            InStore([own, otherHardware, otherScene], store =>
            {
                var report = History(store, scene: own.Scene); var point = report.History.Single();
                assert(report.HardwareId == hardware && report.SelectedScene == own.Scene && Near(point.Cpu, 20)
                    && Near(point.CpuObservedSeconds, 10), "another machine or unselected scene supplied load or weighted duration");
            });
            var missing = own with { Time = own.Time.AddHours(1), Cpu = null, MemoryUsedGb = null, Gpu = null };
            var later = own with { Time = own.Time.AddHours(2) };
            var points = Analyze([own, missing, later]).History;
            assert(points.Count == 3 && points[1].Cpu == null && points[1].Memory == null && points[1].Gpu == null
                && points[1].CpuObservedSeconds == 0 && points[1].MemoryObservedSeconds == 0 && points[1].GpuObservedSeconds == 0,
                "an unobserved hour became a measured zero or borrowed coverage from neighboring hours");
            assert(Analyze([own with { Seconds = 0 }]).History.Count == 0, "a zero-duration interruption created hourly coverage");
        });
        check("Legacy saved history points keep absent duration metadata unknown and new points roundtrip their provenance", () =>
        {
            const string legacy = "{\"Time\":\"2026-10-22T11:00:00+00:00\",\"Cpu\":10,\"Memory\":20,\"Gpu\":30}";
            var old = JsonSerializer.Deserialize<HistoryPoint>(legacy)!;
            assert(old.CpuObservedSeconds == null && old.MemoryObservedSeconds == null && old.GpuObservedSeconds == null,
                "older saved report points fabricated zero or complete observed duration");
            var frame = Row(0, 10, 30, 40, 50) with { Time = now.AddDays(-1).AddSeconds(2), CpuCoverage = 4 };
            var point = Analyze([frame]).History[0];
            var restored = JsonSerializer.Deserialize<HistoryPoint>(JsonSerializer.Serialize(point));
            assert(restored == point && restored!.CpuCoverageEstimated && Near(restored.CpuObservedSeconds, 3.2),
                "report serialization lost the weighted denominator or the partial-allocation estimate flag");
        });
        check("CPU period busy labels disclose batch-mean duration while GPU keeps its measured heavy exposure", () =>
        {
            foreach (int highReadings in new[] { 1, 4 })
            {
                var raw = Enumerable.Range(0, 5).Select(i => Row((i + 1) * 2, 2,
                    cpu: i < highReadings ? 100 : 0, gpu: i < highReadings ? 100 : 0)).ToArray();
                var batch = SampleBatch.Merge(raw);
                assert(batch.Cpu == highReadings * 20 && batch.CoreMax == 100 && batch.CpuHighSeconds == highReadings * 2,
                    "the real aggregation fixture did not retain a low mean, core peak and independently measured CPU exposure");
                var report = Analyze([batch]);
                var cpu = report.DemandTrends.Single(t => t.Key == "cpu" && t.Days == 7);
                var gpu = report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7);
                assert(Near(cpu.RecentBusyMinutes, highReadings == 1 ? 0 : 10d / 60)
                    && cpu.BusyLabel.Contains("批次平均负载"),
                    "CPU batch-mean coverage was mislabeled as actual time above 80 percent or confused with measured time above 90 percent");
                assert(Near(gpu.RecentBusyMinutes, highReadings * 2d / 60) && gpu.BusyLabel.Contains("负载")
                    && !gpu.BusyLabel.Contains("批次平均"), "GPU measured heavy duty inherited the CPU batch-mean limitation");
                string html = ReportExporter.Html(report, null);
                assert(html.Contains(System.Net.WebUtility.HtmlEncode(cpu.BusyLabel))
                    && html.Contains(System.Net.WebUtility.HtmlEncode(gpu.BusyLabel)),
                    "HTML obscured the different provenance of CPU batch-mean duration and GPU measured duty");
            }
        });
        check("Distinct overlapping idle records trigger production history diagnostics and withdraw hourly evidence", () =>
        {
            var idle = Row(10, 10, 20, 30, 40) with { Active = false };
            var conflicting = idle with { Cpu = 80, Gpu = 90, Scene = "另一闲置来源" };
            InStore([idle, conflicting], store =>
            {
                var report = History(store); var issue = report.HistoryQuality.Issues.Single();
                assert(report.HistoryQuality.HasIntervalConflicts && report.HistoryQuality.DuplicateCount == 0
                    && issue.Line == 2 && issue.Reason.Contains("时间片重叠") && report.History.Count == 0
                    && report.ActiveHours == 0 && report.SceneProfiles.All(p => p.ActiveHours == 0),
                    "two conflicting idle observations doubled chart weights without an overlap diagnostic or acquired active duration");
            });
        });
        check("Idle-active overlap is diagnosed in either order without consuming active-only unique duration", () =>
        {
            var active = Row(10, 10, 40, 30, 20);
            var idle = active with { Active = false, Cpu = 10 };
            foreach (bool idleFirst in new[] { true, false })
                InStore(idleFirst ? [idle, active] : [active, idle], store =>
                {
                    var report = History(store);
                    assert(report.HistoryQuality.HasIntervalConflicts && report.History.Count == 0
                        && Near(report.ActiveHours * 3600, 10) && Near(report.SceneProfiles.Single().ActiveHours * 3600, 10),
                        "an earlier idle recording occupied later active time or mixed activity escaped overlap checks");
                });
            var laterActive = Row(15, 10, 50, 30, 20);
            InStore([idle, active, laterActive], store =>
            {
                var report = History(store);
                assert(report.HistoryQuality.HasIntervalConflicts && Near(report.ActiveHours * 3600, 15)
                    && Near(report.SceneProfiles.Single().ActiveHours * 3600, 15),
                    "the all-record audit changed the existing union of partially overlapping active intervals");
            });
        });
        check("Adjacent idle intervals, exact idle duplicates and zero-duration records do not create false overlaps", () =>
        {
            var first = Row(10, 10, 20, 30, 40) with { Active = false };
            var adjacent = Row(20, 10, 40, 50, 60) with { Active = false };
            var zero = first with { Seconds = 0, Cpu = 100, Gpu = 100 };
            InStore([first, adjacent, first, zero], store =>
            {
                var report = History(store); var point = report.History.Single();
                assert(!report.HistoryQuality.HasIssues && !report.HistoryQuality.HasIntervalConflicts
                    && report.HistoryQuality.DuplicateCount == 1 && report.SampleCount == 3 && report.ActiveHours == 0
                    && Near(point.Cpu, 30) && Near(point.CpuObservedSeconds, 20),
                    "adjacent or duplicate idle data falsely blocked history, a zero interval gained weight, or a precise copy was counted twice");
            });
        });
    }
}
