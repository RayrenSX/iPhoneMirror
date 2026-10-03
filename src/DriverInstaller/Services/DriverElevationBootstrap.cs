using System.Collections;
using System.Diagnostics;
using System.Text;

namespace IPhoneMirror.DriverInstaller.Services;

// Only the OS PowerShell host crosses UAC. It stages the entire verified bundle
// and its native extraction directory before any driver code runs elevated.
internal static class DriverElevationBootstrap
{
    internal static void ValidateEnvironment(IDictionary environment)
    {
        foreach (DictionaryEntry entry in environment)
        {
            var name = (string)entry.Key;
            if (string.IsNullOrEmpty(entry.Value?.ToString())) continue;
            if (name.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DEVPATH", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("APPDOMAIN_MANAGER_ASM", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("APPDOMAIN_MANAGER_TYPE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CLRConfigFile", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Driver elevation does not accept runtime overrides: {name}");
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable,
        string expectedSha256, IReadOnlyList<string> arguments)
    {
        ValidateEnvironment(Environment.GetEnvironmentVariables());
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA256 bundle digest is required.", nameof(expectedSha256));
        using var resource = typeof(DriverElevationBootstrap).Assembly
            .GetManifestResourceStream("DriverElevation.Bootstrap.ps1")
            ?? throw new InvalidOperationException("The elevation bootstrap is missing.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var script = reader.ReadToEnd()
            .Replace("'$SOURCE_LITERAL$'", Quote(executable), StringComparison.Ordinal)
            .Replace("'$HASH_LITERAL$'", Quote(expectedSha256), StringComparison.Ordinal)
            .Replace("'$ARGUMENT_LINE_LITERAL$'", Quote(string.Join(" ",
                arguments.Select(QuoteWindowsArgument))), StringComparison.Ordinal);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe"),
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive",
                     "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    internal static string QuoteWindowsArgument(string value)
    {
        // CommandLineToArgvW quoting, including quotes and trailing backslashes.
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { ++backslashes; continue; }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
