using System.IO;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunTaskPlanTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var machine = new TaskMachine("plan fixture CPU", "plan fixture GPU", 32, "plan fixture OS", "Online", "方案回归环境，非性能基准");
        string DirectoryForTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-task-plan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); return root;
        }
        TaskRunRequest Request(string? directory = null)
        {
            string root = directory ?? DirectoryForTest(); Directory.CreateDirectory(root);
            string input = Path.Combine(root, "task.cmd");
            File.WriteAllText(input, "@echo off\r\necho executed>> sentinel.txt\r\nexit /b 0\r\n", Encoding.ASCII);
            return new("跨机方案回归", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /q /c task.cmd", root,
                "Windows command fixture", input, "受控开发进程；只验证方案复用与运行入口，不衡量硬件性能。", 3, false, 5);
        }
        TaskPlan Create(TaskRunRequest request, TaskMachine? current = null)
            => TaskPlans.CreateAsync(request, current ?? machine, CancellationToken.None).GetAwaiter().GetResult();
        void Reject(Action action, string fragment = "")
        {
            string? message = null;
            try { action(); } catch (ArgumentException error) { message = error.Message; }
            assert(message != null && (fragment.Length == 0 || message.Contains(fragment)), "invalid plan was accepted or the mismatch lacked an actionable field: " + fragment);
        }
        void RejectedBeforeStart(TaskRunRequest request, TaskPlan plan, string field, TaskMachine? current = null)
        {
            Reject(() => TaskMeasurements.RunAsync(request, current ?? machine, null, CancellationToken.None, plan).GetAwaiter().GetResult(), field);
            assert(!File.Exists(Path.Combine(request.WorkingDirectory, "sentinel.txt")), "a plan mismatch launched the executable before validation");
        }

        check("Task plans roundtrip the existing protocol without selected absolute paths or machine/result identity", () =>
        {
            var request = Request(); var plan = Create(request);
            string path = Path.Combine(request.WorkingDirectory, "plan.json"); TaskPlans.Save(path, plan);
            var reopened = TaskPlans.Load(path);
            assert(reopened == plan && reopened.Version == "task-plan-1.1"
                && reopened.ProtocolFingerprint == TaskMeasurements.Fingerprint(plan.Recipe, machine),
                "plan serialization changed the recipe, environment or existing measurement fingerprint");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path)); var root = document.RootElement;
            assert(!root.TryGetProperty("Machine", out _) && !root.TryGetProperty("Attempts", out _)
                && !root.TryGetProperty("Outcome", out _) && !root.TryGetProperty("MedianSeconds", out _)
                && !root.TryGetProperty("WorkingDirectory", out _) && !root.TryGetProperty("Executable", out _)
                && !root.TryGetProperty("InputFile", out _), "a portable plan serialized local paths or a completed result");
            var recipe = root.GetProperty("Recipe");
            assert(recipe.GetProperty("ExecutableName").GetString() == Path.GetFileName(request.Executable)
                && recipe.GetProperty("InputName").GetString() == Path.GetFileName(request.InputFile)
                && !File.ReadAllText(path).Contains(machine.Cpu) && !File.ReadAllText(path).Contains(machine.OperatingSystem),
                "portable file names became full local paths or machine identity leaked into the plan");
        });
        check("Creating saving and importing a plan only hash or read data and never execute stored arguments", () =>
        {
            var request = Request(); string sentinel = Path.Combine(request.WorkingDirectory, "sentinel.txt");
            // The argument names an absolute sentinel so an accidental launch
            // cannot hide by inheriting a different current working directory.
            request = request with { Arguments = "/d /q /c echo imported> \"" + sentinel + "\"" };
            var plan = Create(request); string path = Path.Combine(request.WorkingDirectory, "plan.json");
            TaskPlans.Save(path, plan); _ = TaskPlans.Load(path);
            assert(!File.Exists(sentinel), "plan preparation or import executed the selected command");
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool cancelled = false;
            try { _ = TaskPlans.CreateAsync(request, machine, cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && !File.Exists(sentinel), "cancelled plan preparation ignored cancellation or started a process");
        });
        check("Moving identical program and input files to another directory or hardware retains the same protocol", () =>
        {
            var first = Request(); var second = Request();
            string firstExe = Path.Combine(first.WorkingDirectory, "cmd.exe"), secondExe = Path.Combine(second.WorkingDirectory, "cmd.exe");
            File.Copy(first.Executable, firstExe); File.Copy(second.Executable, secondExe);
            first = first with { Executable = firstExe }; second = second with { Executable = secondExe };
            var plan = Create(first);
            var differentMachine = machine with { Cpu = "other CPU", Gpu = "other GPU", MemoryGiB = 64, OperatingSystem = "other OS" };
            var relocated = Create(second, differentMachine); TaskPlans.Verify(plan, relocated.Recipe, differentMachine);
            assert(first.WorkingDirectory != second.WorkingDirectory && first.Executable != second.Executable
                && first.InputFile != second.InputFile && plan.ProtocolFingerprint == relocated.ProtocolFingerprint,
                "machine identity or selected absolute paths prevented the same task protocol from being reused");
            assert(!File.Exists(Path.Combine(first.WorkingDirectory, "sentinel.txt")) && !File.Exists(Path.Combine(second.WorkingDirectory, "sentinel.txt")),
                "relocating a plan ran either machine's task");
        });
        check("Every protocol field reports its own mismatch including filenames hashes and declared power", () =>
        {
            var plan = Create(Request()); var r = plan.Recipe;
            foreach (var change in new (TaskRecipe Recipe, string Field)[]
            {
                (r with { Name = r.Name + "不同" }, "任务名称"), (r with { ExecutableName = "different.exe" }, "程序文件名"),
                (r with { ExecutableSha256 = new string('A', 64) }, "程序文件内容"), (r with { Arguments = r.Arguments + " " }, "参数"),
                (r with { SoftwareVersion = r.SoftwareVersion + "不同" }, "软件版本"), (r with { InputName = "different.cmd" }, "输入文件名"),
                (r with { InputSha256 = new string('B', 64) }, "输入文件内容"), (r with { Procedure = r.Procedure + "不同" }, "统一步骤"),
                (r with { Repeats = 4 }, "正式重复次数"), (r with { Warmup = true }, "预热设置"), (r with { TimeoutSeconds = 6 }, "单轮超时")
            }) Reject(() => TaskPlans.Verify(plan, change.Recipe, machine), change.Field);
            Reject(() => TaskPlans.Verify(plan, r, machine with { PowerSource = "Offline" }), "插电 / 电池状态");
            Reject(() => TaskPlans.Verify(plan, r, machine with { PowerSource = "Unknown" }), "插电 / 电池状态");
            Reject(() => TaskPlans.Verify(plan, r, machine with { PowerProfile = "另一性能模式" }), "电源 / 性能模式");
        });
        check("Changed run settings and power are rejected before any real process can create its sentinel", () =>
        {
            var request = Request(); var plan = Create(request);
            foreach (var change in new (TaskRunRequest Request, string Field)[]
            {
                (request with { Name = "新任务" }, "任务名称"), (request with { Arguments = request.Arguments + " " }, "参数"),
                (request with { SoftwareVersion = "新版本" }, "软件版本"), (request with { Procedure = "新步骤" }, "统一步骤"),
                (request with { Repeats = 4 }, "正式重复次数"), (request with { Warmup = true }, "预热设置"),
                (request with { TimeoutSeconds = 6 }, "单轮超时")
            }) RejectedBeforeStart(change.Request, plan, change.Field);
            RejectedBeforeStart(request, plan, "插电 / 电池状态", machine with { PowerSource = "Offline" });
            RejectedBeforeStart(request, plan, "插电 / 电池状态", machine with { PowerSource = "Unknown" });
            RejectedBeforeStart(request, plan, "电源 / 性能模式", machine with { PowerProfile = "不同声明" });
        });
        check("Changed or renamed local files are detected in the actual pre-run hash step before execution", () =>
        {
            var request = Request(); var plan = Create(request);
            string renamedInput = Path.Combine(request.WorkingDirectory, "renamed.cmd"); File.Copy(request.InputFile, renamedInput);
            RejectedBeforeStart(request with { InputFile = renamedInput }, plan, "输入文件名");
            string renamedExe = Path.Combine(request.WorkingDirectory, "renamed.exe"); File.Copy(request.Executable, renamedExe);
            RejectedBeforeStart(request with { Executable = renamedExe }, plan, "程序文件名");
            byte[] originalInput = File.ReadAllBytes(request.InputFile);
            File.AppendAllText(request.InputFile, "rem changed\r\n", Encoding.ASCII);
            RejectedBeforeStart(request, plan, "输入文件内容"); File.WriteAllBytes(request.InputFile, originalInput);
            string changedExe = Path.Combine(request.WorkingDirectory, "cmd.exe"); File.Copy(request.Executable, changedExe);
            using (var append = new FileStream(changedExe, FileMode.Append, FileAccess.Write, FileShare.None)) append.WriteByte(0);
            RejectedBeforeStart(request with { Executable = changedExe }, plan, "程序文件内容");
        });
        check("An explicitly bound matching run uses the same fingerprint and explicit unbinding permits a new protocol", () =>
        {
            var request = Request(); var plan = Create(request);
            var bound = TaskMeasurements.RunAsync(request, machine, null, CancellationToken.None, plan).GetAwaiter().GetResult();
            string sentinel = Path.Combine(request.WorkingDirectory, "sentinel.txt");
            assert(bound.Outcome == "completed" && bound.Attempts.Count == 3 && bound.ProtocolFingerprint == plan.ProtocolFingerprint
                && File.ReadAllLines(sentinel).Length == 3, "an explicitly run matching plan did not execute exactly its declared formal repeats");
            // A new directory and an omitted expectedPlan simulate the explicit
            // UI action to detach the old plan; they never reuse prior results.
            var changed = Request() with { Name = "明确解除绑定后的新任务", Procedure = "新任务步骤" };
            var unbound = TaskMeasurements.RunAsync(changed, machine, null, CancellationToken.None).GetAwaiter().GetResult();
            assert(unbound.Outcome == "completed" && unbound.ProtocolFingerprint != plan.ProtocolFingerprint
                && File.ReadAllLines(Path.Combine(changed.WorkingDirectory, "sentinel.txt")).Length == 3,
                "explicitly detached execution silently retained the old bound protocol or stale results");
        });
        check("Legacy measurement files can supply a plan without importing attempts outcomes or machine identity", () =>
        {
            string root = DirectoryForTest(); string source = Path.Combine(root, "measurement.json");
            var measured = MeasurementFixture(1, 2, 3); TaskMeasurements.Save(source, measured);
            var plan = TaskPlans.Load(source);
            assert(plan.Recipe == measured.Recipe && plan.ProtocolFingerprint == measured.ProtocolFingerprint
                && plan.PowerSource == measured.Machine.PowerSource && plan.PowerProfile == measured.Machine.PowerProfile,
                "legacy measurement conversion changed its protocol");
            string target = Path.Combine(root, "portable.json"); TaskPlans.Save(target, plan);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(target));
            assert(!doc.RootElement.TryGetProperty("Attempts", out _) && !doc.RootElement.TryGetProperty("Outcome", out _)
                && !doc.RootElement.TryGetProperty("Machine", out _) && !File.ReadAllText(target).Contains(measured.Machine.Cpu),
                "loading a recipe reused successful results or machine identity on another computer");
            TaskMeasurements.Save(source, measured with { Outcome = "failed" });
            assert(TaskPlans.Load(source).ProtocolFingerprint == plan.ProtocolFingerprint,
                "a structurally valid failed session could not supply its recipe independently of its unusable result");
        });
        check("Unknown power cannot become a portable comparable plan while the legacy measurement reader stays unchanged", () =>
        {
            var request = Request(); var plan = Create(request);
            Reject(() => TaskPlans.Validate(plan with { PowerSource = "Unknown" }), "电源状态");
            Reject(() => Create(request, machine with { PowerSource = "Unknown" }), "电源状态");
            string path = Path.Combine(request.WorkingDirectory, "unknown-power.json");
            var measured = MeasurementFixture(1, 2, 3); measured = measured with { Machine = measured.Machine with { PowerSource = "Unknown" } };
            TaskMeasurements.Save(path, measured);
            assert(TaskMeasurements.Load(path).Machine.PowerSource == "Unknown", "plan validation changed existing measurement schema handling");
            Reject(() => TaskPlans.Load(path), "电源状态");
        });
        check("Plan import preserves exact whitespace and every already-supported repeat strategy", () =>
        {
            var request = Request();
            foreach (int repeats in Enumerable.Range(3, 7))
            {
                var current = machine with { PowerProfile = "  declared mode  " };
                var plan = Create(request with { Name = "  task name  ", Arguments = "  /d /q /c task.cmd  ", SoftwareVersion = "  version  ", Procedure = "  same steps  ", Repeats = repeats, Warmup = repeats % 2 == 0 }, current);
                string path = Path.Combine(request.WorkingDirectory, "strategy-" + repeats + ".json"); TaskPlans.Save(path, plan);
                var reopened = TaskPlans.Load(path); TaskPlans.Verify(plan, reopened.Recipe, current);
                assert(reopened == plan && reopened.Recipe.Repeats == repeats && reopened.PowerProfile == current.PowerProfile,
                    "plan import trimmed a hashed field or silently selected a different repeat/warmup strategy");
            }
        });
        check("Plan loading rejects oversized deep duplicated incomplete and inconsistent protocol documents", () =>
        {
            var request = Request(); var plan = Create(request); string path = Path.Combine(request.WorkingDirectory, "invalid.json");
            string json = JsonSerializer.Serialize(plan);
            void RejectFile(string content, string fragment = "") { File.WriteAllText(path, content, new UTF8Encoding(false)); Reject(() => TaskPlans.Load(path), fragment); }
            RejectFile(json.Replace("\"Repeats\":3", "\"Repeats\":3,\"Repeats\":5", StringComparison.Ordinal), "重复字段");
            RejectFile(json.Replace("\"Warmup\":false,", "", StringComparison.Ordinal), "字段不完整");
            RejectFile(json[..^1] + ",\"Machine\":{}}", "不支持的字段");
            RejectFile(json.Replace(TaskPlans.Version, "task-plan-99", StringComparison.Ordinal), "不支持此文件版本");
            RejectFile(json.Replace(plan.ProtocolFingerprint, new string('F', 64), StringComparison.Ordinal), "指纹");
            File.WriteAllBytes(path, new byte[TaskPlans.MaximumFileBytes + 1]); Reject(() => TaskPlans.Load(path), "128 KiB");
            File.WriteAllText(path, "{\"Version\":\"task-plan-1.0\",\"Nested\":" + new string('[', 20) + "0" + new string(']', 20) + "}");
            bool rejected = false; try { TaskPlans.Load(path); } catch (JsonException) { rejected = true; }
            assert(rejected, "deep input bypassed the bounded JSON parser");
        });
        check("Failed plan saves preserve the existing target and never reuse another writer's temporary file", () =>
        {
            var request = Request(); var plan = Create(request); string path = Path.Combine(request.WorkingDirectory, "plan.json");
            TaskPlans.Save(path, plan); byte[] original = File.ReadAllBytes(path);
            string foreignTemporary = path + ".tmp"; File.WriteAllText(foreignTemporary, "another writer's file");
            var changed = plan with { Recipe = plan.Recipe with { Name = "另一个有效方案" } };
            bool refused = false;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { TaskPlans.Save(path, changed); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
                assert(refused && File.ReadAllBytes(path).SequenceEqual(original), "failed replacement damaged an existing plan");
            }
            Reject(() => TaskPlans.Save(path, plan with { PowerSource = "Unknown" }), "电源状态");
            assert(File.ReadAllBytes(path).SequenceEqual(original) && Directory.GetFiles(request.WorkingDirectory, "plan.json.*.tmp").Length == 0
                && File.ReadAllText(foreignTemporary) == "another writer's file", "failed save published partial content, leaked its own temporary or removed an unrelated file");
            TaskPlans.Save(path, changed);
            assert(TaskPlans.Load(path) == changed && File.ReadAllText(foreignTemporary) == "another writer's file",
                "successful plan publication failed to replace the target independently of an existing .tmp path");
        });
    }
}
