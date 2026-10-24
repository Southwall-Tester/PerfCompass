using System.IO;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

internal static class TaskInputFixtures
{
    internal const string RestoreScript = "@echo off\r\nif exist second.flag goto third\r\nif exist first.flag goto second\r\n>first.flag echo 1\r\n(echo beta)>payload.txt || exit /b 7\r\nexit /b 0\r\n:second\r\n>second.flag echo 1\r\ntype payload.txt>second-saw.txt\r\nexit /b 0\r\n:third\r\n(echo alpha)>payload.txt || exit /b 7\r\nexit /b 0\r\n";
    internal static TaskRunRequest Create(string root, string body = "@echo off\r\necho ran>sentinel.txt\r\nexit /b 0\r\n")
    {
        Directory.CreateDirectory(root);
        string exe = Path.Combine(root, "cmd.exe"); File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        string input = Path.Combine(root, "payload.txt"); File.WriteAllText(input, "alpha\r\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(root, "case.cmd"), body, Encoding.ASCII);
        return new("Synthetic input integrity", exe, "/d /q /c case.cmd", root, "Windows command processor (controlled synthetic test)", input,
            "Read or modify only isolated fixtures; not hardware performance validation", 3, false, 10);
    }
    internal static string Resource(string name)
    {
        using var stream = typeof(TaskInputFixtures).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8); return reader.ReadToEnd();
    }
    internal sealed class Progress(Action<TaskRunProgress> action) : IProgress<TaskRunProgress>
    { public void Report(TaskRunProgress value) => action(value); }
}

public static partial class SelfTests
{
    private static void RunTaskFileProtectionTests(Action<string, Action> check, Action<bool, string> assert)
    {
        TaskMachine Machine() => new("synthetic CPU", "synthetic GPU", 32, "synthetic OS", TaskMeasurements.PowerSource(), "controlled process test");
        void Fixture(Action<string> action)
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-input-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Exception? failure = null;
            try { action(root); } catch (Exception error) { failure = error; }
            try
            {
                string expectedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "PerfCompass-input-");
                if (!Path.GetFullPath(root).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(Path.GetFileName(root)["PerfCompass-input-".Length..], "N", out _)) throw new IOException("Unexpected fixture cleanup scope.");
                var cleanup = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    try { Directory.Delete(root, true); break; }
                    catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && cleanup.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(20); }
                }
            }
            catch (Exception cleanupError) { if (failure != null) throw new AggregateException(failure, cleanupError); throw; }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        void Writable(string path) { using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
        void EventuallyWritable(string path, string outcome)
        {
            // Windows may retain an executable image briefly after its root has
            // signaled exit. Do not confuse that with our own read-share guard.
            // A leaked guard still fails this bounded check; no GC is forced.
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); int denied = 0;
            while (true)
            {
                try { Writable(path); break; }
                catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && elapsed.Elapsed < TimeSpan.FromSeconds(2)) { denied++; Thread.Sleep(20); }
            }
            if (denied > 0)
            {
                Directory.CreateDirectory("artifacts");
                File.AppendAllText("artifacts/task-file-release-diagnostics.jsonl", JsonSerializer.Serialize(new { Outcome = outcome, File = Path.GetFileName(path), DeniedAttempts = denied, WaitMilliseconds = elapsed.Elapsed.TotalMilliseconds }) + Environment.NewLine);
            }
        }
        bool Blocked(Action action) { try { action(); return false; } catch (IOException) { return true; } }
        check("Selected input and executable remain readable but ordinary writes and renames are blocked through warmup and every repeat", () => Fixture(root =>
        {
            var request = TaskInputFixtures.Create(root) with { Warmup = true }; int protectedRounds = 0;
            var progress = new TaskInputFixtures.Progress(p =>
            {
                if (p.Stage != "预热运行，不计入正式结果" && !p.Stage.StartsWith("正在运行第 ", StringComparison.Ordinal)) return;
                foreach (var file in new[] { request.Executable, request.InputFile })
                {
                    assert(File.ReadAllBytes(file).Length > 0, "guard blocks normal reads");
                    assert(Blocked(() => Writable(file)) && Blocked(() => File.Move(file, file + ".moved")), "ordinary write or replacement permitted during a protected round");
                }
                protectedRounds++;
            });
            var result = TaskMeasurements.RunAsync(request, Machine(), progress, CancellationToken.None).GetAwaiter().GetResult();
            assert(result.Outcome == "completed" && protectedRounds == 4 && File.Exists(Path.Combine(root, "sentinel.txt")), "guarded real executable could not run");
            EventuallyWritable(request.Executable, "completed-warmup"); Writable(request.InputFile);
        }));
        check("Real restore-at-end input counterexample stops at the denied first write and never becomes comparable", () => Fixture(root =>
        {
            var request = TaskInputFixtures.Create(root, TaskInputFixtures.RestoreScript);
            var result = TaskMeasurements.RunAsync(request, Machine(), null, CancellationToken.None).GetAwaiter().GetResult();
            assert(result.Outcome == "failed" && result.Attempts.Count == 1 && result.Attempts[0].ExitCode == 7
                && !TaskMeasurements.Comparable(result) && !File.Exists(Path.Combine(root, "second-saw.txt")) && File.ReadAllText(request.InputFile) == "alpha\r\n", "mutating repeats completed or input changed");
            Writable(request.InputFile);
        }));
        check("A task that ignores its denied write cannot mutate the protected input and zero exit still does not verify artifacts", () => Fixture(root =>
        {
            var request = TaskInputFixtures.Create(root, "@echo off\r\n(echo beta)>payload.txt\r\nexit /b 0\r\n");
            var result = TaskMeasurements.RunAsync(request, Machine(), null, CancellationToken.None).GetAwaiter().GetResult();
            assert(result.Outcome == "completed" && File.ReadAllText(request.InputFile) == "alpha\r\n" && result.Detail.Contains("不证明任务输出正确"), "write denial changed input or zero exit claimed output correctness");
        }));
        check("An already-open writer prevents launch and partial acquisition releases the executable for retry", () => Fixture(root =>
        {
            var request = TaskInputFixtures.Create(root);
            using (var writer = new FileStream(request.InputFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                assert(Blocked(() => TaskMeasurements.RunAsync(request, Machine(), null, CancellationToken.None).GetAwaiter().GetResult()), "existing writer admitted");
                assert(!File.Exists(Path.Combine(root, "sentinel.txt")), "task launched before both guards acquired"); Writable(request.Executable);
            }
            assert(TaskMeasurements.RunAsync(request, Machine(), null, CancellationToken.None).GetAwaiter().GetResult().Outcome == "completed", "retry after writer release failed");
        }));
        check("Success failure timeout and cancellation release both task file guards", () =>
        {
            foreach (string outcome in new[] { "completed", "failed", "timeout", "cancelled" }) Fixture(root =>
            {
                string body = outcome == "completed" ? "exit /b 0\r\n" : outcome == "failed" ? "exit /b 7\r\n" : "echo started>started.flag\r\nping -n 20 127.0.0.1 >nul\r\n";
                var request = TaskInputFixtures.Create(root, body) with { TimeoutSeconds = outcome == "timeout" ? 1 : 10 };
                using var cancellation = new CancellationTokenSource();
                Task cancelAfterStart = outcome != "cancelled" ? Task.CompletedTask : Task.Run(async () =>
                {
                    for (int i = 0; i < 500 && !File.Exists(Path.Combine(root, "started.flag")); i++) await Task.Delay(10);
                    cancellation.Cancel();
                });
                var result = TaskMeasurements.RunAsync(request, Machine(), null, cancellation.Token).GetAwaiter().GetResult();
                cancelAfterStart.GetAwaiter().GetResult();
                assert(result.Outcome == outcome && result.Attempts.All(a => a.RootExited), "unexpected real process terminal state " + outcome);
                EventuallyWritable(request.Executable, outcome); Writable(request.InputFile);
                File.Move(request.InputFile, request.InputFile + ".moved");
            });
        });
        check("Cancellation before hash or launch leaves files writable and creates no task sentinel", () => Fixture(root =>
        {
            var request = TaskInputFixtures.Create(root); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { TaskMeasurements.RunAsync(request, Machine(), null, cancellation.Token).GetAwaiter().GetResult(); assert(false, "pre-cancelled task started"); }
            catch (OperationCanceledException) { }
            assert(!File.Exists(Path.Combine(root, "sentinel.txt")), "cancelled preflight executed task"); Writable(request.Executable); Writable(request.InputFile);
        }));
        check("Actual old-writer measurement retains its legacy fingerprint and raw rounds but cannot assert a protected task pass", () => Fixture(root =>
        {
            string raw = TaskInputFixtures.Resource("PerfCompass.LegacyInputMeasurement.json"); string path = Path.Combine(root, "old.json"); File.WriteAllText(path, raw);
            var old = TaskMeasurements.Load(path); string fingerprint = JsonDocument.Parse(raw).RootElement.GetProperty("ProtocolFingerprint").GetString()!;
            var goal = new PurchaseCriterion { Metric = "task", Unit = "秒", Target = 999, Direction = "max", MeasurementProtocol = old.ProtocolFingerprint };
            assert(old.Version == TaskMeasurements.LegacyVersion && old.ProtocolFingerprint == fingerprint && old.Attempts.Count == 3
                && !TaskMeasurements.Comparable(old) && TaskMeasurements.Assess(old, goal, old.MedianSeconds).State == "unknown" && TaskMeasurements.Evidence(old).Contains("旧方法"), "legacy record promoted to current protection");
            string saved = Path.Combine(root, "preserved.json"); TaskMeasurements.Save(saved, old);
            assert(TaskMeasurements.Load(saved).Version == TaskMeasurements.LegacyVersion && File.ReadAllText(path) == raw, "legacy result version or original evidence rewritten");
        }));
        check("Actual legacy plan and measurement import reuse only task settings after validating the original fingerprint", () => Fixture(root =>
        {
            foreach (string resource in new[] { "PerfCompass.LegacyInputPlan.json", "PerfCompass.LegacyInputMeasurement.json" })
            {
                string raw = TaskInputFixtures.Resource(resource), path = Path.Combine(root, resource + ".json"); File.WriteAllText(path, raw);
                var plan = TaskPlans.Load(path); string oldFingerprint = JsonDocument.Parse(raw).RootElement.GetProperty("ProtocolFingerprint").GetString()!;
                assert(plan.Version == TaskPlans.Version && plan.UpgradedFromLegacy && plan.ProtocolFingerprint != oldFingerprint
                    && File.ReadAllText(path) == raw, "legacy method silently retained or source modified");
                string saved = Path.Combine(root, "new-plan.json"); TaskPlans.Save(saved, plan);
                var reopened = TaskPlans.Load(saved);
                assert(reopened.Recipe == plan.Recipe && reopened.ProtocolFingerprint == plan.ProtocolFingerprint && !reopened.UpgradedFromLegacy, "upgraded plan changed task settings or current protocol");
                File.WriteAllText(path, raw.Replace(oldFingerprint, new string('F', 64)));
                try { TaskPlans.Load(path); assert(false, "bad original fingerprint accepted after upgrade"); } catch (ArgumentException) { }
            }
        }));
        check("Measurement method is part of the protocol while legacy fingerprints and original task fields remain stable", () =>
        {
            var current = MeasurementFixture(1, 2, 3); var old = current with { Version = TaskMeasurements.LegacyVersion };
            assert(current.ProtocolFingerprint != old.ProtocolFingerprint && old.ProtocolFingerprint == TaskMeasurements.Fingerprint(old.Recipe, old.Machine, TaskMeasurements.LegacyVersion), "method not bound into protocol");
            assert(TaskMeasurements.Assess(old, new() { Metric = "task", Unit = "秒", Target = 10, MeasurementProtocol = old.ProtocolFingerprint }, old.MedianSeconds).State == "unknown", "legacy elapsed values used as current guarded evidence");
        });
        check("A stale serialized measurement fingerprint or duplicate version cannot silently relabel old protection", () => Fixture(root =>
        {
            string raw = TaskInputFixtures.Resource("PerfCompass.LegacyInputMeasurement.json"), path = Path.Combine(root, "changed.json");
            File.WriteAllText(path, raw.Replace(TaskMeasurements.LegacyVersion, TaskMeasurements.Version));
            try { TaskMeasurements.Load(path); assert(false, "stale encoded protocol accepted after method relabel"); } catch (ArgumentException) { }
            File.WriteAllText(path, raw.Replace("{", "{\"Version\":\"" + TaskMeasurements.Version + "\","));
            try { TaskMeasurements.Load(path); assert(false, "duplicate members accepted"); } catch (ArgumentException) { }
        }));
        check("A directly supplied legacy plan cannot execute without current-method import and explicit environment review", () => Fixture(root =>
        {
            var request = TaskInputFixtures.Create(root); var machine = Machine(); var plan = TaskPlans.CreateAsync(request, machine, CancellationToken.None).GetAwaiter().GetResult();
            try { TaskMeasurements.RunAsync(request, machine, null, CancellationToken.None, plan with { Version = TaskPlans.LegacyVersion }).GetAwaiter().GetResult(); assert(false, "legacy plan executed directly"); }
            catch (ArgumentException) { }
            assert(!File.Exists(Path.Combine(root, "sentinel.txt")), "legacy plan rejection followed execution"); Writable(request.InputFile); Writable(request.Executable);
        }));
    }
}
