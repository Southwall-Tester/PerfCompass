using System.Windows;
using System.Windows.Controls;

namespace PerfCompass;

public partial class MainWindow
{
    private readonly Dictionary<string, string> settingsFailures = [];
    private bool restoringSceneSelection;
    internal Func<StartupState> ReadStartup = StartupRegistration.Read;
    internal Action<StartupState> WriteStartup = StartupRegistration.Apply;

    private void SettingsFailure(string area, string message, Exception error)
    {
        store.Log(error); settingsFailures[area] = message; RefreshSettingsFailures();
    }
    private void ClearSettingsFailure(string area) { settingsFailures.Remove(area); RefreshSettingsFailures(); }
    private void RefreshSettingsFailures()
    {
        SettingsErrorBanner.Text = string.Join("\n", settingsFailures.Values);
        SettingsErrorPanel.Visibility = settingsFailures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool TrySaveSettings(string area, Settings value, string failure, bool acknowledgeRecovery = false)
    {
        try { store.SaveSettings(value, acknowledgeRecovery); ClearSettingsFailure(area); return true; }
        catch (Exception error) { SettingsFailure(area, failure, error); return false; }
    }
    private void SelectSavedScene()
    {
        restoringSceneSelection = true;
        try
        {
            var item = SceneCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == store.Settings.Scene);
            if (item == null) { item = new ComboBoxItem { Content = store.Settings.Scene }; SceneCombo.Items.Add(item); }
            SceneCombo.SelectedItem = item;
        }
        finally { restoringSceneSelection = false; }
    }
}
