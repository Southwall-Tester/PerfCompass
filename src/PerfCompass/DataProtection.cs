using System.Windows;

namespace PerfCompass;

public partial class MainWindow
{
    private DataProtectionWindow? protectionWindow;
    internal Action<DataProtectionWindow>? ProtectionWindowReady;
    private void RefreshDataProtection()
    {
        DataProtectionLabel.Text = store.SettingsNotice + (store.RetentionProtected ? " 自动清理保护中，现存记录继续保留。" + store.RetentionNotice : "")
            + "\n备份历史、需求和候选比较，恢复到新目录。";
        ConfirmRecoveredSettingsButton.Visibility = store.SettingsRecoveryPending ? Visibility.Visible : Visibility.Collapsed;
        RecoveryBanner.Visibility = store.RetentionProtected ? Visibility.Visible : Visibility.Collapsed;
        RecoveryBanner.Text = store.SettingsRecoveryPending ? "设置恢复后需核对，原件已保留。请查看记录设置。" : "历史自动清理已暂停，请在记录设置核对日期与到期文件。";
    }
    private void ConfirmRecoveredSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveSettings("recovery", store.Settings, "恢复核对未完成，保护状态继续保留。请检查文件占用或目录权限后重试。", acknowledgeRecovery: true)) return;
        RefreshDataProtection(); StatusLine.Text = "恢复设置已保存；损坏原件继续保留在 recovery 目录。";
    }
    private void DataProtection_Click(object sender, RoutedEventArgs e)
    {
        if (measurementActive || semanticCancellation != null) { StatusLine.Text = "请先结束任务测量或模型解析，再备份恢复。"; return; }
        if (protectionWindow != null) { protectionWindow.Activate(); return; }
        bool wasPaused = paused;
        if (!paused) TogglePause();
        try
        {
            protectionWindow = new(store) { Owner = this };
            ProtectionWindowReady?.Invoke(protectionWindow);
            protectionWindow.ShowDialog();
        }
        finally
        {
            protectionWindow = null; RefreshDataProtection();
            if (!wasPaused && paused && !exiting) TogglePause();
        }
    }
}
