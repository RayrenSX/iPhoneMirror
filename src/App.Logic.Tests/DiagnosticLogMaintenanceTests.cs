using System.Diagnostics;
using System.IO;
using IPhoneMirror.App.Services;

internal static class DiagnosticLogMaintenanceTests
{
    private const long MiB = 1024 * 1024;

    internal static async Task RunAsync()
    {
        // Program assigns a unique process log directory before this type is used.
        // Do not call Initialize/Cleanup: those also inspect real driver logs.
        var root = DiagnosticLogger.DirectoryPath;
        Directory.CreateDirectory(root);
        try
        {
            TestRetentionAndDirectoryLimit(Path.Combine(root, "limits"));
            await TestContinuousMaintenanceAsync(Path.Combine(root, "continuous"));
            TestManagedRotationMaintainsDirectory(reverseControl: false);
            TestManagedRotationMaintainsDirectory(reverseControl: true);
            Console.WriteLine("PASS diagnostic log maintenance: continuous cleanup, retention, directory budget, active/locked file protection, recovery and immediate managed rotation cleanup.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestRetentionAndDirectoryLimit(string directory)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var activeNames = new[] { "application.log", "capture.log", "reverse-control.log", "startup.log" };
        foreach (var name in activeNames)
            CreateFile(directory, name, MiB, now.AddDays(-30));
        var expired = CreateFile(directory, "expired.log.1", MiB, now.AddDays(-15));
        var oldest = CreateFile(directory, "capture.log.1", 24 * MiB, now.AddDays(-4));
        var newer = CreateFile(directory, "application.log.1", 24 * MiB, now.AddDays(-3));
        var newest = CreateFile(directory, "reverse-control.log.1", 24 * MiB, now.AddDays(-2));
        var unrelated = CreateFile(directory, "keep.data", 30 * MiB, now.AddDays(-30));

        var result = DiagnosticLogger.CleanupDirectory(directory, now, includeActiveLogs: false);
        Assert(result.DeletedFiles == 2 && result.DeletedBytes == 25 * MiB,
            "Cleanup must remove the expired archive and then the oldest archive needed for the budget.");
        Assert(!File.Exists(expired) && !File.Exists(oldest) && File.Exists(newer) && File.Exists(newest),
            "Directory cleanup must preserve recent archives once the total fits.");
        Assert(activeNames.All(name => File.Exists(Path.Combine(directory, name))),
            "Automatic cleanup must preserve active log names even if their timestamps have expired.");
        Assert(File.Exists(unrelated) && LogBytes(directory) <= DiagnosticLogger.MaximumDirectoryBytes,
            "Non-log files must neither be deleted nor count toward the log budget.");
    }

    private static async Task TestContinuousMaintenanceAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await using var maintenance = DiagnosticLogger.StartDirectoryMaintenance(directory, TimeSpan.FromMilliseconds(25));
        var locked = CreateFile(directory, "capture.log.1", MiB, DateTimeOffset.UtcNow);
        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddDays(-30));
            var removable = CreateFile(directory, "application.log.1", MiB, DateTimeOffset.UtcNow.AddDays(-30));
            await EventuallyAsync(() => !File.Exists(removable),
                "Maintenance must run during the session and continue past a locked archive.");
            Assert(File.Exists(locked), "Maintenance must skip a file that Windows does not allow it to delete.");
        }
        await EventuallyAsync(() => !File.Exists(locked),
            "A later maintenance tick must clean an archive after its lock is released.");

        var now = DateTimeOffset.UtcNow;
        var oldest = CreateFile(directory, "capture.log.1", 24 * MiB, now.AddDays(-3));
        CreateFile(directory, "application.log.1", 24 * MiB, now.AddDays(-2));
        CreateFile(directory, "reverse-control.log.1", 24 * MiB, now.AddDays(-1));
        await EventuallyAsync(() => !File.Exists(oldest) && LogBytes(directory) <= DiagnosticLogger.MaximumDirectoryBytes,
            "A running session must enforce the directory budget without a restart or managed log write.");
    }

    private static void TestManagedRotationMaintainsDirectory(bool reverseControl)
    {
        var directory = DiagnosticLogger.DirectoryPath;
        foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path);
        var now = DateTimeOffset.UtcNow;
        var oldest = CreateFile(directory, "capture.log.1", 16 * MiB, now.AddDays(-5));
        CreateFile(directory, "capture.log", 16 * MiB, now);
        CreateFile(directory, "application.log", 8 * MiB, now);
        CreateFile(directory, "reverse-control.log", 8 * MiB, now);
        var managedName = reverseControl ? "reverse-control.log" : "application.log";
        for (var index = 1; index <= 3; ++index)
            CreateFile(directory, $"{managedName}.{index}", 8 * MiB, now.AddDays(-index));

        if (reverseControl) DiagnosticLogger.ReverseControl("test", "rotation_budget_probe");
        else DiagnosticLogger.Info("test", "rotation_budget_probe");

        Assert(!File.Exists(oldest) && LogBytes(directory) <= DiagnosticLogger.MaximumDirectoryBytes,
            $"Rotating {managedName} must immediately trim the total directory budget.");
        Assert(File.ReadAllText(Path.Combine(directory, managedName)).Contains("rotation_budget_probe", StringComparison.Ordinal),
            $"Rotating {managedName} must retain the event that triggered rotation.");
    }

    private static string CreateFile(string directory, string name, long length, DateTimeOffset modified)
    {
        var path = Path.Combine(directory, name);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            stream.SetLength(length);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private static long LogBytes(string directory) => Directory.EnumerateFiles(directory)
        .Where(path => Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                       Path.GetFileName(path).Contains(".log.", StringComparison.OrdinalIgnoreCase))
        .Sum(path => new FileInfo(path).Length);

    private static async Task EventuallyAsync(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
