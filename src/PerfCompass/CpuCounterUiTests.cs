using System.IO;
using System.Windows;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunCpuCounterUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot)
    {
        var original = collector!.CpuReadOverride; var healthy = live!;
        async Task WaitFor(Func<bool> ready)
        {
            for (int i = 0; i < 150; i++) { if (ready()) return; await Task.Delay(100); }
            throw new TimeoutException("Live collector did not reach the expected CPU-counter state.");
        }
        try
        {
            // A deliberately incomplete frame enters the production Collector. The
            // real OS counters, processor affinity and hardware remain unchanged.
            collector.CpuReadOverride = () => new(2, 1, [new(100, 100, 0)]);
            await WaitFor(() => live!.Sample.Time > healthy.Sample.Time && live.CpuObservationIssue.Length > 0);
            require(live!.Sample.Cpu == null && live.Sample.CoreMax == null && live.Sample.MemoryTotalGb > 0
                && (!healthy.Sample.Gpu.HasValue || live.Sample.Gpu.HasValue) && live.Sample.Seconds is > 0 and <= 15,
                "Production collector rejects incomplete CPU counters while independent real memory and GPU polling continue");
            require(CpuValue.Text == "—" && CpuDetail.Text.Contains("—") && CpuObservationBanner.Visibility == Visibility.Visible
                && CpuObservationBanner.Text.Contains("本次整机与最忙单核占用未知"),
                "Native live page displays unknown CPU and its incomplete-counter reason instead of zero load");
            await screenshot("43-cpu-counters-incomplete");
        }
        finally { collector.CpuReadOverride = original; }
        var failedAt = live!.Sample.Time;
        await WaitFor(() => live!.Sample.Time > failedAt && live.Sample.Cpu.HasValue && live.CpuObservationIssue.Length == 0);
        require(CpuObservationBanner.Visibility == Visibility.Collapsed && CpuValue.Text != "—" && live!.Sample.CoreMax.HasValue,
            "Real CPU sampling recovers after rebuilding a complete native counter baseline");
        await screenshot("44-cpu-counters-recovered");
        File.WriteAllText(Path.Combine(output, "cpu-counter-notice.txt"), "An incomplete CPU counter frame is injected into the production Collector on isolated data. Memory and GPU use actual hardware paths. Native controls, recovery and rendering are exercised. No OS counters, affinity, drivers or power state are changed. This is not a physical processor hotplug, multi-group hardware, suspend/resume or kernel-fault test.");
    }
}
