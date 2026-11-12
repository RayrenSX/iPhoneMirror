using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewToolbarCustomizationTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "toolbar-settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        Console.WriteLine("Basic review: main ready.");
        void Settle() { DrainDispatcher(); main.UpdateLayout(); }
        try
        {
            InteractionAssert(main.FindName("WindowEditorButton") is null && main.FindName("WindowEditorPanel") is null,
                "Whole-window layout management returned.");
            var live = (Panel)main.FindName("PreviewQuickActions");
            var original = live.Children.OfType<Button>().ToArray();
            var catalog = original.Select(b => new ToolbarAction(AutomationProperties.GetAutomationId(b), AutomationProperties.GetName(b),
                ((StackPanel)b.Content).Children.OfType<IPhoneMirror.App.Controls.ThemedSymbolIcon>().First().Symbol)).ToArray();
            var screenshot = original.Single(b => AutomationProperties.GetAutomationId(b) == "QuickScreenshotButton");
            var originalContext = screenshot.DataContext; var originalEnabled = screenshot.IsEnabled;
            var baseline = new PreviewToolbarSettings();
            var allowSave = false;
            var editor = new PreviewToolbarSettingsWindow(baseline, catalog, main.ApplyPreviewToolbar, draft =>
            {
                if (!allowSave) return false;
                app.UpdateSettings.PreviewToolbar = draft.Clone();
                store.Save(app.UpdateSettings); return true;
            }) { Owner = main };
            editor.ConfigureToolbarFocus(main, (FrameworkElement)main.FindName("EnvironmentPanel"));
            Console.WriteLine("Basic review: focus configured.");
            editor.AnimateInPreview = true;
            try
            {
                editor.Show();
                Console.WriteLine("Basic review: focus shown.");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(110));
                InteractionAssert(editor.IsFocusMoving && editor.FocusProgress is > 0 and < 1 &&
                    !((FrameworkElement)editor.FindName("EditorContent")).IsHitTestVisible,
                    "Entrance did not animate or allowed editing during zoom.");
                var image = (System.Windows.Controls.Image)editor.FindName("SceneImage");
                InteractionAssert(image.Source is not null && image.Width >= main.ActualWidth - 1 && image.Height >= main.ActualHeight - 1,
                    "The transition captured only the toolbar rather than the whole main interface.");
                var motion = image.RenderTransform.Value;
                InteractionAssert(motion.M11 > 1 && Math.Abs(motion.M11 - motion.M22) < .0001,
                    "The main interface is not uniformly zoomed.");
                SaveWindowRender(editor, Path.Combine(output, "basic-entering.png"));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(450)); editor.UpdateLayout();
                InteractionAssert(!editor.IsFocusMoving && editor.FocusProgress == 1 &&
                    ((FrameworkElement)editor.FindName("EditorContent")).IsHitTestVisible && editor.SceneTargetMatrix.M11 > 1 &&
                    editor.TargetToolbarBounds.Top < editor.ActualHeight * .30,
                    "Editing did not settle near the upper toolbar region.");
                InteractionAssert(Math.Abs(((FrameworkElement)editor.FindName("SurroundShade")).Opacity - .85) < .001,
                    "Surrounding interface was not darkened.");
                InteractionAssert(editor.FindName("FullScreenButton") is null &&
                    editor.FindName("EditGroupButton") is null && ((Panel)editor.FindName("LibraryItems")).Children.Count == 8,
                    "Nonessential features remain in the basic editor.");
                ((ListBox)editor.FindName("PreviewList")).SelectedItem = ((ListBox)editor.FindName("PreviewList")).Items.Cast<ToolbarRow>().Single(x => x.Id == "QuickScreenshotButton");
                KeyboardCall(editor, "OnHide", editor, new RoutedEventArgs()); Settle();
                InteractionAssert(!live.Children.Contains(screenshot), "Basic hide failed to remove its real action.");
                InteractionAssert(((ListBox)editor.FindName("PreviewList")).SelectedItem is ToolbarRow,
                    "Hiding a selected action lost the neighboring selection needed for continued editing.");
                var toolbarAnchor = editor.TargetToolbarBounds;
                KeyboardCall(editor, "OnLibrary", editor, new RoutedEventArgs()); Settle();
                InteractionAssert(editor.TargetToolbarBounds == toolbarAnchor, "Opening the library moved the toolbar focus anchor.");
                InteractionAssert(((ScrollViewer)editor.FindName("OptionsScroll")).VerticalOffset < 1,
                    "Opening the library left its heading and first actions out of view.");
                SaveWindowRender(editor, Path.Combine(output, "basic-library.png"));
                var add = ((Panel)editor.FindName("LibraryItems")).Children.OfType<Button>().Single(b => (string)b.Tag == "QuickScreenshotButton");
                add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Settle();
                InteractionAssert(ReferenceEquals(add, ((Panel)editor.FindName("LibraryItems")).Children.OfType<Button>().Single(b => (string)b.Tag == "QuickScreenshotButton")) &&
                    ((ListBox)editor.FindName("PreviewList")).IsKeyboardFocusWithin,
                    "Library addition rebuilt its source control or left focus on its disabled add button.");
                InteractionAssert(live.Children.Contains(screenshot) && screenshot.DataContext == originalContext && screenshot.IsEnabled == originalEnabled,
                    "Adding from the component library lost button identity, binding or availability.");
                KeyboardCall(editor, "OnLibrary", editor, new RoutedEventArgs()); Settle();
                editor.MoveItem("QuickScreenshotButton", -1); Settle();
                InteractionAssert(editor.Draft.Items[^2] == "QuickScreenshotButton", "Basic arrow ordering failed.");
                var deleteTarget = (FrameworkElement)((ListBox)editor.FindName("PreviewList")).ItemContainerGenerator.ContainerFromItem(((ListBox)editor.FindName("PreviewList")).SelectedItem);
                deleteTarget.Focus();
                var delete = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(editor)!, Environment.TickCount, System.Windows.Input.Key.Delete)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                deleteTarget.RaiseEvent(delete); Settle();
                InteractionAssert(delete.Handled && !editor.Draft.Items.Contains("QuickScreenshotButton") &&
                    ((ListBox)editor.FindName("PreviewList")).SelectedItem is ToolbarRow,
                    "Delete did not hide the current action and retain a neighboring selection.");
                editor.SetItemVisible("QuickScreenshotButton", true); Settle();
                ((CheckBox)editor.FindName("IconsOnlyCheck")).IsChecked = true;
                ((CheckBox)editor.FindName("IconsOnlyCheck")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Settle();
                InteractionAssert(((StackPanel)screenshot.Content).Children.OfType<System.Windows.Controls.TextBlock>().Single().Visibility == Visibility.Collapsed,
                    "Icons-only did not update real toolbar labels.");
                InteractionAssert(!editor.SaveDraft() && ((FrameworkElement)editor.FindName("Feedback")).IsVisible,
                    "Failed save did not retain the basic settings draft.");
                allowSave = true; InteractionAssert(editor.SaveDraft(), "Saving basic toolbar settings failed.");
                InteractionAssert(JsonSerializer.Serialize(store.Load().PreviewToolbar) == JsonSerializer.Serialize(editor.Draft), "Basic settings did not survive disk roundtrip.");
                var clone = app.UpdateSettings.Clone(); clone.PreviewToolbar!.Items.Clear();
                InteractionAssert(app.UpdateSettings.PreviewToolbar!.Items.Count == 8, "Settings clone shares toolbar items.");
                foreach (var id in PreviewToolbarSettings.DefaultItems) editor.SetItemVisible(id, false);
                Settle();
                InteractionAssert(editor.Draft.Items.Count == 0 && ((FrameworkElement)editor.FindName("EmptyHint")).IsVisible,
                    "Empty toolbar cannot be understood/recovered.");
                InteractionAssert(((FrameworkElement)editor.FindName("LibraryPanel")).IsVisible,
                    "Hiding the last action did not expose the recovery library.");
                editor.SetItemVisible("QuickScreenshotButton", true); Settle();
                InteractionAssert(live.Children.Count == 1 && ReferenceEquals(live.Children[0], screenshot), "Empty toolbar recovery failed.");
                KeyboardCall(editor, "OnReset", editor, new RoutedEventArgs()); Settle();
                var selectedItem = ((ListBox)editor.FindName("PreviewList")).SelectedItem;
                if (selectedItem is not null)
                {
                    var controls = (FrameworkElement)editor.FindName("SelectionTools");
                    var selectedContainer = (FrameworkElement)((ListBox)editor.FindName("PreviewList")).ItemContainerGenerator.ContainerFromItem(selectedItem);
                    var selectedY = selectedContainer.TranslatePoint(new Point(), editor).Y;
                    InteractionAssert(controls.TranslatePoint(new Point(), editor).Y + controls.ActualHeight <= selectedY + 1 &&
                        selectedY - controls.TranslatePoint(new Point(), editor).Y - controls.ActualHeight < 16,
                        "Rebuilding preview controls left the selection tools detached from the selected button.");
                }
                SaveWindowRender(editor, Path.Combine(output, "basic-editing.png"));
                ((ListBox)editor.FindName("PreviewList")).SelectedIndex = 2; Settle();
                SaveWindowRender(editor, Path.Combine(output, "basic-selected.png"));
                editor.SetItemVisible("QuickScreenshotButton", false);
                KeyboardCall(editor, "OnCancel", editor, new RoutedEventArgs());
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                InteractionAssert(editor.IsFocusMoving && live.Children.Contains(screenshot), "Cancel did not restore the baseline before reverse zoom.");
                SaveWindowRender(editor, Path.Combine(output, "basic-leaving.png"));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
                InteractionAssert(!editor.IsVisible, "Reverse zoom left an overlay window behind.");
                editor.SetItemVisible("QuickScreenshotButton", false);
                InteractionAssert(live.Children.Contains(screenshot), "A closed editor continued to change the toolbar.");
            }
            finally { SetKeyboardField(editor, "_focusCanClose", true); editor.Close(); }

            // Opening from the real production entry and pressing close must restore its baseline.
            var entryBaseline = JsonSerializer.Serialize((PreviewToolbarSettings)KeyboardField(main, "_previewToolbarSettings"));
            app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
            {
                var modal = app.Windows.OfType<PreviewToolbarSettingsWindow>().Single();
                modal.SetItemVisible("QuickScreenshotButton", false);
                InteractionAssert(!modal.Draft.Items.Contains("QuickScreenshotButton"), "The production cancellation fixture did not edit its modal draft.");
                modal.Close();
            });
            KeyboardCall(main, "OnCustomizePreviewToolbar", main, new RoutedEventArgs()); Settle();
            InteractionAssert(JsonSerializer.Serialize((PreviewToolbarSettings)KeyboardField(main, "_previewToolbarSettings")) == entryBaseline,
                "Closing the actual modal editor failed to restore toolbar state.");

            foreach (var scenario in new[] { "early-close", "no-motion", "hidden-source" })
            {
                var source = (FrameworkElement)main.FindName("EnvironmentPanel"); var previous = source.Visibility;
                var boundary = new PreviewToolbarSettingsWindow(new(), catalog, main.ApplyPreviewToolbar, _ => true) { Owner = main };
                if (scenario == "hidden-source") source.Visibility = Visibility.Collapsed;
                boundary.ConfigureToolbarFocus(main, source); boundary.AnimateInPreview = true;
                boundary.FocusAnimationEnabled = scenario == "early-close";
                try
                {
                    boundary.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                    if (scenario == "early-close")
                    {
                        boundary.Close(); AdvanceDispatcher(TimeSpan.FromMilliseconds(850));
                        InteractionAssert(!boundary.IsVisible, "Closing mid-entrance left a modal overlay.");
                    }
                    else
                    {
                        InteractionAssert(!boundary.IsFocusMoving && ((FrameworkElement)boundary.FindName("EditorContent")).IsHitTestVisible,
                            "No-motion/hidden-source editor failed to become usable.");
                        KeyboardCall(boundary, "OnDone", boundary, new RoutedEventArgs()); Settle();
                        InteractionAssert(!boundary.IsVisible && (bool)KeyboardField(boundary, "_focusResult"), "Basic completion lost save result.");
                    }
                }
                finally { source.Visibility = previous; SetKeyboardField(boundary, "_focusCanClose", true); boundary.Close(); }
            }
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP", "zh-TW", "zh-HK" })
            {
                LocalizationService.SetLanguage(language);
                var localized = original.Select(b => new ToolbarAction(AutomationProperties.GetAutomationId(b), AutomationProperties.GetName(b),
                    ((StackPanel)b.Content).Children.OfType<IPhoneMirror.App.Controls.ThemedSymbolIcon>().First().Symbol)).ToArray();
                var render = new PreviewToolbarSettingsWindow(new(), localized, _ => { }, _ => true) { Owner = main };
                render.ConfigureToolbarFocus(main, (FrameworkElement)main.FindName("EnvironmentPanel"));
                render.Show(); Settle(); render.UpdateLayout();
                var controls = (FrameworkElement)render.FindName("SettingsPanel");
                var bounds = controls.TransformToAncestor(render).TransformBounds(new Rect(controls.RenderSize));
                var toolbar = (FrameworkElement)render.FindName("ToolbarSurface");
                var toolbarBounds = toolbar.TransformToAncestor(render).TransformBounds(new Rect(toolbar.RenderSize));
                InteractionAssert(bounds.Bottom <= render.ActualHeight + 1 && bounds.Top > toolbarBounds.Bottom + 8,
                    "Basic settings overlap the focused toolbar or escape the window: " + language);
                SaveWindowRender(render, Path.Combine(output, "basic-" + language + ".png"));
                if (language is "en-US" or "ja-JP")
                {
                    main.Width = 1280; main.Height = 700; Settle();
                    IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, 1.5, false); Settle(); render.UpdateLayout();
                    KeyboardCall(render, "OnLibrary", render, new RoutedEventArgs()); Settle(); render.UpdateLayout();
                    var library = (FrameworkElement)render.FindName("LibraryPanel");
                    var list = (ListBox)render.FindName("PreviewList");
                    var rowTops = list.Items.Cast<object>().Select(row => (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(row))
                        .Select(item => item.TranslatePoint(new Point(), list).Y).ToArray();
                    InteractionAssert(rowTops.Max() - rowTops.Min() < 1,
                        "Editing wrapped toolbar items into multiple rows at compact/large text size.");
                    InteractionAssert(render.FindName("ScrollEarlierButton") is null && render.FindName("ScrollLaterButton") is null,
                        "The focus editor reintroduced browsing arrows.");
                    var previewScroll = Visuals(list).OfType<ScrollViewer>().First();
                    InteractionAssert(previewScroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible && previewScroll.ScrollableHeight == 0 &&
                        list.Items.Cast<object>().All(row => list.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement card &&
                            card.TransformToAncestor(render).TransformBounds(new Rect(card.RenderSize)).Right <= render.ActualWidth - 12),
                        "Focused tools did not fit into one complete row without scrolling.");
                    list.SelectedIndex = list.Items.Count - 1;
                    list.Focus(); Settle();
                    render.MoveItem(((ToolbarRow)list.SelectedItem).Id, -1); Settle();
                    InteractionAssert(list.SelectedItem is ToolbarRow && list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is FrameworkElement selected &&
                        selected.TranslatePoint(new Point(), list).X >= -1 && selected.TranslatePoint(new Point(selected.ActualWidth, 0), list).X <= list.ActualWidth + 1,
                        "Moving an offscreen action failed to reveal and retain its selection.");
                    var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(render)!, Environment.TickCount, System.Windows.Input.Key.Escape)
                        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                    list.RaiseEvent(key); Settle();
                    InteractionAssert(render.IsVisible && !library.IsVisible, "Escape from the library unexpectedly discarded the editing session.");
                    KeyboardCall(render, "OnLibrary", render, new RoutedEventArgs()); Settle();
                    InteractionAssert(library.IsVisible && render.ActualWidth >= 1200 &&
                        Math.Abs(((FrameworkElement)render.FindName("EditorRoot")).ActualHeight - render.ActualHeight) < 1 &&
                        controls.ActualHeight >= 120 && controls.TransformToAncestor(render).TransformBounds(new Rect(controls.RenderSize)).Top >= 0 &&
                        ((FrameworkElement)render.FindName("DoneButton")).TransformToAncestor(render)
                        .TransformBounds(new Rect(((FrameworkElement)render.FindName("DoneButton")).RenderSize)).Bottom <= render.ActualHeight + 1,
                        "Compact/large-text editor clipped completion or hid the library.");
                    var clickable = Visuals(controls).OfType<Button>().Where(b => b.IsVisible && !((FrameworkElement)render.FindName("LibraryItems")).IsAncestorOf(b)).ToArray();
                    SaveWindowRender(render, Path.Combine(output, "basic-compact-" + language + ".png"));
                    for (var i = 0; i < clickable.Length; i++)
                    for (var j = i + 1; j < clickable.Length; j++)
                    {
                        Rect B(FrameworkElement element) => element.TransformToAncestor(controls).TransformBounds(new Rect(element.RenderSize));
                        var overlap = Rect.Intersect(B(clickable[i]), B(clickable[j]));
                        InteractionAssert(overlap.IsEmpty || overlap.Width < 1 || overlap.Height < 1,
                            $"Basic footer buttons overlap at 150% reading size: {clickable[i].Content}/{clickable[j].Content}: {overlap}.");
                    }
                    SaveWindowRender(render, Path.Combine(output, "basic-compact-" + language + ".png"));
                    IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, 1, false); main.Width = 1800; main.Height = 820; Settle();
                }
                KeyboardCall(render, "OnCancel", render, new RoutedEventArgs()); Settle();
            }
            var migrated = JsonSerializer.Deserialize<PreviewToolbarSettings>("""{"Items":["group:1","separator:1","QuickScreenshotButton"],"Groups":{"group:1":["QuickFullScreenButton","QuickRefreshFrameButton","WirelessControlButton"]},"IconsOnly":true}""")!;
            InteractionAssert(migrated.Items.SequenceEqual(["QuickFullScreenButton", "WirelessControlButton", "QuickScreenshotButton"]) && migrated.IconsOnly &&
                !JsonSerializer.Serialize(migrated).Contains("Groups"), "Experimental container configuration was not flattened to basic settings.");
            InteractionAssert(JsonSerializer.Deserialize<PreviewToolbarSettings>("""{"Items":[]}""")!.Items.Count == 0 &&
                JsonSerializer.Deserialize<PreviewToolbarSettings>("""{"Items":[42,null]}""")!.Items.Count == 8,
                "Empty/damaged settings are not handled independently.");
            Console.WriteLine("PASS basic toolbar: whole-interface uniform zoom/dimming, input gates, basic visibility/reorder/icons, save/failure/cancel/clone/migration, hidden/empty/reduced-motion, reverse/early close, five-language layouts, basic action library, no containers/window manager.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(main); app.Shutdown(); }
    }
}
