using System.IO;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunGpuEngineAnalysisTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Sample Row(int day, int index, double? load, string kind = "encode", bool complete = true) => new()
        {
            Time = now.AddDays(-day).AddSeconds(-index * 10), Seconds = 10, Active = true,
            HardwareId = "engine-machine", Scene = "导出视频", Gpu = 3, GpuPeak = 3,
            GpuEngineVersion = GpuEngineEvidence.Version,
            GpuEngines = [new(kind, [kind == "compute" ? "D3D Compute_0" : kind == "core" ? "GPU Core" : "D3D Video Encode"], load) { Complete = complete }]
        };
        List<Sample> Period(int firstDay, double load, string kind = "encode", bool complete = true, int days = 2, int perDay = 180)
            => Enumerable.Range(firstDay, days).SelectMany(day => Enumerable.Range(0, perDay).Select(i => Row(day, i, load, kind, complete))).ToList();
        AnalysisReport Base(bool ready = true, string hardwareId = "engine-machine") => new(now, 30, 1, 10, 3, ready,
            ["导出视频"], [new("gpu", "显卡", "review", "核心偏低", "核心/3D P95 3.0%", "核对候选", "旧口径")],
            [new(now.AddHours(-1), 5, 20, 3)], "") { HardwareId = hardwareId };
        AnalysisReport Analyze(IEnumerable<Sample> rows, AnalysisReport? basis = null, int days = 30)
        {
            var analyzer = new GpuEngineAnalysis(now, days);
            foreach (var row in rows) analyzer.Add(row);
            return analyzer.Apply(basis ?? Base());
        }
        static Advice Gpu(AnalysisReport report) => report.Cards.Single(c => c.Key == "gpu");
        static DemandTrend Trend(AnalysisReport report, string kind, int days = 7) => report.DemandTrends.Single(t => t.Key == "gpu-engine/" + kind && t.Days == days);

        check("Recent encoding pressure cannot be cancelled by legacy low core records or fill missing prior engines with zero", () =>
        {
            var legacy = Period(8, 0).Select(s => s with { GpuEngineVersion = "", GpuEngines = null }).ToArray();
            var recent = Period(1, 100);
            var report = Analyze(legacy.Concat(recent));
            var encoding = report.GpuEngineProfiles.Single(p => p.Kind == "encode");
            var trend = Trend(report, "encode");
            assert(Gpu(report).Status == "pressure" && Gpu(report).Title.Contains("编码"), "busy encoding remained a low-core downgrade review");
            assert(encoding.P95 == 100 && encoding.ObservedHours == 1 && encoding.HeavyMinutes == 60, "old core readings diluted encoding evidence");
            assert(trend.PreviousP95 == null && trend.PreviousHours == 0 && trend.Status == "insufficient", "missing prior engine history became idle or a proven increase");
            assert(recent.All(s => s.Gpu == 3 && s.GpuPeak == 3) && report.History.Single().Gpu == 3,
                "engine expansion rewrote legacy core/3D readings or curve");
        });
        check("Independent engine demand increases retain the old core trend and do not claim sustained pressure", () =>
        {
            var legacyTrend = new DemandTrend("gpu", "显卡核心/3D", "%", 7, "similar", 3, 3, 1, 1, 0, 0, 2, 2, [], "核心相近", "核对任务");
            var report = Analyze(Period(8, 10).Concat(Period(1, 85)), Base() with { DemandTrends = [legacyTrend] });
            assert(Trend(report, "encode").Status == "increased" && Gpu(report).Status == "changed", "encoding increase was omitted or confused with sustained >=95% pressure");
            assert(report.DemandTrends.Single(t => t.Key == "gpu") == legacyTrend && Trend(report, "encode").Name.Contains("编码"),
                "new engine replaced or renamed the legacy trend");
        });
        check("Thirty-day engine comparison is separate from an unavailable adjacent-week baseline", () =>
        {
            var report = Analyze(Period(36, 10, days: 3, perDay: 360).Concat(Period(1, 85, days: 3, perDay: 360)));
            assert(Trend(report, "encode", 30).Status == "increased" && Trend(report, "encode", 7).Status == "insufficient"
                && Gpu(report).Status == "changed", "month demand was dropped or silently supplied a week baseline");
        });
        check("The recent pressure floor survives a long idle-engine history with a low overall P95", () =>
        {
            var report = Analyze(Period(8, 0, days: 20, perDay: 360).Concat(Period(1, 100, perDay: 90)));
            assert(report.GpuEngineProfiles.Single(p => p.Kind == "encode").P95 == 0 && Gpu(report).Status == "pressure",
                "long historical idle time erased newly sustained engine work");
        });
        check("Engine quantiles use elapsed time and a brief peak blocks low-load review without proving pressure", () =>
        {
            var low = Row(1, 1, 0) with { Seconds = 15 };
            var peak = Row(1, 0, 98) with { Seconds = .5 };
            var report = Analyze([low, peak]); var profile = report.GpuEngineProfiles.Single(p => p.Kind == "encode");
            assert(profile.P95 == 0 && profile.Peak == 98 && Math.Abs(profile.ObservedHours * 3600 - 15.5) < 1e-8
                && Math.Abs(profile.HeavyMinutes * 60 - .5) < 1e-8, "sample count replaced time weighting or the peak was averaged away");
            assert(Gpu(report).Status == "fit" && Gpu(report).Title.Contains("偶发"), "brief work became pressure or remained a low-load review");
        });
        check("Partial known high nodes remain positive evidence but do not establish complete-category pressure or trends", () =>
        {
            var report = Analyze(Period(8, 0).Concat(Period(1, 100, complete: false)), Base(ready: false));
            var profile = report.GpuEngineProfiles.Single(p => p.Kind == "encode");
            assert(profile.P95 == 100 && profile.Peak == 100 && profile.ObservedHours == 2 && profile.CompleteHours == 1,
                "known partial work vanished or became complete coverage");
            assert(Trend(report, "encode").RecentP95 == null && Trend(report, "encode").Status == "insufficient"
                && Gpu(report).Status == "unknown" && Gpu(report).Title.Contains("重负载"), "partial nodes invented pressure, increase or surplus");
        });
        check("A batch mean mixing complete and partial nodes cannot stand in for its unavailable complete-only mean", () =>
        {
            var rows = Period(1, 50).Select(s => s with
            {
                GpuEngines = [s.GpuEngines![0] with { Complete = false, Peak = 100, ObservedSeconds = 10, CoverageSeconds = 5, HeavySeconds = 5, HighSeconds = 5 }]
            });
            var report = Analyze(rows); var profile = report.GpuEngineProfiles.Single(p => p.Kind == "encode");
            assert(profile.P95 == 50 && profile.Peak == 100 && profile.ObservedHours == 1 && profile.CompleteHours == .5
                && profile.HeavyMinutes == 30, "mixed batch lost observed duration, peak or separate coverage");
            assert(Trend(report, "encode").RecentP95 == null && Gpu(report).Status is not ("pressure" or "changed" or "review"),
                "mixed batch mean was used as complete-only pressure or ignored as a task clue");
        });
        check("Missing engine rows remain in the active denominator and prevent false comparable coverage", () =>
        {
            var missing = Period(1, 0).Select(s => s with { Time = s.Time.AddHours(-2), GpuEngineVersion = "", GpuEngines = null });
            var report = Analyze(Period(8, 10).Concat(Period(1, 100)).Concat(missing));
            assert(Trend(report, "encode").Status == "insufficient" && Gpu(report).Status is not ("pressure" or "changed" or "review"),
                "absent engine observations were omitted from the coverage denominator");
        });
        check("Changed sensor-node scope is not silently interpreted as changed user demand", () =>
        {
            var prior = Period(8, 10, "compute").Select(s => s with
                { GpuEngines = [s.GpuEngines![0] with { Sources = ["D3D Compute_1"] }] });
            var report = Analyze(prior.Concat(Period(1, 85, "compute")));
            assert(Trend(report, "compute").Status == "insufficient" && Trend(report, "compute").Summary.Contains("来源范围")
                && Gpu(report).Status is not ("changed" or "review"), "different exposed nodes fabricated a demand increase");
            assert(report.GpuEngineProfiles.Single(p => p.Kind == "compute").Sources.SequenceEqual(new[] { "D3D Compute_0", "D3D Compute_1" }),
                "engine evidence lost the measured source labels");
        });
        check("Only-core low records retain a qualified review while missing categories stay unknown", () =>
        {
            var report = Analyze(Period(1, 3, "core"));
            assert(Gpu(report).Status == "review" && Gpu(report).Title.Contains("其他引擎待核对") && Gpu(report).Limits.Contains("低占用不能证明独显过剩"),
                "limited core evidence became a whole-card surplus conclusion or lost its review scope");
            assert(report.GpuEngineProfiles.Where(p => p.Kind != "core").All(p => p.P95 == null && p.Peak == null && p.ObservedHours == 0)
                && !report.DemandTrends.Any(t => t.Key == "gpu-engine/encode"), "missing engine categories acquired zero readings or synthetic history");
        });
        check("Low core with a moderately busy encoding engine keeps only the qualified core review", () =>
        {
            var report = Analyze(Period(1, 60));
            assert(report.GpuEngineProfiles.Single(p => p.Kind == "encode").P95 == 60 && Gpu(report).Status == "review"
                && Gpu(report).Title.Contains("核心/3D负载偏低") && Gpu(report).Title.Contains("其他引擎核对")
                && !Gpu(report).Title.Contains("已采引擎负载偏低"),
                "low legacy core was generalized into low load for a moderately busy encoding engine");
        });
        check("Empty and mixed hardware identifiers cannot create pooled engine conclusions", () =>
        {
            var rows = Period(1, 100);
            foreach (var report in new[]
            {
                Analyze(rows, Base(hardwareId: "")),
                Analyze(rows.Concat([Row(1, 300, 100) with { HardwareId = "other-machine" }])),
                Analyze(rows.Concat([Row(1, 300, 100) with { HardwareId = "" }]))
            })
                assert(report.GpuEngineProfiles.Count == 0 && !report.DemandTrends.Any(t => t.Key.StartsWith("gpu-engine/", StringComparison.Ordinal))
                    && Gpu(report).Status == "review", "unisolated hardware acquired a new engine-pressure conclusion");
        });
        check("Invalid, future, idle and zero-duration readings never manufacture engine evidence", () =>
        {
            var row = Row(1, 0, 100);
            Sample[] rows =
            [
                row with { GpuEngineVersion = "future-engine-rules" },
                row with { GpuEngines = [new("encode", ["GPU Memory Controller"], 100) { Complete = true }] },
                row with { GpuEngines = [new("encode", ["D3D Video Encode"], double.NaN) { Complete = true }] },
                row with { Time = now.AddSeconds(1) }, row with { Active = false },
                row with { Seconds = 0 }, row with { Seconds = 16 },
                row with { GpuEngines = [row.GpuEngines![0] with { ObservedSeconds = 0, CoverageSeconds = 0 }] }
            ];
            var report = Analyze(rows);
            assert(report.GpuEngineProfiles.All(p => p.P95 == null && p.Peak == null && p.ObservedHours == 0)
                && Gpu(report).Status == "review" && report.DemandTrends.All(t => t.Status == "insufficient"),
                "invalid or unobserved work acquired load or positive conclusions");
        });
        check("History diagnostics still withdraw new engine demand-change conclusions", () =>
        {
            var report = Analyze(Period(8, 10).Concat(Period(1, 85)));
            var quality = new HistoryQuality(60, 1, 1, "test", [new("test-history.jsonl", 3, "invalid synthetic history")]);
            var protectedReport = HistoryAnalysis.Apply(report, quality);
            assert(Gpu(protectedReport).Status == "unknown" && protectedReport.DemandTrends.All(t => t.Status == "insufficient"),
                "new engine analysis bypassed existing history-quality protection");
        });
        check("Damaged production history cannot retain an engine period narrative on an independent core-pressure card", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-gpu-pressure-history-" + Guid.NewGuid().ToString("N"));
            using var lease = DataRootLease.Acquire(root) ?? throw new IOException("Cannot lease isolated GPU history test directory.");
            var store = new LocalStore(root);
            foreach (var row in Period(8, 10).Concat(Period(1, 85))) store.Append(row with { Gpu = 100, GpuPeak = 100 });
            var settings = WorkloadCoverage.Select(new(), true, "engine-machine");
            var clean = HistoryAnalysis.Analyze(store, settings, 30, asOf: now, hardwareId: "engine-machine");
            assert(Gpu(clean).Status == "pressure" && Trend(clean, "encode").Status == "increased"
                && Gpu(clean).Evidence.Contains("此前"), "fixture did not establish independent core pressure and engine increase");
            File.AppendAllText(Path.Combine(root, $"samples-{now.LocalDateTime:yyyy-MM-dd}.jsonl"), "{truncated engine history\n");
            var damaged = HistoryAnalysis.Analyze(store, settings, 30, asOf: now, hardwareId: "engine-machine");
            assert(damaged.HistoryQuality.IssueCount == 1 && damaged.SampleCount == clean.SampleCount
                && Gpu(damaged).Status == "pressure" && !Gpu(damaged).Title.Contains("增加")
                && !Gpu(damaged).Evidence.Contains("此前") && damaged.DemandTrends.All(t => t.Status == "insufficient"),
                "invalid period narrative survived on the pressure card or valid core pressure disappeared");
        });
    }
}
