using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly Dictionary<string, FrameworkElement> _previewToolbarItems = [];
    private PreviewToolbarSettings _previewToolbarSettings = new();

    internal Rect ToolbarFocusRegion()
    {
        if (!EnvironmentPanel.IsVisible) return Rect.Empty;
        var bounds = Rect.Empty;
        var viewport = PreviewToolbarScrollViewer.TransformToAncestor(this).TransformBounds(new Rect(PreviewToolbarScrollViewer.RenderSize));
        foreach (FrameworkElement item in PreviewQuickActions.Children)
            if (item.IsVisible && item.ActualWidth > 0 && item.ActualHeight > 0)
            {
                var visible = Rect.Intersect(viewport, item.TransformToAncestor(this).TransformBounds(new Rect(item.RenderSize)));
                if (!visible.IsEmpty) bounds.Union(visible);
            }
        if (!bounds.IsEmpty) bounds.Inflate(10, 10);
        return bounds;
    }

    internal BitmapSource? CaptureToolbarFocusScene(bool excludeToolbar = false)
    {
        var currentOpacity = EnvironmentPanel.Opacity;
        BitmapSource? scene;
        try
        {
            if (excludeToolbar) { EnvironmentPanel.BeginAnimation(OpacityProperty, null); EnvironmentPanel.Opacity = 0; }
            scene = PreviewToolbarSettingsWindow.CaptureFocusVisual(this);
        }
        finally { if (excludeToolbar) EnvironmentPanel.Opacity = currentOpacity; }
        if (scene is null || !MainPreviewHost.IsVisible || _mediaCastActive) return scene;
        var video = _viewModel.CaptureToolbarPreviewFrame();
        if (video is null) return scene;
        var bounds = new Rect(MainPreviewHost.TranslatePoint(new Point(), this), MainPreviewHost.RenderSize);
        return CompositeToolbarFocusFrame(scene, video, bounds);
    }

    internal static BitmapSource CompositeToolbarFocusFrame(BitmapSource scene, BitmapSource video, Rect bounds)
    {
        var picture = PreviewCoordinateMapper.ContentRect(bounds.Width, bounds.Height,
            (uint)video.PixelWidth, (uint)video.PixelHeight);
        if (picture.IsEmpty) return scene;
        picture.Offset(bounds.Left, bounds.Top);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawImage(scene, new Rect(0, 0, scene.Width, scene.Height));
            context.PushClip(new RectangleGeometry(bounds, 15, 15));
            // HwndHost itself is absent from the WPF snapshot. Recreate its
            // black letterbox and aspect fit instead of stretching the raw
            // decoded frame across the entire native preview surface.
            context.DrawRectangle(Brushes.Black, null, bounds);
            context.DrawImage(video, picture); context.Pop();
        }
        var composite = new RenderTargetBitmap(scene.PixelWidth, scene.PixelHeight, scene.DpiX, scene.DpiY, PixelFormats.Pbgra32);
        composite.Render(drawing); composite.Freeze(); return composite;
    }

    internal BitmapSource? CaptureToolbarFocusImage(Rect bounds)
    {
        if (!EnvironmentPanel.IsVisible) return null;
        var origin = TranslatePoint(bounds.TopLeft, EnvironmentPanel);
        return PreviewToolbarSettingsWindow.CaptureFocusRegion(EnvironmentPanel, new Rect(origin, bounds.Size));
    }

    private void InitializePreviewToolbar()
    {
        if (_previewToolbarItems.Count > 0) return;
        foreach (var button in PreviewQuickActions.Children.OfType<Button>())
        {
            var id = AutomationProperties.GetAutomationId(button);
            button.DataContext = _viewModel;
            ToolTipService.SetShowOnDisabled(button, true);
            _previewToolbarItems.Add(id, button);
        }
        ApplyPreviewToolbar((Application.Current as App)?.UpdateSettings.PreviewToolbar ?? new());
    }

    internal void ApplyPreviewToolbar(PreviewToolbarSettings settings)
    {
        _previewToolbarSettings = settings.Normalize();
        var desired = _previewToolbarSettings.Items.Select(id => _previewToolbarItems[id]).ToArray();
        if (!PreviewQuickActions.Children.Cast<UIElement>().SequenceEqual(desired))
        {
            PreviewQuickActions.Children.Clear();
            foreach (var item in desired) PreviewQuickActions.Children.Add(item);
        }
        PreviewToolbarScrollViewer.IconsOnly = _previewToolbarSettings.IconsOnly;
    }

    private void OnCustomizePreviewToolbar(object sender, RoutedEventArgs e) => OpenToolbarEditor();
    private void OpenToolbarEditor()
    {
        InitializePreviewToolbar();
        var baseline = _previewToolbarSettings.Clone();
        var catalog = _previewToolbarItems.Where(x => x.Value is Button).Select(x =>
            new ToolbarAction(x.Key, AutomationProperties.GetName(x.Value),
                ((StackPanel)((Button)x.Value).Content).Children.OfType<Controls.ThemedSymbolIcon>().FirstOrDefault()?.Symbol
                    ?? Wpf.Ui.Controls.SymbolRegular.Apps24)).ToList();
        var dialog = new PreviewToolbarSettingsWindow(baseline, catalog, ApplyPreviewToolbar, draft =>
        {
            if (Application.Current is not App app) return false;
            var previous = app.UpdateSettings.PreviewToolbar;
            app.UpdateSettings.PreviewToolbar = draft.Normalize();
            if (app.SaveUpdateSettings()) return true;
            app.UpdateSettings.PreviewToolbar = previous; return false;
        }) { Owner = this };
        dialog.ConfigureToolbarFocus(this, EnvironmentPanel);
        if (dialog.ShowDialog() != true) ApplyPreviewToolbar(baseline);
    }
}
