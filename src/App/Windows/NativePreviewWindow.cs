using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

/// <summary>
/// Native top-level DirectComposition preview. WS_EX_NOREDIRECTIONBITMAP keeps
/// DWM from allocating a second WPF redirection surface, allowing the native
/// renderer to attach its visual tree directly to this HWND.
/// </summary>
internal sealed class NativePreviewWindow : IDisposable
{
    internal const string StableTitle = "iPhoneMirror OBS Preview";

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WmContextMenu = 0x007B;
    private const int WmNcLeftButtonDoubleClick = 0x00A3;
    private const int WmNcRightButtonDown = 0x00A4;
    private const int WmNcRightButtonUp = 0x00A5;
    private const int WmRightButtonDown = 0x0204;
    private const int WmRightButtonUp = 0x0205;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmClose = 0x0010;
    private const int WmEraseBackground = 0x0014;
    private const int WmSetIcon = 0x0080;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmDpiChanged = 0x02E0;
    private const int VkEscape = 0x1B;
    private const int VkReturn = 0x0D;
    private const int VkF11 = 0x7A;
    private const int VkMenu = 0x12;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int GwlStyle = -16;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsCaption = 0x00C00000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsThickFrame = 0x00040000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoRedirectionBitmap = 0x00200000;

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly nint HwndTopMost = new(-1);
    private static readonly nint HwndNoTopMost = new(-2);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private readonly HwndSource _source;
    private readonly AspectRatioWindowController _aspectController;
    private readonly Func<nint, bool> _attachPreview;
    private readonly Action<nint> _detachPreview;
    private readonly Func<nint, bool> _refreshPreview;
    private readonly ContextMenu _contextMenu;
    private readonly MenuItem _fullScreenItem;
    private readonly MenuItem _windowMenuItem;
    private readonly MenuItem _displayMenuItem;
    private readonly MenuItem _topMostItem;
    private readonly MenuItem _fixedItem;
    private readonly MenuItem? _cornerItem;
    private readonly MenuItem _muteMenuItem;
    private readonly MenuItem _muteThisItem;
    private readonly MenuItem _muteOthersItem;
    private readonly MenuItem _rotateLeftItem;
    private readonly MenuItem _rotateRightItem;
    private readonly MenuItem _closeItem;
    private readonly MenuItem? _imageSettingsItem;
    private readonly MenuItem? _projectionSettingsItem;
    private readonly Func<bool>? _isAudioEnabled;
    private readonly Func<int>? _connectedDeviceCount;
    private readonly Action<bool>? _setAudioEnabled;
    private readonly Action? _muteOtherWindows;
    private readonly Action<nint>? _showImageSettings;
    private readonly Action? _showProjectionSettings;
    private readonly Action<string>? _logDiagnostic;
    private readonly ulong _sessionHandle;
    private readonly double _cornerRadius;
    private readonly double _cornerExponent;
    private readonly Border? _managedContentRoot;
    private readonly FrameworkElement? _managedContent;
    private readonly Transform? _managedContentOriginalLayoutTransform;
    private readonly Action? _managedContentDetached;
    private uint _sourceWidth;
    private uint _sourceHeight;
    private nint _handle;
    private bool _attached;
    private bool _isFullScreen;
    private bool _disposed;
    private bool _closeQueued;
    private bool _isTopMost = true;
    private bool _isFixed;
    private bool _cornersEnabled = true;
    private int _rotation;
    private nint _largeIcon;
    private nint _smallIcon;
    private WindowRect _restoreRectangle;
    private nint _restoreStyle;

    private NativePreviewWindow(uint sourceWidth, uint sourceHeight, string title,
        Func<nint, bool> attachPreview, Action<nint> detachPreview,
        Func<nint, bool> refreshPreview, ulong sessionHandle,
         double cornerRadius, double cornerExponent,
         Func<bool>? isAudioEnabled = null, Func<int>? connectedDeviceCount = null,
         Action<bool>? setAudioEnabled = null, Action? muteOtherWindows = null,
         Action<nint>? showImageSettings = null, Action? showProjectionSettings = null,
         FrameworkElement? managedContent = null, Action? managedContentDetached = null,
         Action<string>? logDiagnostic = null)
    {
        _attachPreview = attachPreview;
        _detachPreview = detachPreview;
        _refreshPreview = refreshPreview;
        _sessionHandle = sessionHandle;
        _cornerRadius = cornerRadius;
        _cornerExponent = cornerExponent;
        _isAudioEnabled = isAudioEnabled;
        _connectedDeviceCount = connectedDeviceCount;
        _setAudioEnabled = setAudioEnabled;
        _muteOtherWindows = muteOtherWindows;
        _showImageSettings = showImageSettings;
        _showProjectionSettings = showProjectionSettings;
        _logDiagnostic = logDiagnostic;
        _managedContent = managedContent;
        _managedContentOriginalLayoutTransform = managedContent?.LayoutTransform;
        _managedContentDetached = managedContentDetached;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _contextMenu = new ContextMenu
        {
            Style = (Style)Application.Current.FindResource("DeviceContextMenuStyle"),
            Placement = PlacementMode.MousePoint,
        };
        var itemStyle = (Style)Application.Current.FindResource("DeviceMenuItemStyle");
        var submenuStyle = (Style)Application.Current.FindResource("DeviceSubmenuItemStyle");
        _fullScreenItem = new MenuItem { Style = itemStyle };
        _fullScreenItem.Click += (_, _) => ToggleFullScreen();
        _windowMenuItem = new MenuItem { Style = submenuStyle };
        _topMostItem = new MenuItem { Style = itemStyle };
        _topMostItem.Click += (_, _) => ToggleTopMost();
        _fixedItem = new MenuItem { Style = itemStyle };
        _fixedItem.Click += (_, _) => ToggleFixedWindow();
        _windowMenuItem.Items.Add(_topMostItem);
        _windowMenuItem.Items.Add(_fixedItem);
        _displayMenuItem = new MenuItem { Style = submenuStyle };
        _muteMenuItem = new MenuItem { Style = submenuStyle };
        _muteThisItem = new MenuItem { Style = itemStyle };
        _muteThisItem.Click += (_, _) => ToggleWindowAudio();
        _muteOthersItem = new MenuItem { Style = itemStyle };
        _muteOthersItem.Click += (_, _) => MuteOtherWindows();
        _muteMenuItem.Items.Add(_muteThisItem);
        _muteMenuItem.Items.Add(_muteOthersItem);
        _rotateLeftItem = new MenuItem { Style = itemStyle };
        _rotateLeftItem.Click += (_, _) => Rotate(-1);
        _rotateRightItem = new MenuItem { Style = itemStyle };
        _rotateRightItem.Click += (_, _) => Rotate(1);
        _closeItem = new MenuItem { Style = itemStyle };
        _closeItem.Click += (_, _) => QueueClose();
        if (managedContent is null)
        {
            _cornerItem = new MenuItem { Style = itemStyle };
            _cornerItem.Click += (_, _) => ToggleCorners();
            if (_showImageSettings is not null)
            {
                _imageSettingsItem = new MenuItem { Style = itemStyle };
                _imageSettingsItem.Click += (_, _) => ShowImageSettings();
                _displayMenuItem.Items.Add(_imageSettingsItem);
            }
            _displayMenuItem.Items.Add(_cornerItem);
            if (_showProjectionSettings is not null)
            {
                _projectionSettingsItem = new MenuItem { Style = itemStyle };
                _projectionSettingsItem.Click += (_, _) => ShowProjectionSettings();
            }
        }
        _displayMenuItem.Items.Add(_rotateLeftItem);
        _displayMenuItem.Items.Add(_rotateRightItem);
        _contextMenu.Items.Add(_fullScreenItem);
        _contextMenu.Items.Add(_windowMenuItem);
        _contextMenu.Items.Add(_displayMenuItem);
        if (_setAudioEnabled is not null) _contextMenu.Items.Add(_muteMenuItem);
        if (_projectionSettingsItem is not null) _contextMenu.Items.Add(_projectionSettingsItem);
        _contextMenu.Items.Add(new Separator
        {
            Style = (Style)Application.Current.FindResource("DeviceMenuSeparatorStyle"),
        });
        _contextMenu.Items.Add(_closeItem);
        UpdateContextMenuLabels();
        var windowTitle = string.IsNullOrWhiteSpace(title) ? StableTitle : title;
        var parameters = new HwndSourceParameters(windowTitle)
        {
            Width = 720,
            Height = 900,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = managedContent is null
                ? WsPopup | WsThickFrame | WsSysMenu | WsMinimizeBox |
                    WsClipChildren | WsClipSiblings
                : WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox |
                    WsMaximizeBox | WsClipChildren | WsClipSiblings,
            ExtendedWindowStyle = WsExAppWindow |
                (managedContent is null ? WsExNoRedirectionBitmap : 0),
            TreatAsInputRoot = true,
        };
        _source = new HwndSource(parameters);
        _handle = _source.Handle;
        if (_handle == 0) throw new InvalidOperationException(
            "Could not create the native preview window.");

        if (managedContent is not null)
        {
            _managedContentRoot = new Border
            {
                Background = Brushes.Black,
                Child = managedContent,
                ClipToBounds = true,
            };
            _source.RootVisual = _managedContentRoot;
        }

        _ = SetWindowTextW(_handle, windowTitle);
        ApplyApplicationIcons();
        if (managedContent is null)
        {
            var cornerPreference = DwmDoNotRound;
            _ = DwmSetWindowAttribute(_handle, DwmWindowCornerPreference,
                ref cornerPreference, sizeof(int));
            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttributeColor(_handle, DwmBorderColor,
                ref borderColor, sizeof(uint));
        }
        else
        {
            var darkTitleBar = 1;
            _ = DwmSetWindowAttribute(_handle, DwmUseImmersiveDarkMode,
                ref darkTitleBar, sizeof(int));
        }

        _aspectController = new AspectRatioWindowController(_source,
            sourceWidth, sourceHeight,
            () => !_disposed && !_isFullScreen && _handle != 0 &&
                !IsIconic(_handle) && !IsZoomed(_handle));
        // Install the instance hook only after every callback dependency is
        // initialized; HwndSource construction itself dispatches messages.
        _source.AddHook(WindowProcedure);
        if (managedContent is null)
        {
            // Preserve the original borderless DirectComposition window for
            // wired/wireless mirroring. Video casting alone uses system chrome.
            _ = SetWindowPos(_handle, 0, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        Log("independent_window_created",
            ("mode", WindowMode), ("handle", AppLog.Handle(_sessionHandle)),
            ("size", $"{sourceWidth}x{sourceHeight}"),
            ("title_bar", managedContent is not null));
    }

    internal event EventHandler? Closed;
    internal ulong SessionHandle => _sessionHandle;
    private string WindowMode => _managedContent is null ? "device" : "media_cast";

    private void Log(string eventName, params (string Key, object? Value)[] fields)
    {
        try
        {
            _logDiagnostic?.Invoke(AppLog.Event(eventName,
                fields.Select(field => (object?)field).ToArray()));
        }
        catch (Exception error)
        {
            // Diagnostics must never interfere with HWND ownership or teardown.
            DiagnosticLogger.ExceptionOnce("preview-log-callback", "logging",
                "preview_log_callback_failed", error);
        }
    }

    internal static bool TryCreateAndShowForSession(ulong handle, uint sourceWidth,
        uint sourceHeight, string title, double cornerRadius, double cornerExponent,
        Func<bool> isAudioEnabled, Func<int> connectedDeviceCount,
        Action<bool> setAudioEnabled, Action muteOtherWindows,
        Action<nint> showImageSettings, Action showProjectionSettings,
        out NativePreviewWindow? window, Action<string>? logDiagnostic = null)
    {
        window = null;
        NativePreviewWindow? candidate = null;
        try
        {
            candidate = new NativePreviewWindow(sourceWidth, sourceHeight, title,
                hwnd => NativeCore.AttachDevicePreview(handle, hwnd),
                hwnd => NativeCore.DetachDevicePreview(handle, hwnd),
                hwnd => NativeCore.AttachDevicePreview(handle, hwnd),
                 handle, cornerRadius, cornerExponent, isAudioEnabled,
                 connectedDeviceCount, setAudioEnabled, muteOtherWindows,
                 showImageSettings, showProjectionSettings,
                 logDiagnostic: logDiagnostic);
            if (!candidate._attachPreview(candidate._handle))
            {
                logDiagnostic?.Invoke(AppLog.Event("independent_window_attach_failed",
                    ("mode", "device"), ("handle", AppLog.Handle(handle))));
                candidate.Dispose();
                return false;
            }
            candidate._attached = true;
            _ = ShowWindow(candidate._handle, SwShow);
            candidate._isTopMost = SetWindowPos(
                candidate._handle, HwndTopMost, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate);
            _ = SetForegroundWindow(candidate._handle);
            window = candidate;
            logDiagnostic?.Invoke(AppLog.Event("independent_window_shown",
                ("mode", "device"), ("handle", AppLog.Handle(handle))));
            return true;
        }
        catch (Exception error)
        {
            logDiagnostic?.Invoke(AppLog.Event("independent_window_create_failed",
                ("mode", "device"), ("handle", AppLog.Handle(handle)),
                ("error", AppLog.Error(error))));
            candidate?.Dispose();
            return false;
        }
    }

    internal static bool TryCreateAndShowForContent(FrameworkElement content,
        uint sourceWidth, uint sourceHeight, string title,
        Func<bool> isAudioEnabled, Action<bool> setAudioEnabled,
        Func<int> connectedDeviceCount, Action muteOtherWindows,
        Action contentDetached, out NativePreviewWindow? window,
        Action<string>? logDiagnostic = null)
    {
        window = null;
        NativePreviewWindow? candidate = null;
        try
        {
            candidate = new NativePreviewWindow(sourceWidth, sourceHeight, title,
                _ => true, _ => { }, _ =>
                {
                    content.InvalidateVisual();
                    return true;
                }, 0, 0, 1, isAudioEnabled, connectedDeviceCount, setAudioEnabled,
                 muteOtherWindows, null, null,
                 content, contentDetached, logDiagnostic: logDiagnostic);
            candidate._attached = true;
            _ = ShowWindow(candidate._handle, SwShow);
            candidate._isTopMost = SetWindowPos(
                candidate._handle, HwndTopMost, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate);
            _ = SetForegroundWindow(candidate._handle);
            window = candidate;
            logDiagnostic?.Invoke(AppLog.Event("independent_window_shown",
                ("mode", "media_cast"), ("handle", AppLog.Handle(0))));
            return true;
        }
        catch (Exception error)
        {
            logDiagnostic?.Invoke(AppLog.Event("independent_window_create_failed",
                ("mode", "media_cast"), ("error", AppLog.Error(error))));
            if (candidate is not null) candidate.Dispose();
            else
            {
                if (content.Parent is Decorator owner) owner.Child = null;
                contentDetached();
            }
            return false;
        }
    }

    internal void Activate()
    {
        if (_disposed || _handle == 0) return;
        if (IsIconic(_handle)) _ = ShowWindow(_handle, SwRestore);
        else _ = ShowWindow(_handle, SwShow);
        if (!_attached && _attachPreview(_handle))
            _attached = true;
        else if (_attached)
            _ = _attachPreview(_handle);
        _ = SetForegroundWindow(_handle);
        _ = SetFocus(_handle);
        Log("independent_window_activated",
            ("mode", WindowMode), ("attached", _attached),
            ("full_screen", _isFullScreen));
    }

    internal void HideForShutdown()
    {
        if (_disposed || _handle == 0) return;
        _contextMenu.IsOpen = false;
        _contextMenu.PlacementTarget = null;
        _ = ShowWindow(_handle, SwHide);
    }

    internal bool RefreshPreview()
    {
        var refreshed = !_disposed && _handle != 0 && _refreshPreview(_handle);
        Log("independent_window_refresh",
            ("mode", WindowMode), ("success", refreshed));
        return refreshed;
    }

    internal void SetSourceDimensions(uint width, uint height)
    {
        var changed = _sourceWidth != width || _sourceHeight != height;
        _sourceWidth = width;
        _sourceHeight = height;
        ApplyRotatedDimensions();
        if (changed)
            Log("independent_window_dimensions",
                ("mode", WindowMode), ("size", $"{width}x{height}"),
                ("rotation", _rotation));
    }

    internal void ToggleFullScreen()
    {
        if (_disposed || _handle == 0) return;
        if (_isFullScreen)
        {
            _isFullScreen = false;
            _ = RemovePropW(_handle, "iPhoneMirrorFullScreen");
            _ = SetWindowLongPtrW(_handle, GwlStyle, _restoreStyle);
            _ = SetWindowPos(_handle, 0, _restoreRectangle.Left, _restoreRectangle.Top,
                Math.Max(1, _restoreRectangle.Right - _restoreRectangle.Left),
                Math.Max(1, _restoreRectangle.Bottom - _restoreRectangle.Top),
                SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
            _aspectController.Reflow();
            _ = SetForegroundWindow(_handle);
            Log("independent_window_fullscreen",
                ("mode", WindowMode), ("enabled", false));
            return;
        }

        if (!GetWindowRect(_handle, out _restoreRectangle)) return;
        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref monitorInfo)) return;

        _restoreStyle = GetWindowLongPtrW(_handle, GwlStyle);
        _isFullScreen = true;
        _ = SetPropW(_handle, "iPhoneMirrorFullScreen", (nint)1);
        var fullScreenStyle = _managedContent is null
            ? (nint)(_restoreStyle.ToInt64() & ~WsThickFrame)
            : (nint)(_restoreStyle.ToInt64() & ~(WsCaption | WsThickFrame));
        _ = SetWindowLongPtrW(_handle, GwlStyle, fullScreenStyle);
        _ = SetWindowPos(_handle, 0, monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top,
            SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
        _ = SetForegroundWindow(_handle);
        Log("independent_window_fullscreen",
            ("mode", WindowMode), ("enabled", true));
    }

    private void ApplyApplicationIcons()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            ExtractIconExW(executable, 0, out _largeIcon, out _smallIcon, 1) == 0)
            return;
        if (_largeIcon != 0) _ = SendMessageW(_handle, WmSetIcon, 1, _largeIcon);
        if (_smallIcon != 0) _ = SendMessageW(_handle, WmSetIcon, 0, _smallIcon);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        switch (message)
        {
            case WmNcCalcSize when _managedContent is null:
                handled = true;
                return 0;
            case WmNcHitTest when _managedContent is null:
                handled = true;
                return _isFullScreen || _isFixed ? HtClient : HitTestWindow(lParam);
            case WmContextMenu:
            case WmRightButtonUp:
                handled = true;
                ShowContextMenu();
                return 0;
            case WmNcRightButtonDown when _managedContent is null:
            case WmNcRightButtonUp when _managedContent is null:
            case WmRightButtonDown when _managedContent is null:
                handled = true;
                ShowContextMenu();
                return 0;
            case WmNcLeftButtonDoubleClick when _managedContent is null &&
                wParam.ToInt32() == HtCaption:
                handled = true;
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Input, ToggleFullScreen);
                return 0;
            case WmLeftButtonDoubleClick:
                handled = true;
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Input, ToggleFullScreen);
                return 0;
            case WmEraseBackground:
                // The native DirectComposition visual paints the complete
                // client. Suppressing class-background erase avoids a white
                // flash while its swap chain is being resized/recreated.
                handled = true;
                return 1;
            case WmClose:
                handled = true;
                QueueClose();
                return 0;
            case WmKeyDown when wParam.ToInt32() == VkF11:
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmKeyDown when wParam.ToInt32() == VkEscape && _isFullScreen:
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmSysKeyDown when wParam.ToInt32() == VkReturn && GetKeyState(VkMenu) < 0:
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmDpiChanged:
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    _aspectController.Reflow);
                break;
        }
        return 0;
    }

    private nint HitTestWindow(nint packedScreenPoint)
    {
        if (_handle == 0 || !GetWindowRect(_handle, out var bounds)) return HtClient;
        var packed = packedScreenPoint.ToInt64();
        var x = (short)(packed & 0xFFFF);
        var y = (short)((packed >> 16) & 0xFFFF);
        var dpi = GetDpiForWindow(_handle);
        var border = Math.Max(6, (int)Math.Round(8.0 * (dpi == 0 ? 1.0 : dpi / 96.0)));

        var left = x < bounds.Left + border;
        var right = x >= bounds.Right - border;
        var top = y < bounds.Top + border;
        var bottom = y >= bounds.Bottom - border;
        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return HtCaption;
    }

    private void QueueClose()
    {
        if (_disposed || _closeQueued) return;
        _closeQueued = true;
        Log("independent_window_close_queued", ("mode", WindowMode));
        try
        {
            _source.Dispatcher.BeginInvoke(DispatcherPriority.Send, Dispose);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher can enter shutdown between WM_CLOSE and the
            // queued callback. Dispose synchronously while the HWND is still
            // on this thread instead of surfacing an unhandled hook error.
            try { Dispose(); }
            catch (Exception error)
            {
                Log("independent_window_close_failed", ("error", AppLog.Error(error)));
            }
        }
    }

    private void ShowContextMenu()
    {
        if (_disposed) return;
        UpdateContextMenuLabels();
        _contextMenu.IsOpen = true;
        Log("independent_window_context_menu",
            ("mode", WindowMode), ("full_screen", _isFullScreen),
            ("top_most", _isTopMost), ("fixed", _isFixed));
    }

    private void UpdateContextMenuLabels()
    {
        _fullScreenItem.Header = LocalizationService.Get(
            _isFullScreen ? "IndependentWindowExitFullScreen" :
                "IndependentWindowEnterFullScreen");
        _windowMenuItem.Header = LocalizationService.Get("IndependentWindowWindowMenu");
        _displayMenuItem.Header = LocalizationService.Get("IndependentWindowDisplayMenu");
        _topMostItem.Header = LocalizationService.Get(
            _isTopMost ? "IndependentWindowUnpin" : "IndependentWindowPin");
        _fixedItem.Header = LocalizationService.Get(
            _isFixed ? "IndependentWindowUnfix" : "IndependentWindowFix");
        _fixedItem.IsEnabled = !_isFullScreen;
        if (_cornerItem is not null)
            _cornerItem.Header = LocalizationService.Get(
                _cornersEnabled ? "IndependentWindowRemoveCorners" :
                    "IndependentWindowKeepCorners");
        if (_imageSettingsItem is not null)
            _imageSettingsItem.Header = LocalizationService.Get(
                "IndependentWindowImageSettings");
        if (_projectionSettingsItem is not null)
            _projectionSettingsItem.Header = LocalizationService.Get(
                "IndependentWindowProjectionSettings");
        _rotateLeftItem.Header = LocalizationService.Get("IndependentWindowRotateLeft");
        _rotateRightItem.Header = LocalizationService.Get("IndependentWindowRotateRight");
        _muteMenuItem.Header = LocalizationService.Get("IndependentWindowAudioMenu");
        _muteThisItem.Header = LocalizationService.Get(
            _isAudioEnabled?.Invoke() == false
                ? "IndependentWindowUnmuteThis"
                : "IndependentWindowMuteThis");
        _muteOthersItem.Header = LocalizationService.Get("IndependentWindowMuteOthers");
        _muteOthersItem.Visibility = IndependentWindowAudioPolicy.ShowMuteOthers(
            _connectedDeviceCount?.Invoke() ?? 1)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _closeItem.Header = LocalizationService.Get("IndependentWindowClose");
    }

    private void ToggleWindowAudio()
    {
        if (_disposed || _isAudioEnabled is null || _setAudioEnabled is null) return;
        var enabled = !_isAudioEnabled();
        _setAudioEnabled(enabled);
        UpdateContextMenuLabels();
        Log("independent_window_audio",
            ("mode", WindowMode), ("enabled", enabled));
    }

    private void MuteOtherWindows()
    {
        if (_disposed || _muteOtherWindows is null) return;
        _muteOtherWindows();
        UpdateContextMenuLabels();
        Log("independent_window_mute_others", ("mode", WindowMode));
    }

    private void ShowImageSettings()
    {
        if (_disposed || _showImageSettings is null) return;
        _showImageSettings(_handle);
        Log("independent_window_image_settings", ("mode", WindowMode));
    }

    private void ShowProjectionSettings()
    {
        if (_disposed || _showProjectionSettings is null) return;
        _showProjectionSettings();
        Log("independent_window_projection_settings", ("mode", WindowMode));
    }

    private void ToggleTopMost()
    {
        if (_disposed || _handle == 0) return;
        _isTopMost = !_isTopMost;
        _ = SetWindowPos(_handle, _isTopMost ? HwndTopMost : HwndNoTopMost,
            0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        UpdateContextMenuLabels();
        Log("independent_window_topmost",
            ("mode", WindowMode), ("enabled", _isTopMost));
    }

    private void ToggleFixedWindow()
    {
        if (_disposed || _handle == 0 || _isFullScreen) return;
        _isFixed = !_isFixed;
        var style = GetWindowLongPtrW(_handle, GwlStyle).ToInt64();
        style = _isFixed ? style & ~WsThickFrame : style | WsThickFrame;
        _ = SetWindowLongPtrW(_handle, GwlStyle, (nint)style);
        _ = SetWindowPos(_handle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        if (!_isFixed) _aspectController.Reflow();
        UpdateContextMenuLabels();
        Log("independent_window_fixed",
            ("mode", WindowMode), ("enabled", _isFixed));
    }

    private void ToggleCorners()
    {
        if (_managedContent is not null || _sessionHandle == 0 || _handle == 0) return;
        _cornersEnabled = !_cornersEnabled;
        _ = NativeCore.SetDeviceWindowCornerProfile(_sessionHandle, _handle,
            _cornersEnabled ? _cornerRadius : 0, _cornerExponent);
        UpdateContextMenuLabels();
        Log("independent_window_corners",
            ("mode", WindowMode), ("enabled", _cornersEnabled));
    }

    private void Rotate(int delta)
    {
        if (_handle == 0) return;
        _rotation = ((_rotation + delta) % 4 + 4) % 4;
        if (_managedContent is not null)
            _managedContent.LayoutTransform = new RotateTransform(_rotation * 90);
        else if (_sessionHandle != 0)
            _ = NativeCore.SetDeviceWindowRotation(_sessionHandle, _handle, _rotation);
        ApplyRotatedDimensions();
        Log("independent_window_rotation",
            ("mode", WindowMode), ("quarter_turns", _rotation));
    }

    private void ApplyRotatedDimensions()
    {
        if (_sourceWidth == 0 || _sourceHeight == 0) return;
        if ((_rotation & 1) != 0)
            _aspectController.SetSourceDimensions(_sourceHeight, _sourceWidth);
        else
            _aspectController.SetSourceDimensions(_sourceWidth, _sourceHeight);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Log("independent_window_dispose_begin",
            ("mode", WindowMode), ("attached", _attached),
            ("full_screen", _isFullScreen));
        _disposed = true;
        _contextMenu.IsOpen = false;
        _contextMenu.PlacementTarget = null;
        _ = ShowWindow(_handle, SwHide);
        _aspectController.Dispose();
        if (_attached && _handle != 0)
        {
            _detachPreview(_handle);
            _attached = false;
        }
        if (_managedContentRoot is not null)
        {
            if (_managedContent is not null)
                _managedContent.LayoutTransform =
                    _managedContentOriginalLayoutTransform ?? Transform.Identity;
            _managedContentRoot.Child = null;
            _source.RootVisual = null;
        }
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
        _handle = 0;
        if (_managedContentRoot is not null)
        {
            try { _managedContentDetached?.Invoke(); }
            catch { /* Window teardown remains best-effort. */ }
        }
        if (_largeIcon != 0) _ = DestroyIcon(_largeIcon);
        if (_smallIcon != 0 && _smallIcon != _largeIcon) _ = DestroyIcon(_smallIcon);
        _largeIcon = 0;
        _smallIcon = 0;
        Log("independent_window_disposed", ("mode", WindowMode));
        var closedHandlers = Closed;
        Closed = null;
        if (closedHandlers is null) return;
        foreach (EventHandler handler in closedHandlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception error)
            {
                Log("independent_window_closed_handler_failed",
                    ("mode", WindowMode), ("error", AppLog.Error(error)));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal WindowRect Monitor;
        internal WindowRect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(nint window, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPropW(nint window, string name, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint RemovePropW(nint window, string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int iconIndex,
        out nint largeIcon, out nint smallIcon, uint iconCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute,
        ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeColor(nint window, int attribute,
        ref uint value, int valueSize);

}
