using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UsbTouchDemo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var bridgePath = ResolveBridge(args);
        using var bridge = new TouchBridge(bridgePath);
        using var form = new TouchForm(bridge, Path.GetFileName(bridgePath));
        form.ShowDialog();
    }

    private static string ResolveBridge(string[] args)
    {
        if (args.Length > 0 && File.Exists(args[0])) return Path.GetFullPath(args[0]);
        var beside = Path.Combine(AppContext.BaseDirectory, "iUsbBridge.exe");
        if (File.Exists(beside)) return beside;
        throw new FileNotFoundException(
            "Put iUsbBridge.exe beside UsbTouchDemo.exe, or pass its path as the first argument.",
            beside);
    }
}

internal sealed class TouchBridge : IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private long _sequence;

    public TouchBridge(string executable)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--rate-hz 120",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };
        if (!_process.Start()) throw new InvalidOperationException("Could not start the USB touch bridge.");
        _input = _process.StandardInput.BaseStream;
        _ = DrainAsync(_process.StandardOutput);
        _ = DrainAsync(_process.StandardError);
    }

    public void Send(string phase, double x, double y)
    {
        if (_process.HasExited) return;
        var message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "iphoneMirror.touch.v2",
            kind = "touch_batch",
            seq = Interlocked.Increment(ref _sequence),
            timestampNs = (ulong)Stopwatch.GetTimestamp(),
            points = new[] { new { pointerId = 1, action = phase, normalizedX = x, normalizedY = y } },
        });
        Span<byte> header = stackalloc byte[4];
        BitConverter.TryWriteBytes(header, message.Length);
        _input.Write(header);
        _input.Write(message);
        _input.Flush();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { _input.Dispose(); } catch { }
        try
        {
            if (!_process.WaitForExit(2000)) _process.Kill(true);
        }
        catch { }
        _process.Dispose();
    }
}

internal sealed class TouchForm : Form
{
    private readonly TouchBridge _bridge;
    private readonly Label _status;
    private bool _pressed;

    public TouchForm(TouchBridge bridge, string bridgeName)
    {
        _bridge = bridge;
        Text = "USB iPhone Touch Demo";
        ClientSize = new Size(430, 820);
        MinimumSize = new Size(280, 460);
        BackColor = Color.FromArgb(16, 19, 23);
        ForeColor = Color.White;
        DoubleBuffered = true;
        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Text = $"USB touch bridge: {bridgeName} | click or drag in this window",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            BackColor = Color.FromArgb(232, 238, 242),
            ForeColor = Color.FromArgb(25, 35, 42),
        };
        Controls.Add(_status);
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        FormClosed += (_, _) => { if (_pressed) _bridge.Send("up", 0.5, 0.5); };
    }

    private (double X, double Y) Normalize(Point point)
    {
        var width = Math.Max(1, ClientSize.Width);
        var height = Math.Max(1, ClientSize.Height - _status.Height);
        return (Math.Clamp((double)point.X / width, 0, 1),
            Math.Clamp((double)point.Y / height, 0, 1));
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        var p = Normalize(e.Location);
        _bridge.Send("down", p.X, p.Y);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_pressed || e.Button != MouseButtons.Left) return;
        var p = Normalize(e.Location);
        _bridge.Send("move", p.X, p.Y);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_pressed || e.Button != MouseButtons.Left) return;
        _pressed = false;
        var p = Normalize(e.Location);
        _bridge.Send("up", p.X, p.Y);
    }
}
