using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static Type AuditFixType(string name) => typeof(App).Assembly.GetType("IPhoneMirror.App." + name)!;
    private static Window AuditFixWindow(string name, params object?[] args) =>
        (Window)Activator.CreateInstance(AuditFixType("Windows." + name), InteractionMembers, null, args, null)!;
    private static object? AuditFixCall(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, InteractionMembers)!.Invoke(target, args);

    private static int RunUiAuditFixTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", InteractionMembers)!.SetValue(app, true);
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var language = AuditFixType("Localization.LocalizationService").GetMethod("ApplyLanguage", InteractionMembers)!;
        void SetLanguage(string value) => language.Invoke(null, [value, false, true]);
        void Check(bool value, string description) => InteractionAssert(value, description);
        try
        {
            SetLanguage("en-US");
            IReadOnlyList<BluetoothClientInfo> clients = [new("AUDIT-ID", "Audit phone", "00:00:00:00:00:00", DateTimeOffset.Now)];
            Func<Task<IReadOnlyList<BluetoothClientInfo>>> refresh = () => Task.FromResult(clients);
            Func<string, bool> unbind = _ => false;
            var picker = AuditFixType("Windows.BluetoothClientBindingWindow");
            foreach (var action in new[] { "confirm", "cancel", "close" })
            {
                main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var window = app.Windows.Cast<Window>().Single(w => w.GetType() == picker);
                    if (action == "close") window.Close();
                    else AuditFixCall(window, action == "confirm" ? "OnConfirmClick" : "OnCancelClick", window, new RoutedEventArgs());
                });
                var result = picker.GetMethod("Show", InteractionMembers | BindingFlags.DeclaredOnly)!
                    .Invoke(null, [main, "Audit phone", clients, null, refresh, unbind]);
                Check(Equals(result, action == "confirm" ? "AUDIT-ID" : null), "Modal Bluetooth result: " + action);
            }
            var asyncResult = (Task<string?>)picker.GetMethod("ShowAsync", InteractionMembers)!
                .Invoke(null, [main, "Audit phone", clients, null, refresh, unbind])!;
            var asyncPicker = app.Windows.Cast<Window>().Single(w => w.GetType() == picker);
            AuditFixCall(asyncPicker, "OnConfirmClick", asyncPicker, new RoutedEventArgs());
            Check(asyncResult.GetAwaiter().GetResult() == "AUDIT-ID", "Modeless Bluetooth confirmation regressed.");

            Func<Task<IReadOnlyList<BluetoothClientInfo>>> failRefresh = () => Task.FromException<IReadOnlyList<BluetoothClientInfo>>(new IOException("Synthetic refresh failure"));
            var failedPicker = AuditFixWindow("BluetoothClientBindingWindow", main, "Audit phone", clients, null, failRefresh, unbind);
            failedPicker.Show();
            AuditFixCall(failedPicker, "OnRefreshClick", failedPicker, new RoutedEventArgs());
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            Check(((TextBlock)failedPicker.FindName("FeedbackText")).Text.Contains("Could not refresh"), "Bluetooth failure must be visible.");
            Check((bool)failedPicker.GetType().GetProperty("CanRefresh")!.GetValue(failedPicker)!, "Refresh must be retryable.");
            failedPicker.Close();

            AuditFixType("Windows.ShortcutSettingsWindow").GetMethod("ShowDeveloperPreview", InteractionMembers)!.Invoke(null, [main]);
            var shortcut = app.Windows.Cast<Window>().Single(w => w.GetType().Name == "ShortcutSettingsWindow");
            shortcut.UpdateLayout();
            var editor = Visuals(shortcut).OfType<TextBox>().First();
            foreach (var key in new[] { Key.Tab, Key.Escape })
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(shortcut), 0, key);
                AuditFixCall(shortcut, "OnShortcutPreviewKeyDown", editor, args);
                Check(!args.Handled, key + " must remain available for navigation/dismissal.");
            }
            shortcut.Close();

            AuditFixType("Windows.InstanceConflictWindow").GetMethod("ShowDeveloperPreview", InteractionMembers)!.Invoke(null, [main]);
            var conflict = app.Windows.Cast<Window>().Single(w => w.GetType().Name == "InstanceConflictWindow");
            AuditFixCall(conflict, "ShowCloseFailure", 2);
            var error = (TextBlock)conflict.FindName("ErrorText");
            Check(error.Visibility == Visibility.Visible && error.Text.Contains('2'), "Instance failure was hidden.");
            Check(((Button)conflict.FindName("CloseOtherInstancesButton")).IsEnabled, "Retry must be enabled after failure.");
            conflict.Close();

            foreach (var culture in new[] { "zh-CN", "en-US", "zh-HK" })
            {
                SetLanguage(culture);
                AuditFixType("Windows.ImageSettingsWindow").GetMethod("ShowDeveloperPreview", InteractionMembers)!.Invoke(null, [main]);
                var image = app.Windows.Cast<Window>().Single(w => w.GetType().Name == "ImageSettingsWindow");
                image.UpdateLayout();
                foreach (var slider in Visuals(image).OfType<Slider>())
                    Check(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(slider)?.GetName()), "Unnamed image slider: " + slider.Name);
                image.Close();
                var advanced = AuditFixWindow("AdvancedSettingsWindow", 1920u, 1080u, true);
                advanced.Owner = main;
                advanced.Show(); advanced.UpdateLayout();
                foreach (var box in Visuals(advanced).OfType<TextBox>())
                    Check(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(box)?.GetName()), "Unnamed size editor: " + box.Name);
                advanced.Close();
            }
            SetLanguage("en-US");
            var media = AuditFixWindow("MediaOutputSettingsWindow", main.DataContext, true);
            media.Owner = main; media.Show(); media.UpdateLayout();
            ((TextBlock)media.FindName("FeedbackText")).Text = "Synthetic operation feedback";
            SetLanguage("zh-HK");
            var status = (TextBlock)media.FindName("OutputStatusText");
            Check(BindingOperations.IsDataBound(status, TextBlock.TextProperty), "Feedback/language change removed live status binding.");
            var source = new AuditOutputStatus(); media.DataContext = source;
            source.MediaOutputStatus = "New running status";
            Check(status.Text == "New running status", "Live status failed to update independently of feedback.");
            var feedback = (TextBlock)media.FindName("FeedbackText");
            AuditFixCall(media, "SetFeedback", "Operation succeeded", IPhoneMirror.UI.Controls.StatusTone.Success);
            Check(SameThemeColor(feedback.Foreground, (Brush)app.FindResource("SuccessBrush")), "Successful output feedback needs the success color.");
            AuditFixCall(media, "SetFeedback", "Operation failed", IPhoneMirror.UI.Controls.StatusTone.Error);
            Check(SameThemeColor(feedback.Foreground, (Brush)app.FindResource("ErrorBrush")), "Failed output feedback needs the error color.");
            Check(BindingOperations.IsDataBound(status, TextBlock.TextProperty), "Feedback color changes must preserve running status binding.");
            media.Close();

            SetLanguage("en-US");
            var rename = AuditFixWindow("TextInputWindow", main, "DeviceBindingRenameTitle", "DeviceBindingRenamePrompt", "Phone");
            rename.Show(); rename.UpdateLayout();
            var input = (TextBox)rename.FindName("InputBox");
            Check(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(input)?.GetName()), "Rename input lacks a label.");
            input.Text = "  ";
            Check(!((Button)rename.FindName("ConfirmButton")).IsEnabled, "Blank names must not be accepted.");
            // Modeless test only inspects the UI; production Show uses ShowDialog.
            SaveWindowRender(rename, Path.Combine(output, "rename.png")); rename.Close();

            var appearance = typeof(App).Assembly.GetType("IPhoneMirror.UI.Services.AccessibilityAppearance")!
                .GetMethod("Apply", InteractionMembers)!;
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ApplyTheme(typeof(App).Assembly, theme);
                var text = new TextBlock(); text.SetResourceReference(TextBlock.FontSizeProperty, "BodyFontSize");
                var host = new Window { Content = text, Width = 200, Height = 100, ShowInTaskbar = false }; host.Show();
                appearance.Invoke(null, [app, (double?)2, (bool?)false]);
                Check(Math.Abs(text.FontSize - 26) < 0.01, "Open controls must react to text scaling.");
                var notes = (System.Windows.Documents.FlowDocument)AuditFixType("Updater.MarkdownFlowDocumentRenderer")
                    .GetMethod("Render", InteractionMembers)!.Invoke(null, ["# Release notes\n\nBody text\n\n```text\nCode example\n```"] )!;
                host.Content = new FlowDocumentScrollViewer { Document = notes, IsToolBarVisible = false };
                host.UpdateLayout();
                Check(notes.FontSize == 26 && notes.LineHeight == 42 && notes.Blocks.FirstBlock.FontSize == 44 &&
                      notes.Blocks.LastBlock.FontSize == 24, "Release-note body, headings and code must scale with system text.");
                appearance.Invoke(null, [app, (double?)1, (bool?)true]);
                Check(notes.FontSize == 13 && notes.Blocks.FirstBlock.FontSize == 22,
                    "Open release notes must update their text size without recreation.");
                host.Content = text;
                Check(((SolidColorBrush)app.FindResource("TextBrush")).Color == SystemColors.WindowTextColor, "High contrast foreground.");
                Check(((SolidColorBrush)app.FindResource("PrimaryActionTextBrush")).Color == SystemColors.HighlightTextColor, "High contrast action pair.");
                Check(((SolidColorBrush)app.FindResource("SelectionBrush")).Color == SystemColors.WindowColor,
                    "Selected tab surface must not use the same highlight color as its label.");
                var selectedInput = new TextBox { Text = "Selected input", Style = (Style)app.FindResource("RoundedTextBox") };
                host.Content = selectedInput; selectedInput.Focus(); selectedInput.SelectAll(); host.UpdateLayout();
                Check(selectedInput.SelectionOpacity == 1 &&
                      ((SolidColorBrush)selectedInput.SelectionBrush).Color == SystemColors.HighlightColor &&
                      ((SolidColorBrush)selectedInput.SelectionTextBrush).Color == SystemColors.HighlightTextColor,
                    "High contrast selection must use the opaque system highlight/text pair.");
                SaveWindowRender(host, Path.Combine(output, $"contrast-selection-{theme}.png"));
                host.Content = text;
                appearance.Invoke(null, [app, (double?)1, (bool?)false]);
                Check(Math.Abs(text.FontSize - 13) < 0.01, "Text scaling must reset without compounding."); host.Close();
            }
            appearance.Invoke(null, [app, (double?)2, (bool?)false]);
            var shortDialog = AuditFixWindow("TextInputWindow", main, "DeviceBindingRenameTitle", "DeviceBindingRenamePrompt", "Phone");
            shortDialog.MinHeight = 0; shortDialog.Height = 280;
            shortDialog.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            var title = Visuals(shortDialog).OfType<TextBlock>().First(t => t.Text == shortDialog.Title);
            var close = Visuals(shortDialog).OfType<Button>().First(b =>
                System.Windows.Automation.AutomationProperties.GetName(b) == (string)app.FindResource("Close"));
            Check(!title.TransformToAncestor(shortDialog).TransformBounds(new Rect(title.RenderSize)).IntersectsWith(
                    close.TransformToAncestor(shortDialog).TransformBounds(new Rect(close.RenderSize))),
                "Large-text title must leave room for the close button.");
            var confirm = (Button)shortDialog.FindName("ConfirmButton");
            confirm.Focus(); confirm.BringIntoView();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100)); shortDialog.UpdateLayout();
            var actionBounds = confirm.TransformToAncestor(shortDialog).TransformBounds(new Rect(confirm.RenderSize));
            Check(confirm.IsKeyboardFocused && actionBounds.Top >= 0 && actionBounds.Bottom <= shortDialog.ActualHeight,
                "Large-text dialog action must be reachable by keyboard and scrolling.");
            SaveWindowRender(shortDialog, Path.Combine(output, "large-text-action.png")); shortDialog.Close();
            appearance.Invoke(null, [app, (double?)1, (bool?)false]);

            var surface = (FrameworkElement)main.FindName("MediaCastSurface");
            var controls = (FrameworkElement)main.FindName("MediaCastControlsPanel");
            var play = (Button)main.FindName("MediaCastPlayPauseButton");
            surface.Visibility = Visibility.Visible; controls.IsEnabled = true; play.IsEnabled = true;
            main.Activate(); main.UpdateLayout();
            foreach (var field in new[] { "_mediaCastActive", "_mediaShouldPlay" })
                typeof(MainWindow).GetField(field, InteractionMembers)!.SetValue(main, true);
            foreach (var field in new[] { "_mediaBuffering", "_mediaWaitingForFirstFrame", "_mediaSeekInteraction" })
                typeof(MainWindow).GetField(field, InteractionMembers)!.SetValue(main, false);
            AuditFixCall(main, "SetMediaCastControlsVisible", false, false);
            play.Focus(); AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
            Check(play.IsKeyboardFocused && controls.Opacity > 0.99, "Keyboard focus must reveal hidden playback controls.");
            AuditFixCall(main, "OnMediaControlsHideTimerTick", null!, EventArgs.Empty);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
            Check(controls.Opacity > 0.99, "Playback controls must stay visible while keyboard focus is inside.");
            foreach (var field in new[] { "_mediaCastActive", "_mediaShouldPlay" })
                typeof(MainWindow).GetField(field, InteractionMembers)!.SetValue(main, false);
            surface.Visibility = Visibility.Collapsed;
            appearance.Invoke(null, [app, (double?)2, (bool?)false]);
            main.MinWidth = main.MinHeight = 0; main.Width = 768; main.Height = 416;
            AuditFixCall(main, "SetSettingsPanelVisible", true);
            ((FrameworkElement)main.FindName("EnvironmentPanel")).Visibility = Visibility.Visible;
            ((FrameworkElement)main.FindName("PreviewQuickActions")).Visibility = Visibility.Visible;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(400)); main.UpdateLayout();
            Check(((FrameworkElement)main.FindName("PreviewPanel")).ActualHeight >= 80,
                "The short work area must retain a usable preview with 200% text.");
            SaveWindowRender(main, Path.Combine(output, "large-text-workspace.png"));
            appearance.Invoke(null, [app, (double?)1, (bool?)false]);
            foreach (var size in new[] { 96d, 160d, 200d, 480d })
            {
                var overlay = AuditFixWindow("ProtectedContentOverlayWindow", (nint)0, "Audio remains available");
                overlay.Width = overlay.Height = size;
                overlay.Show(); overlay.UpdateLayout(); AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                var root = (FrameworkElement)overlay.Content;
                foreach (var text in Visuals(root).OfType<TextBlock>().Where(t => t.IsVisible))
                {
                    var bounds = text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
                    Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
                        $"Protected overlay text exceeds {size} DIP: {text.Text}, {bounds}");
                }
                SaveWindowRender(overlay, Path.Combine(output, $"protected-{size}.png")); overlay.Close();
            }
            Console.WriteLine("UI audit regressions passed: Bluetooth results/errors, navigation, conflict feedback, localized UIA names, live output status, rename validation, live text scaling, contrast palette and small overlays.");
            return 0;
        }
        finally
        {
            foreach (var window in app.Windows.Cast<Window>().Where(w => w != main).ToArray()) window.Close();
            CloseWorkspaceTestWindow(main); app.Shutdown();
        }
    }

    private sealed class AuditOutputStatus : INotifyPropertyChanged
    {
        private string _status = "Initial status";
        public string MediaOutputStatus
        {
            get => _status;
            set { _status = value; PropertyChanged?.Invoke(this, new(nameof(MediaOutputStatus))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
