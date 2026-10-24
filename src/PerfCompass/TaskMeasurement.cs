using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public record TaskMachine(string Cpu, string Gpu, double? MemoryGiB, string OperatingSystem, string PowerSource, string PowerProfile);
public record TaskRecipe(string Name, string ExecutableName, string ExecutableSha256, string Arguments, string SoftwareVersion,
    string InputName, string InputSha256, string Procedure, int Repeats, bool Warmup, int TimeoutSeconds);
public record TaskAttempt(int Number, bool Warmup, double Seconds, int? ExitCode, string Outcome, bool RootExited, string PowerBefore, string PowerAfter);
public record TaskMeasurementResult(string Version, DateTimeOffset Started, TaskRecipe Recipe, TaskMachine Machine, List<TaskAttempt> Attempts, string Outcome, string Detail)
{
    public string ProtocolFingerprint => TaskMeasurements.Fingerprint(Recipe, Machine, Version);
    public double? MedianSeconds => Attempts.Any(a => !a.Warmup && a.Outcome == "completed") ? AnalysisEngine.Percentile(Attempts.Where(a => !a.Warmup && a.Outcome == "completed").Select(a => a.Seconds).ToList(), .5) : null;
    public string Summary => $"{Recipe.Name} · {TaskMeasurements.OutcomeLabel(Outcome)} · 正式 {Attempts.Count(a => !a.Warmup && a.Outcome == "completed")}/{Recipe.Repeats} 次 · 中位数 {(MedianSeconds?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—")} 秒";
}
public record TaskRunRequest(string Name, string Executable, string Arguments, string WorkingDirectory, string SoftwareVersion,
    string InputFile, string Procedure, int Repeats = 3, bool Warmup = true, int TimeoutSeconds = 120);
public record TaskRunProgress(string Stage, int Finished, int Total, double? LastSeconds = null);

public static class TaskMeasurements
{
    public const string Version = "task-measurement-1.1";
    public const string LegacyVersion = "task-measurement-1.0";
    public static string OutcomeLabel(string outcome) => outcome switch { "completed" => "完成", "cancelled" => "已取消", "timeout" => "超时", _ => "失败" };
    public static string Fingerprint(TaskRecipe recipe, TaskMachine machine, string measurementVersion = Version)
    {
        string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
        string legacy = Hash(new { recipe.Name, recipe.ExecutableName, recipe.ExecutableSha256, recipe.Arguments, recipe.SoftwareVersion, recipe.InputName, recipe.InputSha256, recipe.Procedure,
            recipe.Repeats, recipe.Warmup, recipe.TimeoutSeconds, machine.PowerProfile, machine.PowerSource });
        Need(measurementVersion is Version or LegacyVersion, "测量方法版本无效。");
        return measurementVersion == LegacyVersion ? legacy : Hash(new { Protocol = legacy, FileProtection = "read-share-1.0" });
    }
    private static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new ArgumentException(message); }
    private static bool Text(string? text, int max) => text != null && text.Length <= max;
    private static bool Hash(string? hash) => hash != null && hash.Length == 64 && hash.All(Uri.IsHexDigit);
    public static void Validate(TaskMeasurementResult result)
    {
        Need(result != null && result.Version is Version or LegacyVersion && result.Recipe != null && result.Machine != null && result.Attempts != null, "测量文件版本或内容无效。");
        var r = result.Recipe; var m = result.Machine;
        ValidateRecipe(r);
        Need(Text(m.Cpu, 200) && Text(m.Gpu, 200) && Text(m.OperatingSystem, 300) && m.PowerSource is "Online" or "Offline" or "Unknown" && Text(m.PowerProfile, 200) && !string.IsNullOrWhiteSpace(m.PowerProfile)
            && (m.MemoryGiB == null || double.IsFinite(m.MemoryGiB.Value) && m.MemoryGiB is > 0 and < 100000), "测量机器或电源信息无效。");
        Need(result.Outcome is "completed" or "failed" or "cancelled" or "timeout" && Text(result.Detail, 1000) && result.Started >= DateTimeOffset.UnixEpoch && result.Started <= DateTimeOffset.Now.AddMinutes(5), "测量状态或时间无效。");
        Need(result.Attempts.Count <= r.Repeats + (r.Warmup ? 1 : 0), "测量次数超出协议。");
        for (int i = 0; i < result.Attempts.Count; i++)
        {
            var a = result.Attempts[i];
            Need(a != null && a.Number == i + 1 && a.Warmup == (r.Warmup && i == 0) && double.IsFinite(a.Seconds) && a.Seconds >= 0 && a.Seconds <= r.TimeoutSeconds + 30
                && a.Outcome is "completed" or "failed" or "cancelled" or "timeout" && a.PowerBefore is "Online" or "Offline" or "Unknown" && a.PowerAfter is "Online" or "Offline" or "Unknown", "测量轮次或耗时无效。");
            if (a.Outcome == "completed") Need(a.RootExited && a.ExitCode == 0 && a.Seconds > 0, "成功轮次必须有正常退出的证据。");
        }
        if (result.Outcome == "completed") Need(result.Attempts.Count == r.Repeats + (r.Warmup ? 1 : 0) && result.Attempts.All(a => a.Outcome == "completed"), "测量尚未完整结束。");
    }
    internal static void ValidateRecipe(TaskRecipe r)
    {
        Need(r != null && Text(r.Name, 150) && !string.IsNullOrWhiteSpace(r.Name) && Text(r.ExecutableName, 200) && !string.IsNullOrWhiteSpace(r.ExecutableName) && Hash(r.ExecutableSha256)
            && Text(r.Arguments, 4000) && Text(r.SoftwareVersion, 200) && !string.IsNullOrWhiteSpace(r.SoftwareVersion) && Text(r.InputName, 200) && Hash(r.InputSha256)
            && Text(r.Procedure, 2000) && !string.IsNullOrWhiteSpace(r.Procedure) && r.Repeats is >= 3 and <= 9 && r.TimeoutSeconds is >= 1 and <= 600, "测量协议的程序、输入、版本、步骤或次数无效。");
        Need(Path.GetFileName(r.ExecutableName) == r.ExecutableName && Path.GetFileName(r.InputName) == r.InputName && r.InputName.Length > 0, "测量文件名不能包含目录。");
    }
    public static bool Comparable(TaskMeasurementResult result)
        => HasCompleteRoundsAndStablePower(result) && result.Version == Version;
    internal static bool HasCompleteRoundsAndStablePower(TaskMeasurementResult result)
    {
        Validate(result);
        return result.Outcome == "completed" && result.Machine.PowerSource != "Unknown"
            && result.Attempts.All(a => a.PowerBefore == result.Machine.PowerSource && a.PowerAfter == result.Machine.PowerSource);
    }
    public static (string State, string Reading) Assess(TaskMeasurementResult result, PurchaseCriterion criterion, double? displayedValue)
    {
        Validate(result);
        if (result.Version == LegacyVersion) return ("unknown", "旧测量仅校验批次前后文件，未记录全程只读保护；请复用方案重新测量");
        double scale = criterion.Unit switch { "秒" or "s" or "seconds" => 1, "毫秒" or "ms" => 1000, _ => 0 };
        if (scale == 0 || criterion.Metric != "task" || !criterion.Target.HasValue || criterion.MeasurementProtocol != result.ProtocolFingerprint) return ("unknown", "测量协议或时间单位与目标不一致");
        if (!Comparable(result)) return ("unknown", "测量未完整成功，或电源状态改变/未知");
        double median = result.MedianSeconds!.Value * scale;
        if (!displayedValue.HasValue || Math.Abs(displayedValue.Value - median) > Math.Max(1e-8, median * 1e-9)) return ("unknown", "显示值已修改，与原始测量不一致");
        var values = result.Attempts.Where(a => !a.Warmup).Select(a => a.Seconds * scale).ToArray();
        double min = values.Min(), max = values.Max();
        if (!CandidateComparison.Directions.Any(d => d.Key == criterion.Direction)) return ("unknown", "目标方向无效");
        var passing = values.Select(v => CandidateComparison.Meets(v, criterion.Target.Value, criterion.Direction)).ToArray();
        string state = passing.All(v => v) ? "pass" : passing.All(v => !v) ? "fail" : "unknown";
        return (state, $"中位数 {median:G6} {criterion.Unit}；范围 {min:G6}–{max:G6} {criterion.Unit}；{values.Length} 次；目标{CandidateComparison.DirectionLabel(criterion.Direction)} {criterion.Target:G6} {criterion.Unit}" + (state == "unknown" ? "，重复结果跨过目标线，请复测" : ""));
    }
    public static string Evidence(TaskMeasurementResult result) => $"重复测量 {result.Started.LocalDateTime:yyyy-MM-dd HH:mm}；{result.Recipe.Name}；软件 {result.Recipe.SoftwareVersion}；{result.Machine.Cpu} / {result.Machine.Gpu}；内存 {result.Machine.MemoryGiB:0.##} GiB；{result.Machine.PowerSource} / {result.Machine.PowerProfile}；协议 SHA256 {result.ProtocolFingerprint}；程序 SHA256 {result.Recipe.ExecutableSha256}；输入 SHA256 {result.Recipe.InputSha256}。" + (result.Version == Version ? "测量方法 1.1：运行期间限制对所选程序和输入的写入与替换。" : "旧方法 1.0：仅有批次前后文件校验，需复用方案复测。") + "根进程退出耗时，不包含已分离后台任务的完整完成时间。";
    public static TaskMeasurementResult Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Need(stream.Length is > 0 and <= 131072, "测量文件为空或过大。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        TaskPlans.RejectDuplicateMembers(document.RootElement);
        var result = document.RootElement.Deserialize<TaskMeasurementResult>(new JsonSerializerOptions { MaxDepth = 12 }) ?? throw new ArgumentException("测量文件为空。");
        Validate(result);
        if (document.RootElement.TryGetProperty("ProtocolFingerprint", out var fingerprint))
            Need(fingerprint.ValueKind == JsonValueKind.String && fingerprint.GetString() == result.ProtocolFingerprint, "测量协议指纹与内容不一致，请保留原件并重新导出。");
        return result;
    }
    public static void Save(string path, TaskMeasurementResult result)
    {
        Validate(result);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true });
        string target = Path.GetFullPath(path);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true; stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (created)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    public static string PowerSource() => System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus.ToString();
    public static void ValidateRequest(TaskRunRequest request)
    {
        Need(Text(request.Name, 150) && !string.IsNullOrWhiteSpace(request.Name) && Text(request.Arguments, 4000) && Text(request.SoftwareVersion, 200) && !string.IsNullOrWhiteSpace(request.SoftwareVersion)
            && Text(request.Procedure, 2000) && !string.IsNullOrWhiteSpace(request.Procedure) && request.Repeats is >= 3 and <= 9 && request.TimeoutSeconds is >= 1 and <= 600, "请填写任务名称、软件版本、统一步骤及有效重复次数和超时。");
        Need(Path.IsPathFullyQualified(request.Executable) && File.Exists(request.Executable) && Path.GetExtension(request.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase), "请选择实际的 EXE，不使用文件关联或脚本自动打开。");
        Need(Path.IsPathFullyQualified(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory) && Path.IsPathFullyQualified(request.InputFile) && File.Exists(request.InputFile), "请选择有效的工作目录和任务输入文件或输入清单。");
    }
    internal static async Task<TaskRecipe> ReadRecipeAsync(TaskRunRequest request, CancellationToken cancellation)
    {
        ValidateRequest(request);
        using var executable = TaskFileProtection.Open(request.Executable);
        using var input = TaskFileProtection.Open(request.InputFile);
        return await ReadRecipeAsync(request, executable, input, cancellation).ConfigureAwait(false);
    }
    private static async Task<TaskRecipe> ReadRecipeAsync(TaskRunRequest request, FileStream executable, FileStream input, CancellationToken cancellation)
    {
        string executableHash = await TaskFileProtection.Hash(executable, cancellation).ConfigureAwait(false), inputHash = await TaskFileProtection.Hash(input, cancellation).ConfigureAwait(false);
        return new(request.Name, Path.GetFileName(request.Executable), executableHash, request.Arguments, request.SoftwareVersion,
            Path.GetFileName(request.InputFile), inputHash, request.Procedure, request.Repeats, request.Warmup, request.TimeoutSeconds);
    }
    public static async Task<TaskMeasurementResult> RunAsync(TaskRunRequest request, TaskMachine machine, IProgress<TaskRunProgress>? progress, CancellationToken cancellation, TaskPlan? expectedPlan = null)
    {
        ValidateRequest(request);
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new("正在保持程序和输入只读并校验文件", 0, request.Repeats));
        using var executable = TaskFileProtection.Open(request.Executable);
        using var input = TaskFileProtection.Open(request.InputFile);
        var recipe = await ReadRecipeAsync(request, executable, input, cancellation).ConfigureAwait(false);
        if (expectedPlan != null) TaskPlans.Verify(expectedPlan, recipe, machine);
        var result = new TaskMeasurementResult(Version, DateTimeOffset.Now, recipe, machine, [], "failed", "未执行");
        Validate(result);
        for (int i = 0; i < request.Repeats + (request.Warmup ? 1 : 0); i++)
        {
            bool warmup = request.Warmup && i == 0;
            progress?.Report(new(warmup ? "预热运行，不计入正式结果" : "正在运行第 " + (i + 1 - (request.Warmup ? 1 : 0)) + " 次", Math.Max(0, i - (request.Warmup ? 1 : 0)), request.Repeats));
            var attempt = await RunOne(request, i + 1, warmup, cancellation).ConfigureAwait(false);
            result.Attempts.Add(attempt);
            if (attempt.Outcome != "completed") return result with { Outcome = attempt.Outcome, Detail = attempt.RootExited ? "任务未正常完成，未产生可用于配置判断的测量。" : "任务根进程尚未确认退出；监测保持暂停，请检查任务状态。" };
            progress?.Report(new(warmup ? "预热完成" : "本轮完成", i + 1 - (request.Warmup ? 1 : 0), request.Repeats, attempt.Seconds));
        }
        progress?.Report(new("正在复核程序与输入未改变", request.Repeats, request.Repeats));
        if (recipe.ExecutableSha256 != await TaskFileProtection.Hash(executable, cancellation).ConfigureAwait(false) || recipe.InputSha256 != await TaskFileProtection.Hash(input, cancellation).ConfigureAwait(false)) return result with { Outcome = "failed", Detail = "程序或输入文件在测量中改变，请恢复同一输入后重新测试。" };
        return result with { Outcome = "completed", Detail = "重复测量完成。结果不证明任务输出正确；请核对实际产物、机器和测试条件。" };
    }
    private static async Task<TaskAttempt> RunOne(TaskRunRequest request, int number, bool warmup, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new(request.Executable, request.Arguments) { WorkingDirectory = request.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true } };
        string before = PowerSource(); var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var drainStop = new CancellationTokenSource();
        Task output = Task.CompletedTask, error = Task.CompletedTask; string outcome = "failed"; int? exitCode = null; bool exited = false, started = false;
        try
        {
            if (!process.Start()) return new(number, warmup, watch.Elapsed.TotalSeconds, null, "failed", true, before, PowerSource());
            started = true;
            process.StandardInput.Close();
            // Drain both pipes concurrently with bounded buffers; stdout/stderr are not retained.
            async Task Drain(StreamReader reader) { char[] buffer = new char[4096]; try { while (await reader.ReadAsync(buffer.AsMemory(), drainStop.Token).ConfigureAwait(false) != 0) { } } catch (OperationCanceledException) { } catch (IOException) { } catch (ObjectDisposedException) { } }
            output = Drain(process.StandardOutput); error = Drain(process.StandardError);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); watch.Stop(); exited = true; exitCode = process.ExitCode; outcome = exitCode == 0 ? "completed" : "failed";
        }
        catch (OperationCanceledException) { outcome = cancellation.IsCancellationRequested ? "cancelled" : "timeout"; }
        catch (System.ComponentModel.Win32Exception) { outcome = "failed"; }
        finally
        {
            try
            {
                if (!exited && started)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (Exception e) when (e is AggregateException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // A descendant may be inaccessible. Still attempt to stop and verify our owned root.
                        try { if (!process.HasExited) process.Kill(); }
                        catch (Exception rootError) when (rootError is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    }
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); exited = process.HasExited;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            if (!started) exited = true;
            watch.Stop(); drainStop.Cancel();
            await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(1000)).ConfigureAwait(false);
        }
        return new(number, warmup, watch.Elapsed.TotalSeconds, exitCode, outcome, exited, before, PowerSource());
    }
}
