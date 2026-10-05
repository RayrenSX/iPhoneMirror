using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using Microsoft.Win32;

namespace IPhoneMirror.App.Windows;

public partial class KeyboardMappingWindow
{
    private bool _refreshingProfiles;
    private void RefreshProfiles()
    {
        _refreshingProfiles = true;
        ProfileBox.ItemsSource = _settings.Profiles.Select((p, i) => new ProfileOption(p.Id,
            string.IsNullOrWhiteSpace(p.Name) ? LocalizationService.Format("MappingProfileNumber", i + 1) : p.Name)).ToArray();
        ProfileBox.SelectedValue = _settings.Selected.Id;
        ProfileNameBox.Text = ((ProfileOption)ProfileBox.SelectedItem).Label;
        ModifiersBox.IsChecked = _settings.Selected.ModifiersAsButtons;
        _refreshingProfiles = false;
    }
    private sealed record ProfileOption(Guid Id, string Label);
    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingProfiles || ProfileBox.SelectedValue is not Guid id || id == _settings.Selected.Id) return;
        var next = _settings.Clone(); next.SelectedProfileId = id;
        Save(next, "profile_selected");
    }
    private void OnProfileCommand(object sender, RoutedEventArgs e)
    {
        if (IsEditing) { _editor?.Activate(); return; }
        var command = (sender as FrameworkElement)?.Tag as string;
        if (command == "import") { ImportProfile(); return; }
        if (command == "export") { ExportProfile(); return; }
        var next = _settings.Clone();
        switch (command)
        {
            case "new":
            case "copy":
                if (next.Profiles.Count >= 32) { ErrorText.Text = LocalizationService.Get("MappingInvalidProfiles"); return; }
                var profile = command == "copy" ? next.Selected.Clone() : new KeyboardMappingProfile();
                profile.Id = Guid.NewGuid();
                profile.Name = LocalizationService.Format("MappingProfileNumber", next.Profiles.Count + 1);
                profile.Mappings = profile.Mappings.Select(m => m with { Id = Guid.NewGuid() }).ToList();
                next.Profiles.Add(profile); next.SelectedProfileId = profile.Id;
                break;
            case "rename":
                next.Selected.Name = ProfileNameBox.Text.Trim();
                break;
            case "delete":
                if (next.Profiles.Count == 1) { ErrorText.Text = LocalizationService.Get("MappingKeepOneProfile"); return; }
                if (!AppPromptWindow.ConfirmDestructive(LocalizationService.Get("MappingDeleteProfile"),
                    LocalizationService.Get("MappingDeleteProfileConfirm"), LocalizationService.Get("MappingDelete"), this)) return;
                next.Profiles.Remove(next.Selected); next.SelectedProfileId = next.Profiles[0].Id;
                break;
            default: return;
        }
        Save(next, "profile_" + command);
    }
    private void OnModifiersClick(object sender, RoutedEventArgs e)
    {
        var next = _settings.Clone(); next.Selected.ModifiersAsButtons = ModifiersBox.IsChecked == true;
        Save(next, "modifier_policy_changed");
    }
    private void ImportProfile()
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new InvalidDataException();
            var imported = JsonSerializer.Deserialize<KeyboardMappingSettings>(File.ReadAllText(dialog.FileName));
            if (imported is null || imported.HadInvalidEntries || imported.Validate() is not null) throw new InvalidDataException();
            var next = _settings.Clone();
            if (next.Profiles.Count + imported.Profiles.Count > 32) throw new InvalidDataException();
            foreach (var original in imported.Profiles)
            {
                var profile = original.Clone(); profile.Id = Guid.NewGuid();
                profile.Mappings = profile.Mappings.Select(m => m with { Id = Guid.NewGuid() }).ToList();
                next.Profiles.Add(profile); next.SelectedProfileId = profile.Id;
            }
            Save(next, "profile_imported");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { ErrorText.Text = LocalizationService.Get("MappingImportFailed"); }
    }
    private void ExportProfile()
    {
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "keyboard-mapping.json", AddExtension = true, DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var exported = new KeyboardMappingSettings { Profiles = [_settings.Selected.Clone()], SelectedProfileId = _settings.Selected.Id,
                SuppressOriginalKey = _settings.SuppressOriginalKey };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(exported, new JsonSerializerOptions { WriteIndented = true }));
            ErrorText.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ErrorText.Text = LocalizationService.Get("MappingExportFailed"); }
    }
}
