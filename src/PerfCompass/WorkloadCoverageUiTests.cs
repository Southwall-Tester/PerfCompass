using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunCoverageUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        void ShowCoverage() => ((FrameworkElement)RepresentativeStatus.Parent).BringIntoView(new Rect(0, 0, RepresentativeStatus.ActualWidth, 200));
        Click(SettingsNav); RequirementsConfirm.IsChecked = false;
        store.SaveSettings(store.Settings with { Representative = true, RepresentativeHardwareId = "" });
        InitializeWorkloadCoverage(); await RefreshAnalysis();
        require(RepresentativeCheck.IsChecked == false && RepresentativeStatus.Text.Contains("以前") && report?.Ready == false,
            "Legacy global confirmation opens unchecked and cannot authorize surplus in the native UI");
        store.SaveSettings(store.Settings with { Representative = true, RepresentativeHardwareId = "previous-configuration" });
        InitializeWorkloadCoverage(); await RefreshAnalysis();
        require(RepresentativeCheck.IsChecked == false && RepresentativeStatus.Text.Contains("已改变") && report!.Cards.All(c => c.Status != "surplus"),
            "Changed hardware shows why old task coverage cannot be reused");
        ShowCoverage(); await screenshot("26-hardware-coverage-stale");
        RepresentativeCheck.IsChecked = true; Click(SaveRequirementsButton);
        await wait(() => report?.RepresentativeConfirmed == true);
        require(store.Settings.RepresentativeHardwareId == collector!.HardwareId && report!.Ready && RepresentativeStatus.Text.Contains("已确认"),
            "Explicit native settings save binds task coverage to the identified current configuration");
        require(WorkloadCoverage.For(new LocalStore(store.Root).Settings, collector.HardwareId).Confirmed,
            "Hardware-bound task confirmation survives settings reopening");
        string html = ReportExporter.Html(report!, live); File.WriteAllText(Path.Combine(output, "coverage-report.html"), html);
        require(html.Contains("当前硬件配置的典型任务覆盖已确认"), "Exported report states the actual hardware coverage confirmation");
        ShowCoverage(); await screenshot("27-hardware-coverage-confirmed");
        RepresentativeCheck.IsChecked = false; Click(SaveRequirementsButton); await wait(() => report?.RepresentativeConfirmed == false);
        require(!store.Settings.Representative && store.Settings.RepresentativeHardwareId.Length == 0 && !report!.Ready,
            "Removing confirmation clears the saved binding and drops analysis eligibility");
    }
}
