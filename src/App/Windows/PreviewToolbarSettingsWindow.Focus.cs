using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace IPhoneMirror.App.Windows;

public partial class PreviewToolbarSettingsWindow
{
    private Window? _focusOwner;
    private FrameworkElement? _focusSource;
    private bool _focusMoving, _focusCanClose, _focusExiting;
    private bool _focusResult, _refreshingPlacement, _sceneRefreshQueued, _placementPending;
    private bool _aligningOwnerBounds;
    private int? _ownerCornerPreference;
    private bool? _hasSourceToolbar;
    private Rect _sourceBounds, _targetBounds, _shadeBounds;
    private Matrix _targetMatrix = Matrix.Identity;
    private BitmapSource? _sceneWithoutToolbar;
    private readonly ScaleTransform _previewScale = new(1, 1);
    private readonly List<(DependencyObject Target, DependencyProperty Property)> _motionProperties = [];
    private int _motionVersion;
    private const double FocusScale = 1.25, FocusDimming = .85, FocusGradientRange = 180, ToolbarSlideDistance = 14;
    private bool UsesToolbarSlide => _hasSourceToolbar == false;
    internal bool FocusAnimationEnabled { get; set; } = SystemParameters.ClientAreaAnimation;
    internal bool AnimateInPreview { get; set; }
    internal Rect TargetToolbarBounds => _targetBounds;
    internal Matrix SceneTargetMatrix => _targetMatrix;
    internal bool IsFocusMoving => _focusMoving;
    internal int SceneCaptureCount { get; private set; }
    internal int ToolbarCaptureCount { get; private set; }
    internal int PlacementPassCount { get; private set; }
    private static readonly DependencyProperty FocusProgressProperty = DependencyProperty.Register(
        "FocusProgress", typeof(double), typeof(PreviewToolbarSettingsWindow), new PropertyMetadata(0d));
    internal double FocusProgress => (double)GetValue(FocusProgressProperty);

    internal void ConfigureToolbarFocus(Window owner, FrameworkElement toolbar)
    {
        _focusOwner = owner; _focusSource = toolbar;
        IPhoneMirror.SharedUI.Services.WindowWorkAreaController.UseOwnerBounds(this);
        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = MinHeight = 0;
        // The stage covers the client area and needs no per-pixel-alpha HWND.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        { CaptionHeight = 0, ResizeBorderThickness = new Thickness(), GlassFrameThickness = new Thickness(), UseAeroCaptionButtons = false });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentProperty));
        Template = new ControlTemplate(typeof(Window)) { VisualTree = presenter };
        PreviewList.LayoutTransform = _previewScale;
        RenderOptions.SetBitmapScalingMode(SceneImage, BitmapScalingMode.LowQuality);
        RenderOptions.SetBitmapScalingMode(FocusToolbarImage, BitmapScalingMode.LowQuality);
        AlignFocusOwner(); Topmost = owner.Topmost;
        Closing += OnFocusClosing;
        owner.LocationChanged += OnOwnerGeometryChanged;
        owner.SizeChanged += OnOwnerSizeChanged;
        owner.StateChanged += OnOwnerGeometryChanged;
        owner.Closed += OnOwnerClosed;
        Closed += (_, _) =>
        {
            ++_motionVersion; StopFocusClocks();
            SceneImage.Source = FocusToolbarImage.Source = null; _sceneWithoutToolbar = null;
            owner.LocationChanged -= OnOwnerGeometryChanged;
            owner.SizeChanged -= OnOwnerSizeChanged;
            owner.StateChanged -= OnOwnerGeometryChanged;
            owner.Closed -= OnOwnerClosed;
        };
        CaptureOwnerScene();
    }

    private void AlignFocusOwner()
    {
        if (_aligningOwnerBounds || _focusOwner is not { IsLoaded: true } owner) return;
        _aligningOwnerBounds = true;
        try
        {
            var origin = owner.PointToScreen(new Point());
            var end = owner.PointToScreen(new Point(owner.ActualWidth, owner.ActualHeight));
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == 0)
            {
                var dpi = VisualTreeHelper.GetDpi(owner);
                Left = origin.X / dpi.DpiScaleX; Top = origin.Y / dpi.DpiScaleY;
                Width = owner.ActualWidth; Height = owner.ActualHeight;
                return;
            }
            // Screen coordinates remain physical pixels on all monitors. Do
            // not position this HWND with its owner's DIP scale before it has
            // adopted the destination monitor's DPI.
            var x = (int)Math.Round(origin.X); var y = (int)Math.Round(origin.Y);
            var width = Math.Max(1, (int)Math.Round(end.X) - x);
            var height = Math.Max(1, (int)Math.Round(end.Y) - y);
            if (!FocusGetWindowRect(handle, out var current) || current.Left != x || current.Top != y ||
                current.Right - current.Left != width || current.Bottom - current.Top != height)
                _ = FocusSetWindowPos(handle, 0, x, y, width, height, 0x0014); // NOZORDER | NOACTIVATE
            var corners = owner.WindowState == WindowState.Maximized ||
                owner is MainWindow { WindowCornerPreference: Wpf.Ui.Controls.WindowCornerPreference.DoNotRound } ? 1 : 2;
            if (_ownerCornerPreference != corners)
            {
                _ = FocusSetDwmAttribute(handle, 33, ref corners, sizeof(int));
                _ownerCornerPreference = corners;
            }
        }
        finally { _aligningOwnerBounds = false; }
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // RoundedWindow's initial text sizing is useful for dialogs. Restore
        // the owner surface before the first frame of this stage is shown.
        _ownerCornerPreference = null; AlignFocusOwner();
    }
    private void OnOwnerGeometryChanged(object? sender, EventArgs e)
    {
        if (_focusOwner?.WindowState == WindowState.Minimized)
        {
            ++_motionVersion; StopFocusClocks(); _focusMoving = false;
            EndToolbarDrag(cancel: true); _preview(_baseline.Clone());
            _focusResult = false; CompleteFocusExit(); return;
        }
        AlignFocusOwner();
        if (_focusOwner is not null) Topmost = _focusOwner.Topmost;
    }
    private void OnOwnerSizeChanged(object sender, SizeChangedEventArgs e)
    { OnOwnerGeometryChanged(sender, e); QueueSceneRefresh(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); AlignFocusOwner(); QueueSceneRefresh(); }
    private void QueueSceneRefresh()
    {
        if (!IsLoaded || _focusCanClose || _sceneRefreshQueued) return;
        _sceneRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsLoaded || _focusCanClose) return;
            if (_focusMoving) return;
            // UpdateLayout during capture can deliver the owner's final size
            // notification. It is already included in this snapshot; keep the
            // request marked until capture finishes so it cannot queue a copy.
            try { AlignFocusOwner(); CaptureOwnerScene(); UpdateEditorPlacement(); }
            finally { _sceneRefreshQueued = false; }
        });
    }
    private void OnOwnerClosed(object? sender, EventArgs e) { _focusCanClose = true; Close(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct FocusWindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FocusGetWindowRect(nint window, out FocusWindowRect rectangle);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FocusSetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int FocusSetDwmAttribute(nint window, int attribute, ref int value, int size);

    internal static BitmapSource? CaptureFocusVisual(FrameworkElement visual) =>
        CaptureFocusRegion(visual, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
    internal static BitmapSource? CaptureFocusRegion(FrameworkElement visual, Rect region)
    {
        if (region.Width < 1 || region.Height < 1 || visual.ActualWidth < 1 || visual.ActualHeight < 1) return null;
        var dpi = VisualTreeHelper.GetDpi(visual);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(visual) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region, Stretch = Stretch.Fill },
                null, new Rect(region.Size));
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(region.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(region.Height * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
    }
    private void CaptureOwnerScene()
    {
        if (_focusOwner is null) return;
        _focusOwner.UpdateLayout(); ++SceneCaptureCount;
        _sceneWithoutToolbar = _focusOwner is MainWindow main ? main.CaptureToolbarFocusScene(excludeToolbar: true) : CaptureFocusVisual(_focusOwner);
        SceneImage.Source = _sceneWithoutToolbar;
        SceneImage.Width = _focusOwner.ActualWidth; SceneImage.Height = _focusOwner.ActualHeight;
        CaptureToolbarImage();
    }
    private void CaptureToolbarImage()
    {
        if (_focusOwner is null) return;
        // Choose the entrance once. A hidden toolbar has no on-screen origin
        // or return snapshot, even when editing changes its configured items.
        if (UsesToolbarSlide) return;
        if (_focusSource is { IsArrangeValid: false } invalidSource) invalidSource.UpdateLayout();
        var sourceBounds = _focusOwner is MainWindow main ? main.ToolbarFocusRegion() :
            _focusSource is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } source
                ? new Rect(source.TranslatePoint(new Point(), _focusOwner), source.RenderSize) : Rect.Empty;
        _hasSourceToolbar ??= !sourceBounds.IsEmpty;
        if (UsesToolbarSlide) return;
        if (!sourceBounds.IsEmpty) _sourceBounds = sourceBounds;
        ++ToolbarCaptureCount;
        FocusToolbarImage.Source = _focusOwner is MainWindow owner ? owner.CaptureToolbarFocusImage(_sourceBounds) : CaptureFocusRegion(_focusOwner, _sourceBounds);
        FocusToolbarImage.Width = _sourceBounds.Width; FocusToolbarImage.Height = _sourceBounds.Height;
    }

    private bool AnimateFocus => FocusAnimationEnabled && (AnimateInPreview || (Application.Current as App)?.IsUiPreviewMode != true);
    private void BeginFocusEntrance()
    {
        AlignFocusOwner(); UpdateLayout(); UpdateEditorPlacement(); ApplyMotionState(0);
        if (_focusOwner is null) { FinishFocusEntrance(); return; }
        RunFocusMotion(1, FinishFocusEntrance);
    }
    private void FinishFocusEntrance()
    {
        _focusMoving = false; EditorContent.IsHitTestVisible = true;
        if (_sceneRefreshQueued) { _sceneRefreshQueued = false; QueueSceneRefresh(); }
        if (_placementPending) { _placementPending = false; UpdateEditorPlacement(); }
        PreviewList.Focus();
    }
    private void UpdateEditorPlacement()
    {
        if (_focusMoving) { _placementPending = true; return; }
        if (_refreshingPlacement || _focusExiting || EditorRoot.ActualWidth < 1 || EditorRoot.ActualHeight < 1) return;
        _refreshingPlacement = true; ++PlacementPassCount;
        try
        {
            var width = EditorRoot.ActualWidth; var height = EditorRoot.ActualHeight;
            var readingScale = TryFindResource("BodyFontSize") is double bodySize ? bodySize / 14 : 1;
            SettingsPanel.Width = Math.Min(width - 48, 640 * readingScale);
            OptionsScroll.MaxHeight = Math.Max(60, Math.Min(320, height * .40));
            var (naturalWidth, naturalHeight) = MeasureToolbarRow();
            var toolbarScale = Math.Min(FocusScale, Math.Max(1, width - 66) / naturalWidth);
            var targetWidth = naturalWidth * toolbarScale + 18; var targetHeight = naturalHeight * toolbarScale + 18;
            var targetY = UsesToolbarSlide ? 64 : Math.Max(64, _sourceBounds.Top - 40); var targetX = (width - targetWidth) / 2;
            _targetBounds = new Rect(targetX, targetY, targetWidth, targetHeight);
            _targetMatrix = UsesToolbarSlide ? Matrix.Identity : new Matrix(FocusScale, 0, 0, FocusScale,
                (width - _sourceBounds.Width * FocusScale) / 2 - _sourceBounds.Left * FocusScale, targetY - _sourceBounds.Top * FocusScale);
            ToolbarSurface.Width = targetWidth;
            _previewScale.ScaleX = _previewScale.ScaleY = toolbarScale;
            SettingsPanel.MaxHeight = Math.Max(180, height - targetY - Math.Max(targetHeight, ToolbarSurface.ActualHeight) - 70);
            ToolbarSurface.Margin = new Thickness(targetX, targetY, 0, 0);
            PrepareShade(); ApplyMotionState(FocusProgress); PositionSelectionTools();
        }
        finally { _refreshingPlacement = false; }
    }
    private (double Width, double Height) MeasureToolbarRow()
    {
        var width = 0d; var height = 0d;
        foreach (var row in PreviewList.Items)
        {
            if (PreviewList.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement item) continue;
            item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += item.DesiredSize.Width; height = Math.Max(height, item.DesiredSize.Height);
        }
        return (Math.Max(240, width), Math.Max(40, height));
    }
    private void PrepareShade()
    {
        var bounds = new Rect(EditorRoot.RenderSize);
        if (_shadeBounds == _targetBounds && SurroundShade.Data?.Bounds == bounds) return;
        _shadeBounds = _targetBounds;
        var center = new Point(_targetBounds.Left + _targetBounds.Width / 2, _targetBounds.Top + _targetBounds.Height / 2);
        var gradient = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, Center = center, GradientOrigin = center,
            RadiusX = _targetBounds.Width / 2 + FocusGradientRange, RadiusY = _targetBounds.Height / 2 + FocusGradientRange,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(0, 0, 0, 0), 0), new(Color.FromArgb(0, 0, 0, 0), .32),
                new(Color.FromArgb(70, 0, 0, 0), .58), new(Colors.Black, 1)
            }
        };
        gradient.Freeze();
        // The soft shade has no fine detail. Bake it once into a small texture
        // instead of rasterizing a window-sized radial gradient on every frame,
        // including machines that fall back to WPF software rendering.
        var textureScale = Math.Min(1, 768 / Math.Max(1, Math.Max(bounds.Width, bounds.Height)));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(textureScale, textureScale));
            context.DrawRectangle(gradient, null, bounds); context.Pop();
        }
        var texture = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width * textureScale)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height * textureScale)), 96, 96, PixelFormats.Pbgra32);
        texture.Render(drawing); texture.Freeze();
        var brush = new ImageBrush(texture) { Stretch = Stretch.Fill }; brush.Freeze(); SurroundShade.Fill = brush;
        var geometry = new RectangleGeometry(bounds); geometry.Freeze(); SurroundShade.Data = geometry;
    }

    private void ApplyMotionState(double progress)
    {
        SceneScale.ScaleX = SceneScale.ScaleY = 1 + (_targetMatrix.M11 - 1) * progress;
        SceneShift.X = _targetMatrix.OffsetX * progress; SceneShift.Y = _targetMatrix.OffsetY * progress;
        if (UsesToolbarSlide)
        {
            SnapshotScale.ScaleX = SnapshotScale.ScaleY = LiveToolbarScale.ScaleX = LiveToolbarScale.ScaleY = 1;
            SnapshotShift.X = SnapshotShift.Y = LiveToolbarShift.X = 0;
            LiveToolbarShift.Y = -ToolbarSlideDistance * (1 - progress);
        }
        else
        {
            SnapshotScale.ScaleX = 1 + (_targetBounds.Width / Math.Max(1, _sourceBounds.Width) - 1) * progress;
            SnapshotScale.ScaleY = 1 + (_targetBounds.Height / Math.Max(1, _sourceBounds.Height) - 1) * progress;
            SnapshotShift.X = _sourceBounds.Left + (_targetBounds.Left - _sourceBounds.Left) * progress;
            SnapshotShift.Y = _sourceBounds.Top + (_targetBounds.Top - _sourceBounds.Top) * progress;
            LiveToolbarScale.ScaleX = _sourceBounds.Width / Math.Max(1, _targetBounds.Width) * (1 - progress) + progress;
            LiveToolbarScale.ScaleY = _sourceBounds.Height / Math.Max(1, _targetBounds.Height) * (1 - progress) + progress;
            LiveToolbarShift.X = (_sourceBounds.Left - _targetBounds.Left) * (1 - progress);
            LiveToolbarShift.Y = (_sourceBounds.Top - _targetBounds.Top) * (1 - progress);
        }
        SurroundShade.Opacity = FocusDimming * progress;
        EditorContent.Opacity = 1; ToolbarSurface.Opacity = SettingsPanel.Opacity = progress;
        FocusToolbarImage.Opacity = UsesToolbarSlide ? 0 : 1 - progress; FooterShift.Y = 10 * (1 - progress);
    }
    private static void BeginValueAnimation(DependencyObject target, DependencyProperty property, AnimationTimeline? animation)
    {
        if (target is Animatable animatable) animatable.BeginAnimation(property, animation);
        else if (target is UIElement element) element.BeginAnimation(property, animation);
    }
    private void StopFocusClocks()
    {
        var progress = FocusProgress;
        BeginAnimation(FocusProgressProperty, null); SetValue(FocusProgressProperty, progress);
        foreach (var (target, property) in _motionProperties)
        {
            var current = target.GetValue(property);
            BeginValueAnimation(target, property, null); target.SetValue(property, current);
        }
        _motionProperties.Clear();
    }
    private void RunFocusMotion(double target, Action completed)
    {
        var from = FocusProgress; var version = ++_motionVersion;
        // Each transform/opacity owns a WPF animation clock. No per-frame
        // managed callback, bitmap capture, brush mutation or layout is needed.
        var durationMs = target == 1 ? (UsesToolbarSlide ? 240 : 360) : Math.Max(100, (UsesToolbarSlide ? 200 : 280) * from);
        var duration = TimeSpan.FromMilliseconds(AnimateFocus ? durationMs : 0);
        var easing = new CubicEase { EasingMode = target == 1 ? EasingMode.EaseOut : EasingMode.EaseInOut };
        void Move(DependencyObject item, DependencyProperty property, double value, bool crossfade = false)
        {
            var current = (double)item.GetValue(property);
            BeginValueAnimation(item, property, null); item.SetValue(property, duration == TimeSpan.Zero ? value : current);
            if (!_motionProperties.Contains((item, property))) _motionProperties.Add((item, property));
            if (duration == TimeSpan.Zero) return;
            AnimationTimeline animation;
            if (crossfade && target == 1)
            {
                var frames = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
                frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromPercent(0)));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(.9), new SineEase { EasingMode = EasingMode.EaseInOut }));
                animation = frames;
            }
            else animation = new DoubleAnimation(current, value, duration) { EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd };
            BeginValueAnimation(item, property, animation);
        }
        EditorContent.IsHitTestVisible = false; SelectionTools.Visibility = Visibility.Collapsed;
        _focusMoving = true;
        if (AnimateFocus && RenderCapability.Tier >> 16 > 0 && RenderOptions.ProcessRenderMode != System.Windows.Interop.RenderMode.SoftwareOnly)
        {
            ToolbarSurface.CacheMode ??= new BitmapCache { RenderAtScale = Math.Min(2, VisualTreeHelper.GetDpi(this).DpiScaleX) };
            SettingsPanel.CacheMode ??= new BitmapCache();
        }
        if (UsesToolbarSlide)
        {
            Move(LiveToolbarShift, TranslateTransform.YProperty, -ToolbarSlideDistance * (1 - target));
        }
        else
        {
            Move(SceneScale, ScaleTransform.ScaleXProperty, target == 1 ? _targetMatrix.M11 : 1);
            Move(SceneScale, ScaleTransform.ScaleYProperty, target == 1 ? _targetMatrix.M11 : 1);
            Move(SceneShift, TranslateTransform.XProperty, _targetMatrix.OffsetX * target);
            Move(SceneShift, TranslateTransform.YProperty, _targetMatrix.OffsetY * target);
            Move(SnapshotScale, ScaleTransform.ScaleXProperty, target == 1 ? _targetBounds.Width / Math.Max(1, _sourceBounds.Width) : 1);
            Move(SnapshotScale, ScaleTransform.ScaleYProperty, target == 1 ? _targetBounds.Height / Math.Max(1, _sourceBounds.Height) : 1);
            Move(SnapshotShift, TranslateTransform.XProperty, target == 1 ? _targetBounds.Left : _sourceBounds.Left);
            Move(SnapshotShift, TranslateTransform.YProperty, target == 1 ? _targetBounds.Top : _sourceBounds.Top);
            Move(LiveToolbarScale, ScaleTransform.ScaleXProperty, target == 1 ? 1 : _sourceBounds.Width / Math.Max(1, _targetBounds.Width));
            Move(LiveToolbarScale, ScaleTransform.ScaleYProperty, target == 1 ? 1 : _sourceBounds.Height / Math.Max(1, _targetBounds.Height));
            Move(LiveToolbarShift, TranslateTransform.XProperty, (_sourceBounds.Left - _targetBounds.Left) * (1 - target));
            Move(LiveToolbarShift, TranslateTransform.YProperty, (_sourceBounds.Top - _targetBounds.Top) * (1 - target));
            Move(FocusToolbarImage, OpacityProperty, 1 - target, crossfade: true);
        }
        Move(SurroundShade, OpacityProperty, FocusDimming * target);
        Move(ToolbarSurface, OpacityProperty, target, crossfade: !UsesToolbarSlide);
        Move(SettingsPanel, OpacityProperty, target, crossfade: !UsesToolbarSlide);
        Move(FooterShift, TranslateTransform.YProperty, 10 * (1 - target));
        BeginAnimation(FocusProgressProperty, null); SetValue(FocusProgressProperty, duration == TimeSpan.Zero ? target : from);
        if (duration == TimeSpan.Zero) { _focusMoving = false; StopFocusClocks(); completed(); return; }
        var progressAnimation = new DoubleAnimation(from, target, duration) { EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd };
        progressAnimation.Completed += (_, _) =>
        {
            if (version != _motionVersion || !IsLoaded) return;
            StopFocusClocks(); _focusMoving = false;
            ToolbarSurface.CacheMode = SettingsPanel.CacheMode = null;
            completed();
        };
        BeginAnimation(FocusProgressProperty, progressAnimation);
    }
    private void ExitFocusedEditor(bool save)
    {
        if (_focusCanClose || _focusExiting) return;
        var early = _focusMoving;
        EndToolbarDrag(cancel: true); _focusResult = save; _focusExiting = true;
        if (!save) _preview(_baseline.Clone());
        // Reuse the background already on screen. Only the small toolbar needs
        // a fresh image if editing changed what will be restored below it.
        if (!early && !UsesToolbarSlide)
        {
            CaptureToolbarImage();
            // Hiding/adding tools can change the return image's width. Match
            // the current edited surface before fading that image back in.
            SnapshotScale.ScaleX = _targetBounds.Width / Math.Max(1, _sourceBounds.Width);
            SnapshotScale.ScaleY = _targetBounds.Height / Math.Max(1, _sourceBounds.Height);
            SnapshotShift.X = _targetBounds.Left; SnapshotShift.Y = _targetBounds.Top;
        }
        RunFocusMotion(0, CompleteFocusExit);
    }
    private void CompleteFocusExit()
    {
        _focusMoving = false; _focusCanClose = true;
        try { DialogResult = _focusResult; }
        catch (InvalidOperationException) { Close(); }
    }
    private void OnFocusClosing(object? sender, CancelEventArgs e)
    {
        if (_focusCanClose) return;
        e.Cancel = true;
        // Leave WPF's Closing stack before closing again in reduced-motion
        // mode; a nested synchronous Close would cancel the modal's own exit.
        if (!_focusExiting) Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => ExitFocusedEditor(false));
    }
}
