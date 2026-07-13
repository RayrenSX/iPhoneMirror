using System.Text;

namespace IPhoneMirror.DriverInstaller.Services;

internal static class DriverLogger
{
    private static readonly object Gate = new();

    internal static string DirectoryPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror.Driver", "Logs");
    internal static string Path => System.IO.Path.Combine(DirectoryPath, "driver-ui.log");

    internal static void EnsureCreated()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!File.Exists(Path))
                File.WriteAllText(Path, string.Empty, Encoding.UTF8);
        }
    }

    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path,
                    $"{DateTime.UtcNow:O} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never prevent a driver operation from finishing.
        }
    }
}
