using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunValidationRunTests(Action<string, Action> check, Action<bool, string> assert)
    {
        void Reject(Action action) { bool failed = false; try { action(); } catch (ArgumentException) { failed = true; } assert(failed, "invalid validation request accepted"); }
        check("Validation modes require one bounded unambiguous duration and do not change ordinary startup", () =>
        {
            assert(ValidationRunOptions.Parse([]) == null && ValidationRunOptions.Parse(["--soak-test"])?.Seconds == 28800
                && ValidationRunOptions.Parse(["--telemetry-check"])?.Seconds == 40, "mode defaults changed");
            foreach (string value in new[] { "-1", "0", "9", "604801", "NaN", "Infinity", "20.5", "1,000", "+30" }) Reject(() => ValidationRunOptions.Parse(["--soak-test", "--validation-seconds", value]));
            Reject(() => ValidationRunOptions.Parse(["--telemetry-check", "--validation-seconds", "301"]));
            Reject(() => ValidationRunOptions.Parse(["--soak-test", "--validation-seconds"]));
            Reject(() => ValidationRunOptions.Parse(["--soak-test", "--validation-seconds", "30", "--validation-seconds", "40"]));
            foreach (string other in new[] { "--soak-test", "--telemetry-check", "--ui-test", "--smoke-test", "--diagnose", "--self-test", "--check-instance" })
                Reject(() => ValidationRunOptions.Parse(["--soak-test", other]));
        });
        check("Validation storage requires an explicit empty ordinary directory and preserves existing contents", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-validation-root-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Reject(() => ValidationRunOptions.RequireEmptyRoot(root, false)); ValidationRunOptions.RequireEmptyRoot(root, true);
            string note = Path.Combine(root, "notes.txt"); File.WriteAllText(note, "existing user bytes");
            Reject(() => ValidationRunOptions.RequireEmptyRoot(root, true)); assert(File.ReadAllText(note) == "existing user bytes" && Directory.GetFiles(root).Length == 1, "storage guard mutated existing data");
            Reject(() => ValidationRunOptions.RequireEmptyRoot(note, true));
        });
        check("Online resource summaries keep finite observations and produce stable moments without raw history", () =>
        {
            var metric = new RunningMetric(); metric.Add(double.NaN); metric.Add(double.PositiveInfinity);
            using var empty = JsonDocument.Parse(JsonSerializer.Serialize(metric.Snapshot())); assert(empty.RootElement.GetProperty("Mean").ValueKind == JsonValueKind.Null, "missing resource became zero");
            foreach (double n in new[] { 1d, 2, 3 }) metric.Add(n);
            using var value = JsonDocument.Parse(JsonSerializer.Serialize(metric.Snapshot())); var v = value.RootElement;
            assert(v.GetProperty("Count").GetInt32() == 3 && v.GetProperty("Mean").GetDouble() == 2 && v.GetProperty("StandardDeviation").GetDouble() == 1, "resource moments incorrect");
        });
        check("Soak evidence distinguishes missing GPU coverage from unavailable CPU and interrupted observation", () =>
        {
            var good = new SoakHeartbeat(30, 30, 1, 1, false, false, .5, 300, 10000000, 800, true, true, false, false);
            var evidence = new SoakEvidence(); evidence.Add(good, 30); assert(evidence.Healthy && evidence.MissingGpu == 1, "GPU absence became a zero or mandatory CPU failure");
            foreach (var bad in new[] { good with { SampleAgeSeconds = null }, good with { GapSeconds = 61 }, good with { Paused = true }, good with { CpuAvailable = false },
                good with { MemoryAvailable = false }, good with { HistoryWriteFailed = true }, good with { RetentionProtected = true } })
            { var failed = new SoakEvidence(); failed.Add(bad, 30); assert(!failed.Healthy, "incomplete or failed observation became a healthy run"); }
        });
        check("Actual validation startup refuses an existing data directory without touching it", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-validation-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string note = Path.Combine(root, "notes.txt"); File.WriteAllText(note, "keep exact bytes");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--soak-test"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(root);
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Validation guard probe did not exit."); }
            assert(process.ExitCode == 3 && Directory.GetFiles(root).Length == 1 && File.ReadAllText(note) == "keep exact bytes", "actual startup wrote to an occupied root");
        });
    }
}
