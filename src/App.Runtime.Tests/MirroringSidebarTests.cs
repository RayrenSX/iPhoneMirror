using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunMirroringSidebarTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        try
        {
            var page = (FrameworkElement)owner.FindName("MirroringPanel");
            var center = (FrameworkElement)owner.FindName("CenterPanel");
            var scroll = (ScrollViewer)owner.FindName("MirroringActionsScrollViewer");
            var actions = (StackPanel)owner.FindName("MirroringActions");
            var buttons = actions.Children.OfType<Button>().ToArray();
            var ids = new[] { "SidebarWirelessControlButton", "SidebarUsbControlButton", "SidebarBluetoothControlButton", "SidebarQuickMediaOutputButton",
                "SidebarQuickImageSettingsButton", "SidebarQuickPreviewWindowButton", "SidebarQuickScreenshotButton", "SidebarCopyScreenTextButton",
                "SidebarQuickRefreshButton", "SidebarSendPreviewFilesButton", "SidebarQuickFullScreenButton" };
            InteractionAssert(buttons.Length == ids.Length && buttons.Select(AutomationProperties.GetAutomationId).SequenceEqual(ids),
                "The Mirroring page must expose all eleven actions once, including copy screen text and send files.");
            var expectedBindings = new Dictionary<string, string>
            {
                ["SidebarWirelessControlButton"] = "CanToggleWirelessControl", ["SidebarUsbControlButton"] = "CanToggleWiredControl",
                ["SidebarBluetoothControlButton"] = "CanToggleBluetoothControl", ["SidebarQuickMediaOutputButton"] = "CanUseVisualPreviewTools",
                ["SidebarQuickImageSettingsButton"] = "HasMirroringVideo",
                ["SidebarQuickPreviewWindowButton"] = "CanUseVisualPreviewTools", ["SidebarQuickScreenshotButton"] = "CanUseVisualPreviewTools",
                ["SidebarCopyScreenTextButton"] = "CanUseVisualPreviewTools", ["SidebarSendPreviewFilesButton"] = "CanUseVisualPreviewTools",
                ["SidebarQuickRefreshButton"] = "CanUseVisualPreviewTools", ["SidebarQuickFullScreenButton"] = "CanUseVisualPreviewTools",
            };
            foreach (var button in buttons)
            {
                var id = AutomationProperties.GetAutomationId(button);
                if (expectedBindings.TryGetValue(id, out var path))
                    InteractionAssert(BindingOperations.GetBinding(button, UIElement.IsEnabledProperty)?.Path.Path == path,
                        $"{id}: action enabled state changed during relocation.");
            }
            var topActions = (Panel)owner.FindName("PreviewQuickActions");
            InteractionAssert(center.IsAncestorOf((FrameworkElement)owner.FindName("EnvironmentPanel")),
                "The original preview toolbar was removed.");
            foreach (var button in buttons.Where(button => AutomationProperties.GetAutomationId(button) != "SidebarQuickRefreshButton"))
            {
                var topId = AutomationProperties.GetAutomationId(button) switch
                {
                    "SidebarCopyScreenTextButton" => "QuickCopyScreenTextButton",
                    "SidebarSendPreviewFilesButton" => "QuickSendPreviewFilesButton",
                    var id => id["Sidebar".Length..],
                };
                var top = topActions.Children.OfType<Button>().Single(candidate => AutomationProperties.GetAutomationId(candidate) == topId);
                InteractionAssert(BindingOperations.GetBinding(top, UIElement.IsEnabledProperty)?.Path.Path ==
                    BindingOperations.GetBinding(button, UIElement.IsEnabledProperty)?.Path.Path,
                    $"{topId}: the two entries do not share their enabled state.");
            }
            var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
            KeyboardCall(owner, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Mirroring"));
            KeyboardCall(owner, "SetSettingsPanelVisible", true);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(450));
            InteractionAssert(page.IsAncestorOf(actions) && !center.IsAncestorOf(actions),
                "Actions still occupy the preview area instead of the sidebar.");
            var vm = (MainViewModel)owner.DataContext;
            var device = DeviceViewModel.FromNative(new NativeDeviceInfo { Udid = "fixture-sidebar-1", Name = "Preview iPhone",
                ConnectionType = "USB", State = ConnectionState.Ready });
            ((HashSet<string>)KeyboardField(owner, "_deviceProfileGuidanceShown")).Add(device.Udid);
            vm.Devices.Add(device);
            KeyboardCall(vm, "SetSelectedDevice", device, false);
            var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
            using var handle = new NativeSessionHandle(814, ownsHandle: false);
            var capture = new DeviceCaptureState { Udid = device.Udid, Handle = handle };
            var gatedIds = new[] { "WirelessControlButton", "UsbControlButton", "QuickImageSettingsButton" };
            var gated = topActions.Children.OfType<Button>().Where(button => gatedIds.Contains(AutomationProperties.GetAutomationId(button)))
                .Concat(buttons.Where(button => gatedIds.Any(id => AutomationProperties.GetAutomationId(button) == "Sidebar" + id))).ToArray();
            void VerifyActions(bool enabled)
            {
                DrainDispatcher();
                InteractionAssert(gated.Length == 6 && gated.All(button => button.IsEnabled == enabled),
                    "Both entry points must reflect the current source's video readiness.");
            }
            ((RadioButton)owner.FindName("ApplicationSettingsTab")).IsChecked = true;
            VerifyActions(false);
            buttons[4].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(((RadioButton)owner.FindName("ApplicationSettingsTab")).IsChecked == true,
                "An unavailable picture shortcut changed the settings page.");
            sessions.Set(capture);
            try
            {
                KeyboardCall(vm, "NotifyCaptureSessionChanged");
                KeyboardCall(vm, "ObserveMirroringVideo", capture, new NativeCaptureStatus { State = CaptureState.Handshaking });
                VerifyActions(false);
                KeyboardCall(vm, "ObserveMirroringVideo", capture, new NativeCaptureStatus { State = CaptureState.Streaming });
                VerifyActions(false);
                KeyboardCall(vm, "ObserveMirroringVideo", capture, new NativeCaptureStatus { State = CaptureState.Streaming, Width = 1170, Height = 2532 });
                VerifyActions(true);
                buttons[4].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                InteractionAssert(((RadioButton)owner.FindName("ProjectionSettingsTab")).IsChecked == true,
                    "The sidebar picture shortcut must open the projection settings for a live preview.");
                capture.IsStopping = true;
                KeyboardCall(vm, "NotifyCaptureSessionChanged");
                VerifyActions(false);
            }
            finally { sessions.Remove(device.Udid); KeyboardCall(vm, "NotifyCaptureSessionChanged"); }
            owner.MinWidth = 600; owner.MinHeight = 400;
            foreach (var culture in new[] { "zh-CN", "zh-TW", "zh-HK", "en-US", "ja-JP" })
            {
                DisplayLanguage(culture);
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                {
                    ApplyTheme(typeof(App).Assembly, theme);
                    foreach (var size in new[] { new Size(1800, 900), new Size(1280, 700), new Size(900, 520), new Size(1800, 900) })
                    {
                        owner.Width = size.Width; owner.Height = size.Height;
                        owner.UpdateLayout(); AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                        scroll.ApplyTemplate(); owner.UpdateLayout();
                        InteractionAssert(buttons.All(button => button.IsVisible), $"{culture}/{theme}/{size}: sidebar actions disappeared.");
                        var previousBottom = 0.0;
                        foreach (var button in buttons)
                        {
                            var bounds = button.TransformToAncestor(actions).TransformBounds(new Rect(button.RenderSize));
                            InteractionAssert(Math.Abs(button.ActualWidth - actions.ActualWidth) < 1 && bounds.Top >= previousBottom - 1,
                                $"{culture}/{theme}/{size}: action widths differ or rows overlap.");
                            previousBottom = bounds.Bottom;
                            foreach (var text in Visuals(button).OfType<TextBlock>())
                                InteractionAssert(text.Visibility == Visibility.Visible && text.ActualWidth + 1 >= text.DesiredSize.Width - text.Margin.Left - text.Margin.Right,
                                    $"{culture}/{size}: action label '{text.Text}' is hidden or clipped: actual={text.ActualWidth}, desired={text.DesiredSize.Width}, margin={text.Margin}.");
                        }
                        InteractionAssert(scroll.ScrollableWidth < 1, "Sidebar actions overflow horizontally.");
                        buttons[^1].BringIntoView(); DrainDispatcher(); owner.UpdateLayout();
                        var last = buttons[^1].TransformToAncestor(scroll).TransformBounds(new Rect(buttons[^1].RenderSize));
                        InteractionAssert(last.Bottom <= scroll.ActualHeight + 1 && last.Top >= -1,
                            "The final action is unreachable by scrolling.");
                        scroll.ScrollToHome();
                    }
                    if (culture == "zh-CN")
                    {
                        // Show the session-only toolbar for a screenshot without starting phone capture.
                        var topFrame = (FrameworkElement)owner.FindName("EnvironmentPanel");
                        topFrame.Visibility = Visibility.Visible;
                        topActions.Visibility = Visibility.Visible;
                        owner.UpdateLayout(); DrainDispatcher();
                        InteractionAssert(topFrame.IsVisible && page.IsVisible,
                            "The original toolbar and the Mirroring sidebar cannot be shown together.");
                        SaveWindowRender(owner, Path.Combine(output, $"mirroring-actions-{culture}-{theme}.png"));
                        topFrame.ClearValue(UIElement.VisibilityProperty);
                        topActions.ClearValue(UIElement.VisibilityProperty);
                    }
                }
            }
            Console.WriteLine("PASS: all eleven sidebar actions, preserved commands/enabled bindings, five languages, both themes, narrow/short layouts and scroll reachability.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(owner); app.Shutdown(); }
    }
}
