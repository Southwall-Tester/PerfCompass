using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunDataProtectionUiTests(string output, Action<bool, string> require)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        async Task Capture(Window window, string name)
        {
            await Task.Delay(150); window.UpdateLayout(); var visual = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        Click(SettingsNav); DataProtectionButton.BringIntoView();
        require(DataProtectionButton.IsVisible, "Data protection has a visible entry in record settings");
        if (paused) TogglePause();
        string backup = Path.Combine(output, "native-flow.pcbackup");
        string parent = Path.Combine(output, "restores-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(parent);
        string? restored = null;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProtectionWindowReady = dialog =>
        {
            dialog.PickSave = () => backup; dialog.PickBackup = () => backup; dialog.PickParent = () => parent;
            dialog.Loaded += async (_, _) =>
            {
                try
                {
                    require(paused && dialog.Owner == this && dialog.IsVisible, "Opening real modal backup window pauses monitoring and binds the owner");
                    TogglePause(); require(paused, "Tray recording action cannot resume monitoring during backup maintenance");
                    Click(dialog.CreateBackupButton); require(dialog.Busy, "Backup exposes a busy state through actual save control");
                    await dialog.Completion;
                    require(File.Exists(backup) && dialog.OperationStatus.Text.Contains("已保存并校验") && !dialog.Busy, "Native backup saves and verifies the archive");
                    Click(dialog.SelectBackupButton); await dialog.Completion;
                    require(dialog.RestoreBackupButton.IsEnabled && dialog.BackupPreviewLabel.Text.Contains("校验通过"), "Selecting backup validates all contents before enabling restore");
                    await Capture(dialog, "18-backup-preview");
                    Click(dialog.RestoreBackupButton); await dialog.Completion;
                    restored = Directory.GetDirectories(parent, "PerfCompass-恢复-*").Single();
                    require(dialog.OpenRestoredButton.Visibility == Visibility.Visible && File.Exists(Path.Combine(restored, "restore-protection.json")), "Restore control creates a new protected data folder");
                    string? requested = null; dialog.LaunchRestored = p => requested = p; Click(dialog.OpenRestoredButton);
                    require(requested == restored, "Restored data activation passes its directory through the native action");
                    dialog.OpenRestoredButton.BringIntoView(); await Capture(dialog, "19-backup-restored");
                    require(File.ReadAllBytes(Path.Combine(restored, "comparison.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(store.Root, "comparison.json"))), "Native restore preserves comparison evidence bytes");
                    dialog.PickBackup = () => Path.Combine(output, "invalid-backup.pcbackup"); File.WriteAllText(dialog.PickBackup()!, "not a zip");
                    Click(dialog.SelectBackupButton); await dialog.Completion;
                    require(!dialog.RestoreBackupButton.IsEnabled && dialog.OperationStatus.Text.Contains("未完成"), "Invalid new backup clears old preview and cannot be restored");
                    complete.SetResult();
                }
                catch (Exception e) { complete.SetException(e); }
                finally { dialog.Close(); }
            };
        };
        try { Click(DataProtectionButton); await complete.Task; }
        finally { ProtectionWindowReady = null; }
        require(!paused && IsEnabled, "Closing backup window restores the previous recording state");
        TogglePause();
        var restoredStore = new LocalStore(restored!);
        string old = Path.Combine(restored!, $"samples-{DateTime.Today.AddDays(-120):yyyy-MM-dd}.jsonl"); File.WriteAllText(old, "isolated expired fixture");
        var protection = new DataProtectionWindow(restoredStore) { Owner = this };
        protection.Show();
        try
        {
            string? reviewed = null; protection.ConfirmCleanup = text => { reviewed = text; return false; };
            Click(protection.EnableRetentionButton);
            require(File.Exists(old) && restoredStore.RetentionProtected && reviewed?.Contains("1 个过期历史文件") == true, "Cleanup previews expired file count and cancellation keeps protected records");
            protection.EnableRetentionButton.BringIntoView(); await Capture(protection, "20-restored-retention");
            protection.ConfirmCleanup = _ => true; Click(protection.EnableRetentionButton);
            require(!File.Exists(old) && !restoredStore.RetentionProtected && !protection.EnableRetentionButton.IsEnabled, "Explicit cleanup applies only to the isolated restored directory");
        }
        finally { protection.Close(); }
        // Closing while work is in flight must cancel and wait rather than re-enable the owner early.
        var cancelled = new DataProtectionWindow(store) { Owner = this, PickSave = () => Path.Combine(output, "cancelled.pcbackup") };
        cancelled.Show(); Click(cancelled.CreateBackupButton); cancelled.Close();
        await cancelled.Completion;
        require(!cancelled.IsVisible && !cancelled.Busy && !Directory.GetFiles(output, "*.tmp").Any(), "Closing active maintenance waits for cancellation and temporary cleanup");
        File.WriteAllText(Path.Combine(output, "backup-notice.txt"), "Native WPF controls and real backup/restore of isolated UI test data. Data includes synthetic history and quotes. File/folder selections, launch action and cleanup confirmation are injected; this is not OS dialog mouse automation or a destructive action on user data.");
    }
}
