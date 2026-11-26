using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private ScreenTextSelectionWindow? _screenTextSelection;
    private CancellationTokenSource? _screenTextSelectionRequest;
    private string? _screenTextSelectionSource;

    private nint GetMainScreenTextWindow(DeviceViewModel device)
    {
        if (device.IsMediaCast && _mediaCastPreviewWindow is { } media) return media.Handle;
        if (!MainPreviewHost.IsVisible && _secondaryMirrors.GetWindowHandle(device.Udid) is var preview && preview != 0)
            return preview;
        return _windowSource?.Handle ?? 0;
    }

    private void SelectFocusedScreenText()
    {
        var window = _keyboardForegroundWindow();
        if (GetScreenTextSource(window) is { } source) _ = OpenScreenTextSelectionAsync(source, window);
    }

    private void OnSelectScreenTextClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedDevice is { } device)
            _ = OpenScreenTextSelectionAsync(device.Udid, GetMainScreenTextWindow(device));
    }

    private void CloseScreenTextSelection()
    {
        _screenTextSelectionRequest?.Cancel();
        _screenTextSelection?.Dismiss();
    }

    private void CloseScreenTextSelectionForSource(string source)
    {
        if (DeviceViewModel.UdidEquals(_screenTextSelectionSource, source)) CloseScreenTextSelection();
    }

    private async Task OpenScreenTextSelectionAsync(string source, nint window)
    {
        if (window == 0) return;
        var mediaCast = DeviceViewModel.IsMediaCastUdid(source);
        var session = _viewModel.GetDeviceSessionHandle(source);
        var generation = _mediaCastEvents.CurrentGeneration;
        var independent = _secondaryMirrors.FindWindow(window);
        var targetWindow = independent ?? (window == _mediaCastPreviewWindow?.Handle ? _mediaCastPreviewWindow : null);
        var geometry = independent?.ControlGeometry;
        (DeviceFrameProfile? Frame, int Rotation) Frame() => independent is not null
            ? (independent.ActiveDeviceFrame, independent.ActiveDeviceFrame is null ? 0 : independent.DeviceFrameRotation)
            : !mediaCast && window == _windowSource?.Handle ? GetMainPreviewDeviceFrame() : (null, 0);
        var (artwork, artworkRotation) = Frame();
        bool Current() => !_mappingClosing && !_shutdownStarted && !_bossKeyHidden &&
            targetWindow?.IsClickThrough != true &&
            DeviceViewModel.UdidEquals(source, GetScreenTextSource(window)) &&
            independent?.ControlGeometry == geometry &&
            Frame() == (artwork, artworkRotation) &&
            (mediaCast ? _mediaCastActive && _mediaCastEvents.CurrentGeneration == generation
                : _viewModel.CanCopyDeviceScreenText(source, session));
        if (!Current()) return;

        CloseScreenTextSelection();
        _screenTextFeedback?.Close();
        using var request = new CancellationTokenSource();
        _screenTextSelectionRequest = request;
        _screenTextSelectionSource = source;
        var rotation = geometry?.Rotation ?? 0;
        var preview = mediaCast ? MediaCastVideoHost : window == _windowSource?.Handle ? MainPreviewHost : (FrameworkElement?)null;
        var originalForeground = _keyboardForegroundWindow();
        ScreenTextSelectionWindow? selection = null;
        try
        {
            var readFrame = _viewModel.CreateDeviceScreenshotCapture(source);
            var mediaFrame = mediaCast ? CaptureMediaCastSelectionFrame() : null;
            var (frame, bitmap, mask) = await Task.Run(() =>
            {
                request.Token.ThrowIfCancellationRequested();
                var captured = (mediaCast ? mediaFrame : readFrame()) ??
                    throw new InvalidOperationException(LocalizationService.Get("ScreenshotNoFrame"));
                var oriented = OrientScreenTextFrame(captured, rotation);
                var screenMask = artwork is null ? null : PhoneFrameGeometry.CreateScreenMask(artwork, artworkRotation);
                return (oriented, ScreenshotService.CreateBitmap(oriented), screenMask);
            }, request.Token);
            if (!Current() || request.IsCancellationRequested ||
                (originalForeground == window && _keyboardForegroundWindow() != window)) return;

            ScreenTextPreviewBounds PreviewBounds()
            {
                if (!ScreenTextFeedbackWindow.TryReadPreviewBounds(window, preview, out var bounds))
                    return new(Rect.Empty, Rect.Empty);
                if (mediaCast) return new(bounds, bounds);
                if (artwork is not null)
                {
                    var screen = PhoneFrameGeometry.ScreenBounds(artwork, artworkRotation,
                        (int)bounds.Width, (int)bounds.Height);
                    var outer = PhoneFrameGeometry.ArtworkBounds(artwork, artworkRotation,
                        (int)bounds.Width, (int)bounds.Height);
                    screen.Offset(bounds.Left, bounds.Top);
                    outer.Offset(bounds.Left, bounds.Top);
                    return new(screen, outer);
                }
                var picture = PreviewCoordinateMapper.ContentRect(bounds.Width, bounds.Height,
                    frame.Width, frame.Height);
                picture.Offset(bounds.Left, bounds.Top);
                return new(picture, picture);
            }

            selection = new ScreenTextSelectionWindow(window, bitmap, PreviewBounds, Current, Clipboard.SetText, mask);
            _screenTextSelection = selection;
            selection.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_screenTextSelection, selection)) return;
                if (ReferenceEquals(_screenTextSelectionRequest, request)) request.Cancel();
                _screenTextSelection = null;
                _screenTextSelectionSource = null;
            };
            ResetKeyboardOwnership();
            if (!selection.ShowForPreview()) return;
            var page = await Task.Run(() => ScreenTextRecognition.ReadPageAsync(frame, request.Token), request.Token);
            if (Current() && ReferenceEquals(_screenTextSelection, selection)) selection.SetPage(page);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!Current() || request.IsCancellationRequested) return;
            var message = LocalizationService.Format("ScreenTextSelectionFailed", error.Message);
            if (selection is not null && ReferenceEquals(_screenTextSelection, selection))
                selection.SetStatus(message);
            else
                ShowPreviewFeedback(window, "ScreenTextSelectionFailed", source, null, error.Message);
        }
        finally
        {
            if (ReferenceEquals(_screenTextSelectionRequest, request))
            {
                _screenTextSelectionRequest = null;
                if (_screenTextSelection is null) _screenTextSelectionSource = null;
            }
        }
    }

    private VideoFrame? CaptureMediaCastSelectionFrame()
    {
        var width = MediaCastVideoHost.ActualWidth;
        var height = MediaCastVideoHost.ActualHeight;
        if (width < 2 || height < 2) return null;
        var scale = Math.Min(2, Math.Min(1920 / width, 1080 / height));
        return CaptureMediaCastVideoFrame(
            (uint)Math.Max(2, (int)(width * scale) & ~1), (uint)Math.Max(2, (int)(height * scale) & ~1));
    }

    internal static VideoFrame OrientScreenTextFrame(VideoFrame frame, int turns)
    {
        if (turns % 4 == 0) return frame;
        var source = ScreenshotService.CreateBitmap(frame);
        var bitmap = new TransformedBitmap(source, new RotateTransform(turns * 90));
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(bitmap.PixelHeight * stride)];
        bitmap.CopyPixels(pixels, stride, 0);
        return new VideoFrame((uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, (uint)stride, frame.Timestamp100Ns, pixels);
    }
}
