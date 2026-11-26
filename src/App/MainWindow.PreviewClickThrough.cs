using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private const int FirstPreviewInteractionHotkeyId = 0x498C;
    private static readonly KeyboardShortcut PreviewInteractionHotkey =
        new(KeyboardShortcut.Control | KeyboardShortcut.Alt | KeyboardShortcut.Shift, 'P');
    private const string PreviewInteractionShortcut = "Ctrl+Alt+Shift+P";
    private readonly HashSet<NativePreviewWindow> _clickThroughWindows = [];
    private bool _previewInteractionHotkeyRegistered;
    private int _previewInteractionHotkeyId;
    private TrayIconService? _previewInteractionTrayIcon;

    private void ConfigurePreviewClickThrough(NativePreviewWindow window)
    {
        window.ClickThroughRequested = enabled =>
            enabled ? EnablePreviewClickThrough(window) : window.SetClickThrough(false);
        window.ClickThroughChanged += enabled =>
        {
            if (!enabled && _clickThroughWindows.Remove(window)) UpdatePreviewInteractionRecovery();
        };
        window.Closed += (_, _) =>
        {
            if (_clickThroughWindows.Remove(window)) UpdatePreviewInteractionRecovery();
        };
    }

    private bool EnablePreviewClickThrough(NativePreviewWindow window)
    {
        if (_shutdownStarted || _bossKeyHidden) return false;
        if (window.IsClickThrough) return true;

        _clickThroughWindows.Add(window);
        UpdatePreviewInteractionRecovery();
        if (!HasPreviewInteractionRecovery || !window.SetClickThrough(true))
        {
            _clickThroughWindows.Remove(window);
            UpdatePreviewInteractionRecovery();
            return false;
        }

        if (GetScreenTextSource(window.Handle) is { } source) CloseScreenTextSelectionForSource(source);
        if (_previewInteractionHotkeyRegistered)
            ShowPreviewFeedback(window.Handle, "IndependentWindowClickThroughEnabled", null,
                () => window.IsClickThrough, PreviewInteractionShortcut);
        else
            ShowPreviewFeedback(window.Handle, "IndependentWindowClickThroughTrayHint", null,
                () => window.IsClickThrough);
        return true;
    }

    private bool HasPreviewInteractionRecovery =>
        _previewInteractionHotkeyRegistered || _trayPanel is not null || _previewInteractionTrayIcon is not null;

    private void RestorePreviewInteraction()
    {
        foreach (var window in _clickThroughWindows.ToArray())
            _ = window.SetClickThrough(false);
    }

    private bool CanUsePreviewInteractionHotkey =>
        _previewInteractionHotkeyRegistered && !_shutdownStarted && _clickThroughWindows.Count != 0;

    private void QueuePreviewInteractionRestore()
    {
        var id = _previewInteractionHotkeyId;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (CanUsePreviewInteractionHotkey && _previewInteractionHotkeyId == id)
                RestorePreviewInteraction();
        });
    }

    private void UpdatePreviewInteractionRecovery()
    {
        var active = !_shutdownStarted && _clickThroughWindows.Count != 0;
        _trayPanel?.SetPreviewInteractionRestoreVisible(active);
        if (!active)
        {
            DisposePreviewInteractionRecovery();
            return;
        }

        if (!_previewInteractionHotkeyRegistered && _windowSource is { Handle: var hwnd } && hwnd != 0)
        {
            // An already queued WM_HOTKEY can outlive UnregisterHotKey.
            _previewInteractionHotkeyId = _previewInteractionHotkeyId == FirstPreviewInteractionHotkeyId
                ? FirstPreviewInteractionHotkeyId + 1 : FirstPreviewInteractionHotkeyId;
            _previewInteractionHotkeyRegistered = RegisterHotKey(hwnd, _previewInteractionHotkeyId,
                PreviewInteractionHotkey.RegistrationModifiers, PreviewInteractionHotkey.VirtualKey);
        }

        if (_trayPanel is not null)
        {
            _previewInteractionTrayIcon?.Dispose();
            _previewInteractionTrayIcon = null;
        }
        else if (_previewInteractionTrayIcon is null)
        {
            try
            {
                _previewInteractionTrayIcon = new TrayIconService(RestorePreviewInteraction,
                    "iPhoneMirror · " + LocalizationService.Get("IndependentWindowRestoreInteraction"));
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                DiagnosticLogger.Exception("tray", "interaction_restore_icon_failed", error);
            }
        }
    }

    private void EnsurePreviewInteractionRecovery()
    {
        UpdatePreviewInteractionRecovery();
        if (!HasPreviewInteractionRecovery) RestorePreviewInteraction();
    }

    private void DisposePreviewInteractionRecovery()
    {
        if (_previewInteractionHotkeyRegistered && _windowSource is { Handle: var hwnd } && hwnd != 0)
            _ = UnregisterHotKey(hwnd, _previewInteractionHotkeyId);
        _previewInteractionHotkeyRegistered = false;
        _previewInteractionTrayIcon?.Dispose();
        _previewInteractionTrayIcon = null;
    }
}
