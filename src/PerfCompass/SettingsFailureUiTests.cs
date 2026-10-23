using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunSettingsFailureUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Click(SettingsNav); RequirementsConfirm.IsChecked = false;
        if (paused) TogglePause();
        var before = store.Settings; string path = Path.Combine(store.Root, "settings.json"); byte[] oldBytes = File.ReadAllBytes(path);
        var oldRead = ReadStartup; var oldWrite = WriteStartup; bool oldCheck = StartupCheck.IsChecked == true;
        StartupState previous = new(true, "%SYNTHETIC%/previous.exe", RegistryValueKind.ExpandString), startup = previous;
        ReadStartup = () => startup; WriteStartup = v => startup = v;
        try
        {
            string desired = "准备学习视频剪辑"; FutureBox.Text = desired;
            int newScene = (SceneCombo.SelectedIndex + 1) % SceneCombo.Items.Count;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click(SaveRequirementsButton);
                require(RequirementsStatus.Text.Contains("未保存") && FutureBox.Text == desired && store.Settings == before && SettingsErrorPanel.Visibility == Visibility.Visible,
                    "Locked settings save retains typed input and previous active settings without reporting success");
                var time = live!.Sample.Time;
                // Resuming changes the recording generation; the collector first discards
                // its previous batch before the next real sample can reach the UI.
                for (int i = 0; i < 150 && live!.Sample.Time <= time; i++) await Task.Delay(100);
                require(live!.Sample.Time > time && SettingsErrorPanel.Visibility == Visibility.Visible && SettingsErrorBanner.Text.Contains("需求设置未保存"),
                    "Live telemetry refresh does not erase the persistent settings failure");
                ((FrameworkElement)RepresentativeStatus.Parent).BringIntoView(new Rect(0, 0, RepresentativeStatus.ActualWidth, 220)); await screenshot("28-settings-write-blocked");
                SceneCombo.SelectedIndex = newScene;
                require(store.Settings.Scene == before.Scene && ((ComboBoxItem)SceneCombo.SelectedItem).Content.ToString() == before.Scene && SettingsErrorBanner.Text.Contains("场景未切换"),
                    "Failed scene switch returns the selector to the scene actually recorded");
                StartupCheck.IsChecked = true; CloseToTrayCheck.IsChecked = !before.CloseToTray;
                RetentionCombo.SelectedItem = RetentionCombo.Items.OfType<ComboBoxItem>().Single(i => i.Tag.ToString() == "30");
                Click(SaveBackgroundButton);
                require(startup.EquivalentTo(previous) && store.Settings == before && BackgroundSaveStatus.Text.Contains("未保存") && BackgroundSaveStatus.Text.Contains("已恢复原状态"),
                    "Failed background save restores exact simulated startup state and retains saved preferences");
                const string source = "内存至少32GB"; FutureBox.Text = source;
                ShowSemanticDraft(new(source, "synthetic-save-fault", "unit.invalid", [new("内存", "resource", "required", "current", "至少32", "GB", "", source)], []));
                ((SemanticFactEditor)SemanticEditors.Items[0]).Include = true; SemanticReviewConfirm.IsChecked = true;
                Click(SaveSemanticButton);
                require(SemanticStatus.Text.Contains("未保存") && store.Settings == before && ((SemanticFactEditor)SemanticEditors.Items[0]).Include && SemanticReviewConfirm.IsChecked == true,
                    "Failed reviewed-fact save retains the editable selected draft without changing stored requirements");
            }
            require(File.ReadAllBytes(path).SequenceEqual(oldBytes), "Failed native saves preserve the exact previous settings file bytes");
            Click(SaveSemanticButton); await wait(() => report!.SemanticRequirements?.Source == "内存至少32GB");
            require(store.Settings.SemanticRequirements?.Source == "内存至少32GB" && !SettingsErrorBanner.Text.Contains("需求设置未保存") && !SettingsErrorBanner.Text.Contains("核对的事项未保存") && SettingsErrorBanner.Text.Contains("后台设置未保存"),
                "Retry saves reviewed facts and clears only failures resolved by that save");
            SceneCombo.SelectedIndex = newScene; Click(SaveBackgroundButton);
            require(store.Settings.Scene == ((ComboBoxItem)SceneCombo.SelectedItem).Content.ToString() && store.Settings.RetentionDays == 30 && startup.EquivalentTo(StartupRegistration.Requested(true))
                && SettingsErrorPanel.Visibility == Visibility.Collapsed && BackgroundSaveStatus.Text.Contains("已保存"),
                "Explicit scene and background retries commit pending values and clear resolved failures");
            ((FrameworkElement)BackgroundSaveStatus.Parent).BringIntoView(new Rect(0, 0, BackgroundSaveStatus.ActualWidth, 350)); await screenshot("29-settings-retry-saved");
            File.WriteAllText(Path.Combine(output, "settings-failure-notice.txt"), "Actual settings-file exclusive locks and native WPF controls on isolated test data. Startup registry reads/writes use an injected in-memory snapshot; no Windows startup registration was changed. No real model request was made.");
        }
        finally { ReadStartup = oldRead; WriteStartup = oldWrite; StartupCheck.IsChecked = oldCheck; }
    }
}
