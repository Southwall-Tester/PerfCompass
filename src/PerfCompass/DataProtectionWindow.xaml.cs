using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PerfCompass;

public partial class DataProtectionWindow : Window
{
    private readonly LocalStore store;
    private CancellationTokenSource? cancellation;
    private bool closeRequested;
    private string? selectedBackup, restoredRoot;
    private BackupPreview? preview;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool Busy => cancellation != null;
    internal Func<string?> PickSave = () => { var d = new Microsoft.Win32.SaveFileDialog { Filter = "PerfCompass 备份|*.pcbackup", DefaultExt = ".pcbackup", FileName = $"PerfCompass-{DateTime.Now:yyyyMMdd-HHmmss}.pcbackup" }; return d.ShowDialog() == true ? d.FileName : null; };
    internal Func<string?> BeforeBackup = () => null;
    internal Func<string?> PickBackup = () => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "PerfCompass 备份|*.pcbackup", CheckFileExists = true }; return d.ShowDialog() == true ? d.FileName : null; };
    internal Func<string?> PickParent = () => { var d = new Microsoft.Win32.OpenFolderDialog { Title = "选择恢复父目录（将在其中创建新文件夹）" }; return d.ShowDialog() == true ? d.FolderName : null; };
    internal Func<string, bool> ConfirmCleanup = text => MessageBox.Show(text, "重新启用自动清理", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    internal Action<string> LaunchRestored = path =>
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(path); start.ArgumentList.Add("--paused"); Process.Start(start);
    };
    public DataProtectionWindow(LocalStore store)
    {
        this.store = store; InitializeComponent(); RefreshProtection();
        Closing += OnClosing;
    }
    private void RefreshProtection()
    {
        ProtectionLabel.Text = store.RetentionProtected ? "自动清理已暂停，现存记录继续保留。" + store.RetentionNotice + (store.SettingsRecoveryPending ? "请先在主窗口核对并保存恢复的设置。" : "") : $"按当前设置保留最近 {store.Settings.RetentionDays} 天的历史。备份文件不受此处清理影响。";
        EnableRetentionButton.IsEnabled = store.RetentionProtected && !store.SettingsRecoveryPending && !Busy;
    }
    private void SetBusy(bool value)
    {
        CreateBackupButton.IsEnabled = SelectBackupButton.IsEnabled = !value;
        RestoreBackupButton.IsEnabled = !value && preview != null;
        OpenRestoredButton.IsEnabled = !value;
        CancelWorkButton.IsEnabled = value; WorkProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        RefreshProtection();
    }
    private Task Work(string label, Func<CancellationToken, Task> action)
    {
        if (Busy) return Completion;
        cancellation = new(); SetBusy(true); OperationStatus.Text = label;
        Completion = Execute(); return Completion;
        async Task Execute()
        {
            try { await action(cancellation.Token); }
            catch (OperationCanceledException) { OperationStatus.Text = "操作已取消，当前数据保留。"; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
            { OperationStatus.Text = "操作未完成：" + e.Message; }
            finally { cancellation.Dispose(); cancellation = null; SetBusy(false); if (closeRequested) Close(); }
        }
    }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        string? blocked = BeforeBackup();
        if (blocked != null) { OperationStatus.Text = blocked; return; }
        string? path = PickSave(); if (path == null) return;
        await Work("正在保存并校验备份…", async token =>
        {
            var result = await Task.Run(() => DataBackup.Create(store, path, token), token);
            OperationStatus.Text = $"备份已保存并校验：{path}\n{result.Manifest.Files.Count} 个数据文件。";
        });
    }
    private async void Select_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return; string? path = PickBackup(); if (path == null) return;
        preview = null; selectedBackup = null; BackupPreviewLabel.Text = ""; BackupPathLabel.Text = path;
        await Work("正在校验全部备份内容…", async token =>
        {
            var result = await Task.Run(() => DataBackup.Inspect(path, token), token);
            preview = result; selectedBackup = path; BackupPreviewLabel.Text = result.Summary;
            OperationStatus.Text = "备份校验通过。请选择恢复父目录。";
        });
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || preview == null || selectedBackup == null) return;
        string? parent = PickParent(); if (parent == null) return;
        string archive = selectedBackup, fingerprint = preview.ManifestHash;
        await Work("正在重新校验并恢复到新目录…", async token =>
        {
            string root = await Task.Run(() => DataBackup.Restore(archive, parent, fingerprint, token), token);
            restoredRoot = root; RestoredPathLabel.Text = "已恢复到：\n" + root;
            OpenRestoredButton.Visibility = Visibility.Visible;
            OperationStatus.Text = "恢复完成。当前数据保留，可用恢复数据打开新窗口。";
        });
    }
    private void OpenRestored_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || restoredRoot == null) return;
        try { LaunchRestored(restoredRoot); OperationStatus.Text = "已请求打开恢复目录，新的监测窗口初始暂停。"; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { OperationStatus.Text = "打开失败：" + ex.Message; }
    }
    private async void EnableRetention_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || !EnableRetentionButton.IsEnabled) return;
        await Work("正在核对到期文件内容…", async token =>
        {
            var reviewed = await Task.Run(() => store.PreviewRetention(token), token);
            string text = $"请先核对系统日期：{reviewed.Date}。\n将重新启用最近 {reviewed.Days} 天的自动清理。\n当前会删除 {reviewed.Files.Length} 个过期历史文件。\n" + (reviewed.Files.Length > 0 ? string.Join("\n", reviewed.Files.Select(f => f.Name).Take(5)) + (reviewed.Files.Length > 5 ? "\n…" : "") + "\n" : "") + "日期、名单与内容变化时需重新核对。校验阶段可取消，删除开始后无法取消本批次。删除无法撤销，请先保存备份。是否继续？";
            token.ThrowIfCancellationRequested();
            if (!ConfirmCleanup(text)) { OperationStatus.Text = "已取消清理，保护状态继续保留。"; return; }
            OperationStatus.Text = "正在重新校验并清理核对文件…";
            await Task.Run(() => store.EnableRetention(reviewed, token), token);
            RefreshProtection(); OperationStatus.Text = "已按核对名单完成清理，并重新启用自动清理。";
        });
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!Busy) return;
        e.Cancel = true; closeRequested = true; cancellation?.Cancel(); OperationStatus.Text = "正在取消并清理临时文件，完成后关闭…";
    }
}
