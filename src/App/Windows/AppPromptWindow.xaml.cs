using System.Windows;
using System.Windows.Input;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

public partial class AppPromptWindow : Window
{
    public string PromptTitle { get; }
    public string PromptBody { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public Visibility CancelVisibility { get; }

    private AppPromptWindow(string title, string body, bool showCancel)
    {
        PromptTitle = title;
        PromptBody = body;
        ConfirmText = LocalizationService.Get(showCancel ? "Continue" : "Close");
        CancelText = LocalizationService.Get("Cancel");
        CancelVisibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        DataContext = this;
        InitializeComponent();
    }

    internal static bool Confirm(string title, string body) =>
        new AppPromptWindow(title, body, true) { Owner = Application.Current.MainWindow }
            .ShowDialog() == true;

    internal static void Inform(string title, string body) =>
        new AppPromptWindow(title, body, false) { Owner = Application.Current.MainWindow }
            .ShowDialog();

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
