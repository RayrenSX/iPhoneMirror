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
    // Call only for public component/update transport URLs after trust checking.
    // Signed redirect queries, fragments and user credentials are never retained.
    private sealed record DownloadLocation(Uri? Uri);
    internal static object DownloadUrl(Uri? uri) => new DownloadLocation(uri);
    internal const long MaximumLogBytes = 8L * 1024 * 1024;
    internal const int RetainedArchives = 4;
    internal const int RetentionDays = 14;
    internal const long MaximumDirectoryBytes = 64L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly HashSet<string> OnceKeys = new(StringComparer.Ordinal);
    private static bool _sessionStarted;
    private static int _initialized;
    private static Timer? _maintenanceTimer;
    private static readonly Dictionary<string, int> MainLogFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application.log"] = RetainedArchives,
        ["reverse-control.log"] = RetainedArchives,
        ["capture.log"] = 1,
        ["startup.log"] = 2,
    };
    private static readonly Dictionary<string, int> TemporaryLogFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iPhoneMirror-capture.log"] = 1,
        ["iPhoneMirror-fallback.log"] = 1,
    };
    private static readonly Dictionary<string, int> DriverLogFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["driver-ui.log"] = 3,
        ["apple-support-install.log"] = 0,
        ["bonjour-install.log"] = 0,
    };

    internal static string DefaultDirectoryPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "Logs");
    internal static string DirectoryPath { get; } = ResolveDirectoryPath();
    internal static string Path { get; } = System.IO.Path.Combine(
        DirectoryPath, "application.log");
    internal static string NativeLogPath { get; } = System.IO.Path.Combine(
        DirectoryPath, "capture.log");
    // Bluetooth, USB, and wireless reverse control share one focused log so a
    // support capture does not need to be reconstructed from application.log.
    internal static string ReverseControlLogPath { get; } = System.IO.Path.Combine(
        DirectoryPath, "reverse-control.log");
    internal static string FallbackPath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "iPhoneMirror-fallback.log");
    private static string NativeFallbackPath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "iPhoneMirror-capture.log");

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        try
        {
            _maintenanceTimer = StartDirectoryMaintenance(DirectoryPath,
                TimeSpan.FromMinutes(1), includeAuxiliaryLogs: true);
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE")))
                Environment.SetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE",
                    NativeFallbackPath, EnvironmentVariableTarget.Process);
            Directory.CreateDirectory(DirectoryPath);
            Environment.SetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE",
                NativeLogPath, EnvironmentVariableTarget.Process);
            var cleanup = Cleanup(includeActiveLogs: false);
            Write("INFO", "lifecycle", "session_start",
                ("version", Assembly.GetEntryAssembly()?
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion ??
                    Assembly.GetExecutingAssembly().GetName().Version?.ToString()),
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

    internal static void ReverseControl(string mode, string eventName,
        params (string Key, object? Value)[] fields) =>
        WriteReverseControl("INFO", mode, eventName, fields);

    internal static void ReverseControlWarning(string mode, string eventName,
        params (string Key, object? Value)[] fields) =>
        WriteReverseControl("WARN", mode, eventName, fields);

    internal static void ReverseControlError(string mode, string eventName,
        params (string Key, object? Value)[] fields) =>
        WriteReverseControl("ERROR", mode, eventName, fields);

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
            if (!string.Equals(DirectoryPath, DefaultDirectoryPath, StringComparison.OrdinalIgnoreCase))
                result = Add(result, CleanupDirectory(DefaultDirectoryPath, now, includeActiveLogs));
            var driverDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "iPhoneMirror.Driver", "Logs");
            result = Add(result, CleanupFiles(driverDirectory, now,
                includeActiveLogs, DriverLogFamilies));
            var driverPackagesDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "iPhoneMirror.Driver", "Packages");
            result = Add(result, CleanupFiles(driverPackagesDirectory, now,
                includeActiveLogs, DriverLogFamilies));
            result = Add(result, CleanupFiles(System.IO.Path.GetTempPath(), now,
                includeActiveLogs, TemporaryLogFamilies));
            if (includeActiveLogs) _sessionStarted = false;
            return result;
        }
    }

    internal static void Shutdown(int exitCode)
    {
        Interlocked.Exchange(ref _maintenanceTimer, null)?.Dispose();
        Info("lifecycle", "session_end", ("exit_code", exitCode));
    }

    // Native logging can remain active while the managed UI is idle. Keep the
    // directory budget and age policy running independently of managed writes.
    internal static Timer StartDirectoryMaintenance(string directory,
        TimeSpan interval, bool includeAuxiliaryLogs = false)
    {
        var running = 0;
        return new Timer(_ =>
        {
            if (Interlocked.Exchange(ref running, 1) != 0) return;
            try
            {
                if (includeAuxiliaryLogs) Cleanup();
                else
                    lock (Gate)
                        CleanupDirectory(directory, DateTimeOffset.UtcNow,
                            includeActiveLogs: false);
            }
            catch
            {
                // Retry at the next interval without creating more logs.
            }
            finally
            {
                Volatile.Write(ref running, 0);
            }
        }, null, interval, interval);
    }

    private static void Write(string level, string category, string eventName,
        params (string Key, object? Value)[] fields)
    {
        string? entry = null;
        try
        {
            entry = FormatEntry(level, category, eventName, fields);
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                AppendLogLocked(Path, entry, MaximumLogBytes, RetainedArchives,
                    "logger", startSession: !_sessionStarted, maintainDirectory: true);
                _sessionStarted = true;
            }
        }
        catch (Exception error)
        {
            // Never throw from an error-reporting path.
            TryWriteFallback(entry +
                FormatEntry("ERROR", "logging", "primary_write_failed",
                    ("exception", error.GetType().FullName),
                    ("message", error.Message)));
        }
    }

    private static void WriteReverseControl(string level, string mode,
        string eventName, params (string Key, object? Value)[] fields)
    {
        string? entry = null;
        try
        {
            var all = new (string Key, object? Value)[fields.Length + 1];
            all[0] = ("mode", mode);
            fields.CopyTo(all, 1);
            entry = FormatEntry(level, "reverse_control", eventName, all);
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                AppendLogLocked(ReverseControlLogPath, entry, MaximumLogBytes, RetainedArchives,
                    "reverse_control", startSession: false, maintainDirectory: true);
            }
        }
        catch (Exception error)
        {
            TryWriteFallback(entry + FormatEntry("ERROR", "logging", "reverse_control_write_failed",
                ("exception", error.GetType().FullName), ("message", error.Message)));
        }
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
            var rendered = value is DownloadLocation location
                ? location.Uri?.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped) ?? "unknown"
                : IsVersionField(key, raw)
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

    internal static void AppendLog(string path, string entry, long maximumBytes,
        int retainedArchives, string? headerCategory = null, bool startSession = false,
        bool maintainDirectory = false)
    {
        lock (Gate)
            AppendLogLocked(path, entry, maximumBytes, retainedArchives, headerCategory,
                startSession, maintainDirectory);
    }

    private static void AppendLogLocked(string path, string entry, long maximumBytes,
        int retainedArchives, string? headerCategory, bool startSession, bool maintainDirectory)
    {
        string CreateHeader() => headerCategory is null ? string.Empty :
            FormatEntry("INFO", headerCategory, "log_opened",
                ("pid", Environment.ProcessId),
                ("path", System.IO.Path.GetFileName(path)));

        var file = new FileInfo(path);
        var now = DateTimeOffset.Now;
        var length = file.Exists ? file.Length : 0;
        var header = length == 0 || startSession ? CreateHeader() : string.Empty;
        var entryBytes = Encoding.UTF8.GetByteCount(entry);
        var rotate = length > 0 &&
            (file.LastWriteTime.Date != now.Date ||
             entryBytes + Encoding.UTF8.GetByteCount(header) > maximumBytes - length);
        if (rotate && header.Length == 0) header = CreateHeader();
        if (entryBytes + Encoding.UTF8.GetByteCount(header) > maximumBytes)
            throw new IOException("Diagnostic entry exceeds the log file size limit.");
        if (rotate)
        {
            EnsureArchiveCapacity(file, retainedArchives + 1);
            File.Move(path, FormattableString.Invariant(
                $"{path}.{file.LastWriteTime:yyyyMMdd'T'HHmmssfff}-{Guid.NewGuid():N}"));
        }
        File.AppendAllText(path, header + entry);
        if (!rotate) return;

        var directory = file.DirectoryName!;
        if (maintainDirectory) CleanupDirectory(directory, now, includeActiveLogs: false);
        else CleanupFiles(directory, now, includeActiveLogs: false,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [file.Name] = retainedArchives });
    }

    private static void EnsureArchiveCapacity(FileInfo active, int limit)
    {
        var prefix = active.Name + ".";
        var archives = active.Directory!.EnumerateFiles()
            .Where(file => file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                           IsArchiveSuffix(file.Name.AsSpan(prefix.Length)))
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var count = archives.Length;
        foreach (var archive in archives)
        {
            if (count < limit) return;
            try
            {
                archive.Delete();
                --count;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // One locked archive can temporarily use the extra slot.
            }
        }
        if (count >= limit) throw new IOException("All available diagnostic archive slots are occupied.");
    }

    internal static LogCleanupResult CleanupDirectory(string directory,
        DateTimeOffset now, bool includeActiveLogs) =>
        CleanupFiles(directory, now, includeActiveLogs, MainLogFamilies, includeInstallerLogs: true);

    private static LogCleanupResult CleanupFiles(string directory, DateTimeOffset now,
        bool includeActiveLogs, Dictionary<string, int> families, bool includeInstallerLogs = false)
    {
        if (!Directory.Exists(directory)) return default;
        var cutoff = now.UtcDateTime.AddDays(-RetentionDays);
        var files = new List<(FileInfo File, string? Family, bool Active)>();
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            {
                var family = LogFamily(file.Name, families);
                if (family is not null)
                    files.Add((file, family, families.ContainsKey(file.Name)));
                else if (includeInstallerLogs && IsInstallerLog(file.Name))
                    files.Add((file, null, false));
            }
            // With equal timestamps, prefer timestamped archives and then the
            // smaller legacy index (the most recent in the old file scheme).
            files.Sort((left, right) =>
            {
                var order = right.File.LastWriteTimeUtc.CompareTo(left.File.LastWriteTimeUtc);
                if (order != 0) return order;
                var leftLegacy = int.TryParse(left.File.Extension.AsSpan(1), out var leftIndex);
                var rightLegacy = int.TryParse(right.File.Extension.AsSpan(1), out var rightIndex);
                if (leftLegacy && rightLegacy) return leftIndex.CompareTo(rightIndex);
                if (leftLegacy != rightLegacy) return leftLegacy ? 1 : -1;
                return StringComparer.OrdinalIgnoreCase.Compare(right.File.Name, left.File.Name);
            });
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            return new LogCleanupResult(0, 0, 1);
        }
        var totalBytes = files.Sum(entry => SafeLength(entry.File));
        var excessArchives = files.Where(entry => !entry.Active && entry.Family is not null)
            .GroupBy(entry => entry.Family!, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Skip(families[group.Key]))
            .Select(entry => entry.File.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedFiles = 0;
        long deletedBytes = 0;
        var skippedFiles = 0;
        for (var index = files.Count - 1; index >= 0; --index)
        {
            var (file, _, active) = files[index];
            var shouldDelete = includeActiveLogs || file.LastWriteTimeUtc < cutoff ||
                (!active && (totalBytes > MaximumDirectoryBytes || excessArchives.Contains(file.Name)));
            if (!shouldDelete) continue;
            try
            {
                if (active && !includeActiveLogs)
                {
                    file.Refresh();
                    if (!file.Exists || file.LastWriteTimeUtc >= cutoff) continue;
                }
                var length = SafeLength(file);
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

    private static string? LogFamily(string name, Dictionary<string, int> families)
    {
        if (families.ContainsKey(name)) return name;
        var extension = name.IndexOf(".log.", StringComparison.OrdinalIgnoreCase);
        if (extension < 0) return null;
        var family = name[..(extension + 4)];
        if (!families.ContainsKey(family)) return null;
        return IsArchiveSuffix(name.AsSpan(extension + 5)) ? family : null;
    }

    private static bool IsArchiveSuffix(ReadOnlySpan<char> suffix)
    {
        var legacy = int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture,
            out var index) && index > 0;
        var dated = suffix.Length == 51 && suffix[18] == '-' &&
            DateTime.TryParseExact(suffix[..18], "yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _) &&
            Guid.TryParseExact(suffix[19..], "N", out _);
        return legacy || dated;
    }

    private static bool IsInstallerLog(string name) =>
        name.StartsWith("installer-update-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
        DateTime.TryParseExact(name.AsSpan(17, name.Length - 21), "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

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
        {
            try { return System.IO.Path.GetFullPath(configured); }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            {
                // Ignore malformed environment overrides so diagnostics can still initialize.
            }
        }
        return DefaultDirectoryPath;
    }

    private static void TryWriteFallback(string entry)
    {
        try
        {
            AppendLog(FallbackPath, entry, MaximumLogBytes, retainedArchives: 1);
        }
        catch
        {
            // If both LocalAppData and TEMP are unwritable there is no safe
            // local persistence target left; do not hide the original error.
        }
    }
}
