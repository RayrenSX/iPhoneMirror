using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace IPhoneMirror.App.Services;

internal static class StartupDiagnostics
{
    private const long MaximumLogBytes = 1024 * 1024;
    private const int RetainedArchives = 2;
    private static readonly object WriteGate = new();
    private static bool _sessionStarted;
    private static int _initialized;

    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "Logs", "startup.log");

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        DiagnosticLogger.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
                Write("Unhandled application exception", error);
            else
                DiagnosticLogger.Error("runtime", "unhandled_non_exception",
                    ("value_type", args.ExceptionObject?.GetType().FullName),
                    ("terminating", args.IsTerminating));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    internal static string Write(string stage, Exception error)
    {
        DiagnosticLogger.Exception("runtime", "exception",
            error, ("stage", stage));
        try
        {
            lock (WriteGate)
            {
                var directory = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(directory);
                if (!DiagnosticLogger.TryRotateIfNeeded(LogPath, MaximumLogBytes,
                        RetainedArchives, ref _sessionStarted)) return LogPath;

                var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
                var entry = new StringBuilder()
                    .AppendLine("============================================================")
                    .AppendLine($"Timestamp (UTC): {DateTimeOffset.UtcNow:O}")
                    .AppendLine($"Stage: {stage}")
                    .AppendLine($"Version: {assemblyVersion}")
                    .AppendLine($"OS: {RuntimeInformation.OSDescription}")
                    .AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}")
                    .AppendLine($"Base directory: {AppLog.Sanitize(AppContext.BaseDirectory)}")
                    .AppendLine("Native runtime inventory:");
                foreach (var (relative, _) in RequiredNativeFiles())
                {
                    var path = Path.Combine(AppContext.BaseDirectory, relative);
                    if (!File.Exists(path))
                    {
                        entry.AppendLine($"  {relative}: missing");
                        continue;
                    }
                    entry.AppendLine($"  {relative}: present; size={new FileInfo(path).Length}; " +
                        $"version={TryGetFileVersion(path)}");
                }
                entry.AppendLine("Exception:").AppendLine(AppLog.Message(error.ToString()));
                File.AppendAllText(LogPath, entry.ToString(), new UTF8Encoding(false));
                _sessionStarted = true;
            }
        }
        catch
        {
            // A secondary logging failure must not obscure the original crash.
        }
        return LogPath;
    }

    internal static void ValidateRequiredRuntime()
    {
        var inventory = RequiredNativeFiles().ToArray();
        var missing = inventory
            .Where(item => item.Required &&
                !File.Exists(Path.Combine(AppContext.BaseDirectory, item.Relative)))
            .Select(item => item.Relative)
            .ToArray();
        var optionalMissing = inventory
            .Where(item => !item.Required &&
                !File.Exists(Path.Combine(AppContext.BaseDirectory, item.Relative)))
            .Select(item => item.Relative)
            .ToArray();
        DiagnosticLogger.Info("startup", "native_runtime_inventory",
            ("required_missing", string.Join(',', missing)),
            ("optional_missing", string.Join(',', optionalMissing)));
        if (missing.Length != 0)
            throw new FileNotFoundException(
                $"Required application components are missing: {string.Join(", ", missing)}");
    }

    internal static string UserMessage(Exception error, bool simplifiedChinese) =>
        UserMessage(error, simplifiedChinese ? "zh-CN" : "en-US");

    internal static string UserMessage(Exception error, string language)
    {
        var hongKong = language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-Hant-HK", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-MO", StringComparison.OrdinalIgnoreCase);
        var simplifiedChinese = language.Equals("zh-CN",
            StringComparison.OrdinalIgnoreCase);
        var nativeLoadFailure = Find(error, static candidate =>
            candidate is DllNotFoundException or BadImageFormatException or
                FileNotFoundException);
        if (nativeLoadFailure)
        {
            if (hongKong)
                return "無法載入應用程式所需的原生元件。請重新安裝最新的完整安裝程式；詳細診斷資料已寫入下方記錄。";
            return simplifiedChinese
                ? "无法加载程序所需的原生组件。请重新安装最新的完整安装包；详细诊断已写入下方日志。"
                : "A required native component could not be loaded. Reinstall the latest full Setup package; detailed diagnostics were written to the log below.";
        }
        if (hongKong)
            return "iPhoneMirror 啟動時發生錯誤。詳細診斷資料已寫入下方記錄。";
        return simplifiedChinese
            ? "iPhoneMirror 启动时遇到错误。详细诊断已写入下方日志。"
            : "iPhoneMirror encountered an error during startup. Detailed diagnostics were written to the log below.";
    }

    private static bool Find(Exception error, Func<Exception, bool> predicate)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (predicate(current)) return true;
        }
        return false;
    }

    private static string TryGetFileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "none"; }
        catch { return "unavailable"; }
    }

    private static IEnumerable<(string Relative, bool Required)> RequiredNativeFiles()
    {
        yield return ("iPhoneMirror.Core.dll", true);
        yield return ("iPhoneMirror.UsbConfigurationSwitch.exe", true);
        yield return ("libusb-1.0.dll", true);
        yield return ("libusb0.dll", true);
        yield return ("msvcp140.dll", true);
        yield return ("vcruntime140.dll", true);
        yield return ("vcruntime140_1.dll", true);
        yield return ("iPhoneMirror.VirtualCamera.dll", false);
        yield return ("iPhoneMirror.VirtualCamera.Admin.exe", false);
        yield return (Path.Combine("tools", "ffmpeg", "ffmpeg.exe"), false);
        yield return (Path.Combine("Wireless", "iPhoneMirror.WirelessHost.exe"), false);
        yield return (Path.Combine("Wireless", "UxPlay", "iPhoneMirror.UxPlayHost.exe"), false);
        yield return (Path.Combine("Wireless", "UxPlay", "uxplay.exe"), false);
        yield return (Path.Combine("Wireless", "dnssd.dll"), false);
        yield return (Path.Combine("Wireless", "airplay2dll.dll"), false);
        yield return (Path.Combine("Wireless", "avcodec-58.dll"), false);
        yield return (Path.Combine("Wireless", "avutil-56.dll"), false);
        yield return (Path.Combine("Wireless", "swresample-3.dll"), false);
        yield return (Path.Combine("Wireless", "swscale-5.dll"), false);
        yield return (Path.Combine("Wireless", "msvcp140.dll"), false);
        yield return (Path.Combine("Wireless", "vcruntime140.dll"), false);
        yield return (Path.Combine("Wireless", "vcruntime140_1.dll"), false);
    }
}
