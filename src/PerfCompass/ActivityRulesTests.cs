using System.IO;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunActivityRulesTests(Action<string, Action> check, Action<bool, string> assert, Func<List<Sample>> fixture)
    {
        check("Unattended busy single-core and slow busy-disk tasks count even when whole-machine CPU is low", () =>
        {
            assert(ActivityRules.IsActive(900, 6, 100, 0, 0, 0, 0) && ActivityRules.IsActive(900, 3, 10, 0, 99, .5, .5), "observed background work was classified as idle");
            assert(ActivityRules.IsActive(double.PositiveInfinity, 0, null, null, null, 25, null), "one available high throughput reading was discarded");
        });
        check("Idle and invalid readings do not manufacture activity while recent valid input still counts", () =>
        {
            assert(!ActivityRules.IsActive(900, 1, 10, 0, 0, 0, 0) && !ActivityRules.IsActive(double.NaN, null, double.NaN, double.PositiveInfinity, -1, -1, null), "missing or invalid values became active evidence");
            assert(ActivityRules.IsActive(0, null, null, null, null, null, null) && !ActivityRules.IsActive(-1, null, null, null, null, null, null), "input age bounds changed");
        });
        check("New activity decisions retain unattended memory peaks through production merge persistence and analysis", () =>
        {
            var rows = fixture(); var prototype = rows[^1]; var now = DateTimeOffset.Now.AddMinutes(-1);
            var raw = Enumerable.Range(0, 5).Select(i => prototype with { Time = now.AddSeconds(i * 2), Seconds = 2, Cpu = 6, CoreMax = 100, MemoryUsedGb = i == 2 ? 50 : 9,
                Active = ActivityRules.IsActive(900, 6, 100, 0, 0, 0, 0), ObservationVersion = ActivityRules.Version }).ToArray();
            var bucket = SampleBatch.Merge(raw); var store = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-background-" + Guid.NewGuid().ToString("N")));
            store.Append(bucket); var saved = store.Read(7).Single();
            assert(saved.Active && saved.Seconds == 10 && saved.MemoryPeakGb == 50 && saved.CoreMax == 100 && saved.ObservationVersion == ActivityRules.Version, "background task lost elapsed time, peak, or provenance");
            string csv = Path.Combine(store.Root, "observation.csv"); ReportExporter.Csv(csv, [saved]);
            assert(File.ReadAllText(csv).Contains("observation_version") && File.ReadAllText(csv).Contains(ActivityRules.Version), "export lost observation provenance");
            var report = AnalysisEngine.Analyze(rows.Concat([saved]), WorkloadCoverage.Select(new(), true, "test-machine"), 7);
            assert(report.Cards.Single(c => c.Key == "memory").Status != "surplus" && report.ActiveHours > 10, "newly observed memory peak was ignored");
            store.Append(saved with { Time = saved.Time.AddSeconds(20), ObservationVersion = "unknown-future" });
            var unknown = HistoryAnalysis.Analyze(store, WorkloadCoverage.Select(new(), true, "test-machine"), 7, hardwareId: "test-machine");
            assert(unknown.HistoryQuality.HasIssues && unknown.SampleCount == 1 && !unknown.Ready, "unknown observation semantics entered sizing evidence");
        });
        check("Hardware-bound confirmation from the previous activity rules cannot silently authorize surplus", () =>
        {
            var legacy = new Settings(Representative: true) { RepresentativeHardwareId = "test-machine" };
            var report = AnalysisEngine.Analyze(fixture(), legacy, 7);
            assert(!report.Ready && !report.RepresentativeConfirmed && report.CoverageMessage.Contains("采集规则") && legacy.Representative,
                "old observation coverage was promoted or its original state discarded");
            var renewed = WorkloadCoverage.Select(legacy, true, "test-machine");
            assert(renewed.RepresentativeObservationVersion == ActivityRules.Version && WorkloadCoverage.For(renewed, "test-machine").Confirmed, "explicit new-rule review failed");
            var cleared = WorkloadCoverage.Select(renewed, false, null); assert(cleared.RepresentativeObservationVersion == "", "unchecking retained current-rule confirmation");
        });
        check("Observation-version binding round-trips and malformed bindings are preserved as invalid settings", () =>
        {
            var store = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-observation-version-" + Guid.NewGuid().ToString("N")));
            store.SaveSettings(WorkloadCoverage.Select(new(), true, "test-machine"));
            assert(WorkloadCoverage.For(new LocalStore(store.Root).Settings, "test-machine").Confirmed, "rule binding did not persist");
            bool invalid = false; try { SettingsFile.Encode(store.Settings with { RepresentativeObservationVersion = null! }); } catch (InvalidDataException) { invalid = true; }
            assert(invalid, "null confirmation version accepted");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new Settings(Representative: true) { RepresentativeHardwareId = "test-machine" }));
            assert(json.RootElement.GetProperty("RepresentativeObservationVersion").GetString() == "", "default binding silently became current");
        });
    }
}
