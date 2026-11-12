using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunToolbarDragTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "drag-settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        try
        {
            var live = (Panel)main.FindName("PreviewQuickActions");
            var original = live.Children.OfType<Button>().ToArray();
            var catalog = original.Select(b => new ToolbarAction(AutomationProperties.GetAutomationId(b), AutomationProperties.GetName(b),
                ((StackPanel)b.Content).Children.OfType<IPhoneMirror.App.Controls.ThemedSymbolIcon>().First().Symbol)).ToArray();
            var background = ((Border)main.FindName("EnvironmentPanel")).Background;
            InteractionAssert(ReferenceEquals(background, main.FindResource("WorkspaceCardBrush")) &&
                main.FindName("ToolbarTint") is null && main.FindName("VisualEffectsSettingsButton") is null,
                "Toolbar background or removed visual controls remain overridden.");
            SaveWindowRender(main, Path.Combine(output, "main-original-background.png"));
            var old = JsonSerializer.Deserialize<UpdateSettings>("""{"VisualEffects":{"FocusScale":1.8,"BackgroundColor":"#FF0000","VisibilityMode":2},"PreviewToolbar":{"Items":["QuickScreenshotButton","WirelessControlButton"]}}""")!;
            InteractionAssert(old.PreviewToolbar!.Items[0] == "QuickScreenshotButton" && !JsonSerializer.Serialize(old).Contains("VisualEffects"),
                "Old visual effects continue to apply or discard toolbar ordering.");

            var editor = new PreviewToolbarSettingsWindow(new(), catalog, main.ApplyPreviewToolbar, draft =>
            { app.UpdateSettings.PreviewToolbar = draft.Clone(); store.Save(app.UpdateSettings); return true; }) { Owner = main };
            editor.ConfigureToolbarFocus(main, (FrameworkElement)main.FindName("EnvironmentPanel"));
            editor.Show(); DrainDispatcher(); editor.UpdateLayout();
            var list = (ListBox)editor.FindName("PreviewList");
            Point Center(string id)
            {
                var row = list.Items.Cast<ToolbarRow>().Single(r => r.Id == id);
                var item = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(row);
                return item.TranslatePoint(new Point(item.ActualWidth / 2, item.ActualHeight / 2), list);
            }
            void Settle() { DrainDispatcher(); editor.UpdateLayout(); }
            try
            {
                InteractionAssert(editor.FindName("VisualSettingsPanel") is null && editor.FindName("VisualSettingsToggle") is null &&
                    editor.FindName("ScrollEarlierButton") is null && editor.FindName("ScrollLaterButton") is null &&
                    editor.FindName("EarlierButton") is null && editor.FindName("LaterButton") is null,
                    "Simple drag editor retains visual configuration or arrow controls.");
                var footer = (FrameworkElement)editor.FindName("SettingsPanel");
                InteractionAssert(footer.ActualHeight < 100, "Closed library still leaves a large settings form below the preview.");
                SaveWindowRender(editor, Path.Combine(output, "simple-focused.png"));
                var first = editor.Draft.Items[0]; var last = editor.Draft.Items[^1];
                var cardInstances = list.Items.Cast<object>().Select(row => list.ItemContainerGenerator.ContainerFromItem(row)).ToArray();
                var firstCard = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
                var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
                firstCard.RaiseEvent(press);
                InteractionAssert(press.Handled && list.IsMouseCaptureWithin && list.SelectedItem is ToolbarRow { Id: "WirelessControlButton" },
                    "The routed press did not select the actual card and capture the row for dragging.");
                editor.EndToolbarDrag(cancel: false);
                var order = editor.Draft.Items.ToArray(); var origin = Center(first);
                editor.BeginToolbarDrag(first, origin); editor.UpdateToolbarDrag(new Point(origin.X + 1, origin.Y));
                InteractionAssert(editor.Draft.Items.SequenceEqual(order) && !((Image)editor.FindName("DragPreview")).IsVisible,
                    "A normal click or tiny pointer movement started a reorder.");
                editor.EndToolbarDrag(cancel: false);

                editor.BeginToolbarDrag(first, Center(first));
                editor.UpdateToolbarDrag(new Point(Center(last).X + 100, Center(last).Y));
                InteractionAssert(editor.Draft.Items[^1] == first && live.Children.Cast<FrameworkElement>()
                    .Select(AutomationProperties.GetAutomationId).SequenceEqual(order) &&
                    cardInstances.SequenceEqual(list.Items.Cast<object>().Select(row => list.ItemContainerGenerator.ContainerFromItem(row))) &&
                    ((Image)editor.FindName("DragPreview")).Source is not null,
                    "Press/drag did not move the card across item regeneration.");
                SaveWindowRender(editor, Path.Combine(output, "dragging-to-end.png"));
                var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent };
                list.RaiseEvent(release); Settle();
                InteractionAssert(release.Handled && editor.Draft.Items[^1] == first && !list.IsMouseCaptureWithin &&
                    live.Children.Cast<FrameworkElement>().Select(AutomationProperties.GetAutomationId).SequenceEqual(editor.Draft.Items) &&
                    ((Image)editor.FindName("DragPreview")).Source is null && editor.SaveDraft() &&
                    store.Load().PreviewToolbar!.Items.SequenceEqual(editor.Draft.Items),
                    "Releasing the drag lost order/capture cleanup/persistence.");
                list.SelectedIndex = list.Items.Count - 1; Settle();
                InteractionAssert(((FrameworkElement)editor.FindName("SelectionTools")).IsVisible,
                    "The final tool lost its hide control after the row was scaled.");
                InteractionAssert(original.All(button => live.Children.Contains(button)), "Dragging recreated the original command buttons.");
                order = editor.Draft.Items.ToArray();
                editor.BeginToolbarDrag(first, Center(first)); editor.UpdateToolbarDrag(new Point(-40, Center(first).Y));
                InteractionAssert(editor.Draft.Items[0] == first, "Dragging back to the beginning failed.");
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                list.RaiseEvent(escape); Settle();
                InteractionAssert(escape.Handled && editor.IsVisible && editor.Draft.Items.SequenceEqual(order) && !list.IsMouseCaptureWithin,
                    "Escape during a drag did not restore just the pre-drag order.");
                editor.BeginToolbarDrag(first, Center(first)); editor.UpdateToolbarDrag(new Point(-40, Center(first).Y));
                list.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = Mouse.LostMouseCaptureEvent }); Settle();
                InteractionAssert(editor.Draft.Items.SequenceEqual(order) && !((Image)editor.FindName("DragPreview")).IsVisible,
                    "Losing capture left a partial drag or stale floating image.");
                editor.SetItemVisible(first, false); Settle(); KeyboardCall(editor, "OnLibrary", editor, new RoutedEventArgs()); Settle();
                SaveWindowRender(editor, Path.Combine(output, "simple-library.png"));
                KeyboardCall(editor, "OnCancel", editor, new RoutedEventArgs()); Settle();
                InteractionAssert(!editor.IsVisible && live.Children.Cast<FrameworkElement>().Select(AutomationProperties.GetAutomationId)
                    .SequenceEqual(PreviewToolbarSettings.DefaultItems), "Cancelling the editor failed to restore its initial command order.");
            }
            finally { SetKeyboardField(editor, "_focusCanClose", true); editor.Close(); }

            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP", "zh-TW", "zh-HK" })
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                LocalizationService.SetLanguage(language); ApplyTheme(typeof(App).Assembly, theme);
                main.Width = 1280; main.Height = 700;
                IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, 1.5, false); DrainDispatcher(); main.UpdateLayout();
                var localized = original.Select(b => new ToolbarAction(AutomationProperties.GetAutomationId(b), AutomationProperties.GetName(b),
                    ((StackPanel)b.Content).Children.OfType<IPhoneMirror.App.Controls.ThemedSymbolIcon>().First().Symbol)).ToArray();
                var render = new PreviewToolbarSettingsWindow(new(), localized, _ => { }, _ => true) { Owner = main };
                render.ConfigureToolbarFocus(main, (FrameworkElement)main.FindName("EnvironmentPanel")); render.Show(); DrainDispatcher(); render.UpdateLayout();
                try
                {
                    var preview = (ListBox)render.FindName("PreviewList");
                    var boxes = preview.Items.Cast<object>().Select(row => (FrameworkElement)preview.ItemContainerGenerator.ContainerFromItem(row))
                        .Select(item => item.TransformToAncestor(render).TransformBounds(new Rect(item.RenderSize))).ToArray();
                    InteractionAssert(boxes.Length == 8 && boxes.Max(b => b.Top) - boxes.Min(b => b.Top) < 1 &&
                        boxes.Min(b => b.Left) >= 12 && boxes.Max(b => b.Right) <= render.ActualWidth - 12 &&
                        Visuals(preview).OfType<ScrollViewer>().All(scroll => scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible),
                        "Tools are clipped, wrapped or scrolled in " + language + "/" + theme);
                    var done = (FrameworkElement)render.FindName("DoneButton");
                    InteractionAssert(done.TransformToAncestor(render).TransformBounds(new Rect(done.RenderSize)).Bottom <= render.ActualHeight,
                        "Simple footer hides completion in " + language);
                    SaveWindowRender(render, Path.Combine(output, "complete-row-" + language + "-" + theme + "-150.png"));
                }
                finally { SetKeyboardField(render, "_focusCanClose", true); render.Close(); }
            }
            Console.WriteLine("PASS simple toolbar: original theme background; no visual settings/arrows/scrolling; compact footer; drag threshold, insert both directions, mouse release, capture loss, Escape rollback, persistence and command identity; five languages/two themes at 150% with all eight tools visible.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(main); app.Shutdown(); }
    }
}
