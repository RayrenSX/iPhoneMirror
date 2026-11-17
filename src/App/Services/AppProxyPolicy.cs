using System.Net;
using System.Net.Http;

namespace IPhoneMirror.App.Services;

// Applied once at startup. Saving preferences deliberately does not alter
// active transfers, receiver processes or media decoders.
internal static class AppProxyPolicy
{
    private static readonly string[] ProxyVariables =
        ["http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY"];
    private static readonly string[] BypassVariables = ["no_proxy", "NO_PROXY"];
    private static readonly Dictionary<string, string?> StartupEnvironment =
        ProxyVariables.Concat(BypassVariables).ToDictionary(name => name,
            Environment.GetEnvironmentVariable, StringComparer.Ordinal);
    private static readonly IWebProxy StartupProxy = HttpClient.DefaultProxy;
    private static readonly object Gate = new();
    private static int _currentMode;

    internal static AppProxyMode CurrentMode => (AppProxyMode)Volatile.Read(ref _currentMode);

    internal static bool RequiresExplicitMediaRouting(Uri source) =>
        CurrentMode is AppProxyMode.Direct or AppProxyMode.Manual &&
        source.IsAbsoluteUri && !source.IsLoopback &&
        (source.Scheme == Uri.UriSchemeHttp || source.Scheme == Uri.UriSchemeHttps);

    internal static bool TryNormalize(AppProxySettings? input, out AppProxySettings normalized)
    {
        normalized = new();
        if (input is null || !Enum.IsDefined(input.Mode)) return false;
        var validAddress = TryNormalizeAddress(input.Address, out var address);
        if (input.Mode == AppProxyMode.Manual && !validAddress) return false;
        normalized.Mode = input.Mode;
        normalized.Address = validAddress ? address : AppProxySettings.DefaultAddress;
        return true;
    }

    private static bool TryNormalizeAddress(string? input, out string address)
    {
        address = AppProxySettings.DefaultAddress;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 2048 || text.Any(char.IsWhiteSpace) ||
            text.Any(char.IsControl)) return false;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || string.IsNullOrEmpty(uri.Host) ||
            uri.HostNameType == UriHostNameType.Unknown || uri.Port is < 1 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/") return false;
        address = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return true;
    }

    internal static void Apply(AppProxySettings? settings)
    {
        if (!TryNormalize(settings, out var normalized)) normalized = new();
        lock (Gate)
        {
            if (normalized.Mode == AppProxyMode.Default)
            {
                foreach (var pair in StartupEnvironment)
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value,
                        EnvironmentVariableTarget.Process);
                HttpClient.DefaultProxy = StartupProxy;
            }
            else
            {
                foreach (var variable in ProxyVariables)
                    Environment.SetEnvironmentVariable(variable,
                        normalized.Mode == AppProxyMode.Manual ? normalized.Address : null,
                        EnvironmentVariableTarget.Process);
                var bypass = string.Join(',', StartupEnvironment
                    .Where(pair => BypassVariables.Contains(pair.Key)).Select(pair => pair.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .SelectMany(value => value!.Split(',', StringSplitOptions.TrimEntries |
                        StringSplitOptions.RemoveEmptyEntries))
                    .Concat(["localhost", "127.0.0.1", "::1"])
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                foreach (var variable in BypassVariables)
                    Environment.SetEnvironmentVariable(variable, bypass,
                        EnvironmentVariableTarget.Process);
                HttpClient.DefaultProxy = normalized.Mode == AppProxyMode.Manual
                    ? new LoopbackBypassProxy(new WebProxy(new Uri(normalized.Address), false))
                    : new WebProxy();
            }
            Volatile.Write(ref _currentMode, (int)normalized.Mode);
        }
    }

    private sealed class LoopbackBypassProxy(IWebProxy inner) : IWebProxy
    {
        public ICredentials? Credentials { get => inner.Credentials; set => inner.Credentials = value; }
        public Uri? GetProxy(Uri destination) => destination.IsLoopback
            ? destination : inner.GetProxy(destination);
        public bool IsBypassed(Uri host) => host.IsLoopback || inner.IsBypassed(host);
    }
}
