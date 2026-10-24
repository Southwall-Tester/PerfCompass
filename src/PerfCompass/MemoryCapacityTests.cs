namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunMemoryCapacityTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = DateTimeOffset.Now;
        var rows = Enumerable.Range(0, 30).Select(i => new Sample { Time = now.AddSeconds(-600 + i * 10), HardwareId = "memory-capacity-fixture",
            Seconds = 10, Active = true, Cpu = 5, CoreMax = 20, MemoryTotalGb = 24, MemoryUsedGb = 12, MemoryPeakGb = 17.5, CommitGb = 22 }).ToArray();
        AnalysisReport Analyze(IEnumerable<Sample> input, string? hardware = "memory-capacity-fixture") => AnalysisEngine.Analyze(input, new(), 7, null, now, hardware);
        check("Capacity shopping compares physical peaks without treating commit as installed RAM", () =>
        {
            var preview = MemoryCapacityPreview.From(Analyze(rows)) ?? throw new Exception("No memory capacity preview");
            assert(preview.Evidence.PeakGiB == 17.5 && preview.Evidence.CommitPeakGiB == 22, "Typed memory readings lost");
            assert(preview.Rows.Single(r => r.CapacityGiB == 16).DifferenceGiB == -1.5
                && preview.Rows.Single(r => r.CapacityGiB == 24).DifferenceGiB == 6.5, "Wrong capacity delta or commit used as physical peak");
            assert(preview.Rows.All(r => !r.Meaning.Contains("达标")) && MemoryCapacityPreview.Notice.Contains("不等于"), "Arithmetic promoted a candidate to a performance verdict");
        });
        check("Capacity preview does not invent readings from missing short or sparse memory coverage", () =>
        {
            foreach(var input in new[] { rows.Take(29), rows.Select(s => s with { MemoryCoverage = 1 }), rows.Select(s => s with { MemoryUsedGb = null, MemoryPeakGb = null }) })
                assert(MemoryCapacityPreview.From(Analyze(input)) == null, "Incomplete memory readings supplied a shopping baseline");
        });
        check("Capacity preview preserves unknown hardware and damaged-history boundaries", () =>
        {
            var mixed = Analyze(rows.Concat(rows.Select(s => s with { HardwareId = "other-machine" })), null);
            assert(MemoryCapacityPreview.From(mixed) == null, "Mixed machines pooled");
            var report = Analyze(rows); using var diagnostic = new HistoryDiagnostics(7); diagnostic.Add("synthetic.jsonl", 1, "invalid row");
            assert(MemoryCapacityPreview.From(report with { HistoryQuality = diagnostic.Build() }) == null, "Damaged history supplied a capacity baseline");
        });
        check("Selected-window memory peaks survive a quiet recent week in capacity comparisons", () =>
        {
            var older = rows[0] with { Time = now.AddDays(-12), MemoryPeakGb = 23.5 };
            var input = rows.Select(s => s with { MemoryUsedGb = 4, MemoryPeakGb = 4, CommitGb = 5 }).Append(older);
            var report = AnalysisEngine.Analyze(input, new(), 30, null, now, "memory-capacity-fixture");
            var preview = MemoryCapacityPreview.From(report) ?? throw new Exception("Missing selected-window capacity preview");
            assert(report.Cards.Single(c => c.Key == "memory").Observation?.State == "light" && preview.Evidence.PeakGiB == 23.5
                && preview.Evidence.FirstRecord == older.Time && preview.Evidence.Scope.Contains("30")
                && preview.Rows.Single(r => r.CapacityGiB == 16).DifferenceGiB == -7.5, "Recent light readings erased the older selected task");
            assert(MemoryCapacityPreview.From(Analyze(input))!.Evidence.PeakGiB == 4, "A seven-day selection pulled in the older out-of-window task");
        });
        check("Capacity comparisons keep arbitrary GiB inputs and reject invalid capacities", () =>
        {
            assert(MemoryCapacityPreview.Compare(40, 17.5).DifferenceGiB == 22.5 && MemoryCapacityPreview.Compare(17.5, 17.5).DifferenceLabel.Contains("相同"), "Custom capacity or equal boundary lost");
            assert(!MemoryCapacityPreview.Compare(17.5001, 17.5).DifferenceLabel.Contains("约 0 GiB"), "Small nonzero difference rounded into an equal boundary");
            foreach(double bad in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
            { bool rejected = false; try { MemoryCapacityPreview.Compare(bad, 17.5); } catch(ArgumentException) { rejected = true; } assert(rejected, "Invalid capacity accepted"); }
        });
        check("Capacity preview retains measured date range and different recorded capacities", () =>
        {
            var input = rows.Select((s, i) => s with { MemoryTotalGb = i < 15 ? 24 : 23.2 }).ToArray();
            var data = MemoryCapacityPreview.From(Analyze(input))!.Evidence;
            assert(data.MinimumObservedCapacityGiB == 23.2 && data.MaximumObservedCapacityGiB == 24
                && data.FirstRecord == rows[0].Time && data.LastRecord == rows[^1].Time, "Dates or capacity range fabricated");
        });
        check("Capacity observations export but ordinary numeric updates do not invalidate candidate scope", () =>
        {
            var report = Analyze(rows); string html = ReportExporter.Html(report, null);
            assert(html.Contains("容量与已测峰值对照") && html.Contains("24 GiB") && html.Contains("首末记录") && html.Contains("不等于实际余量"), "Report lost the actionable comparison or scope");
            var memory = report.Cards.Single(c => c.Key == "memory");
            var changed = report with { SelectedMemoryCapacity = report.SelectedMemoryCapacity! with { PeakGiB = 17.4 }, Cards = report.Cards.Select(c => c.Key == "memory" ? c with { Observation = memory.Observation! with { MemoryCapacity = memory.Observation!.MemoryCapacity! with { PeakGiB = 17.4 } } } : c).ToList() };
            assert(CandidateComparison.Stamp(report) == CandidateComparison.Stamp(changed), "Display-only arithmetic changed reviewed candidate scope");
        });
    }
}
