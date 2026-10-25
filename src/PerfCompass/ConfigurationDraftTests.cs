namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunConfigurationDraftTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        const string hardware = "synthetic-configuration-draft";
        Sample[] Rows() => Enumerable.Range(0, 60).Select(i => new Sample
        {
            Time = now.AddHours(-1).AddSeconds(i * 10), Seconds = 10, Active = true, HardwareId = hardware,
            Cpu = 12, CoreMax = i == 0 ? 100 : 35, MemoryTotalGb = 32, MemoryUsedGb = 5, MemoryPeakGb = 5,
            CommitGb = 7, Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core", ObservationVersion = ActivityRules.Version,
            DiskUsedGb = 600, DiskTotalGb = 1000, DiskCapacityFieldsPresent = true, MaxDiskPercent = 60
        }).ToArray();
        AnalysisReport Analyze(IEnumerable<Sample> rows, int days = 7) => AnalysisEngine.Analyze(rows, new(), days, null, now, hardware);
        var baseline = Analyze(Rows());
        ConfigurationDraftRow Row(AnalysisReport report, string key) => ConfigurationDraft.Build(report).Baseline.Rows.Single(r => r.Key == key);
        SemanticFact Goal(string subject, string quantity, string source, string condition = "", string polarity = "required")
            => new(subject, "resource", polarity, "future", quantity, "GB", condition, source) { DecisionUse = "require" };
        AnalysisReport WithGoals(AnalysisReport report, params SemanticFact[] facts)
        {
            string source = string.Join("；", facts.Select(f => f.Evidence));
            var plan = SemanticRequirements.Confirm(new(source, "synthetic", "local", facts.ToList(), []), facts.Select(f => new SemanticFactEditor(f) { Include = true }));
            return SemanticRequirements.Apply(report, new(FutureNeeds: source, SemanticRequirements: plan));
        }
        check("Configuration draft separates a single-core peak from a multi-core buying requirement", () =>
        {
            var cpu = baseline.Cards.Single(c => c.Key == "cpu");
            assert(cpu.Observation?.State == "busy" && cpu.Observation.CpuDemand?.TotalP95 == 12
                && Row(baseline, "cpu").Recommendation.Contains("单核响应"), "A short core peak became a requirement for more cores");
            var parallel = Analyze(Rows().Select(s => s with { Cpu = 85, CoreMax = 95 }));
            assert(Row(parallel, "cpu").Recommendation.Contains("多核任务"), "Persistent parallel load was erased");
        });
        check("Memory draft compares usable GiB separately from product GB and commit", () =>
        {
            var light = Row(baseline, "memory");
            assert(light.AvailableMemoryGiB == 8 && light.NominalCapacityGB == null && light.Recommendation.Contains("GiB"), "Commit or product GB substituted for usable RAM");
            var highCommit = Row(Analyze(Rows().Select(s => s with { CommitGb = 48 })), "memory");
            assert(highCommit.AvailableMemoryGiB == 32 && highCommit.NominalCapacityGB == null,
                "Commit pressure was turned into a 48 GB installed-memory requirement");
            var occupied = Analyze(Rows().Select(s => s with { MemoryPeakGb = 27, MemoryUsedGb = 26 }));
            assert(Row(occupied, "memory").AvailableMemoryGiB == 32 && Row(occupied, "memory").Recommendation.Contains("保留"), "Non-light memory was automatically downsized");
            var shortPressure = Analyze(Rows().Select(s => s with { MemoryPeakGb = 31, MemoryUsedGb = 31, CommitGb = 33 }));
            assert(shortPressure.Cards.Single(c => c.Key == "memory").Status != "pressure"
                && Row(shortPressure, "memory").AvailableMemoryGiB == 32, "Short high usage was promoted to sustained pressure");
            var frame = Rows()[0] with { MemoryPeakGb = 31, MemoryUsedGb = 31, CommitGb = 33 };
            var pressure = Analyze(Enumerable.Range(0, 180).Select(i => frame with { Time = now.AddHours(-1).AddSeconds(i * 10) }));
            assert(Row(pressure, "memory").AvailableMemoryGiB == 48 && Row(pressure, "memory").Recommendation.Contains("优先比较"),
                "Physical memory pressure did not open a larger-capacity comparison");
        });
        check("Draft keeps selected older memory peaks while the recent CPU scope remains explicit", () =>
        {
            var rows = Rows().Append(Rows()[0] with { Time = now.AddDays(-20), MemoryPeakGb = 30, MemoryUsedGb = 30 });
            assert(Row(Analyze(rows, 30), "memory").AvailableMemoryGiB == 32
                && Row(Analyze(rows), "memory").AvailableMemoryGiB == 8, "Selected-window memory peak was lost or crossed the selected boundary");
            assert(Row(Analyze(rows, 30), "cpu").Scope.Contains("最近 7 天"), "Recent CPU evidence was relabeled as full-window evidence");
        });
        check("Storage draft proposes decimal retail capacity and distinguishes a restrictive upper bound", () =>
        {
            assert(Row(baseline, "disk").NominalCapacityGB == 1000, "600 GiB footprint did not open a 1 TB comparison");
            var restricted = WithGoals(baseline, Goal("存储", "不超过256", "存储不超过256GB"));
            var small = Row(restricted, "disk");
            assert(small.NominalCapacityGB == 256 && small.Spending.Contains("外迁"), "Upper bound was ignored or below-footprint capacity certified");
            var full = Analyze(Rows().Select(s => s with { DiskUsedGb = 930, MaxDiskPercent = 93 }));
            assert(Row(full, "disk").NominalCapacityGB == 2000, "Capacity pressure simply repeated the nearly full tier");
        });
        check("Confirmed strict capacity goals outrank a light observation without certifying usable RAM", () =>
        {
            var report = WithGoals(baseline, Goal("内存", "大于64", "内存大于64GB"));
            var row = Row(report, "memory");
            assert(row.NominalCapacityGB == 96 && row.Spending.Contains("严格大于"), "Strict nominal capacity goal became a non-strict or observed floor");
            assert(!row.Recommendation.Contains("达标") && row.Evidence.Contains("内存大于64GB"), "Evidence or suitability boundary lost");
        });
        check("Conditional configuration goals stay separate and do not contaminate the base plan", () =>
        {
            var report = WithGoals(baseline, Goal("内存", "至少64", "训练时内存至少64GB", "训练", "conditional"),
                Goal("内存", "不超过16", "出差时内存不超过16GB", "出差", "conditional"));
            var draft = ConfigurationDraft.Build(report);
            assert(draft.Conditions.Length == 2 && draft.Baseline.Rows.Single(r => r.Key == "memory").NominalCapacityGB == null,
                "Independent scenarios were combined with the base");
            assert(draft.Conditions.Single(c => c.Label == "训练").Rows.Single(r => r.Key == "memory").NominalCapacityGB == 64
                && draft.Conditions.All(c => c.Notice.Length == 0), "Separate scenarios produced a false conflict");
        });
        check("A confirmed VRAM target changes the route and conflicting memory bounds only block memory", () =>
        {
            var discrete = WithGoals(baseline, Goal("显存", "至少12", "独立显存至少12GB"));
            assert(Row(discrete, "gpu").Recommendation.Contains("独显方案优先"), "Positive dedicated VRAM requirement left integrated graphics first");
            var conflict = WithGoals(baseline, Goal("内存", "至少64", "内存至少64GB"), Goal("内存", "不超过32", "内存不超过32GB"));
            assert(Row(conflict, "memory").Recommendation.Contains("冲突") && Row(conflict, "memory").NominalCapacityGB == null
                && Row(conflict, "disk").NominalCapacityGB == 1000, "Conflict became a tier or erased an independent storage recommendation");
        });
        check("Draft invalid history and missing hardware cannot supply observed capacity or downgrade directions", () =>
        {
            foreach (var report in new[] { baseline with { HardwareId = "" }, Analyze([]) })
            {
                var rows = ConfigurationDraft.Build(report).Baseline.Rows;
                assert(rows.All(r => r.NominalCapacityGB == null && r.AvailableMemoryGiB == null)
                    && !rows.Single(r => r.Key == "cpu").Recommendation.Contains("主流"), "Unknown history created a concrete observed configuration");
            }
            var need = WithGoals(Analyze([]), Goal("内存", "至少64", "内存至少64GB"));
            assert(Row(need, "memory").NominalCapacityGB == 64, "Independent explicit capacity goal disappeared with no telemetry");
            using var damage = new HistoryDiagnostics(7); damage.Add("fixture.jsonl", 1, "synthetic corruption");
            var invalid = baseline with { HistoryQuality = damage.Build() };
            assert(Row(invalid, "memory").AvailableMemoryGiB == null && Row(invalid, "disk").NominalCapacityGB == null,
                "Damaged history supplied a footprint tier");
        });
        check("Configuration draft HTML preserves source escaping and does not create candidates or confirmations", () =>
        {
            string stamp = CandidateComparison.Stamp(baseline);
            var report = WithGoals(baseline, Goal("内存", "至少64", "<任务>内存至少64GB", "<训练>", "conditional"));
            string html = ReportExporter.Html(report, null);
            assert(html.Contains("下一台电脑配置草案") && html.Contains("&lt;训练&gt;") && !html.Contains("<训练>"), "Draft missing from HTML or unescaped source injected");
            assert(CandidateComparison.Stamp(baseline) == stamp && !baseline.RepresentativeConfirmed && !baseline.Ready,
                "Read-only planning changed confirmation or comparison scope");
        });
    }
}
