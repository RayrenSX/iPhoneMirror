using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private void RestoreSettingsCategory(string? category)
    {
        var tab = category switch
        {
            "application" => ApplicationSettingsTab,
            "control" => ControlSettingsTab,
            _ => ProjectionSettingsTab,
        };
        tab.IsChecked = true;
    }

    private void OnSettingsCategoryChecked(object sender, RoutedEventArgs e)
    {
        // Initial XAML construction and restoring preferences must not save over
        // the stored category before all named controls have been initialized.
        if (!_workspaceControlsReady || sender is not RadioButton { Tag: string category } ||
            Application.Current is not App app || app.UpdateSettings.SettingsCategory == category)
            return;

        var previous = app.UpdateSettings.SettingsCategory;
        app.UpdateSettings.SettingsCategory = category;
        if (!app.SaveUpdateSettings()) app.UpdateSettings.SettingsCategory = previous;
    }

    private void OnOpenProjectionSettingsClick(object sender, RoutedEventArgs e)
    {
        RestoreSettingsCategory("projection");
        SetSettingsPanelVisible(true);
    }
}
