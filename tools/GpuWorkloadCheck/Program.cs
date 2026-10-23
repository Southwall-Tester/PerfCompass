using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static readonly UTF8Encoding Utf8 = new(false);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: GpuWorkloadCheck <absolute PerfCompass.exe> <absolute ffmpeg.exe> <absolute nvidia-smi.exe> <absolute new empty artifacts directory>");
            return 2;
        }
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        { Console.Error.WriteLine("This tool requires 64-bit Windows."); return 2; }

        Run? run = null;
        int exitCode = 1;
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            string app = RequireExecutable(args[0]), ffmpeg = RequireExecutable(args[1]), smi = RequireExecutable(args[2]);
            string ffprobe = RequireExecutable(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"));
            string output = RequireOutput(args[3]);
            run = new Run(app, ffmpeg, ffprobe, smi, output);
            await run.Execute(cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            run.Result.Status = "recording-completed";
            run.Result.RecordingCompleted = true;
            exitCode = 0;
        }
        catch (Exception ex)
        {
            if (run != null)
            {
                run.Result.Status = ex is OperationCanceledException ? "cancelled" : "failed";
                run.Result.Error = new { Type = ex.GetType().FullName, ex.Message };
            }
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
        }
        finally
        {
            if (run != null)
            {
                try { await run.Cleanup(); }
                catch (Exception ex)
                {
                    run.Result.CleanupErrors.Add(ex.Message);
                    run.Result.Status = "cleanup-failed"; run.Result.RecordingCompleted = false;
                }
                if (!run.Result.RecordingCompleted || run.Result.CleanupErrors.Count > 0) exitCode = 1;
                run.Result.FinishedUtc = DateTimeOffset.UtcNow;
                run.Result.ElapsedSeconds = run.Clock.Elapsed.TotalSeconds;
                try { run.SaveResult(); }
                catch (Exception ex) { Console.Error.WriteLine("Could not save result: " + ex.Message); exitCode = 1; }
            }
            Console.CancelKeyPress -= onCancel;
        }
        return exitCode;
    }

    private static string RequireExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Executable paths must be absolute: " + path);
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) || !Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Executable does not exist: " + full);
        return full;
    }

    private static string RequireOutput(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Output directory must be absolute.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("artifacts", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Use a new directory beneath artifacts; never release/data.");
        if (File.Exists(full) || Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new ArgumentException("Output directory must be new or empty; existing evidence is never replaced.");
        for (DirectoryInfo? dir = new(full); dir != null; dir = dir.Parent)
        {
            if (dir.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output must not be inside a data directory.");
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Output directory ancestors must not be links or junctions.");
        }
        Directory.CreateDirectory(full);
        // CreateNew also prevents two copies of this tool from sharing an empty directory.
        using (new FileStream(Path.Combine(full, "run.claim"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        return full;
    }

    internal static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    internal static void WriteJson(string path, object value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally
        {
            // Cleanup failure must not replace the original write/replace error.
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class Run(string app, string ffmpeg, string ffprobe, string smi, string output)
{
    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    internal readonly RunResult Result = new();
    private readonly List<OwnedProcess> owned = [];
    private readonly string seed = Path.Combine(output, "seed.mkv"), encoded = Path.Combine(output, "encoded.mkv");
    private string? gpuUuid;

    internal async Task Execute(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Result.OutputDirectory = output;
        Result.ToolProcessId = Environment.ProcessId;
        Result.ToolExecutable = Environment.ProcessPath!;
        Result.ToolExecutableSha256 = Program.Hash(Environment.ProcessPath!);
        Result.ToolAssemblySha256 = Program.Hash(typeof(Program).Assembly.Location);
        SaveResult();

        var identity = Start("identity", smi,
            ["--query-gpu=index,uuid,pci.bus_id,name,driver_version", "--format=csv,noheader,nounits"]);
        await NaturalExit(identity, TimeSpan.FromSeconds(15), token);
        string[][] rows = File.ReadAllLines(identity.Info.StdoutPath).Where(s => !string.IsNullOrWhiteSpace(s)).Select(Csv).ToArray();
        if (rows.Length != 1 || rows[0].Length != 5 || !rows[0][1].StartsWith("GPU-", StringComparison.Ordinal)
            || !rows[0][3].Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Exactly one NVIDIA GPU with a UUID and PCI address is required; see identity.stdout.log.");
        gpuUuid = rows[0][1];
        var cuda = CudaIdentity.Read(gpuUuid);
        if (!gpuUuid.Equals(cuda.Uuid, StringComparison.OrdinalIgnoreCase)
            || NormalizeBdf(rows[0][2]) != NormalizeBdf(cuda.PciBusId))
            throw new InvalidOperationException("CUDA device 0 does not match the NVIDIA-SMI UUID and PCI address; no workload was started.");
        Result.Identity = new { SmiIndex = rows[0][0], Uuid = gpuUuid, PciBusId = rows[0][2], Name = rows[0][3], Driver = rows[0][4], Cuda = cuda };
        SaveResult();
        token.ThrowIfCancellationRequested();

        // CPU seed generation occurs before the telemetry baseline. This is synthetic input,
        // not a user's task or evidence that a purchased configuration meets their needs.
        var seedProcess = Start("seed-cpu", ffmpeg, FfmpegArguments(
            ["-nostdin", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60", "-t", "5", "-an",
             "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", "-pix_fmt", "yuv420p", seed]));
        await NaturalExit(seedProcess, TimeSpan.FromSeconds(30), token);
        RequireFile(seed);
        token.ThrowIfCancellationRequested();

        var telemetry = Start("telemetry", app,
            ["--telemetry-check", "--validation-seconds", "90", "--data-dir", Path.Combine(output, "data")]);
        var monitor = Start("nvidia-smi-loop", smi,
            ["--id=" + gpuUuid, "--query-gpu=timestamp,uuid,pci.bus_id,utilization.gpu,utilization.encoder,utilization.decoder,utilization.memory,memory.used,memory.total,temperature.gpu",
             "--format=csv", "--loop-ms=500"]);

        await Phase("baseline", () => DelayMonitoring(TimeSpan.FromSeconds(15), telemetry, monitor, token));
        OwnedProcess? encoding = null, decoding = null;
        await Phase("encode", async () =>
        {
            token.ThrowIfCancellationRequested();
            CheckMonitoring(telemetry, monitor);
            encoding = Start("encode-nvenc", ffmpeg, FfmpegArguments(
                ["-stream_loop", "-1", "-i", seed, "-map", "0:v:0", "-an", "-sn", "-dn",
                 "-c:v", "h264_nvenc", "-gpu", "0", "-preset", "p7", "-pix_fmt", "yuv420p", "-fs", "134217728", encoded]), cudaVisible: gpuUuid);
            await TimedWorkload(encoding, TimeSpan.FromSeconds(20), token);
            RequireFile(encoded);
        }, end: () => ExitMark(encoding));
        await Recovery("recovery-after-encode", encoding!, telemetry, monitor, token);
        await Phase("decode", async () =>
        {
            token.ThrowIfCancellationRequested();
            CheckMonitoring(telemetry, monitor);
            decoding = Start("decode-cuvid", ffmpeg, FfmpegArguments(
                ["-stream_loop", "-1", "-c:v", "h264_cuvid", "-gpu", "0", "-i", encoded,
                 "-map", "0:v:0", "-an", "-sn", "-dn", "-f", "null", "-"]), cudaVisible: gpuUuid);
            await TimedWorkload(decoding, TimeSpan.FromSeconds(20), token);
        }, end: () => ExitMark(decoding));
        await Recovery("recovery-after-decode", decoding!, telemetry, monitor, token);

        double remaining = 150 - (Clock.Elapsed.TotalSeconds - telemetry.Info.LaunchedElapsedSeconds);
        await NaturalExit(telemetry, TimeSpan.FromSeconds(Math.Max(.001, remaining)), token);
        if (monitor.Process.HasExited) throw new InvalidOperationException("NVIDIA-SMI monitoring ended before telemetry completed.");
        await monitor.StopOwned("planned-monitor-stop", expected: true);
        string telemetryPath = Path.Combine(output, "data", "validation", "telemetry-cross-check.json");
        RequireFile(telemetryPath);
        using (var document = JsonDocument.Parse(File.ReadAllText(telemetryPath)))
        {
            var root = document.RootElement;
            var samples = root.GetProperty("Rows");
            int count = samples.GetArrayLength();
            if (count < 2) throw new InvalidOperationException("Telemetry has fewer than two recorded samples.");
            if (!string.Equals(root.GetProperty("ExecutableSha256").GetString(), telemetry.Info.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Telemetry executable hash differs from the executable launched by this tool.");
            if (!string.Equals(root.GetProperty("GpuName").GetString(), rows[0][3], StringComparison.Ordinal))
                throw new InvalidOperationException("Production telemetry GPU name differs from the single NVIDIA-SMI device; do not compare these records as the same GPU.");
            var knownEngineSamples = new Dictionary<string, int>(StringComparer.Ordinal);
            int anyKnownEngine = 0;
            foreach (var row in samples.EnumerateArray())
            {
                var sample = row.GetProperty("Sample"); bool known = false;
                if (sample.TryGetProperty("GpuEngines", out var engines) && engines.ValueKind == JsonValueKind.Array)
                    foreach (var engine in engines.EnumerateArray())
                        if (engine.TryGetProperty("Load", out var load) && load.ValueKind == JsonValueKind.Number
                            && load.TryGetDouble(out double value) && double.IsFinite(value) && value is >= 0 and <= 100
                            && engine.TryGetProperty("Kind", out var kind) && kind.GetString() is string name)
                        { known = true; knownEngineSamples[name] = knownEngineSamples.GetValueOrDefault(name) + 1; }
                if (known) anyKnownEngine++;
            }
            Result.Telemetry = new { Path = telemetryPath, Sha256 = Program.Hash(telemetryPath), Rows = count,
                GpuName = root.GetProperty("GpuName").GetString(), Version = root.GetProperty("Version").GetString(),
                ExecutableHashMatches = true, GpuModelNameMatches = true, SamplesWithAnyKnownEngine = anyKnownEngine,
                KnownEngineSamples = knownEngineSamples };
        }

        token.ThrowIfCancellationRequested();
        var probe = Start("encoded-ffprobe", ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries",
             "stream=codec_name,width,height,r_frame_rate,nb_read_frames:format=duration,size", "-of", "json", encoded]);
        await NaturalExit(probe, TimeSpan.FromSeconds(30), token);
        using (var document = JsonDocument.Parse(File.ReadAllText(probe.Info.StdoutPath)))
        {
            var streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() != 1 || streams[0].GetProperty("codec_name").GetString() != "h264"
                || !long.TryParse(streams[0].GetProperty("nb_read_frames").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long frames) || frames <= 0)
                throw new InvalidOperationException("The encoded artifact has no verified readable H.264 frames.");
            Result.EncodedArtifact = new { Path = encoded, Sha256 = Program.Hash(encoded), Bytes = new FileInfo(encoded).Length, Frames = frames };
        }
        token.ThrowIfCancellationRequested();
        var software = Start("encoded-software-readback", ffmpeg, FfmpegArguments(
            ["-nostdin", "-xerror", "-hwaccel", "none", "-c:v", "h264", "-threads", "2", "-i", encoded,
             "-t", "5", "-map", "0:v:0", "-an", "-sn", "-dn", "-f", "null", "-"]));
        await NaturalExit(software, TimeSpan.FromSeconds(30), token);
        if (software.Info.FirstFrame == null) throw new InvalidOperationException("Software readback did not report a decoded frame.");
        Result.SoftwareReadbackScope = "Successful software decode of at most the first 5 seconds of media; not an exhaustive artifact validation.";
    }

    private OwnedProcess Start(string role, string executable, string[] arguments, string? cudaVisible = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = output, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Program.Utf8, StandardErrorEncoding = Program.Utf8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (cudaVisible != null) start.Environment["CUDA_VISIBLE_DEVICES"] = cudaVisible;
        start.Environment.TryGetValue("CUDA_VISIBLE_DEVICES", out string? effectiveCudaVisible);
        var info = new ProcessEvidence
        {
            Role = role, Executable = executable, ExecutableSha256 = Program.Hash(executable), Arguments = arguments,
            Environment = new() { ["CUDA_VISIBLE_DEVICES"] = effectiveCudaVisible },
            LaunchedUtc = DateTimeOffset.UtcNow, LaunchedElapsedSeconds = Clock.Elapsed.TotalSeconds,
            StdoutPath = Path.Combine(output, role + ".stdout.log"), StderrPath = Path.Combine(output, role + ".stderr.log")
        };
        var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidOperationException("Could not start " + role); }
        catch { process.Dispose(); throw; }
        info.ProcessId = process.Id;
        var tracked = new OwnedProcess(process, info, Clock, Path.Combine(output, role + ".process.json"));
        owned.Add(tracked); Result.Processes.Add(info);
        try { info.StartedUtc = process.StartTime.ToUniversalTime(); } catch (InvalidOperationException) { }
        tracked.Save();
        Console.WriteLine($"{role}: PID {info.ProcessId}");
        return tracked;
    }

    private static string[] FfmpegArguments(string[] arguments) =>
        ["-hide_banner", "-loglevel", "verbose", "-y", "-nostats", "-stats_period", "0.5", "-progress", "pipe:1", .. arguments];

    private async Task NaturalExit(OwnedProcess process, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            await Task.WhenAny(process.Exit, process.LogFailure).WaitAsync(timeout, token);
            process.ThrowIfLogFailed();
            await process.Exit;
        }
        catch (TimeoutException) { await process.StopOwned("deadline-exceeded", expected: false); throw new TimeoutException(process.Info.Role + " exceeded its deadline."); }
        await process.Finish();
        if (process.Info.ExitCode != 0) throw new InvalidOperationException(process.Info.Role + " exited with code " + process.Info.ExitCode + "; see its stderr log.");
    }

    private async Task TimedWorkload(OwnedProcess process, TimeSpan duration, CancellationToken token)
    {
        // The deadline starts at launch, not after log setup or metadata writes.
        process.Info.RequestedWorkloadSeconds = duration.TotalSeconds;
        double remaining = duration.TotalSeconds - (Clock.Elapsed.TotalSeconds - process.Info.LaunchedElapsedSeconds);
        Task delay = Task.Delay(TimeSpan.FromSeconds(Math.Max(0, remaining)), token);
        var completed = await Task.WhenAny(process.Exit, process.LogFailure, delay);
        process.ThrowIfLogFailed();
        if (completed == process.Exit || process.Process.HasExited)
        {
            process.Info.EndedBeforeRequestedStop = true;
            process.Info.StopReason = "unexpected-early-exit-including-possible-output-size-limit";
            await process.Finish();
            throw new InvalidOperationException(process.Info.Role + " ended before its requested wall-clock stage (including a possible -fs output limit); it is not a complete workload recording.");
        }
        await delay;
        process.Info.QRequested = new(DateTimeOffset.UtcNow, Clock.Elapsed.TotalSeconds);
        process.Info.StopReason = "planned-q-after-wall-clock-stage";
        // Send the stop request before any metadata I/O; a full disk must not
        // delay q or prevent the finally path from stopping this owned process.
        await process.Process.StandardInput.WriteLineAsync("q");
        await process.Process.StandardInput.FlushAsync();
        process.Info.QSent = true;
        try
        {
            await Task.WhenAny(process.Exit, process.LogFailure).WaitAsync(TimeSpan.FromSeconds(5), token);
            process.ThrowIfLogFailed();
            await process.Exit;
        }
        catch (TimeoutException) { await process.StopOwned("q-grace-exceeded", expected: false); throw new TimeoutException(process.Info.Role + " did not exit within 5 seconds of q."); }
        await process.Finish();
        if (process.Info.ExitCode != 0 || process.Info.FirstFrame == null)
            throw new InvalidOperationException(process.Info.Role + " has no successful framed output; see progress and stderr logs.");
    }

    private static TimeMark? ExitMark(OwnedProcess? process) => process?.Info is { ExitedUtc: { } utc, ExitedElapsedSeconds: { } elapsed }
        ? new(utc, elapsed) : null;

    private Task Recovery(string name, OwnedProcess workload, OwnedProcess telemetry, OwnedProcess monitor, CancellationToken token)
    {
        var stopped = ExitMark(workload) ?? throw new InvalidOperationException("Recovery requires a confirmed workload exit.");
        return Phase(name, () => DelayMonitoring(TimeSpan.FromSeconds(Math.Max(0, 10 - (Clock.Elapsed.TotalSeconds - stopped.ElapsedSeconds))),
            telemetry, monitor, token), start: stopped);
    }

    private async Task Phase(string name, Func<Task> operation, TimeMark? start = null, Func<TimeMark?>? end = null)
    {
        var phase = new PhaseEvidence { Name = name, StartedUtc = start?.Utc ?? DateTimeOffset.UtcNow,
            StartedElapsedSeconds = start?.ElapsedSeconds ?? Clock.Elapsed.TotalSeconds };
        Result.Phases.Add(phase); SaveResult();
        Console.WriteLine($"Phase {name} started at {phase.StartedUtc:O} ({phase.StartedElapsedSeconds:0.0}s).");
        try { await operation(); phase.Status = "completed"; }
        catch { phase.Status = "failed"; throw; }
        finally
        {
            var stopped = end?.Invoke();
            phase.EndedUtc = stopped?.Utc ?? DateTimeOffset.UtcNow;
            phase.EndedElapsedSeconds = stopped?.ElapsedSeconds ?? Clock.Elapsed.TotalSeconds;
            SaveResult();
            Console.WriteLine($"Phase {name} {phase.Status}; elapsed {phase.EndedElapsedSeconds - phase.StartedElapsedSeconds:0.0}s.");
        }
    }

    private static void CheckMonitoring(OwnedProcess telemetry, OwnedProcess monitor)
    {
        telemetry.ThrowIfLogFailed(); monitor.ThrowIfLogFailed();
        if (telemetry.Process.HasExited || monitor.Process.HasExited)
            throw new InvalidOperationException("Telemetry or NVIDIA-SMI ended before the scheduled phases completed.");
    }
    private static async Task DelayMonitoring(TimeSpan duration, OwnedProcess telemetry, OwnedProcess monitor, CancellationToken token)
    {
        CheckMonitoring(telemetry, monitor);
        Task delay = Task.Delay(duration, token);
        await Task.WhenAny(delay, telemetry.Exit, monitor.Exit, telemetry.LogFailure, monitor.LogFailure);
        CheckMonitoring(telemetry, monitor); await delay;
    }

    internal async Task Cleanup()
    {
        foreach (var process in owned.AsEnumerable().Reverse())
        {
            try
            {
                if (!process.Process.HasExited) await process.StopOwned("cleanup-owned-process", expected: false);
                else await process.Finish();
            }
            catch (Exception ex)
            {
                Result.CleanupErrors.Add(process.Info.Role + ": " + ex.Message);
                Result.RecordingCompleted = false; Result.Status = "cleanup-failed";
            }
            finally
            {
                try { process.Dispose(); }
                catch (Exception ex)
                {
                    Result.CleanupErrors.Add(process.Info.Role + " disposal: " + ex.Message);
                    Result.RecordingCompleted = false; Result.Status = "cleanup-failed";
                }
            }
        }
    }
    internal void SaveResult() => Program.WriteJson(Path.Combine(output, "result.json"), Result);
    private static void RequireFile(string path)
    { if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidOperationException("Expected nonempty artifact missing: " + path); }

    private static string[] Csv(string line)
    {
        var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append(c); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { fields.Add(field.ToString().Trim()); field.Clear(); }
            else field.Append(c);
        }
        if (quoted) throw new InvalidOperationException("Unclosed quoted NVIDIA-SMI CSV field.");
        fields.Add(field.ToString().Trim()); return fields.ToArray();
    }
    private static string NormalizeBdf(string value)
    {
        var match = Regex.Match(value, @"^([0-9a-fA-F]{4,8}):([0-9a-fA-F]{2}):([0-9a-fA-F]{2})\.([0-7])$");
        if (!match.Success) throw new InvalidOperationException("Unsupported PCI address: " + value);
        return string.Join(":", Enumerable.Range(1, 4).Select(i => uint.Parse(match.Groups[i].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
    }
}

internal sealed class OwnedProcess : IDisposable
{
    internal Process Process { get; }
    internal ProcessEvidence Info { get; }
    internal Task Exit { get; }
    internal Task<string> LogFailure => logFailure.Task;
    private readonly Stopwatch clock;
    private readonly string metadataPath;
    private readonly CancellationTokenSource readCancellation = new();
    private readonly Task stdout, stderr;
    private readonly TaskCompletionSource<string> logFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool finished;

    internal OwnedProcess(Process process, ProcessEvidence info, Stopwatch clock, string metadataPath)
    {
        Process = process; Info = info; this.clock = clock; this.metadataPath = metadataPath;
        Exit = ObserveExit();
        stdout = Pump(process.StandardOutput, info.StdoutPath, progress: true);
        stderr = Pump(process.StandardError, info.StderrPath, progress: false);
    }

    private async Task ObserveExit()
    {
        await Process.WaitForExitAsync();
        Info.ExitObservedUtc = DateTimeOffset.UtcNow; Info.ExitedElapsedSeconds = clock.Elapsed.TotalSeconds;
        // Native exit wall time avoids including pipe draining in the workload.
        // The elapsed timestamp is when this process exit was observed, not an
        // independently known QPC timestamp of the native termination itself.
        Info.ExitedUtc = Process.ExitTime.ToUniversalTime();
        Info.ExitCode = Process.ExitCode;
    }

    private async Task Pump(StreamReader reader, string path, bool progress)
    {
        try
        {
            await using var raw = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Program.Utf8) { AutoFlush = true };
            await using var received = new StreamWriter(new FileStream(path + ".received.jsonl", FileMode.CreateNew, FileAccess.Write, FileShare.Read), Program.Utf8) { AutoFlush = true };
            while (await reader.ReadLineAsync(readCancellation.Token) is string line)
            {
                var utc = DateTimeOffset.UtcNow; double elapsed = clock.Elapsed.TotalSeconds;
                await raw.WriteLineAsync(line);
                await received.WriteLineAsync(JsonSerializer.Serialize(new { ReceivedUtc = utc, ReceivedElapsedSeconds = elapsed, Line = line }));
                if (progress && line.StartsWith("frame=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long frames) && frames > 0)
                {
                    Info.FirstFrame ??= new(utc, elapsed);
                    Info.LastFrame = new(utc, elapsed); Info.LastReportedFrames = frames;
                }
            }
        }
        catch (Exception ex)
        {
            string message = Path.GetFileName(path) + ": " + ex.Message;
            logFailure.TrySetResult(message);
            throw;
        }
    }

    internal async Task Finish()
    {
        if (finished) return;
        await Exit;
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            finished = true;
        }
        catch
        {
            readCancellation.Cancel();
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            throw;
        }
        finally { Info.LogFailure = logFailure.Task.IsCompletedSuccessfully ? logFailure.Task.Result : null; Save(); }
    }

    internal async Task StopOwned(string reason, bool expected)
    {
        Exception? treeFailure = null;
        if (!Process.HasExited)
        {
            Info.StopReason = reason; Info.Forced = true; Info.ExpectedMonitorStop = expected;
            Info.StopRequested = new(DateTimeOffset.UtcNow, clock.Elapsed.TotalSeconds);
            // Never make stopping an owned process depend on successful logging.
            try { Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (Process.HasExited) { }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or AggregateException)
            {
                treeFailure = ex; Info.KillTreeError = ex.Message;
                // Preserve the tree failure, but still try to stop the exact
                // owned root through the retained Process handle.
                if (!Process.HasExited) Process.Kill();
            }
        }
        await Exit.WaitAsync(TimeSpan.FromSeconds(5));
        await Finish();
        if (treeFailure != null) throw new InvalidOperationException(Info.Role + " process-tree termination was not fully confirmed.", treeFailure);
    }
    internal void ThrowIfLogFailed()
    {
        if (logFailure.Task.IsCompletedSuccessfully)
            throw new IOException(Info.Role + " output recording failed: " + logFailure.Task.Result);
    }
    internal void Save() => Program.WriteJson(metadataPath, Info);
    public void Dispose() { readCancellation.Cancel(); readCancellation.Dispose(); Process.Dispose(); }
}

internal static class CudaIdentity
{
    internal static CudaObservation Read(string uuid)
    {
        string? previous = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", uuid);
        try
        {
            if (Marshal.SizeOf<CuUuid>() != 16) throw new InvalidOperationException("CUDA UUID ABI must occupy exactly 16 bytes.");
            Check(cuInit(0), "cuInit"); Check(cuDeviceGetCount(out int count), "cuDeviceGetCount");
            if (count != 1) throw new InvalidOperationException("CUDA_VISIBLE_DEVICES did not expose exactly the requested GPU.");
            Check(cuDeviceGet(out int device, 0), "cuDeviceGet");
            var bus = new StringBuilder(32); Check(cuDeviceGetPCIBusId(bus, bus.Capacity, device), "cuDeviceGetPCIBusId");
            CuUuid id;
            string api = "cuDeviceGetUuid_v2";
            try { Check(cuDeviceGetUuidV2(out id, device), api); }
            catch (EntryPointNotFoundException) { api = "cuDeviceGetUuid"; Check(cuDeviceGetUuid(out id, device), api); }
            string hex = Convert.ToHexString(id.Bytes).ToLowerInvariant();
            string actual = $"GPU-{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
            return new(DateTimeOffset.UtcNow, count, 0, bus.ToString(), actual, uuid, previous, api);
        }
        finally { Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", previous); }
    }
    private static void Check(int status, string operation)
    { if (status != 0) throw new InvalidOperationException(operation + " returned CUDA status " + status + "; no workload was started."); }
    internal sealed record CudaObservation(DateTimeOffset ObservedUtc, int DeviceCount, int Ordinal, string PciBusId, string Uuid,
        string CudaVisibleDevices, string? InheritedCudaVisibleDevices, string UuidApi);
    [StructLayout(LayoutKind.Sequential)] private struct CuUuid { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Bytes; }
    [DllImport("nvcuda.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)] private static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)] private static extern int cuDeviceGetCount(out int count);
    [DllImport("nvcuda.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)] private static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport("nvcuda.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)] private static extern int cuDeviceGetPCIBusId(StringBuilder pciBusId, int length, int device);
    [DllImport("nvcuda.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi, EntryPoint = "cuDeviceGetUuid_v2")] private static extern int cuDeviceGetUuidV2(out CuUuid uuid, int device);
    [DllImport("nvcuda.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)] private static extern int cuDeviceGetUuid(out CuUuid uuid, int device);
}

internal sealed record TimeMark(DateTimeOffset Utc, double ElapsedSeconds);
internal sealed class ProcessEvidence
{
    public string Role { get; init; } = "";
    public int ProcessId { get; set; }
    public string Executable { get; init; } = "";
    public string ExecutableSha256 { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public Dictionary<string, string?> Environment { get; init; } = [];
    public DateTimeOffset LaunchedUtc { get; init; }
    public double LaunchedElapsedSeconds { get; init; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? ExitedUtc { get; set; }
    public DateTimeOffset? ExitObservedUtc { get; set; }
    public double? ExitedElapsedSeconds { get; set; }
    public int? ExitCode { get; set; }
    public string StdoutPath { get; init; } = "";
    public string StderrPath { get; init; } = "";
    public TimeMark? FirstFrame { get; set; }
    public TimeMark? LastFrame { get; set; }
    public long? LastReportedFrames { get; set; }
    public double? RequestedWorkloadSeconds { get; set; }
    public TimeMark? QRequested { get; set; }
    public bool QSent { get; set; }
    public TimeMark? StopRequested { get; set; }
    public string? StopReason { get; set; }
    public bool Forced { get; set; }
    public bool ExpectedMonitorStop { get; set; }
    public bool EndedBeforeRequestedStop { get; set; }
    public string? LogFailure { get; set; }
    public string? KillTreeError { get; set; }
}
internal sealed class PhaseEvidence
{
    public string Name { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
    public double StartedElapsedSeconds { get; init; }
    public DateTimeOffset? EndedUtc { get; set; }
    public double? EndedElapsedSeconds { get; set; }
    public string Status { get; set; } = "running";
}
internal sealed class RunResult
{
    public string Format { get; } = "perfcompass-gpu-workload-recording-1.0";
    public string Status { get; set; } = "running";
    public bool RecordingCompleted { get; set; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public double? ElapsedSeconds { get; set; }
    public int ToolProcessId { get; set; }
    public string ToolExecutable { get; set; } = "";
    public string ToolExecutableSha256 { get; set; } = "";
    public string ToolAssemblySha256 { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public object? Identity { get; set; }
    public object? Error { get; set; }
    public object? Telemetry { get; set; }
    public string ProductionGpuBinding { get; } = "Not established by this tool: CUDA and NVIDIA-SMI UUID/BDF are matched; production telemetry is checked only for executable hash and GPU model name. A separate current production adapter identity probe is required to bind the physical GPU. No multi-GPU verification is claimed.";
    public object? EncodedArtifact { get; set; }
    public string? SoftwareReadbackScope { get; set; }
    public List<PhaseEvidence> Phases { get; } = [];
    public List<ProcessEvidence> Processes { get; } = [];
    public List<string> CleanupErrors { get; } = [];
    public string Scope { get; } = "Explicit synthetic encode/decode recording, not a task benchmark or GPU accuracy certificate. RecordingCompleted does not imply valid engine coverage, phase response or recovery; analyze raw per-phase samples separately. NVIDIA-SMI/NVML and production driver counters are acquisition paths, not independent physical ground truth. FirstFrame is progress-line receipt time. Workload phases include initialization and q drain up to native process exit; recovery starts at confirmed workload exit. Exit elapsed time is the observer's monotonic time, while ExitedUtc is Windows process exit time. Exclude transition intervals when analyzing. Unknown counters stay unknown. This does not establish purchase suitability, stability over days, or results on other hardware. No production data directory is used.";
}
