using System.IO;
using System.Windows;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunHardwareSnapshotUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot)
    {
        var healthy = live!; var originalUpdate = collector!.HardwareUpdateOverride;
        async Task WaitFor(Func<bool> ready)
        {
            for (int i = 0; i < 150; i++) { if (ready()) return; await Task.Delay(100); }
            throw new TimeoutException("Live collector did not reach the expected sensor-refresh state.");
        }
        try
        {
            collector.HardwareUpdateOverride = _ => throw new IOException("Synthetic test fault before hardware refresh; physical device settings were not changed.");
            await WaitFor(() => live!.Sample.Time > healthy.Sample.Time && live.SensorFailures.Length > 0);
            require(live!.Sample.Cpu.HasValue && live.Sample.MemoryTotalGb > 0 && live.Sample.Gpu == null && live.Sample.VramUsedGb == null
                && live.Sample.VramTotalGb == null && live.Sample.CpuTemp == null && live.Sample.GpuTemp == null,
                "Actual collector drops cached hardware values on injected update failure while Windows CPU and memory continue");
            require(SensorHealthBanner.Visibility == Visibility.Visible && SensorHealthBanner.Text.Contains("本次传感器更新失败")
                && GpuValue.Text == "—" && GpuBar.Value == 0 && !StatusLine.Text.Contains("采集正常"),
                "Native live page distinguishes failed sensor readings from zero load and reports automatic retry");
            await screenshot("30-sensor-refresh-failed");
        }
        finally { collector.HardwareUpdateOverride = originalUpdate; }
        var failedAt = live!.Sample.Time;
        await WaitFor(() => live!.Sample.Time > failedAt && live.SensorFailures.Length == 0);
        require(SensorHealthBanner.Visibility == Visibility.Collapsed && live!.Sample.Cpu.HasValue && live.Sample.MemoryTotalGb > 0
            && (!healthy.Sample.Gpu.HasValue || live.Sample.Gpu.HasValue),
            "Next successful real sensor poll restores readings and clears the live failure notice");
        await screenshot("31-sensor-refresh-recovered");
        File.WriteAllText(Path.Combine(output, "sensor-refresh-notice.txt"), "Real Collector and native WPF live page on isolated test data. A synthetic exception is injected before hardware Update calls; device settings and drivers are not modified. Windows CPU/memory sampling continues. This is not a physical driver-failure or suspend/resume test, and a successful Update call cannot by itself establish sensor-value freshness inside a driver.");
    }
}
