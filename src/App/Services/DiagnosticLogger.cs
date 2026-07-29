using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace IPhoneMirror.App.Services;

internal readonly record struct LogCleanupResult(
    int DeletedFiles, long DeletedBytes, int SkippedFiles);

/// <summary>
/// Process-wide managed diagnostics that remain available before the native
/// core starts and after it shuts down. All writes are bounded and best-effort.
/// </summary>
internal static class DiagnosticLogger
{
    internal const long MaximumLogBytes = 8L * 1024 * 1024;
    internal const int RetainedArchives = 4;
    internal const int RetentionDays = 14;
    internal const long MaximumDirectoryBytes = 64L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly HashSet<string> OnceKeys = new(StringComparer.Ordinal);
    private static bool _sessionStarted;
    private static int _initialized;

    internal static string DirectoryPath { get; } = ResolveDirectoryPath();
    internal static string Path { get; } = System.IO.Path.Combine(
        DirectoryPath, "application.log");
    internal static string NativeLogPath { get; } = System.IO.Path.Combine(
        DirectoryPath, "capture.log");
    internal static string FallbackPath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "iPhoneMirror-fallback.log");

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            Environment.SetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE",
                NativeLogPath, EnvironmentVariableTarget.Process);
            var cleanup = Cleanup(includeActiveLogs: false);
            Write("INFO", "lifecycle", "session_start",
                ("version", Assembly.GetExecutingAssembly().GetName().Version),
                ("os", RuntimeInformation.OSDescription),
                ("architecture", RuntimeInformation.ProcessArchitecture),
                ("framework", RuntimeInformation.FrameworkDescription),
                ("cleanup_deleted", cleanup.DeletedFiles),
                ("cleanup_skipped", cleanup.SkippedFiles));
        }
        catch (Exception error)
        {
            // Diagnostics must never prevent application startup.
            TryWriteFallback(FormatEntry("ERROR", "logging", "initialize_failed",
                ("exception", error.GetType().FullName),
                ("message", error.Message)));
        }
    }

    internal static void Info(string category, string eventName,
        params (string Key, object? Value)[] fields) =>
        Write("INFO", category, eventName, fields);

    internal static void Warning(string category, string eventName,
        params (string Key, object? Value)[] fields) =>
        Write("WARN", category, eventName, fields);

    internal static void Error(string category, string eventName,
        params (string Key, object? Value)[] fields) =>
        Write("ERROR", category, eventName, fields);

    internal static void Exception(string category, string eventName,
        Exception error, params (string Key, object? Value)[] fields)
    {
        var all = new (string Key, object? Value)[fields.Length + 5];
        fields.CopyTo(all, 0);
        all[^5] = ("exception", error.GetType().FullName);
        all[^4] = ("hresult", $"0x{error.HResult:X8}");
        all[^3] = ("message", error.Message);
        all[^2] = ("source", error.Source);
        all[^1] = ("detail", error.ToString());
        Write("ERROR", category, eventName, all);
    }

    internal static void ExceptionOnce(string key, string category,
        string eventName, Exception error,
        params (string Key, object? Value)[] fields)
    {
        lock (Gate)
        {
            if (!OnceKeys.Add(key)) return;
        }
        Exception(category, eventName, error, fields);
    }

    internal static LogCleanupResult Cleanup(bool includeActiveLogs = false)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            var result = CleanupDirectory(DirectoryPath, now,
                includeActiveLogs);
            var driverDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "iPhoneMirror.Driver", "Logs");
            result = Add(result, CleanupDirectory(driverDirectory, now,
                includeActiveLogs));
            result = Add(result, CleanupLegacyNativeLogs(now, includeActiveLogs));
            if (includeActiveLogs) _sessionStarted = false;
            return result;
        }
    }

    internal static void Shutdown(int exitCode)
    {
        Info("lifecycle", "session_end", ("exit_code", exitCode));
    }

    private static void Write(string level, string category, string eventName,
        params (string Key, object? Value)[] fields)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                if (!TryRotateIfNeeded(Path, MaximumLogBytes,
                        RetainedArchives, ref _sessionStarted))
                {
                    TryWriteFallback(FormatEntry(level, category, eventName, fields) +
                        FormatEntry("ERROR", "logging", "rotation_failed",
                            ("file", System.IO.Path.GetFileName(Path))));
                    return;
                }
                EnsureSessionHeader();
                File.AppendAllText(Path,
                    FormatEntry(level, category, eventName, fields),
                    new UTF8Encoding(false));
            }
        }
        catch (Exception error)
        {
            // Never throw from an error-reporting path.
            TryWriteFallback(FormatEntry(level, category, eventName, fields) +
                FormatEntry("ERROR", "logging", "primary_write_failed",
                    ("exception", error.GetType().FullName),
                    ("message", error.Message)));
        }
    }

    private static void EnsureSessionHeader()
    {
        if (_sessionStarted) return;
        File.AppendAllText(Path,
            FormatEntry("INFO", "logger", "log_opened",
                ("pid", Environment.ProcessId),
                ("path", System.IO.Path.GetFileName(Path))),
            new UTF8Encoding(false));
        _sessionStarted = true;
    }

    internal static string FormatEntry(string level, string category,
        string eventName, params (string Key, object? Value)[] fields)
    {
        var builder = new StringBuilder(4096);
        builder.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        builder.Append(" level=").Append(Token(level));
        builder.Append(" pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        builder.Append(" tid=").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        builder.Append(" category=").Append(Token(category));
        builder.Append(" event=").Append(Token(eventName));
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var raw = Convert.ToString(value, CultureInfo.InvariantCulture);
            var rendered = IsVersionField(key, raw)
                ? raw!
                : AppLog.Message(raw);
            builder.Append(' ').Append(Token(key)).Append('=');
            builder.Append(rendered.Length == 0 ? "<empty>" : Quote(rendered));
            if (builder.Length > 16 * 1024)
            {
                builder.Length = 16 * 1024 - " truncated=true".Length;
                builder.Append(" truncated=true");
                break;
            }
        }
        builder.AppendLine();
        return builder.ToString();
    }

    internal static bool TryRotateIfNeeded(string path, long maximumBytes,
        int retainedArchives, ref bool sessionStarted)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < maximumBytes)
                return true;
            sessionStarted = false;
            for (var index = retainedArchives; index >= 1; --index)
            {
                var source = index == 1 ? path : $"{path}.{index - 1}";
                var destination = $"{path}.{index}";
                if (File.Exists(destination)) File.Delete(destination);
                if (File.Exists(source)) File.Move(source, destination);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static LogCleanupResult CleanupDirectory(string directory,
        DateTimeOffset now, bool includeActiveLogs)
    {
        if (!Directory.Exists(directory)) return default;
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            System.IO.Path.GetFileName(Path),
            System.IO.Path.GetFileName(NativeLogPath),
            "startup.log",
        };
        FileInfo[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                               file.Name.Contains(".log.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            return new LogCleanupResult(0, 0, 1);
        }
        var totalBytes = files.Sum(file => SafeLength(file));
        var cutoff = now.UtcDateTime.AddDays(-RetentionDays);
        var deletedFiles = 0;
        long deletedBytes = 0;
        var skippedFiles = 0;
        foreach (var file in files)
        {
            var isActive = activeNames.Contains(file.Name);
            var shouldDelete = (includeActiveLogs || !isActive) &&
                (includeActiveLogs || file.LastWriteTimeUtc < cutoff ||
                 totalBytes > MaximumDirectoryBytes);
            if (!shouldDelete) continue;
            var length = SafeLength(file);
            try
            {
                file.Delete();
                ++deletedFiles;
                deletedBytes += length;
                totalBytes = Math.Max(0, totalBytes - length);
            }
            catch (Exception error) when (error is IOException or
                                          UnauthorizedAccessException)
            {
                ++skippedFiles;
            }
        }
        return new LogCleanupResult(deletedFiles, deletedBytes, skippedFiles);
    }

    private static LogCleanupResult CleanupLegacyNativeLogs(DateTimeOffset now,
        bool includeActiveLogs)
    {
        var deleted = 0;
        long bytes = 0;
        var skipped = 0;
        foreach (var path in new[]
                 {
                     System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                         "iPhoneMirror-capture.log"),
                     System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                         "iPhoneMirror-capture.log.1"),
                     FallbackPath,
                 })
        {
            if (!File.Exists(path)) continue;
            var file = new FileInfo(path);
            if (!includeActiveLogs && file.LastWriteTimeUtc >=
                    now.UtcDateTime.AddDays(-RetentionDays)) continue;
            var length = SafeLength(file);
            try
            {
                file.Delete();
                ++deleted;
                bytes += length;
            }
            catch (Exception error) when (error is IOException or
                                          UnauthorizedAccessException)
            {
                ++skipped;
            }
        }
        return new LogCleanupResult(deleted, bytes, skipped);
    }

    private static LogCleanupResult Add(LogCleanupResult left,
        LogCleanupResult right) => new(
        left.DeletedFiles + right.DeletedFiles,
        left.DeletedBytes + right.DeletedBytes,
        left.SkippedFiles + right.SkippedFiles);

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch { return 0; }
    }

    private static string Token(string? value)
    {
        var safe = AppLog.Message(value).Trim();
        if (safe.Length == 0) return "unknown";
        return new string(safe.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'
                ? character : '_').ToArray());
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ||
        value.Contains('"') || value.Contains('=')
        ? $"\"{value.Replace("\"", "'", StringComparison.Ordinal)}\""
        : value;

    private static bool IsVersionField(string key, string? value)
    {
        if (!key.EndsWith("version", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(value)) return false;
        var core = value.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        return Version.TryParse(core, out _);
    }

    private static string ResolveDirectoryPath()
    {
        var configured = Environment.GetEnvironmentVariable(
            "IPHONE_MIRROR_APP_LOG_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configured) &&
            System.IO.Path.IsPathFullyQualified(configured))
            return System.IO.Path.GetFullPath(configured);
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "iPhoneMirror", "Logs");
    }

    private static void TryWriteFallback(string entry)
    {
        try
        {
            if (File.Exists(FallbackPath) &&
                new FileInfo(FallbackPath).Length >= MaximumLogBytes)
                File.WriteAllText(FallbackPath, string.Empty, new UTF8Encoding(false));
            File.AppendAllText(FallbackPath, entry, new UTF8Encoding(false));
        }
        catch
        {
            // If both LocalAppData and TEMP are unwritable there is no safe
            // local persistence target left; do not hide the original error.
        }
    }
}
