using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    internal static TaskMeasurementResult MeasurementFixture(params double[] seconds)
    {
        return new(TaskMeasurements.Version, DateTimeOffset.Now,
            new("合成重复任务", "fixture.exe", new string('A', 64), "--fixture", "fixture-1", "input.txt", new string('B', 64), "同一输入与软件设置", seconds.Length, false, 60),
            new("fixture CPU", "fixture GPU", 32, "fixture OS", "Online", "合成环境"),
            seconds.Select((s, i) => new TaskAttempt(i + 1, false, s, 0, "completed", true, "Online", "Online")).ToList(), "completed", "合成数据，非实际硬件性能测量");
    }
    private static void RunTaskMeasurementTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var machine = new TaskMachine("fixture CPU", "fixture GPU", 32, "fixture OS", TaskMeasurements.PowerSource(), "进程生命周期测试，非性能比较");
        TaskRunRequest Request(string body, int timeout = 5)
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-task-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string input = Path.Combine(root, "task.cmd"); File.WriteAllText(input, "@echo off\r\n" + body, Encoding.ASCII);
            return new("进程生命周期验证", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /q /c task.cmd", root, "Windows cmd fixture", input, "受控开发测试，不衡量硬件性能", 3, false, timeout);
        }
        PurchaseCriterion Goal(TaskMeasurementResult result, double target = 12) => new() { Name = "任务耗时", Metric = "task", Unit = "秒", Target = target, Direction = "max", Procedure = result.Recipe.Procedure, MeasurementProtocol = result.ProtocolFingerprint, Reviewed = true };
        check("Repeated measurements use every run at the target boundary rather than median alone", () =>
        {
            var data = MeasurementFixture(9, 10, 14); var goal = Goal(data);
            assert(TaskMeasurements.Assess(data, goal, 10).State == "unknown", "median promoted a threshold-crossing trace");
            assert(TaskMeasurements.Assess(MeasurementFixture(8, 9, 10), goal, 9).State == "pass", "all passing runs rejected");
            assert(TaskMeasurements.Assess(MeasurementFixture(13, 14, 15), goal, 14).State == "fail", "all failing runs ignored");
            goal.Direction = "min"; goal.Target = 10;
            assert(TaskMeasurements.Assess(MeasurementFixture(9, 10, 11), goal, 10).State == "unknown", "lower bound variation ignored");
        });
        check("Measurement protocol matches software input and environment while allowing different CPUs", () =>
        {
            var data = MeasurementFixture(8, 9, 10);
            assert(data.ProtocolFingerprint == (data with { Machine = data.Machine with { Cpu = "another CPU", Gpu = "another GPU", MemoryGiB = 64 } }).ProtocolFingerprint, "hardware change blocked the same task protocol");
            assert(data.ProtocolFingerprint != (data with { Recipe = data.Recipe with { ExecutableSha256 = new string('C', 64) } }).ProtocolFingerprint, "changed executable not detected");
            assert(data.ProtocolFingerprint != (data with { Recipe = data.Recipe with { InputSha256 = new string('D', 64) } }).ProtocolFingerprint, "changed input not detected");
            assert(data.ProtocolFingerprint != (data with { Machine = data.Machine with { PowerProfile = "other mode" } }).ProtocolFingerprint, "power mode change ignored");
        });
        check("Edited measurement values and mismatched protocols remain unknown", () =>
        {
            var data = MeasurementFixture(8, 9, 10); var goal = Goal(data);
            assert(TaskMeasurements.Assess(data, goal, 7).State == "unknown", "edited median accepted");
            goal.Unit = "ms"; goal.Target = 12000;
            assert(TaskMeasurements.Assess(data, goal, 9000).State == "pass", "millisecond normalization wrong");
            goal.MeasurementProtocol = new string('F', 64);
            assert(TaskMeasurements.Assess(data, goal, 9000).State == "unknown", "foreign protocol accepted");
        });
        check("Unknown power changed power failed and incomplete sessions cannot become comparable", () =>
        {
            var data = MeasurementFixture(8, 9, 10);
            assert(!TaskMeasurements.Comparable(data with { Machine = data.Machine with { PowerSource = "Unknown" } }), "missing power inferred");
            var attempts = data.Attempts.ToList(); attempts[1] = attempts[1] with { PowerAfter = "Offline" };
            assert(!TaskMeasurements.Comparable(data with { Attempts = attempts }), "power change ignored");
            assert(!TaskMeasurements.Comparable(data with { Outcome = "failed" }), "failed session passed");
            bool rejected = false; try { TaskMeasurements.Validate(data with { Attempts = data.Attempts.Take(2).ToList() }); } catch (ArgumentException) { rejected = true; }
            assert(rejected, "incomplete session accepted");
        });
        check("Measurement validation rejects nonfinite times contradictory exits and oversized fields", () =>
        {
            var data = MeasurementFixture(8, 9, 10);
            void Reject(TaskMeasurementResult value) { bool rejected = false; try { TaskMeasurements.Validate(value); } catch (ArgumentException) { rejected = true; } assert(rejected, "invalid measurement accepted"); }
            var attempts = data.Attempts.ToList(); attempts[0] = attempts[0] with { Seconds = double.NaN }; Reject(data with { Attempts = attempts });
            attempts = data.Attempts.ToList(); attempts[0] = attempts[0] with { RootExited = false }; Reject(data with { Attempts = attempts });
            attempts = data.Attempts.ToList(); attempts[0] = attempts[0] with { ExitCode = 7 }; Reject(data with { Attempts = attempts });
            Reject(data with { Recipe = data.Recipe with { Arguments = new string('a', 4001) } });
        });
        check("Imported repeated evidence participates in purchase decisions without accepting manual substitutes", () =>
        {
            var data = MeasurementFixture(9, 10, 14); var goal = Goal(data);
            var report = new AnalysisReport(DateTimeOffset.Now, 7, 0, 0, 0, false, [], [], [], "");
            var candidate = new PurchaseCandidate { Name = "测试候选", Source = "合成报价", PriceCny = 8000 };
            var observation = new PurchaseObservation { CandidateId = candidate.Id, CriterionId = goal.Id, Value = 10, Evidence = TaskMeasurements.Evidence(data), Measurement = data, Comparable = true, ProofStamp = CandidateComparison.ObservationStamp(candidate, goal) };
            var workspace = new PurchaseWorkspace { ContextStamp = CandidateComparison.Stamp(report), CoverageConfirmed = true, Candidates = [candidate], Criteria = [goal], Observations = [observation] };
            assert(CandidateComparison.Evaluate(workspace, report).Candidates.Single().State == "unknown", "median bypassed repeated range");
            Directory.CreateDirectory("artifacts");
            File.WriteAllText("artifacts/task-comparison-report.html", CandidateComparison.Html(workspace, CandidateComparison.Evaluate(workspace, report)), new UTF8Encoding(false));
            observation.Measurement = null;
            assert(CandidateComparison.Evaluate(workspace, report).Candidates.Single().State == "unknown", "manual value replaced protocol-bound evidence");
        });
        check("Legacy comparison review stamps remain unchanged when no measurement protocol is bound", () =>
        {
            var goal = new PurchaseCriterion { Name = "旧目标", Metric = "memory", Target = 32, Unit = "GB" };
            string previous = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { goal.Name, goal.Metric, goal.Direction, goal.Target, goal.Unit, goal.Condition, goal.Evidence, goal.Procedure }))));
            assert(previous == CandidateComparison.CriterionStamp(goal), "upgrade invalidated all existing reviews");
        });
        check("Measurement files round-trip as data and do not run stored arguments", () =>
        {
            var request = Request("exit /b 0\r\n"); string sentinel = Path.Combine(request.WorkingDirectory, "must-not-exist.txt");
            var data = MeasurementFixture(8, 9, 10); data = data with { Recipe = data.Recipe with { Arguments = "/c echo unsafe > must-not-exist.txt" } };
            string path = Path.Combine(request.WorkingDirectory, "result.json"); TaskMeasurements.Save(path, data); var reopened = TaskMeasurements.Load(path);
            assert(reopened.ProtocolFingerprint == data.ProtocolFingerprint && reopened.MedianSeconds == 9 && !File.Exists(sentinel), "file loading ran a task or lost evidence");
        });
        check("Measurement save failures preserve the target and other writers' temporary files before a successful retry", () =>
        {
            var request = Request("exit /b 0\r\n"); string path = Path.Combine(request.WorkingDirectory, "result.json");
            var original = MeasurementFixture(8, 9, 10); TaskMeasurements.Save(path, original);
            byte[] bytes = File.ReadAllBytes(path);
            string foreignTemporary = path + ".tmp"; File.WriteAllText(foreignTemporary, "another writer's file", new UTF8Encoding(false));
            var replacement = MeasurementFixture(5, 6, 7); bool failed = false;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { TaskMeasurements.Save(path, replacement); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
                assert(failed && File.ReadAllBytes(path).SequenceEqual(bytes), "failed measurement replacement modified the existing target");
            }
            assert(Directory.GetFiles(request.WorkingDirectory, "result.json.*.tmp").Length == 0
                && File.ReadAllText(foreignTemporary) == "another writer's file",
                "measurement failure left its own temporary or overwrote another writer's fixed .tmp file");
            TaskMeasurements.Save(path, replacement);
            var restored = TaskMeasurements.Load(path);
            assert(restored.Version == TaskMeasurements.Version && restored.ProtocolFingerprint == replacement.ProtocolFingerprint
                && restored.MedianSeconds == 6 && restored.Attempts.SequenceEqual(replacement.Attempts)
                && Directory.GetFiles(request.WorkingDirectory, "result.json.*.tmp").Length == 0
                && File.ReadAllText(foreignTemporary) == "another writer's file",
                "successful retry did not publish the complete replacement or interfered with an unrelated temporary");
        });
        check("Legacy workspace upgrades preserve reviewed data and save a schema older clients reject", () =>
        {
            var goal = new PurchaseCriterion { Name = "旧内存目标", Metric = "memory", Target = 32, Unit = "GB", Reviewed = true };
            var workspace = new PurchaseWorkspace { Version = "comparison-1.0", Criteria = [goal] };
            var request = Request("exit /b 0\r\n"); string path = Path.Combine(request.WorkingDirectory, "comparison.json");
            File.WriteAllText(path, JsonSerializer.Serialize(workspace));
            var upgraded = CandidateComparison.Load(request.WorkingDirectory);
            assert(upgraded.Version == "comparison-1.4" && upgraded.Criteria[0].Reviewed && upgraded.Criteria[0].ReviewStamp == CandidateComparison.CriterionStamp(goal), "legacy review lost");
            CandidateComparison.Save(request.WorkingDirectory, upgraded);
            assert(File.ReadAllText(path).Contains("comparison-1.4"), "old client could silently drop repeated evidence on save");
        });
        check("Real task process drains large stdout and stderr without retaining their contents", () =>
        {
            var request = Request("for /l %%i in (1,1,1000) do (\r\necho synthetic-output-%%i\r\necho synthetic-error-%%i 1>&2\r\n)\r\nexit /b 0\r\n");
            var result = TaskMeasurements.RunAsync(request, machine, null, CancellationToken.None).GetAwaiter().GetResult();
            TaskMeasurements.Validate(result);
            assert(result.Outcome == "completed" && result.Attempts.Count == 3 && result.Attempts.All(a => a.RootExited), "stream handling did not complete");
            assert(!JsonSerializer.Serialize(result).Contains("synthetic-output-"), "stdout retained in measurement");
        });
        check("Real nonzero task exit prevents subsequent repeats and usable completion", () =>
        {
            var result = TaskMeasurements.RunAsync(Request("exit /b 7\r\n"), machine, null, CancellationToken.None).GetAwaiter().GetResult();
            assert(result.Outcome == "failed" && result.Attempts.Count == 1 && result.Attempts[0].ExitCode == 7, "failure promoted to completed measurement");
        });
        check("Real task timeout stops its root and leaves the session unusable", () =>
        {
            var result = TaskMeasurements.RunAsync(Request("ping -n 20 127.0.0.1 >nul\r\n", 1), machine, null, CancellationToken.None).GetAwaiter().GetResult();
            assert(result.Outcome == "timeout" && result.Attempts.Count == 1 && result.Attempts[0].RootExited, "timed out root remains running or result passed");
        });
        check("Real task cancellation stops its root rather than reporting a successful short run", () =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = TaskMeasurements.RunAsync(Request("ping -n 20 127.0.0.1 >nul\r\n", 10), machine, null, cancellation.Token).GetAwaiter().GetResult();
            assert(result.Outcome == "cancelled" && result.Attempts.Count == 1 && result.Attempts[0].RootExited, "cancelled task passed or did not stop");
        });
        check("Selected batch input cannot change even when a successful root ignores write denial", () =>
        {
            var request = Request("echo rem changed>> task.cmd\r\nexit /b 0\r\n");
            byte[] original = File.ReadAllBytes(request.InputFile);
            var result = TaskMeasurements.RunAsync(request, machine, null, CancellationToken.None).GetAwaiter().GetResult();
            assert(result.Outcome == "completed" && original.SequenceEqual(File.ReadAllBytes(request.InputFile)) && result.Detail.Contains("不证明任务输出正确"), "input changed or zero exit proved output correctness");
        });
    }
}
