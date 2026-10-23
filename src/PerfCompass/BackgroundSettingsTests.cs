using System.IO;
using Microsoft.Win32;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunBackgroundSettingsTests(Action<string, Action> check, Action<bool, string> assert)
    {
        LocalStore Store()
        {
            var s = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-background-" + Guid.NewGuid().ToString("N")));
            s.SaveSettings(new(FutureNeeds: "保留原始需求", RetentionDays: 90)); return s;
        }
        check("Startup snapshots compare exact raw values and registry kinds", () =>
        {
            assert(new StartupState(true, "%ROOT%/old.exe", RegistryValueKind.ExpandString).EquivalentTo(new(true, "%ROOT%/old.exe", RegistryValueKind.ExpandString)), "raw expandable value changed");
            assert(!new StartupState(true, "same", RegistryValueKind.ExpandString).EquivalentTo(new(true, "same")), "registry kind ignored");
            assert(new StartupState(true, new byte[] { 1, 2 }, RegistryValueKind.Binary).EquivalentTo(new(true, new byte[] { 1, 2 }, RegistryValueKind.Binary)), "binary snapshot compared by reference");
            assert(!new StartupState(true, "").EquivalentTo(new(false, null)), "missing entry confused with an empty value");
        });
        check("A locked settings file restores exact previous startup state and keeps old settings", () =>
        {
            foreach (var prior in new[] { new StartupState(false, null), new StartupState(true, "%ROOT%/old.exe", RegistryValueKind.ExpandString), new StartupState(true, new byte[] { 3, 7 }, RegistryValueKind.Binary) })
            {
                var s = Store(); var before = s.Settings; string path = Path.Combine(s.Root, "settings.json"); byte[] bytes = File.ReadAllBytes(path);
                StartupState current = prior; bool rejected = false;
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { BackgroundSettings.Save(s, before with { RetentionDays = 30 }, true, () => current, v => current = v); }
                    catch (BackgroundSettingsException e) { rejected = !e.RollbackFailed; }
                }
                assert(rejected && current.EquivalentTo(prior) && s.Settings == before && File.ReadAllBytes(path).SequenceEqual(bytes), "failed background save left partial startup or file changes");
            }
        });
        check("Background settings commit only after startup verification and support retry", () =>
        {
            var s = Store(); StartupState current = new(false, null);
            BackgroundSettings.Save(s, s.Settings with { RetentionDays = 30, CloseToTray = false }, true, () => current, v => current = v);
            assert(current.EquivalentTo(StartupRegistration.Requested(true)) && new LocalStore(s.Root).Settings.RetentionDays == 30 && !s.Settings.CloseToTray, "successful changes not committed together");
            BackgroundSettings.Save(s, s.Settings with { RetentionDays = 60 }, false, () => current, v => current = v);
            assert(!current.Exists && s.Settings.RetentionDays == 60, "subsequent explicit disable failed");
        });
        check("Startup write failure or mismatched readback cannot commit settings", () =>
        {
            foreach (bool partialWrite in new[] { false, true })
            {
                var s = Store(); var before = s.Settings; StartupState previous = new(false, null), current = previous; int writes = 0; bool rejected = false;
                void Write(StartupState value)
                {
                    writes++;
                    if (writes == 1) { if (partialWrite) { current = value; throw new IOException("synthetic partial startup failure"); } return; }
                    current = value;
                }
                try { BackgroundSettings.Save(s, before with { RetentionDays = 30 }, true, () => current, Write); }
                catch (BackgroundSettingsException e) { rejected = !e.RollbackFailed; }
                assert(rejected && writes == (partialWrite ? 2 : 1) && current.EquivalentTo(previous) && s.Settings == before, "startup failure or failed verification became saved settings");
            }
        });
        check("Unconfirmed startup rollback is reported and a later retry can recover", () =>
        {
            var s = Store(); var before = s.Settings; StartupState current = new(false, null); int writes = 0; bool uncertain = false;
            string path = Path.Combine(s.Root, "settings.json");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { BackgroundSettings.Save(s, before with { RetentionDays = 30 }, true, () => current, v => { if (++writes > 1) throw new IOException("synthetic rollback failure"); current = v; }); }
                catch (BackgroundSettingsException e) { uncertain = e.RollbackFailed; }
            }
            assert(uncertain && s.Settings == before && current.Exists, "rollback failure hidden");
            BackgroundSettings.Save(s, before with { RetentionDays = 30 }, true, () => current, v => current = v);
            assert(s.Settings.RetentionDays == 30 && current.EquivalentTo(StartupRegistration.Requested(true)), "retry did not recover from partial state");
        });
        check("Invalid settings or unreadable startup state cause no startup writes", () =>
        {
            var s = Store(); var before = s.Settings; int reads = 0, writes = 0; bool rejected = false;
            try { BackgroundSettings.Save(s, before with { RetentionDays = 0 }, true, () => { reads++; return new(false, null); }, _ => writes++); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected && reads == 0 && writes == 0, "invalid input touched startup state");
            rejected = false;
            try { BackgroundSettings.Save(s, before with { RetentionDays = 30 }, true, () => throw new UnauthorizedAccessException("synthetic startup read denial"), _ => writes++); }
            catch (UnauthorizedAccessException) { rejected = true; }
            assert(rejected && writes == 0 && s.Settings == before, "startup read denial changed settings");
        });
        check("Unchanged startup state does not require registry write access", () =>
        {
            var s = Store(); int writes = 0;
            BackgroundSettings.Save(s, s.Settings with { RetentionDays = 30 }, false, () => new(false, null), _ => { writes++; throw new UnauthorizedAccessException("synthetic write denial"); });
            assert(writes == 0 && s.Settings.RetentionDays == 30, "unchanged startup forced an unnecessary registry write");
        });
        check("Rollback does not overwrite a concurrent different startup value", () =>
        {
            var s = Store(); var before = s.Settings; StartupState current = new(false, null), external = new(true, "synthetic-other-writer"); int reads = 0, writes = 0; bool uncertain = false;
            using (var held = new FileStream(Path.Combine(s.Root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { BackgroundSettings.Save(s, before with { RetentionDays = 30 }, true, () => { if (++reads == 3) current = external; return current; }, v => { writes++; current = v; }); }
                catch (BackgroundSettingsException e) { uncertain = e.RollbackFailed; }
            }
            assert(uncertain && writes == 1 && current.EquivalentTo(external) && s.Settings == before, "compensation overwrote another writer or concealed uncertainty");
        });
    }
}
