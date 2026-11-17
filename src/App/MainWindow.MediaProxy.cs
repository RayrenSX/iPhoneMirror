using IPhoneMirror.App.Services;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private MediaHttpProxyBridge? _mediaHttpProxyBridge;
    private Uri? _mediaHttpProxySource;

    private Uri ResolveNativeMediaCastSource(Uri source)
    {
        if (!AppProxyPolicy.RequiresExplicitMediaRouting(source)) return source;
        if (_mediaHttpProxyBridge is not null && _mediaHttpProxySource == source)
            return _mediaHttpProxyBridge.PlaybackUri;

        DisposeMediaHttpProxyBridge();
        _mediaHttpProxyBridge = MediaHttpProxyBridge.TryStart(source,
            message => _viewModel.AddDiagnosticLog(AppLog.Event("media_http_proxy",
                ("message", message))));
        if (_mediaHttpProxyBridge is null)
            throw new InvalidOperationException(
                LocalizationService.Get("UnknownError"));
        _mediaHttpProxySource = source;
        return _mediaHttpProxyBridge.PlaybackUri;
    }

    private void DisposeMediaHttpProxyBridge()
    {
        var bridge = _mediaHttpProxyBridge;
        _mediaHttpProxyBridge = null;
        _mediaHttpProxySource = null;
        try { bridge?.Dispose(); }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_http_proxy_dispose_failed",
                ("error", error.GetType().Name)));
        }
    }
}
