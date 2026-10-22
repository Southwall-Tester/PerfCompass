using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunRecoveryStartupUiTest()
    {
        string output = Path.GetFullPath("artifacts/ui"); Directory.CreateDirectory(output);
        var lines = new List<string>();
        void Check(bool condition, string name) { lines.Add((condition ? "PASS " : "FAIL ") + name); }
        try
        {
            Check(paused && PauseButton.Content.ToString() == "继续记录", "Recovered data starts paused before telemetry collection");
            Check(store.RetentionProtected && store.ExpiredHistoryFiles().Length > 0, "Old restored records survive opening the actual app");
            Check(RecoveryBanner.Visibility == Visibility.Visible && SettingsPage.Visibility == Visibility.Visible, "Recovery warning and settings are visible on startup");
            Check(store.SettingsRecoveryPending && ConfirmRecoveredSettingsButton.Visibility == Visibility.Visible, "Corrupt restored settings expose an explicit review action");
            Check(store.Settings.FutureNeeds == "恢复启动检查原文" && !store.Settings.Representative, "Startup uses previous valid settings without inventing representative coverage");
            ConfirmRecoveredSettingsButton.BringIntoView(); await Task.Delay(200); UpdateLayout();
            var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "21-recovery-startup.png"))) encoder.Save(file);
            ConfirmRecoveredSettingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!store.SettingsRecoveryPending && store.RetentionProtected && Directory.GetFiles(Path.Combine(store.Root, "recovery")).Length >= 1, "Review action saves recovered settings while retaining originals and restored-history protection");
        }
        catch (Exception e) { lines.Add("FAIL " + e); }
        finally
        {
            File.WriteAllLines(Path.Combine(output, "recovery-startup-results.txt"), lines);
            Environment.ExitCode = lines.Any(x => x.StartsWith("FAIL")) ? 1 : 0; Exit();
        }
    }
}
