namespace PerfCompass;

public static class WorkloadCoverage
{
    public static (bool Confirmed, string Message) For(Settings settings, string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return (false, "记录缺少统一的硬件配置标识，请在当前电脑重新采集并确认典型任务。");
        if (!settings.Representative) return (false, "完成常用及偶发的重要任务后，在记录设置中确认当前硬件配置的任务覆盖情况。");
        if (string.IsNullOrWhiteSpace(settings.RepresentativeHardwareId)) return (false, "以前的典型任务确认未绑定硬件配置，请重新勾选并保存。");
        if (settings.RepresentativeHardwareId != hardwareId) return (false, "硬件配置已改变，请在当前配置上完成典型任务后重新勾选并保存。");
        return (true, "当前硬件配置的典型任务覆盖已确认。");
    }

    public static Settings Select(Settings settings, bool confirmed, string? hardwareId)
    {
        if (confirmed && (string.IsNullOrWhiteSpace(hardwareId) || hardwareId.Length > 200)) throw new ArgumentException("硬件配置尚未识别完成，请稍后再确认典型任务。");
        return settings with { Representative = confirmed, RepresentativeHardwareId = confirmed ? hardwareId! : "" };
    }
}

public partial class MainWindow
{
    private void InitializeWorkloadCoverage()
    {
        var state = WorkloadCoverage.For(store.Settings, collector?.HardwareId);
        RepresentativeCheck.IsChecked = state.Confirmed;
        RepresentativeStatus.Text = state.Message;
    }
}
