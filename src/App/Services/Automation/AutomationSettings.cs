using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IPhoneMirror.App.Services.Automation;

internal sealed class AutomationSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 17890;
    public int RequestsPerSecond { get; set; } = 60;
    public int RequestBurst { get; set; } = 120;
    public int ScreenshotsPerSecond { get; set; } = 5;
    public string[] Permissions { get; set; } = ["device.read", "device.control", "screen.capture",
        "keyboard.input", "clipboard.read", "clipboard.write"];
    internal AutomationSettings Clone() => new()
    {
        Enabled = Enabled, Port = Port, RequestsPerSecond = RequestsPerSecond,
        RequestBurst = RequestBurst, ScreenshotsPerSecond = ScreenshotsPerSecond,
        Permissions = [.. Permissions ?? []]
    };
    internal void Validate()
    {
        if (Port is < 1024 or > 65535 || RequestsPerSecond is < 1 or > 1000 ||
            RequestBurst is < 1 or > 2000 || ScreenshotsPerSecond is < 1 or > 30)
            throw new InvalidOperationException("Invalid Automation API settings.");
    }
}

internal sealed class AutomationKeyStore(string? path = null)
{
    private readonly object _sync = new();
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "automation-key.bin");
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("iPhoneMirror.Automation.v1");
    internal string LoadOrCreate()
    {
        lock (_sync) return LoadOrCreateCore();
    }
    private string LoadOrCreateCore()
    {
        if (!File.Exists(_path)) return Regenerate();
        var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), Purpose, DataProtectionScope.CurrentUser));
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid API key store.");
        return key;
    }
    internal string Regenerate()
    {
        lock (_sync) return RegenerateCore();
    }
    private string RegenerateCore()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Purpose, DataProtectionScope.CurrentUser));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return key;
    }
}
