using System.IO;
using System.Text.Json;
using System.Windows;

namespace PerfCompass;

public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test")) { Shutdown(SelfTests.Run()); return; }
        string root = Path.Combine(AppContext.BaseDirectory, "data");
        int idx = Array.IndexOf(e.Args, "--data-dir");
        if (idx >= 0 && idx + 1 < e.Args.Length) root = Path.GetFullPath(e.Args[idx + 1]);
        if (e.Args.Contains("--ui-test") && (idx < 0 || (Directory.Exists(root) && Directory.GetFiles(root, "samples-*.jsonl").Length > 0)))
        {
            System.Windows.MessageBox.Show("UI 测试需要通过 --data-dir 指定一个没有历史记录的独立测试目录。", "PerfCompass"); Shutdown(3); return;
        }
        mutex = new Mutex(true, "Local\\PerfCompass-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..16], out bool first);
        if (e.Args.Contains("--check-instance")) { Shutdown(first ? 0 : 10); return; }
        if (!first) { System.Windows.MessageBox.Show("PerfCompass 已在运行。双击系统托盘中的图标打开窗口。", "PerfCompass"); Shutdown(); return; }
        LocalStore store;
        try { store = new LocalStore(root); }
        catch (Exception ex) { System.Windows.MessageBox.Show("无法创建数据目录：\n" + ex.Message, "PerfCompass"); Shutdown(1); return; }
        DispatcherUnhandledException += (_, args) => { store.Log(args.Exception); System.Windows.MessageBox.Show("操作未完成，请重试。错误已记录到 data/errors.log。", "PerfCompass"); args.Handled = true; };
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
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
