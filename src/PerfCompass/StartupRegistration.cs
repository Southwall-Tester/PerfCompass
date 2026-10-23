using Microsoft.Win32;

namespace PerfCompass;

public record StartupState(bool Exists, object? Value, RegistryValueKind Kind = RegistryValueKind.String)
{
    public bool EquivalentTo(StartupState other) => Exists == other.Exists && (!Exists || Kind == other.Kind
        && (object.Equals(Value, other.Value) || Value is Array a && other.Value is Array b && a.Cast<object>().SequenceEqual(b.Cast<object>())));
}

public static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled()
    {
        var state = Read();
        return state.Exists && state.Value is string value && (state.Kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(value) : value)
            .Contains(Environment.ProcessPath ?? "PerfCompass.exe", StringComparison.OrdinalIgnoreCase);
    }
    public static StartupState Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        object? value = key?.GetValue("PerfCompass", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value == null ? new(false, null) : new(true, value, key!.GetValueKind("PerfCompass"));
    }
    public static StartupState Requested(bool enabled) => new(enabled, enabled ? $"\"{Environment.ProcessPath}\" --minimized" : null);
    public static void Apply(StartupState state)
    {
        using var key = state.Exists ? Registry.CurrentUser.CreateSubKey(Key) : Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (state.Exists) key!.SetValue("PerfCompass", state.Value!, state.Kind);
        else key?.DeleteValue("PerfCompass", false);
    }
    public static void Set(bool enabled) => Apply(Requested(enabled));
}
