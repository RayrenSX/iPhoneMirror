using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private sealed class PreviewTouchContact(MappedTouchRoute route, double x, double y)
    {
        internal readonly MappedTouchRoute Route = route;
        internal double X = x, Y = y;
        internal bool Pressed = true, MovePending, MoveDraining;
    }

    private readonly Dictionary<string, Dictionary<(nint Window, uint Id), PreviewTouchContact>> _previewTouches =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task HandlePreviewTouchAsync(PreviewPointerEventArgs input, string udid)
    {
        if (!_previewTouches.TryGetValue(udid, out var contacts))
            _previewTouches[udid] = contacts = [];
        var key = (input.SourceWindow, input.TouchId);
        contacts.TryGetValue(key, out var contact);
        try
        {
            if (input.Kind == PreviewPointerKind.TouchDown)
            {
                if (contact is not null || contacts.Count >= CoreDeviceTouchProtocol.MaxSlots) return;
                var width = input.SourceWidth != 0 ? input.SourceWidth : _viewModel.SourceVideoWidth;
                var height = input.SourceHeight != 0 ? input.SourceHeight : _viewModel.SourceVideoHeight;
                var mapped = MapPointerToNormalized(input, width, height);
                if (mapped is null) return; // Letterbox bars do not start contacts.
                var sourceWindow = GetControlKeyboardWindow(udid);
                var guard = CapturePointerSendGuard(sourceWindow);
                var portrait = _viewModel.AppliedBluetoothPortraitMouseDirection;
                var landscape = _viewModel.AppliedBluetoothLandscapeMouseDirection;
                var reverseX = _viewModel.AppliedBluetoothMouseReverseHorizontal;
                var reverseY = _viewModel.AppliedBluetoothMouseReverseVertical;
                var route = _viewModel.CaptureTouchRoute(udid,
                    () => guard() && GetControlKeyboardWindow(udid) == sourceWindow && contact?.Pressed != false,
                    (x, y) => BluetoothMouseOrientationMapper.MapNormalized(x, y, width, height, input.Rotation,
                        portrait, landscape, reverseX, reverseY));
                if (route is null) return;
                var position = route.Transform(mapped.Value.X, mapped.Value.Y);
                contact = new PreviewTouchContact(route, position.X, position.Y);
                contacts.Add(key, contact);
                await route.SendAsync("down", contact.X, contact.Y, default);
            }
            else if (contact is not null)
            {
                var width = input.SourceWidth != 0 ? input.SourceWidth : _viewModel.SourceVideoWidth;
                var height = input.SourceHeight != 0 ? input.SourceHeight : _viewModel.SourceVideoHeight;
                var mapped = MapPointerToNormalized(input, width, height);
                if (mapped is { } point)
                    (contact.X, contact.Y) = contact.Route.Transform(point.X, point.Y);
                if (input.Kind == PreviewPointerKind.TouchUp)
                {
                    contacts.Remove(key);
                    await ReleasePreviewTouchAsync(contact);
                }
                else if (input.Kind == PreviewPointerKind.TouchMove)
                {
                    contact.MovePending = true;
                    if (contact.MoveDraining) return;
                    contact.MoveDraining = true;
                    try
                    {
                        while (contact.Pressed && contact.MovePending)
                        {
                            contact.MovePending = false;
                            await contact.Route.SendAsync("move", contact.X, contact.Y, default);
                        }
                    }
                    finally { contact.MoveDraining = false; }
                }
            }
        }
        catch (Exception error)
        {
            // Each callback owns only its original contact, even if Windows
            // has already reused the native ID for a new down.
            if (contact is not null)
            {
                if (contacts.TryGetValue(key, out var current) && ReferenceEquals(current, contact))
                    contacts.Remove(key);
                await ReleasePreviewTouchAsync(contact);
            }
            if (error is not OperationCanceledException)
                _viewModel.AddDiagnosticLog(AppLog.Event("preview_touch_failed", ("error", AppLog.Error(error))));
        }
    }

    private static async Task ReleasePreviewTouchAsync(PreviewTouchContact contact)
    {
        if (!contact.Pressed) return;
        contact.Pressed = false;
        contact.MovePending = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await contact.Route.SendAsync("up", contact.X, contact.Y, timeout.Token);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        catch (System.IO.IOException) { }
    }

    private Task ResetPreviewTouchesAsync(string udid)
    {
        if (!_previewTouches.Remove(udid, out var contacts)) return Task.CompletedTask;
        // Start all releases before yielding; subsequent downs cannot overtake them.
        return Task.WhenAll(contacts.Values.Select(ReleasePreviewTouchAsync));
    }

    private void ResetAllPreviewTouches()
    {
        foreach (var udid in _previewTouches.Keys.ToArray()) _ = ResetPreviewTouchesAsync(udid);
    }

    private void RetireInvalidPreviewTouches()
    {
        foreach (var contacts in _previewTouches.Values)
            foreach (var pair in contacts.Where(pair => !pair.Value.Route.IsCurrent()).ToArray())
            {
                contacts.Remove(pair.Key);
                _ = ReleasePreviewTouchAsync(pair.Value);
            }
    }
}
