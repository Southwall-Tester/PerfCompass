using System.IO;
using System.Text.Json;
using System.Windows;

namespace PerfCompass;

public partial class App : System.Windows.Application
{
    private DataRootLease? dataLease;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ValidationRunOptions? validation;
        try { validation = ValidationRunOptions.Parse(e.Args); }
        catch (ArgumentException) { Shutdown(3); return; }
        if (e.Args.Contains("--self-test")) { Shutdown(SelfTests.Run()); return; }
        if (e.Args.Contains("--nlp-evaluate")) { Shutdown(NlpEvaluation.Run()); return; }
        string root = Path.Combine(AppContext.BaseDirectory, "data");
        int idx = Array.IndexOf(e.Args, "--data-dir");
        if (idx >= 0 && idx + 1 < e.Args.Length) root = Path.GetFullPath(e.Args[idx + 1]);
        if (validation != null)
        {
            try { ValidationRunOptions.RequireEmptyRoot(root, idx >= 0 && idx + 1 < e.Args.Length); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { Shutdown(3); return; }
        }
        if (e.Args.Contains("--recovery-ui-test") && (idx < 0 || !File.Exists(Path.Combine(root, ".recovery-test-data"))))
        { System.Windows.MessageBox.Show("恢复启动检查需要显式指定带测试标记的独立数据目录。", "PerfCompass"); Shutdown(3); return; }
        if (e.Args.Contains("--ui-test") && (idx < 0 || (Directory.Exists(root) && (Directory.GetFiles(root, "samples-*.jsonl").Length > 0 || Directory.GetFiles(root, "experience-*.jsonl").Length > 0))))
        {
            System.Windows.MessageBox.Show("UI 测试需要通过 --data-dir 指定一个没有历史记录的独立测试目录。", "PerfCompass"); Shutdown(3); return;
        }
        try { dataLease = DataRootLease.Acquire(root); }
        catch (Exception ex)
        {
            if (!e.Args.Contains("--check-instance")) System.Windows.MessageBox.Show("无法锁定数据目录：\n" + ex.Message, "PerfCompass");
            Shutdown(2); return;
        }
        if (e.Args.Contains("--check-instance")) { Shutdown(dataLease == null ? 10 : 0); return; }
        if (dataLease == null) { System.Windows.MessageBox.Show("数据目录已被占用。请打开已运行的 PerfCompass，或选择另一个数据目录。", "PerfCompass"); Shutdown(); return; }
        LocalStore store;
        try { store = new LocalStore(root); }
        catch (Exception ex) { System.Windows.MessageBox.Show("无法安全打开数据目录或设置：\n" + ex.Message, "PerfCompass"); Shutdown(1); return; }
        DispatcherUnhandledException += (_, args) => { store.Log(args.Exception); System.Windows.MessageBox.Show("操作未完成，请重试。错误已记录到 data/errors.log。", "PerfCompass"); args.Handled = true; };
        if (validation?.Mode == "telemetry")
        {
            try { int code = await Task.Run(() => TelemetryCrossCheck.RunAsync(store, validation.Seconds)); Shutdown(code); }
            catch (Exception error) { store.Log(error); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--diagnose"))
        {
            try
            {
                using var collector = await Task.Run(() => new Collector(store));
                await Task.Delay(2000);
                var live = await Task.Run(collector.Collect);
                File.WriteAllText(Path.Combine(root, "diagnostics.json"), JsonSerializer.Serialize(live, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(live.Sample.Cpu.HasValue && live.Sample.MemoryTotalGb > 0 ? 0 : 2);
            }
            catch (Exception ex) { store.Log(ex); Shutdown(1); }
            return;
        }
        var window = new MainWindow(store);
        MainWindow = window; window.Show();
        if (e.Args.Contains("--minimized")) window.Hide();
    }
    protected override void OnExit(ExitEventArgs e) { dataLease?.Dispose(); base.OnExit(e); }
}
