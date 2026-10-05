using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestWizardOwnerClose(App app)
    {
        foreach (var mode in new[] { "clean-new", "clean-edit", "dirty-new", "dirty-edit", "picking" })
        {
            var saves = 0;
            var managerClosed = false;
            Action<KeyboardMappingEntry?>? completePick = null;
            var manager = new KeyboardMappingWindow(new(), _ => { saves++; return null; }, _ => null,
                _ => null, () => { }, () => "MappingReady",
                beginPick: (_, complete) => { completePick = complete; return null; },
                cancelPick: () => completePick?.Invoke(null), previewSurface: WizardSurface);
            manager.Closed += (_, _) => managerClosed = true;
            app.MainWindow = manager; manager.Show();
            KeyboardMappingEditorWindow? editor = null;
            try
            {
                KeyboardCall(manager, "Edit", new object?[] { mode.EndsWith("new") ? null : MappingEntry(MappedTouchAction.LongPress) });
                editor = (KeyboardMappingEditorWindow)KeyboardField(manager, "_editor");
                if (mode.StartsWith("dirty") || mode == "picking")
                {
                    if (mode.EndsWith("new")) editor.Wizard.SetKey(MappingTestKey);
                    else editor.Wizard.Duration = "900";
                    if (mode == "picking")
                    {
                        WizardTo(editor, MappingWizardStep.Position);
                        ((Button)editor.FindName("PickButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        MappingAssert(!manager.IsVisible && !editor.IsVisible, "Pick fixture did not hide the owner and editor.");
                    }
                }
                manager.Close();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                if (mode.StartsWith("clean"))
                {
                    MappingAssert(managerClosed && editor.IsEditorClosed && saves == 0,
                        $"Closing a clean wizard prompted or saved: {mode}.");
                    continue;
                }
                MappingAssert(!managerClosed && !editor.IsEditorClosed && manager.IsVisible && editor.IsVisible &&
                    ((Border)editor.FindName("DiscardPanel")).Visibility == Visibility.Visible && saves == 0,
                    $"Owner close lost the draft or left the confirmation hidden: {mode}.");
                MappingAssert(editor.State != MappingEditorState.PickingPosition, "Owner close left preview picking active.");
                KeyboardCall(editor, "OnContinueEditingClick", editor, new RoutedEventArgs());
                MappingAssert(editor.Wizard.IsDirty &&
                    (mode.EndsWith("new") ? editor.Wizard.Draft.Key == MappingTestKey : editor.Wizard.Duration == "900"),
                    "Continuing after owner close lost the draft.");
                // A later editor-only close must not resume a cancelled owner close.
                DiscardWizard(editor);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                MappingAssert(!managerClosed && manager.IsVisible, "Continue editing retained a pending owner close.");
                KeyboardCall(manager, "Edit", MappingEntry(MappedTouchAction.LongPress));
                editor = (KeyboardMappingEditorWindow)KeyboardField(manager, "_editor");
                editor.Wizard.Duration = "1200";
                manager.Close();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                DiscardWizard(editor);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                MappingAssert(managerClosed && editor.IsEditorClosed && saves == 0,
                    "Explicit discard did not finish the requested owner close.");
            }
            finally
            {
                if (editor is { IsEditorClosed: false })
                {
                    KeyboardCall(editor, "OnContinueEditingClick", editor, new RoutedEventArgs());
                    DiscardWizard(editor);
                }
                if (!managerClosed) manager.Close();
            }
        }
        Console.WriteLine("PASS owner close protects new/edited/hidden picking drafts; clean close, continue editing and explicit discard complete correctly.");
    }

    private static void TestWizardMainWindowClose(App app)
    {
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var manager = new KeyboardMappingWindow(new(), _ => null, _ => null, _ => null, () => { }, () => "MappingOff") { Owner = main };
        SetKeyboardField(main, "_mappingWindow", manager);
        manager.Show();
        KeyboardCall(manager, "Edit", MappingEntry(MappedTouchAction.LongPress));
        var editor = (KeyboardMappingEditorWindow)KeyboardField(manager, "_editor");
        try
        {
            editor.Wizard.Duration = "1200";
            main.Close();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            MappingAssert(main.IsVisible && !editor.IsEditorClosed &&
                ((Border)editor.FindName("DiscardPanel")).Visibility == Visibility.Visible &&
                !(bool)KeyboardField(main, "_shutdownStarted") && !(bool)KeyboardField(main, "_mappingClosing"),
                "Main-window close disposed input or discarded the wizard before confirmation.");
            KeyboardCall(editor, "OnContinueEditingClick", editor, new RoutedEventArgs());
            DiscardWizard(editor);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            MappingAssert(main.IsVisible && !(bool)KeyboardField(main, "_shutdownStarted"),
                "Continue editing retained a pending application shutdown.");
        }
        finally
        {
            if (!editor.IsEditorClosed)
            {
                KeyboardCall(editor, "OnContinueEditingClick", editor, new RoutedEventArgs());
                DiscardWizard(editor);
            }
            manager.Close(); SetKeyboardField(main, "_mappingWindow", null); CloseWorkspaceTestWindow(main);
        }
        Console.WriteLine("PASS application close waits for draft confirmation before starting shutdown or releasing input services.");
    }

    private static void TestWizardInvalidPositionFeedback(App app, string output)
    {
        foreach (var change in new[] { "rotation", "device", "reconnect" })
        {
            MappingPreviewSurface? surface = WizardSurface();
            Action<KeyboardMappingEntry?>? completePick = null;
            var editor = new KeyboardMappingEditorWindow(MappingEntry(MappedTouchAction.LongPress), [], _ => null,
                _ => null, () => { }, (_, _) => "Test save error",
                (_, complete) => { completePick = complete; return null; }, previewSurface: () => surface);
            app.MainWindow = editor; editor.Show();
            try
            {
                editor.Wizard.Duration = "1200";
                KeyboardCall(editor, "OnPreviewStatusTick", editor, EventArgs.Empty);
                WizardTo(editor, MappingWizardStep.Confirmation);
                WizardNext(editor);
                KeyboardCall(editor, "OnPreviewStatusTick", editor, EventArgs.Empty);
                MappingAssert(((TextBlock)editor.FindName("ErrorText")).Text == "Test save error",
                    "An unchanged preview cleared the save failure.");
                if (change == "reconnect")
                {
                    surface = null;
                    KeyboardCall(editor, "OnPreviewStatusTick", editor, EventArgs.Empty);
                }
                surface = change == "device" ? WizardSurface() with { Device = "other-device" }
                    : WizardSurface() with { Session = 2, Width = 600, Height = 300, Rotation = 90 };
                // A second tick must not erase the recovery instruction.
                for (var i = 0; i < 2; i++) KeyboardCall(editor, "OnPreviewStatusTick", editor, EventArgs.Empty);
                MappingAssert(editor.Wizard.Current == MappingWizardStep.Confirmation && !editor.Wizard.HasPosition &&
                    !((Button)editor.FindName("SaveButton")).IsEnabled &&
                    ((TextBlock)editor.FindName("ErrorText")).Text == LocalizationService.Get("WizardRepickRequired") &&
                    editor.Wizard.Duration == "1200", $"Missing persistent repick instruction after {change}.");
                if (change == "rotation")
                {
                    editor.Width = 480; editor.Height = 560;
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(180));
                    SaveWindowRender(editor, Path.Combine(output, "wizard-position-invalid.png"));
                    CheckWizardNavigation(editor);
                }
                WizardBack(editor);
                MappingAssert(editor.Wizard.Current == MappingWizardStep.Position &&
                    ((Button)editor.FindName("PickButton")).IsEnabled, "Repick instruction has no usable recovery path.");
                ((Button)editor.FindName("PickButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                completePick!(editor.Wizard.Draft with { X = .2, Y = .3, DeviceCoordinates = true });
                WizardNext(editor);
                MappingAssert(editor.Wizard.Current == MappingWizardStep.Confirmation &&
                    ((Button)editor.FindName("SaveButton")).IsEnabled &&
                    ((TextBlock)editor.FindName("ErrorText")).Text == "" &&
                    editor.Wizard.BuildEntry() is { X: .2, Y: .3, DurationMs: 1200 },
                    "Repicking did not clear the error and restore saving with the existing parameters.");
            }
            finally { DiscardWizard(editor); }
        }
        Console.WriteLine("PASS rotation/device/reconnection invalidation explains repicking; previous-step recovery preserves draft and restores save.");
    }
}
