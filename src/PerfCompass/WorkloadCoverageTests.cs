using System.IO;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunWorkloadCoverageTests(Action<string, Action> check, Action<bool, string> assert, Func<List<Sample>> fixture)
    {
        check("Legacy global task confirmation cannot establish hardware-bound surplus", () =>
        {
            var legacy = new Settings(Representative: true);
            var report = AnalysisEngine.Analyze(fixture(), legacy, 7);
            assert(!report.Ready && !report.RepresentativeConfirmed && report.Cards.All(c => c.Status is not ("surplus" or "review")), "legacy confirmation authorized a downgrade");
            assert(legacy.Representative && legacy.RepresentativeHardwareId == "" && report.CoverageMessage.Contains("以前"), "legacy state was destroyed or unexplained");
            var pressure = AnalysisEngine.Analyze(fixture().Select(s => s with { Cpu = 99, CoreMax = 100 }), legacy, 7);
            assert(pressure.Cards.Single(c => c.Key == "cpu").Status == "pressure", "requiring confirmation hid real pressure evidence");
        });
        check("A changed hardware configuration needs its own task coverage confirmation", () =>
        {
            var settings = WorkloadCoverage.Select(new(), true, "test-machine");
            var data = fixture().Select(s => s with { HardwareId = "upgraded-machine" }).ToList();
            var report = AnalysisEngine.Analyze(data, settings, 7, hardwareId: "upgraded-machine");
            assert(!report.Ready && !report.RepresentativeConfirmed && report.CoverageMessage.Contains("已改变") && report.SampleCount == data.Count && report.ActiveHours >= 10, "old confirmation followed new hardware or history vanished");
            var confirmed = AnalysisEngine.Analyze(data, WorkloadCoverage.Select(settings, true, "upgraded-machine"), 7, hardwareId: "upgraded-machine");
            assert(confirmed.Ready && confirmed.RepresentativeConfirmed && confirmed.Cards.Single(c => c.Key == "memory").Status == "surplus", "new explicit confirmation did not restore eligible analysis");
        });
        check("Analysis isolates an explicit hardware scope and refuses unscoped mixed histories", () =>
        {
            var mine = fixture(); var other = mine.Select(s => s with { HardwareId = "other", Cpu = 99, Gpu = 99, MemoryUsedGb = 61 }).ToList();
            var settings = WorkloadCoverage.Select(new(), true, "test-machine");
            var mixed = mine.Concat(other).ToList();
            var scoped = AnalysisEngine.Analyze(mixed, settings, 7, hardwareId: "test-machine");
            assert(scoped.Ready && scoped.SampleCount == mine.Count && scoped.Cards.Single(c => c.Key == "memory").Status == "surplus", "other hardware polluted selected evidence");
            mixed.Reverse();
            assert(AnalysisEngine.Analyze(mixed, settings, 7, hardwareId: "test-machine").ActiveHours == scoped.ActiveHours, "hardware isolation depended on order");
            var unscoped = AnalysisEngine.Analyze(mixed, settings, 7);
            assert(!unscoped.Ready && unscoped.Cards.All(c => c.Status == "unknown") && unscoped.DemandTrends.Count == 0, "mixed machines manufactured capacity or demand changes");
            assert(!AnalysisEngine.Analyze(mine.Select(s => s with { HardwareId = "" }), settings, 7).Ready, "unidentified history inherited confirmation");
        });
        check("Hardware-bound confirmation round-trips without upgrading old settings implicitly", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "PerfCompass-coverage-" + Guid.NewGuid().ToString("N"));
            var store = new LocalStore(root);
            store.SaveSettings(new(Representative: true, FutureNeeds: "keep legacy description"));
            var legacy = new LocalStore(root);
            assert(legacy.Settings.Representative && !WorkloadCoverage.For(legacy.Settings, "test-machine").Confirmed && legacy.Settings.FutureNeeds.Contains("legacy"), "old settings silently became bound or lost content");
            legacy.SaveSettings(WorkloadCoverage.Select(legacy.Settings, true, "test-machine"));
            var reopened = new LocalStore(root);
            assert(WorkloadCoverage.For(reopened.Settings, "test-machine").Confirmed && !WorkloadCoverage.For(reopened.Settings, "other").Confirmed, "binding not persisted or matched another configuration");
            var cleared = WorkloadCoverage.Select(reopened.Settings, false, null);
            assert(!cleared.Representative && cleared.RepresentativeHardwareId == "", "unchecking retained active confirmation");
        });
        check("Unknown or malformed hardware identities cannot be confirmed", () =>
        {
            bool rejected = false; try { _ = WorkloadCoverage.Select(new(), true, null); } catch (ArgumentException) { rejected = true; }
            assert(rejected, "unknown hardware accepted");
            foreach (var value in new[] { new Settings { RepresentativeHardwareId = null! }, new Settings { RepresentativeHardwareId = new string('x', 201) } })
            {
                rejected = false; try { _ = SettingsFile.Encode(value); } catch (InvalidDataException) { rejected = true; }
                assert(rejected, "malformed binding accepted");
            }
        });
        check("Hardware scope and task confirmation invalidate old candidate comparison coverage", () =>
        {
            var report = AnalysisEngine.Analyze(fixture(), WorkloadCoverage.Select(new(), true, "test-machine"), 7);
            string stamp = CandidateComparison.Stamp(report);
            assert(CandidateComparison.Stamp(report with { HardwareId = "other" }) != stamp && CandidateComparison.Stamp(report with { RepresentativeConfirmed = false }) != stamp, "new hardware inherited comparison context");
            string html = ReportExporter.Html(report with { CoverageMessage = "<script>重新确认</script>" }, null);
            assert(html.Contains("&lt;script&gt;重新确认&lt;/script&gt;") && !html.Contains("<script>重新确认"), "report lost or failed to escape the coverage explanation");
        });
    }
}
