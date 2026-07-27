using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IPhoneMirror.DriverInstaller.Services;

internal static class DriverLogger
{
    private static readonly object Gate = new();
    // The UI and the elevated helper are separate processes but share the
    // diagnostic file when startup or argument validation fails.
    private static readonly Lazy<Mutex?> CrossProcessGate = new(() =>
    {
        try { return new Mutex(false, @"Local\iPhoneMirror.Driver.Log"); }
        catch { return null; }
    });
    private static bool SessionStarted;
    private const int MaximumMessageLength = 4096;
    internal const long MaximumLogBytes = 8L * 1024 * 1024;
    internal const int RetainedArchives = 3;
    private static readonly Regex AppleInstanceIdPattern = new(
        @"(?i)USB\\VID_05AC[^\s,;""']*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AppleSerialPattern = new(
        @"(?i)(?<![A-F0-9])(?:[A-F0-9]{8}-[A-F0-9]{16}|[A-F0-9]{24,40})(?![A-F0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UserPathPattern = new(
        @"(?i)([A-Z]:\\Users\\)[^\\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string SecretNamePattern =
        @"(?:password|passwd|pass|pwd|token|access[-_]?token|refresh[-_]?token|secret|client[-_]?secret|api[-_]?key|x[-_]?api[-_]?key|key|sig|signature|authorization|proxy[-_]?authorization|cookie|set[-_]?cookie)";
    private static readonly Regex SecretHeaderPattern = new(
        @"(?i)((?<![A-Z0-9_-])(?:proxy-)?authorization\s*:\s*|(?<![A-Z0-9_-])(?:set-)?cookie\s*:\s*|(?<![A-Z0-9_-])x-api-key\s*:\s*)[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedSecretPattern = new(
        @"(?i)((?<![A-Z0-9_-])[""']?" + SecretNamePattern +
        @"[""']?\s*[:=]\s*)([""'])([^\r\n]*?)\2",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnquotedSecretPattern = new(
        @"(?i)((?<![A-Z0-9_-])[""']?" + SecretNamePattern +
        @"[""']?\s*[:=]\s*)[^""'\s,;&}\]]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string DirectoryPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror.Driver", "Logs");
    internal static string Path => System.IO.Path.Combine(DirectoryPath, "driver-ui.log");

    internal static void EnsureCreated()
    {
        lock (Gate)
        {
            if (!TryEnterCrossProcessGate(out var crossProcessGate)) return;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (!TryRotateIfNeeded()) return;
                if (!File.Exists(Path))
                    File.WriteAllText(Path, string.Empty, Encoding.UTF8);
                EnsureSessionStarted();
            }
            finally
            {
                LeaveCrossProcessGate(crossProcessGate);
            }
        }
    }

    internal static void Write(string message)
    {
        WriteEvent("ui", "message", ("message", message));
    }

    internal static void WriteEvent(string category, string eventName,
        params (string Key, object? Value)[] fields)
        => WriteCore("INFO", category, eventName, fields);

    private static void WriteCore(string level, string category, string eventName,
        params (string Key, object? Value)[] fields)
    {
        try
        {
            lock (Gate)
            {
                if (!TryEnterCrossProcessGate(out var crossProcessGate)) return;
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    if (!TryRotateIfNeeded()) return;
                    EnsureSessionStarted();
                    File.AppendAllText(Path,
                        FormatEntry(level, category, eventName, fields), Encoding.UTF8);
                }
                finally
                {
                    LeaveCrossProcessGate(crossProcessGate);
                }
            }
        }
        catch
        {
            // Logging must never prevent a driver operation from finishing.
        }
    }

    private static bool TryEnterCrossProcessGate(out Mutex? enteredGate)
    {
        enteredGate = null;
        try
        {
            var gate = CrossProcessGate.Value;
            if (gate is null) return true;
            if (!gate.WaitOne(TimeSpan.FromSeconds(2))) return false;
            enteredGate = gate;
            return true;
        }
        catch (AbandonedMutexException)
        {
            enteredGate = CrossProcessGate.Value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void LeaveCrossProcessGate(Mutex? enteredGate)
    {
        if (enteredGate is null) return;
        try { enteredGate.ReleaseMutex(); }
        catch { }
    }

    private static void EnsureSessionStarted() =>
        EnsureSessionStarted(Path, ref SessionStarted);

    internal static void EnsureSessionStarted(string path, ref bool sessionStarted)
    {
        if (sessionStarted) return;
        File.AppendAllText(path,
            FormatEntry("INFO", "logger", "session_start",
                ("process", Environment.ProcessId),
                ("log_file", DescribePath(path))), Encoding.UTF8);
        sessionStarted = true;
    }

    private static bool TryRotateIfNeeded() =>
        TryRotateIfNeeded(Path, MaximumLogBytes, RetainedArchives,
            ref SessionStarted);

    internal static bool TryRotateIfNeeded(string path, long maximumBytes,
        int retainedArchives, ref bool sessionStarted)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < maximumBytes)
                return true;

            // The active file is about to be replaced.  SessionStarted is
            // process-local, so it must be cleared before the move; otherwise
            // the next event would be appended to the new file without a
            // session_start record.  Clearing it before any filesystem work
            // also covers a partial rotation: a later successful append will
            // establish a fresh session header.
            sessionStarted = false;
            for (var index = retainedArchives; index >= 1; index--)
            {
                var source = index == 1 ? path : $"{path}.{index - 1}";
                var destination = $"{path}.{index}";
                if (File.Exists(destination)) File.Delete(destination);
                if (File.Exists(source)) File.Move(source, destination);
            }
            File.WriteAllText(path, string.Empty, Encoding.UTF8);
            return true;
        }
        catch
        {
            // Do not append to a full log when rotation is unavailable.
            return false;
        }
    }

    internal static void WriteWarning(string category, string eventName,
        params (string Key, object? Value)[] fields) =>
        WriteCore("WARN", category, eventName, fields);

    internal static void WriteError(string category, string eventName,
        params (string Key, object? Value)[] fields) =>
        WriteCore("ERROR", category, eventName, fields);

    internal static void WriteException(string category, string eventName,
        Exception error, params (string Key, object? Value)[] fields)
    {
        var all = new (string Key, object? Value)[fields.Length + 2];
        fields.CopyTo(all, 0);
        all[^2] = ("exception", error.GetType().Name);
        all[^1] = ("error", error.Message);
        WriteError(category, eventName, all);
    }

    internal static string FormatEntry(string level, string category, string eventName,
        params (string Key, object? Value)[] fields)
    {
        var builder = new StringBuilder(MaximumMessageLength + 160);
        builder.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        builder.Append(" level=").Append(SanitizeToken(level));
        builder.Append(" pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        builder.Append(" tid=").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        builder.Append(" category=").Append(SanitizeToken(category));
        builder.Append(" event=").Append(SanitizeToken(eventName));
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            builder.Append(' ').Append(SanitizeToken(key)).Append('=');
            var raw = Convert.ToString(value, CultureInfo.InvariantCulture);
            // Operation IDs are random GUIDs used to correlate the UI and
            // elevated logs.  They are not device identifiers; preserving a
            // valid GUID in this field keeps cross-event diagnostics useful
            // while all other 24+ character hexadecimal values remain masked.
            var rendered = string.Equals(key, "operation",
                    StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(raw, "N", out _)
                ? raw ?? string.Empty
                : Sanitize(raw);
            if (rendered.Length == 0)
            {
                builder.Append("<empty>");
                continue;
            }
            if (rendered.Any(char.IsWhiteSpace) || rendered.Contains('"'))
                builder.Append('"').Append(rendered.Replace("\"", "'"))
                    .Append('"');
            else
                builder.Append(rendered);
        }
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>Removes device identifiers, local user names and credential-like values.</summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Remove credential-bearing headers before normalizing line endings so a
        // header cannot consume the rest of a multi-line exception message.
        var sanitized = SecretHeaderPattern.Replace(value, "$1<redacted>")
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        sanitized = AppleInstanceIdPattern.Replace(sanitized, "apple-device:<redacted>");
        sanitized = AppleSerialPattern.Replace(sanitized, "<device-id-redacted>");
        sanitized = UserPathPattern.Replace(sanitized, "$1<user>");
        sanitized = QuotedSecretPattern.Replace(sanitized,
            match => match.Groups[1].Value + match.Groups[2].Value +
                     "<redacted>" + match.Groups[2].Value);
        sanitized = UnquotedSecretPattern.Replace(sanitized, "$1<redacted>");
        return sanitized.Length <= MaximumMessageLength
            ? sanitized
            : sanitized[..MaximumMessageLength] + "...<truncated>";
    }

    internal static string DeviceFingerprint(string? serial)
    {
        var normalized = DriverConstants.NormalizeSerial(serial ?? string.Empty);
        if (normalized.Length == 0) return "unknown";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "dev-" + Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
    }

    internal static string DescribeDevice(IPhoneMirror.DriverInstaller.Models.AppleDeviceRecord device) =>
        $"fingerprint={DeviceFingerprint(device.Serial)} present={device.IsPresent} " +
        $"service={Sanitize(device.Service)} model={Sanitize(device.ModelName)} " +
        $"capture_filter={device.HasLibUsb0Filter} upper_filters={device.UpperFilters.Length}";

    internal static string DescribePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "<none>";
        try
        {
            var info = new FileInfo(path);
            var name = Sanitize(info.Name);
            if (Directory.Exists(path)) return $"name={name};kind=directory";
            var size = info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "missing";
            return $"name={name};size={size}";
        }
        catch (Exception error)
        {
            return $"name={Sanitize(System.IO.Path.GetFileName(path))};stat={error.GetType().Name}";
        }
    }

    internal static string DescribeUri(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return "<invalid>";
        return $"scheme={uri.Scheme};host={Sanitize(uri.Host)};port={uri.Port}";
    }

    internal static string HashTag(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return "<none>";
        var compact = new string(hash.Where(char.IsAsciiLetterOrDigit).ToArray());
        return compact.Length <= 12 ? compact.ToLowerInvariant() : compact[..12].ToLowerInvariant();
    }

    private static string SanitizeToken(string? value)
    {
        var token = Sanitize(value).Trim();
        if (token.Length == 0) return "unknown";
        return new string(token.Select(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray());
    }
}
