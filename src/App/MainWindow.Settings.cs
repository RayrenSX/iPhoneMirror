using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private bool _projectionPhoneFrameBusy;
    private bool _refreshingFrameColor;

    private void InitializeSettingsCategoryVisibility()
    {
        // Hidden tray startup can initialize these collapsed styles before
        // ElementName bindings resolve. Bind to the actual tab objects so the
        // first workspace display does not depend on a prior Loaded/layout pass.
        var converter = new BooleanToVisibilityConverter();
        Bind(ApplicationSettingsScrollViewer, ApplicationSettingsTab);
        Bind(ProjectionSettingsScrollViewer, ProjectionSettingsTab);
        Bind(ControlSettingsScrollViewer, ControlSettingsTab);

        void Bind(ScrollViewer page, RadioButton tab) => page.SetBinding(VisibilityProperty,
            new Binding(nameof(RadioButton.IsChecked))
            {
                Source = tab,
                Mode = BindingMode.OneWay,
                Converter = converter,
            });
    }

    private void OnProjectionFrameColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingFrameColor || ProjectionFrameColorComboBox.SelectedValue is not string color) return;
        var success = _secondaryMirrors.SetSelectedFrameColor(color);
        RefreshProjectionPhoneFrame();
        ProjectionFrameColorFeedback.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshProjectionPhoneFrame()
    {
        if (_secondaryMirrors is null || ProjectionPhoneFrameCheckBox is null) return;
        var state = _secondaryMirrors.GetPhoneFrameState();
        ProjectionPhoneFrameCheckBox.IsChecked = state.Enabled;
        ProjectionPhoneFrameCheckBox.IsEnabled = state.Available && !_projectionPhoneFrameBusy;
        ProjectionPhoneFrameDescription.Text = state.Description;
        _refreshingFrameColor = true;
        try
        {
            var options = Services.DeviceFrameCatalog.ColorOptions(_viewModel.SelectedDevice?.ProductType);
            if (ProjectionFrameColorComboBox.ItemsSource is not Services.DeviceFrameColorOption[] old || !old.SequenceEqual(options))
                ProjectionFrameColorComboBox.ItemsSource = options;
            ProjectionFrameColorComboBox.SelectedValue = _secondaryMirrors.SelectedFrameColor;
            ProjectionFrameColorComboBox.IsEnabled = _secondaryMirrors.CanSelectFrameColor;
        }
        finally { _refreshingFrameColor = false; }
    }

    private async void OnProjectionPhoneFrameClick(object sender, RoutedEventArgs e)
    {
        if (_projectionPhoneFrameBusy) return;
        var enabled = ProjectionPhoneFrameCheckBox.IsChecked == true;
        _projectionPhoneFrameBusy = true;
        ProjectionPhoneFrameCheckBox.IsEnabled = false;
        bool success = false;
        try { success = await _secondaryMirrors.SetPhoneFrameAsync(enabled); }
        catch (Exception error) { Services.DiagnosticLogger.Exception("window", "phone_frame_setting_failed", error); }
        finally
        {
            _projectionPhoneFrameBusy = false;
            RefreshProjectionPhoneFrame();
            ProjectionPhoneFrameFeedback.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        }
    }

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
        if (!_viewModel.HasMirroringVideo) return;
        RestoreSettingsCategory("projection");
        SetSettingsPanelVisible(true);
    }
}
