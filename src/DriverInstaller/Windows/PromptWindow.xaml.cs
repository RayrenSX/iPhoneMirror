using System.Windows;
using System.Windows.Input;
using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller.Windows;

public partial class PromptWindow : Window
{
    public string PromptTitle { get; }
    public string PromptBody { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public Visibility CancelVisibility { get; }
    public Style ConfirmStyle { get; }

    private PromptWindow(string title, string body, string confirmText,
        string cancelText, bool showCancel, bool danger)
    {
        PromptTitle = title;
        PromptBody = body;
        ConfirmText = confirmText;
        CancelText = cancelText;
        CancelVisibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        ConfirmStyle = (Style)Application.Current.FindResource(
            danger ? "DangerButton" : "PrimaryButton");
        DataContext = this;
        InitializeComponent();
    }

    internal static bool Confirm(Window owner, string title, string body,
        string? confirmText = null, bool danger = false) =>
        new PromptWindow(title, body, confirmText ?? DriverLocalization.Get("Continue"),
            DriverLocalization.Get("Cancel"), true, danger)
            { Owner = owner }.ShowDialog() == true;

    internal static void Inform(Window owner, string title, string body,
        string? confirmText = null) =>
        new PromptWindow(title, body, confirmText ?? DriverLocalization.Get("Acknowledge"),
            string.Empty, false, false)
            { Owner = owner }.ShowDialog();

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
