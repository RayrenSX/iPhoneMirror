using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void VerifyPreviewBars(MainWindow window, App app, MainViewModel vm, string output, bool live)
    {
        var legacy = JsonSerializer.Deserialize<UpdateSettings>("{}")!;
        InteractionAssert(!legacy.HidePreviewToolbar && !legacy.HidePreviewStatusBar,
            "Legacy settings must keep both bars enabled.");

        var store = new UpdateSettingsStore(Path.GetFullPath(Path.Combine(output, "settings.json")));
        SetKeyboardField(app, "_settingsStore", store);
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        var toolbar = (FrameworkElement)window.FindName("EnvironmentPanel");
        var status = (FrameworkElement)window.FindName("StatsPanel");
        var center = (FrameworkElement)window.FindName("CenterPanel");
        var viewport = (FrameworkElement)window.FindName("PreviewViewportSlot");
        var hideToolbar = (CheckBox)window.FindName("HidePreviewToolbarCheckBox");
        var hideStatus = (CheckBox)window.FindName("HidePreviewStatusBarCheckBox");
        var sidebarScreenshot = (Button)window.FindName("SidebarQuickScreenshotButton");

        void Layout()
        {
            DrainDispatcher();
            window.UpdateLayout();
        }

        void AssertBars(bool showToolbar, bool showStatus)
        {
            Layout();
            InteractionAssert(toolbar.IsVisible == showToolbar && status.IsVisible == showStatus,
                $"Unexpected bar visibility: toolbar={toolbar.IsVisible}, status={status.IsVisible}.");
            var bounds = viewport.TransformToAncestor(center).TransformBounds(new Rect(viewport.RenderSize));
            if (!showToolbar)
                InteractionAssert(Math.Abs(bounds.Top) <= 1, "The hidden toolbar still takes space above the preview.");
            if (!showStatus)
                InteractionAssert(Math.Abs(bounds.Bottom - center.ActualHeight) <= 1, "The hidden status bar still takes space below the preview.");
        }

        KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Mirroring"));
        KeyboardCall(window, "SetSettingsPanelVisible", true);
        ((RadioButton)window.FindName("ApplicationSettingsTab")).IsChecked = true;
        FinishWorkspaceReveal(window);
        if (!live)
        {
            AssertBars(true, true);
            SetKeyboardField(vm, "_isMediaCasting", true);
            KeyboardCall(vm, "OnPropertyChanged", nameof(MainViewModel.PreviewAndObsVisibility));
            var originalWidth = window.Width;
            var originalDescriptions = vm.HideInterfaceDescriptions;
            try
            {
                foreach (var hidden in new[] { false, true })
                foreach (var width in new[] { 1800d, 1440d, 1280d })
                {
                    vm.HideInterfaceDescriptions = hidden; window.Width = width; Layout();
                    var metrics = ((Panel)window.FindName("StatisticsItems")).Children.OfType<StackPanel>().ToArray();
                    var tops = metrics.Select(item => item.TransformToAncestor(status).Transform(new Point()).Y).ToArray();
                    InteractionAssert(metrics.Length == 4 && tops.Max() - tops.Min() < 1,
                        $"The status bar wrapped audio to a second row at width {width}, descriptions hidden={hidden}.");
                    InteractionAssert(((ScrollViewer)window.FindName("StatisticsViewport")).ComputedVerticalScrollBarVisibility != Visibility.Visible,
                        "The metrics strip introduced vertical scrolling.");
                    SaveWindowRender(window, Path.Combine(output, $"status-single-row-{width}-descriptions-{!hidden}.png"));
                }
            }
            finally { vm.HideInterfaceDescriptions = originalDescriptions; window.Width = originalWidth; Layout(); }
            Console.WriteLine("PASS status row: resolution/FPS/latency/audio stay side by side at three widths with descriptions shown or hidden.");
        }

        app.IsUiPreviewMode = false;
        try
        {
            AssertBars(true, true);
            var previewHeight = viewport.ActualHeight;
            var sidebarEnabled = sidebarScreenshot.IsEnabled;
            var descriptionsHidden = vm.HideInterfaceDescriptions;
            foreach (var (topHidden, bottomHidden) in new[] { (true, false), (true, true), (false, true), (false, false) })
            {
                hideToolbar.IsChecked = topHidden;
                hideStatus.IsChecked = bottomHidden;
                AssertBars(!topHidden, !bottomHidden);
                InteractionAssert(sidebarScreenshot.IsVisible && sidebarScreenshot.IsEnabled == sidebarEnabled,
                    "Changing preview bars affected the separate sidebar actions.");
                InteractionAssert(vm.HideInterfaceDescriptions == descriptionsHidden,
                    "Changing preview bars affected the separate descriptions preference.");
                foreach (var settings in new[] { store.Load(), app.UpdateSettings.Clone() })
                    InteractionAssert(settings.HidePreviewToolbar == topHidden && settings.HidePreviewStatusBar == bottomHidden,
                        "Preview bar preferences did not persist independently.");
                if (topHidden || bottomHidden)
                    InteractionAssert(viewport.ActualHeight > previewHeight, "The preview did not grow into the hidden bar's space.");
                if (live)
                    InteractionAssert(vm.IsCapturing && vm.CurrentSessionHandle != 0, "Changing bar preferences interrupted real capture.");
            }

            hideStatus.IsChecked = true;
            KeyboardCall(window, "ToggleFullScreen");
            AssertBars(false, false);
            KeyboardCall(window, "ToggleFullScreen");
            AssertBars(true, false);
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight;
            AssertBars(false, false);
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete;
            AssertBars(true, false);
            if (live) vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, "live-phone-after.png")));
            Console.WriteLine($"PASS {(live ? "LIVE" : "simulated")}: independent bar combinations, reclaimed preview space, persistence, sidebar actions and display mode restoration.");
        }
        finally
        {
            app.IsUiPreviewMode = true;
            if (!live) SetKeyboardField(vm, "_isMediaCasting", false);
        }
    }
}
