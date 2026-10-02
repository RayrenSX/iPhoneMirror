using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App.Windows;

public partial class TextInputWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private bool _previewOnly;
    private TextInputWindow(Window owner, string titleKey, string promptKey, string value)
    {
        InitializeComponent();
        Owner = owner;
        SetResourceReference(TitleProperty, titleKey);
        InputLabel.SetResourceReference(TextBlock.TextProperty, promptKey);
        InputBox.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, promptKey);
        InputBox.Text = value;
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(value);
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    internal static string? Show(Window owner, string titleKey, string promptKey, string value)
    {
        var window = new TextInputWindow(owner, titleKey, promptKey, value);
        return window.ShowDialog() == true ? window.InputBox.Text.Trim() : null;
    }

    internal static void ShowDeveloperPreview(Window owner) =>
        new TextInputWindow(owner, "DeviceBindingRenameTitle", "DeviceBindingRenamePrompt", "iPhone")
        { _previewOnly = true }.Show();

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConfirmButton is not null)
            ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        if (_previewOnly) Close(); else DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) Close(); else DialogResult = false;
    }
}
