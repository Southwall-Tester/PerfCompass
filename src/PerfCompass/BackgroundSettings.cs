using System.IO;

namespace PerfCompass;

internal sealed class BackgroundSettingsException(Exception cause, bool rollbackFailed) : Exception("Background settings were not committed.", cause)
{
    public bool RollbackFailed { get; } = rollbackFailed;
}

internal static class BackgroundSettings
{
    // Registry and a settings file cannot form one atomic transaction. Apply startup first,
    // verify it, then commit settings. On any failure, restore and verify the exact old value.
    internal static void Save(LocalStore store, Settings value, bool startup, Func<StartupState> read, Action<StartupState> write)
    {
        _ = SettingsFile.Encode(value);
        var previous = read();
        var desired = StartupRegistration.Requested(startup);
        bool attempted = false;
        try
        {
            if (!previous.EquivalentTo(desired)) { attempted = true; write(desired); }
            if (!read().EquivalentTo(desired)) throw new IOException("Startup setting verification failed.");
            store.SaveSettings(value);
        }
        catch (Exception cause)
        {
            bool failed;
            try
            {
                var current = read();
                if (current.EquivalentTo(previous)) failed = false;
                else if (attempted && current.EquivalentTo(desired)) { write(previous); failed = !read().EquivalentTo(previous); }
                else failed = true; // A different writer may have changed the shared startup entry.
            }
            catch { failed = true; }
            throw new BackgroundSettingsException(cause, failed);
        }
    }
}
