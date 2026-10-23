using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunRetentionSafetyUiTests(string output, Action<bool, string> require)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        async Task Capture(Window target, string name)
        {
            await Task.Delay(150); target.UpdateLayout(); var visual = (FrameworkElement)target.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        if (!paused) TogglePause();
        var origin = DateTimeOffset.Now; var instant = new SamplingInstant(origin, 0);
        var isolated = new LocalStore(Path.Combine(store.Root, "retention-ui-fixture"), () => instant);
        string old = Path.Combine(isolated.Root, $"samples-{origin.AddDays(-120):yyyy-MM-dd}.jsonl"), recent = Path.Combine(isolated.Root, $"experience-{origin.AddDays(-1):yyyy-MM-dd}.jsonl");
        File.WriteAllText(old, "isolated expired fixture"); File.WriteAllText(recent, "isolated recent fixture");
        instant = new(origin.AddDays(200), Stopwatch.Frequency * 2); isolated.ObserveRetentionTime();
        require(isolated.RetentionProtected && File.Exists(old) && File.Exists(recent) && !isolated.RequiresPausedStart,
            "A simulated calendar jump protects cleanup while allowing monitoring to continue independently");
        var window = new DataProtectionWindow(isolated) { Owner = this }; window.Show();
        try
        {
            require(window.ProtectionLabel.Text.Contains("日期") && window.EnableRetentionButton.IsEnabled, "Native protection page explains date review and offers explicit cleanup review");
            string? preview = null;
            window.ConfirmCleanup = text => { preview = text; return false; };
            Click(window.EnableRetentionButton); await window.Completion;
            require(preview?.Contains(origin.AddDays(200).ToString("yyyy-MM-dd")) == true && preview.Contains("2 个过期历史文件") && File.Exists(old) && File.Exists(recent) && isolated.RetentionProtected,
                "Cancelling a date-bound native cleanup preview preserves both files and protection");
            window.EnableRetentionButton.BringIntoView(); await Capture(window, "39-retention-date-review");
            instant = new(origin, Stopwatch.Frequency * 3);
            window.ConfirmCleanup = text => { preview = text; File.AppendAllText(old, " changed after preview"); return true; };
            Click(window.EnableRetentionButton); await window.Completion;
            require(File.Exists(old) && File.Exists(recent) && isolated.RetentionProtected && window.OperationStatus.Text.Contains("内容已变化") && window.EnableRetentionButton.IsEnabled,
                "Actual cleanup control rejects a changed reviewed file and keeps retry available");
            await Capture(window, "40-retention-review-stale");
            window.ConfirmCleanup = _ => true; Click(window.EnableRetentionButton); await window.Completion;
            require(!File.Exists(old) && File.Exists(recent) && !isolated.RetentionProtected && !window.EnableRetentionButton.IsEnabled && window.OperationStatus.Text.Contains("已按核对名单"),
                "A fresh native review deletes only the expired fixture and then releases protection");
            require(!new LocalStore(isolated.Root, () => instant).RetentionProtected && File.Exists(recent), "Successful date review persists across reopening without deleting recent history");
            await Capture(window, "41-retention-review-completed");
        }
        finally { window.Close(); }
        File.WriteAllText(Path.Combine(output, "retention-notice.txt"), "Native cleanup controls with an isolated nested fixture directory. Calendar and elapsed inputs are injected only into that fixture's LocalStore; the operating-system clock and user data are unchanged. Preview cancellation, post-preview content change, retry, native handle deletion and reopening are exercised. This is not a power-loss, malicious filesystem race or hardware clock validation.");
    }
}
