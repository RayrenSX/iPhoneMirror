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
                DiagnosticLogger.AppendLog(LogPath, entry.ToString(),
                    MaximumLogBytes, RetainedArchives);
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
        var taiwan = language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-Hant-TW", StringComparison.OrdinalIgnoreCase);
        var hongKong = language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-Hant-HK", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-MO", StringComparison.OrdinalIgnoreCase);
        var simplifiedChinese = language.Equals("zh-CN",
            StringComparison.OrdinalIgnoreCase);
        var japanese = language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var nativeLoadFailure = Find(error, static candidate =>
            candidate is DllNotFoundException or BadImageFormatException or
                FileNotFoundException);
        if (nativeLoadFailure)
        {
            if (taiwan)
                return "無法載入必要原生元件。請使用最新的完整安裝套件重新安裝。";
            if (hongKong)
                return "無法載入必要原生元件。請使用最新的完整安裝程式重新安裝。";
            if (japanese)
                return "必要なコンポーネントを読み込めませんでした。最新の完全版を再インストールしてください。";
            return simplifiedChinese
                ? "无法加载原生组件。请用最新的完整安装包重新安装。"
                : "A native component could not load. Reinstall using the latest full installer.";
        }
        if (taiwan)
            return "請重新開啟 iPhoneMirror。若問題持續，請查看診斷記錄。";
        if (hongKong)
            return "請重新開啟 iPhoneMirror。若問題持續，請查看診斷記錄。";
        if (japanese)
            return "iPhoneMirrorを開き直してください。問題が続く場合は診断ログを確認してください。";
        return simplifiedChinese
            ? "请重新打开 iPhoneMirror。如果仍无法启动，请查看诊断日志。"
            : "Reopen iPhoneMirror. If it still won’t start, check the diagnostic log.";
    }

    internal static string Label(string key, string language)
    {
        if (Localization.LocalizationService.ResolveCultureName(language) == "zh-TW")
            return key switch
            {
                "StartupErrorHeading" => "iPhoneMirror 無法啟動",
                "StartupErrorLogLabel" => "診斷記錄檔",
                "StartupErrorDetails" => "錯誤詳細資料",
                "StartupErrorOpenLog" => "開啟記錄檔位置",
                "StartupErrorClose" => "關閉",
                _ => throw new ArgumentOutOfRangeException(nameof(key)),
            };
        if (Localization.LocalizationService.ResolveCultureName(language) == "ja-JP")
            return key switch
            {
                "StartupErrorHeading" => "iPhoneMirrorを起動できませんでした",
                "StartupErrorLogLabel" => "診断ログ",
                "StartupErrorDetails" => "エラーの詳細",
                "StartupErrorOpenLog" => "ログの場所を開く",
                "StartupErrorClose" => "閉じる",
                _ => throw new ArgumentOutOfRangeException(nameof(key)),
            };
        var labels = key switch
        {
            "StartupErrorHeading" => ("iPhoneMirror 无法启动", "iPhoneMirror 無法啟動", "iPhoneMirror could not start"),
            "StartupErrorLogLabel" => ("诊断日志", "診斷記錄", "Diagnostic log"),
            "StartupErrorDetails" => ("错误详情", "錯誤詳細資料", "Error details"),
            "StartupErrorOpenLog" => ("打开日志位置", "開啟記錄位置", "Open log location"),
            "StartupErrorClose" => ("关闭", "關閉", "Close"),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        return Localization.LocalizationService.ResolveCultureName(language) switch
        {
            "zh-CN" => labels.Item1,
            "zh-HK" => labels.Item2,
            _ => labels.Item3,
        };
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
