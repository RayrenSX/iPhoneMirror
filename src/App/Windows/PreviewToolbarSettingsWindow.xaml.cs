using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections.ObjectModel;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using Wpf.Ui.Controls;
using CheckBox = System.Windows.Controls.CheckBox;

namespace IPhoneMirror.App.Windows;

internal sealed record ToolbarAction(string Id, string Name, SymbolRegular Symbol);
internal sealed record ToolbarRow(string Id, string Name, SymbolRegular Symbol, bool IconsOnly)
{
    public Visibility LabelVisibility => IconsOnly ? Visibility.Collapsed : Visibility.Visible;
}

public partial class PreviewToolbarSettingsWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly IReadOnlyList<ToolbarAction> _catalog;
    private readonly Action<PreviewToolbarSettings> _preview;
    private readonly Func<PreviewToolbarSettings, bool> _save;
    private readonly PreviewToolbarSettings _baseline;
    private readonly ObservableCollection<ToolbarRow> _rows = [];
    private readonly Dictionary<string, (System.Windows.Controls.Button Button, System.Windows.Controls.TextBlock Status)> _library = [];
    private int _selectionRevision;
    private bool _refreshing;
    internal PreviewToolbarSettings Draft { get; private set; }
    private static string T(string key) => LocalizationService.Get("Toolbar" + key);

    internal PreviewToolbarSettingsWindow(PreviewToolbarSettings initial, IReadOnlyList<ToolbarAction> catalog,
        Action<PreviewToolbarSettings> preview, Func<PreviewToolbarSettings, bool> save)
    {
        _catalog = catalog; _preview = preview; _save = save;
        _baseline = initial.Normalize(); Draft = _baseline.Clone();
        InitializeComponent();
        PreviewList.ItemsSource = _rows;
        BuildLibrary();
        Refresh();
        Loaded += (_, _) =>
        {
            PreviewList.ApplyTemplate();
            BeginFocusEntrance();
        };
        PreviewList.SizeChanged += (_, _) => PositionSelectionTools();
        ToolbarSurface.SizeChanged += (_, _) => UpdateEditorPlacement();
        SelectionTools.SizeChanged += (_, _) => { if (CanEdit) PositionSelectionTools(); };
        SizeChanged += (_, _) => UpdateEditorPlacement();
        SettingsPanel.SizeChanged += (_, _) => UpdateEditorPlacement();
        EditorRoot.SizeChanged += (_, _) => UpdateEditorPlacement();
        Deactivated += (_, _) => EndToolbarDrag(cancel: true);
        Closed += (_, _) => EndToolbarDrag(cancel: true);
    }

    private bool CanEdit => IsLoaded && IsVisible && EditorContent.IsHitTestVisible && !_focusMoving && !_focusExiting;
    private void Refresh(string? selection = null)
    {
        _refreshing = true;
        var previousFocus = Keyboard.FocusedElement as FrameworkElement;
        var restoreFocus = previousFocus is not null && PreviewList.IsAncestorOf(previousFocus);
        selection ??= (PreviewList.SelectedItem as ToolbarRow)?.Id;
        for (var i = _rows.Count - 1; i >= 0; i--)
            if (!Draft.Items.Contains(_rows[i].Id)) _rows.RemoveAt(i);
        for (var i = 0; i < Draft.Items.Count; i++)
        {
            var id = Draft.Items[i];
            var action = _catalog.Single(x => x.Id == id);
            var existing = _rows.FirstOrDefault(row => row.Id == id);
            if (existing is null) _rows.Insert(i, new(id, action.Name, action.Symbol, Draft.IconsOnly));
            else
            {
                var index = _rows.IndexOf(existing);
                if (index != i) _rows.Move(index, i);
                if (existing.IconsOnly != Draft.IconsOnly) _rows[i] = existing with { IconsOnly = Draft.IconsOnly };
            }
        }
        PreviewList.SelectedItem = PreviewList.Items.Cast<ToolbarRow>().FirstOrDefault(row => row.Id == selection);
        IconsOnlyCheck.IsChecked = Draft.IconsOnly;
        EmptyHint.Visibility = Draft.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _refreshing = false; UpdateSelection(); RenderLibrary();
        var revision = ++_selectionRevision;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (!CanEdit || revision != _selectionRevision) return;
            if (PreviewList.SelectedItem is { } selected)
            {
                PreviewList.ScrollIntoView(selected);
                if (restoreFocus && PreviewList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item) item.Focus();
            }
            UpdateEditorPlacement(); UpdateDragPlaceholder(); PositionSelectionTools();
        });
    }
    private void BuildLibrary()
    {
        foreach (var action in _catalog)
        {
            var button = new System.Windows.Controls.Button { Width = 266, Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10), HorizontalContentAlignment = HorizontalAlignment.Left,
                Style = (Style)FindResource("SecondaryButton"), Tag = action.Id };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new IPhoneMirror.App.Controls.ThemedSymbolIcon { Symbol = action.Symbol, FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            var labels = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            labels.Children.Add(new System.Windows.Controls.TextBlock { Text = action.Name, TextWrapping = TextWrapping.Wrap, MaxWidth = 200 });
            var status = new System.Windows.Controls.TextBlock { Opacity = .6, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
            labels.Children.Add(status);
            content.Children.Add(labels); button.Content = content;
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Toolbar.Add." + action.Id);
            button.Click += (_, _) =>
            {
                if (!CanEdit) return;
                SetItemVisible(action.Id, true);
                FocusSelectedItem();
            };
            _library.Add(action.Id, (button, status));
            LibraryItems.Children.Add(button);
        }
    }
    private void RenderLibrary()
    {
        foreach (var (id, item) in _library)
        {
            var present = Draft.Items.Contains(id);
            item.Button.IsEnabled = !present;
            item.Status.Text = T(present ? "Added" : "Add");
        }
    }
    private void FocusSelectedItem() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
    {
        if (!CanEdit) return;
        if (PreviewList.SelectedItem is { } selected)
        {
            PreviewList.ScrollIntoView(selected);
            if (PreviewList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item) item.Focus();
        }
    });
    private void OnLibrary(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        var opening = LibraryPanel.Visibility != Visibility.Visible;
        LibraryPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        LibraryToggle.IsChecked = opening;
        if (opening)
        {
            OptionsScroll.ScrollToTop();
            // Focus stays on the toggle until the user chooses a library item;
            // auto-focusing an add button can scroll the library past its title.
            LibraryToggle.Focus();
        }
        UpdateEditorPlacement();
    }
    private void Changed(string? selected = null)
    {
        Draft = Draft.Normalize(); _preview(Draft.Clone());
        Feedback.Visibility = Visibility.Collapsed; Refresh(selected);
    }
    internal void SetItemVisible(string id, bool visible)
    {
        if (!CanEdit || !_catalog.Any(x => x.Id == id) || Draft.Items.Contains(id) == visible) return;
        var index = Draft.Items.IndexOf(id);
        if (visible) Draft.Items.Add(id); else Draft.Items.RemoveAt(index);
        var next = visible ? id : Draft.Items.ElementAtOrDefault(Math.Min(index, Draft.Items.Count - 1));
        Changed(next);
        if (Draft.Items.Count == 0)
        {
            LibraryPanel.Visibility = Visibility.Visible; LibraryToggle.IsChecked = true;
            LibraryToggle.Focus();
        }
    }
    internal void MoveItem(string id, int delta)
    {
        if (!CanEdit) return;
        var index = Draft.Items.IndexOf(id); var target = index + delta;
        if (index < 0 || target < 0 || target >= Draft.Items.Count) return;
        Draft.Items.RemoveAt(index); Draft.Items.Insert(target, id); Changed(id);
        if (Keyboard.FocusedElement is UIElement { IsEnabled: false }) FocusSelectedItem();
    }
    private void UpdateSelection()
    {
        var selected = PreviewList.SelectedItem as ToolbarRow;
        SelectionTools.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        SelectionName.Text = selected?.Name ?? "";
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PositionSelectionTools);
    }
    private void PositionSelectionTools()
    {
        if (_dragging) { SelectionTools.Visibility = Visibility.Collapsed; return; }
        if (PreviewList.SelectedItem is null || PreviewList.ItemContainerGenerator.ContainerFromItem(PreviewList.SelectedItem) is not FrameworkElement item ||
            !item.IsArrangeValid || item.ActualHeight < 1) return;
        if (!SelectionTools.IsMeasureValid || SelectionTools.ActualWidth < 1) return;
        var itemBounds = item.TransformToAncestor(EditorRoot).TransformBounds(new Rect(item.RenderSize));
        var viewport = PreviewList.TransformToAncestor(EditorRoot).TransformBounds(new Rect(PreviewList.RenderSize));
        var visible = Rect.Intersect(viewport, itemBounds);
        if (visible.IsEmpty || visible.Width < 2) { SelectionTools.Visibility = Visibility.Hidden; return; }
        SelectionTools.Visibility = Visibility.Visible;
        var margin = new Thickness(Math.Clamp(visible.Left, 8, Math.Max(8, EditorRoot.ActualWidth - SelectionTools.ActualWidth - 8)),
            Math.Max(8, itemBounds.Top - SelectionTools.ActualHeight - 6), 0, 0);
        if (SelectionTools.Margin != margin) SelectionTools.Margin = margin;
    }
    internal bool SaveDraft()
    {
        if (!CanEdit) return false;
        if (_save(Draft.Clone())) return true;
        Feedback.Text = T("SaveFailed"); Feedback.Visibility = Visibility.Visible; return false;
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_refreshing) UpdateSelection(); }
    private void OnHide(object sender, RoutedEventArgs e) { if (PreviewList.SelectedItem is ToolbarRow row) SetItemVisible(row.Id, false); }
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || !CanEdit) return;
        Draft.IconsOnly = IconsOnlyCheck.IsChecked == true; Changed();
    }
    private void OnReset(object sender, RoutedEventArgs e) { if (CanEdit) { Draft = new(); Changed(); } }
    private void OnDone(object sender, RoutedEventArgs e) { if (SaveDraft()) ExitFocusedEditor(true); }
    private void OnCancel(object sender, RoutedEventArgs e) => ExitFocusedEditor(false);
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_dragId is not null) EndToolbarDrag(cancel: true);
            else if (_focusMoving && !_focusExiting) ExitFocusedEditor(false);
            else if (LibraryPanel.Visibility == Visibility.Visible) { LibraryPanel.Visibility = Visibility.Collapsed; LibraryToggle.IsChecked = false; LibraryToggle.Focus(); }
            else ExitFocusedEditor(false);
            e.Handled = true;
        }
        else if (_focusMoving || _dragging) e.Handled = true;
        else if (CanEdit && PreviewList.SelectedItem is ToolbarRow row &&
            (PreviewList.IsKeyboardFocusWithin || SelectionTools.IsKeyboardFocusWithin))
        {
            if (Keyboard.Modifiers == ModifierKeys.Alt && (e.Key == Key.System ? e.SystemKey : e.Key) is Key.Left or Key.Right)
            { MoveItem(row.Id, (e.Key == Key.System ? e.SystemKey : e.Key) == Key.Left ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Delete) { SetItemVisible(row.Id, false); FocusSelectedItem(); e.Handled = true; }
        }
    }
}
