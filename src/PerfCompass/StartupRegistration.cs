using Microsoft.Win32;

namespace PerfCompass;

public static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("PerfCompass") is string value && value.Contains(Environment.ProcessPath ?? "PerfCompass.exe", StringComparison.OrdinalIgnoreCase);
    }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("PerfCompass", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("PerfCompass", false);
    }
}
