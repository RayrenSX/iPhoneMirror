using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunProxyMediaPlaybackTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_APP_LOG_DIRECTORY", Path.Combine(output, "logs"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "settings.json")));
        app.InitializeComponent();
        MainWindow? window = null;
        try
        {
            var ffmpeg = HlsMediaPlaybackBridge.FindBundledFfmpeg();
            InteractionAssert(ffmpeg is not null, "Proxy playback requires trusted bundled FFmpeg.");
            const double duration = 3.2;
            using var fixture = new ShortMediaPlaybackServer(ffmpeg!, output, [duration], [(320, 240)],
                [TimeSpan.Zero], mp4Item: 0);
            var remoteSource = new Uri("http://proxy-media.invalid/signed/video.mp4?signature=fixture%2Bonly");
            using var proxy = new ProxyMediaPlaybackServer(remoteSource, fixture.Mp4Source(0));
            AppProxyPolicy.Apply(new AppProxySettings { Mode = AppProxyMode.Manual, Address = proxy.Address.AbsoluteUri });
            InteractionAssert(AppProxyPolicy.CurrentMode == AppProxyMode.Manual, "Manual proxy policy was not installed.");
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
            window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            var viewModel = (MainViewModel)KeyboardField(window, "_viewModel");
            var stopRequests = 0;
            Action stopObserved = () => ++stopRequests;
            var stopEvent = viewModel.GetType().GetEvent("MediaCastStopRequested", KeyboardTestMembers)!;
            stopEvent.GetAddMethod(true)!.Invoke(viewModel, [stopObserved]);
            var results = new List<object>();
            ulong command = 1;
            try
            {
                void AssertRetained() => InteractionAssert(stopRequests == 0 && viewModel.IsMediaCasting &&
                    (bool)KeyboardField(window, "_mediaCastActive"), "Proxy playback stopped the sender session.");
                void AssertRoute(Uri source, bool relayed)
                {
                    var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                    var bridge = KeyboardField(window, "_mediaHttpProxyBridge") as MediaHttpProxyBridge;
                    InteractionAssert(Equals(KeyboardField(window, "_mediaSource"), source) &&
                        KeyboardField(window, "_mediaHlsBridge") is null &&
                        !(bool)KeyboardField(window, "_mediaUsesHlsBridge"),
                        "Proxy MP4 changed the original source or entered the HLS backend.");
                    InteractionAssert(relayed
                        ? bridge is not null && bridge.PlaybackUri.IsLoopback && Equals(element.Source, bridge.PlaybackUri) &&
                            Equals(KeyboardField(window, "_mediaPlaybackSource"), bridge.PlaybackUri)
                        : bridge is null && Equals(element.Source, source) && source.IsLoopback,
                        "Native MP4 did not use the expected explicit proxy or loopback-bypass route.");
                }
                void Play(Uri source)
                {
                    KeyboardCall(window, "OnMediaCastCommandReceived", new MediaCastRequest(command++, MediaCastCommand.Play,
                        MediaCastFlags.MuteSpecified | MediaCastFlags.Muted, source.AbsoluteUri, duration, 0, .33));
                }
                void Complete(string name, Uri source, bool relayed)
                {
                    AssertRoute(source, relayed);
                    var maximum = 0d;
                    string? first = null;
                    var tailCaptured = false;
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed.TotalSeconds < 15)
                    {
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                        var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                        if ((bool)KeyboardField(window, "_mediaOpened"))
                            maximum = Math.Max(maximum, element.Position.TotalSeconds);
                        if (first is null && maximum >= .4)
                            first = CaptureSlowStartupFrame(window, Path.Combine(output, name + "-first.png")).Hash;
                        if (!tailCaptured && first is not null && maximum >= duration - .4)
                        {
                            var tail = CaptureSlowStartupFrame(window, Path.Combine(output, name + "-tail.png")).Hash;
                            InteractionAssert(tail != first, name + " advanced its clock with a frozen video frame.");
                            tailCaptured = true;
                        }
                        var terminal = (MediaCastPlaybackStatus)KeyboardField(window, "_mediaTerminalStatus");
                        InteractionAssert(terminal != MediaCastPlaybackStatus.Failed, name + " failed; see isolated logs.");
                        if (terminal == MediaCastPlaybackStatus.Completed) break;
                    }
                    InteractionAssert(Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Completed) &&
                        maximum >= duration - .4 && tailCaptured &&
                        !(bool)KeyboardField(window, "_mediaPrematureEndRetried"),
                        name + " did not render to actual natural completion.");
                    AssertRoute(source, relayed);
                    AssertRetained();
                    results.Add(new { name, relayed, maximum, elapsedSeconds = watch.Elapsed.TotalSeconds,
                        proxyRequests = proxy.RequestCount });
                    Console.WriteLine($"PASS proxy MP4 {name}: actual={maximum:F3}, relayed={relayed}, session retained.");
                }

                Play(remoteSource);
                Complete("manual-proxy", remoteSource, relayed: true);
                InteractionAssert(proxy.RequestCount > 0 && proxy.Targets.All(target => target == remoteSource.AbsoluteUri),
                    "The unresolved media address was not requested through the selected HTTP proxy.");
                var firstAttemptRequests = proxy.RequestCount;
                var retired = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                KeyboardCall(window, "OnMediaCastCommandReceived", new MediaCastRequest(command++, MediaCastCommand.Resume,
                    MediaCastFlags.None, "", 0, 0, 0));
                InteractionAssert(!ReferenceEquals(retired, KeyboardField(window, "MediaCastMediaElement")),
                    "Completed proxy MP4 did not reopen its native renderer for replay.");
                Complete("manual-proxy-resume", remoteSource, relayed: true);
                InteractionAssert(proxy.RequestCount > firstAttemptRequests, "Native replay did not reopen the proxy transport.");

                var beforeBypass = proxy.RequestCount;
                Play(fixture.Mp4Source(0));
                Complete("manual-loopback-bypass", fixture.Mp4Source(0), relayed: false);
                InteractionAssert(proxy.RequestCount == beforeBypass && proxy.Failures.IsEmpty,
                    "Loopback playback reached the manual proxy or the proxy rejected a media request.");
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
                {
                    description = "Actual MainWindow/native WPF MP4 from an unresolved .invalid source via a local HTTP proxy, natural completion and Resume, followed by actual loopback playback bypassing that proxy. No real application or phone is controlled.",
                    cases = results, stopRequests, proxyRequests = proxy.RequestCount,
                    proxyTargets = proxy.Targets.ToArray(), proxyRanges = proxy.Ranges.ToArray(),
                    fixtureRequests = fixture.Mp4Requests,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                stopEvent.GetRemoveMethod(true)!.Invoke(viewModel, [stopObserved]);
                KeyboardCall(window, "StopMediaCastPlayback", "proxy_playback_test_cleanup");
                CloseWorkspaceTestWindow(window);
                window = null;
            }
            return 0;
        }
        finally
        {
            if (window is not null)
            {
                KeyboardCall(window, "StopMediaCastPlayback", "proxy_playback_test_cleanup");
                CloseWorkspaceTestWindow(window);
            }
            AppProxyPolicy.Apply(new AppProxySettings { Mode = AppProxyMode.Default });
            app.Shutdown();
        }
    }

    private sealed class ProxyMediaPlaybackServer : IDisposable
    {
        private readonly Uri _source;
        private readonly Uri _fixture;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
        private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(10) };
        private readonly Task _serving;
        private int _requestCount;
        internal readonly ConcurrentQueue<string> Targets = new();
        internal readonly ConcurrentQueue<string> Ranges = new();
        internal readonly ConcurrentQueue<string> Failures = new();
        internal Uri Address { get; }
        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal ProxyMediaPlaybackServer(Uri source, Uri fixture)
        {
            _source = source;
            _fixture = fixture;
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var connection = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                    _connections.TryAdd(connection, 0);
                    _ = ServeRequestAsync(connection);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task ServeRequestAsync(TcpClient connection)
        {
            using (connection)
            {
                try
                {
                    var stream = connection.GetStream();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 2048, leaveOpen: true);
                    var first = (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false))?.Split(' ');
                    if (first is not { Length: 3 } || first[0] is not ("GET" or "HEAD") ||
                        !Uri.TryCreate(first[1], UriKind.Absolute, out var source) || source != _source)
                        throw new InvalidDataException("Unexpected proxy request target or method.");
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var bytes = 0;
                    while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { Length: > 0 } line)
                    {
                        bytes += line.Length;
                        if (bytes > 16 * 1024) throw new InvalidDataException("Proxy fixture header limit exceeded.");
                        var separator = line.IndexOf(':');
                        if (separator > 0) headers[line[..separator]] = line[(separator + 1)..].Trim();
                    }
                    Targets.Enqueue(first[1]);
                    Interlocked.Increment(ref _requestCount);
                    using var upstream = new HttpRequestMessage(new HttpMethod(first[0]), _fixture);
                    if (headers.TryGetValue("Range", out var range))
                    {
                        Ranges.Enqueue(range);
                        upstream.Headers.TryAddWithoutValidation("Range", range);
                    }
                    using var response = await _client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token).ConfigureAwait(false);
                    var header = new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\nConnection: close\r\n");
                    foreach (var name in new[] { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges" })
                    {
                        if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                            header.Append(name).Append(": ").AppendJoin(", ", values).Append("\r\n");
                    }
                    header.Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), deadline.Token).ConfigureAwait(false);
                    if (first[0] != "HEAD")
                        await response.Content.CopyToAsync(stream, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                    if (error is InvalidDataException) Failures.Enqueue(error.Message);
                }
                catch (Exception error) { Failures.Enqueue(error.GetType().Name); }
                finally { _connections.TryRemove(connection, out _); }
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            foreach (var connection in _connections.Keys) connection.Dispose();
            _client.Dispose();
            _serving.GetAwaiter().GetResult();
            _cancellation.Dispose();
        }
    }
}
