using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task VerifyBackupPreparation(string output, string name, bool allowed,
        string expectedText, Action<bool, string> require)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        string previousPage = activePage;
        var originalReady = ProtectionWindowReady;
        string archive = Path.Combine(output, name + "-" + Guid.NewGuid().ToString("N") + ".pcbackup");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProtectionWindowReady = dialog =>
        {
            int pickCalls = 0;
            dialog.PickSave = () => { pickCalls++; return archive; };
            dialog.Loaded += async (_, _) =>
            {
                try
                {
                    Click(dialog.CreateBackupButton); await dialog.Completion;
                    bool correct;
                    if (allowed)
                    {
                        DataBackup.Inspect(archive);
                        using var zip = ZipFile.OpenRead(archive);
                        using var entry = (zip.GetEntry("comparison.json") ?? throw new IOException("Missing comparison backup entry.")).Open();
                        using var copy = new MemoryStream(); entry.CopyTo(copy);
                        var saved = JsonSerializer.Deserialize<PurchaseWorkspace>(copy.ToArray())!;
                        correct = pickCalls == 1 && !ComparisonPage.HasUnsavedDraft
                            && saved.Candidates[0].Name == expectedText
                            && copy.ToArray().SequenceEqual(File.ReadAllBytes(Path.Combine(store.Root, "comparison.json")))
                            && dialog.OperationStatus.Text.Contains("已保存并校验");
                    }
                    else
                        correct = pickCalls == 0 && !File.Exists(archive)
                            && dialog.OperationStatus.Text.Contains(expectedText)
                            && dialog.SelectBackupButton.IsEnabled;
                    dialog.UpdateLayout(); await Task.Delay(100); dialog.UpdateLayout();
                    var visual = (FrameworkElement)dialog.Content;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(Path.Combine(output, name + ".png"))) png.Save(file);
                    require(correct && !dialog.Busy && dialog.Owner == this && dialog.IsVisible,
                        allowed ? "Native backup flushes the outstanding editor and archives the complete latest candidate bytes"
                            : "Native backup blocks incomplete data before choosing a path while keeping recovery available: " + name);
                    finished.SetResult();
                }
                catch (Exception error) { finished.SetException(error); }
                finally { dialog.Close(); }
            };
        };
        try
        {
            Click(SettingsNav); UpdateLayout(); DataProtectionButton.BringIntoView();
            Click(DataProtectionButton); await finished.Task;
        }
        finally
        {
            ProtectionWindowReady = originalReady;
            if (previousPage == "comparison") Click(ComparisonNav);
            else if (previousPage == "measurement") Click(TaskMeasurementNav);
        }
    }
}
