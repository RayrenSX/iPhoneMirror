using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    private static void TestCloseMainWindowToTray(App app, MainWindow main, string output)
    {
        var vm = (MainViewModel)main.DataContext;
        var savedSettings = app.UpdateSettings.Clone();
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                vm.SelectedApplicationDisplayMode = mode;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                vm.CloseMainWindowToTray = false;
                InteractionAssert(!(bool)KeyboardCall(main, "TryCloseMainWindowToTray")!,
                    "Disabled close-to-tray intercepted window closing.");

                var settings = new ProjectionSettingsWindow(vm, () => Task.CompletedTask,
                    () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => { });
                try
                {
                    settings.Show();
                    var checkbox = (CheckBox)settings.FindName("CloseMainWindowToTrayCheckBox");
                    checkbox.BringIntoView();
                    checkbox.IsChecked = true;
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    InteractionAssert(vm.CloseMainWindowToTray && app.UpdateSettings.CloseMainWindowToTray,
                        "The settings checkbox did not update the persisted preference.");
                    SaveWindowRender(settings, Path.Combine(output, $"{mode}-close-to-tray-settings.png"));
                }
                finally { settings.Close(); }

                TestCloseToTrayWithDirtyMapping(main, vm, mode);

                // A real independent media window exercises native visibility
                // without requiring a connected phone or a receiver.
                KeyboardCall(vm, "BeginMediaCast", 1d);
                SetKeyboardField(main, "_mediaCastActive", true);
                KeyboardCall(main, "ShowMediaCastPreviewWindow");
                var preview = KeyboardField(main, "_mediaCastPreviewWindow");
                var handle = (nint)KeyboardField(preview, "Handle");
                InteractionAssert(handle != 0 && IsWindowVisible(handle), "Independent preview was not visible.");

                main.Close(); // WPF Closing also receives Alt+F4.
                main.Close(); // A second request before the dispatcher hides it must not exit.
                AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                InteractionAssert(!main.IsVisible && !main.ShowInTaskbar && vm.IsTrayApplicationMode &&
                    !(bool)KeyboardField(main, "_shutdownStarted") && KeyboardField(main, "_trayIcon") is not null,
                    $"Closing {mode} did not enter tray mode safely.");
                InteractionAssert(ReferenceEquals(preview, KeyboardField(main, "_mediaCastPreviewWindow")) &&
                    (bool)KeyboardField(main, "_mediaCastActive") && IsWindowVisible(handle),
                    "Closing the workspace interrupted or hid the independent preview.");
                InteractionAssert(app.UpdateSettings.ApplicationDisplayMode == mode,
                    "Closing to tray changed the next startup's application mode.");

                var hide = (Task)KeyboardCall(main, "ToggleBossKeyWindowsAsync")!;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                InteractionAssert(hide.IsCompletedSuccessfully && !IsWindowVisible(handle) && !main.IsVisible,
                    "Boss key did not hide the independent preview while the workspace was in the tray.");
                var show = (Task)KeyboardCall(main, "ToggleBossKeyWindowsAsync")!;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                InteractionAssert(show.IsCompletedSuccessfully && IsWindowVisible(handle) && !main.IsVisible,
                    "Boss key restore lost the independent preview or unexpectedly showed the workspace.");

                KeyboardCall(main, "ShowTrayPanel");
                var panel = (TrayPanelWindow)KeyboardField(main, "_trayPanel");
                var restore = (Button)panel.FindName("RestoreMainWindowButton");
                InteractionAssert(restore.IsVisible, "The tray panel did not offer to restore the main window.");
                SaveWindowRender(panel, Path.Combine(output, $"{mode}-close-to-tray-panel.png"));
                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                InteractionAssert(main.IsVisible && main.ShowInTaskbar &&
                    vm.SelectedApplicationDisplayMode == mode && !vm.CanRestoreMainWindowFromTray &&
                    KeyboardField(main, "_trayIcon") is null && IsWindowVisible(handle),
                    $"Restoring from tray did not restore {mode} and preserve the independent preview.");

                KeyboardCall(vm, "RequestMediaCastStop", false);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(80));

                main.Close();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
                vm.SelectedApplicationDisplayMode = mode;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
                InteractionAssert(main.IsVisible && !vm.CanRestoreMainWindowFromTray,
                    "Selecting the original mode from the tray did not restore the workspace.");

                SetKeyboardField(main, "_exitRequested", true);
                try
                {
                    InteractionAssert(!(bool)KeyboardCall(main, "TryCloseMainWindowToTray")!,
                        "Explicit exit was intercepted by close-to-tray.");
                }
                finally { SetKeyboardField(main, "_exitRequested", false); }
                typeof(App).GetProperty("IsSystemSessionEnding", KeyboardTestMembers)!.SetValue(app, true);
                try
                {
                    InteractionAssert(!(bool)KeyboardCall(main, "TryCloseMainWindowToTray")!,
                        "Windows session ending was intercepted by close-to-tray.");
                }
                finally { typeof(App).GetProperty("IsSystemSessionEnding", KeyboardTestMembers)!.SetValue(app, false); }
            }
        }
        finally
        {
            vm.RestoreMainWindowFromTray();
            app.RestoreUpdateSettings(savedSettings);
            KeyboardCall(main, "ApplyTrayMode");
        }
    }

    private static void TestCloseToTrayWithDirtyMapping(MainWindow main, MainViewModel vm,
        ApplicationDisplayMode mode)
    {
        var manager = new KeyboardMappingWindow(new(), _ => null, _ => null,
            _ => null, () => { }, () => "MappingOff") { Owner = main };
        SetKeyboardField(main, "_mappingWindow", manager);
        KeyboardMappingEditorWindow? editor = null;
        try
        {
            manager.Show();
            KeyboardCall(manager, "Edit", MappingEntry(MappedTouchAction.LongPress));
            editor = (KeyboardMappingEditorWindow)KeyboardField(manager, "_editor");
            editor.Wizard.Duration = "1200";
            main.Close();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            InteractionAssert(!main.IsVisible && vm.CanRestoreMainWindowFromTray &&
                !editor.IsEditorClosed && editor.Wizard.IsDirty && editor.Wizard.Duration == "1200" &&
                ((Border)editor.FindName("DiscardPanel")).Visibility != Visibility.Visible &&
                !(bool)KeyboardField(main, "_shutdownStarted"),
                $"Closing {mode} prompted to discard, lost the mapping draft or started shutdown.");
            vm.RestoreMainWindowFromTray();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(160));
            InteractionAssert(main.IsVisible && vm.SelectedApplicationDisplayMode == mode &&
                manager.IsVisible && editor.IsVisible && !editor.IsEditorClosed &&
                editor.Wizard.IsDirty && editor.Wizard.Duration == "1200",
                "Restoring from tray lost the original application mode or the mapping draft.");
            editor.Wizard.Duration = "900";
            InteractionAssert(editor.Wizard.Duration == "900",
                "The mapping draft could not be edited after restoring from the tray.");
        }
        finally
        {
            if (editor is { IsEditorClosed: false })
            {
                KeyboardCall(editor, "OnContinueEditingClick", editor, new RoutedEventArgs());
                DiscardWizard(editor);
            }
            manager.Close();
            SetKeyboardField(main, "_mappingWindow", null);
        }
    }
}
