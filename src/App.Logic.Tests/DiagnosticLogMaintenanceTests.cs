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
            TestManagedDailyAndUtf8SizeRotation(root);
            await TestLockedManagedArchiveAsync(Path.Combine(root, "locked-managed"));
            Console.WriteLine("PASS diagnostic log maintenance: daily and UTF-8 size rotation, continuous cleanup, retention, directory budget and active/locked file protection.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestManagedDailyAndUtf8SizeRotation(string root)
    {
        var path = Path.Combine(root, "daily.log");
        DiagnosticLogger.AppendLog(path, "previous day\n", 32, 2);
        var previousDay = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(path, previousDay);
        previousDay = File.GetLastWriteTimeUtc(path);

        // 10 Chinese characters and a newline use 31 bytes in UTF-8.
        var entry = new string('日', 10) + "\n";
        DiagnosticLogger.AppendLog(path, entry, 32, 2);
        Assert(File.ReadAllText(path) == entry && new FileInfo(path).Length == 31,
            "Writing on a new day must open a new file without a UTF-8 BOM.");
        var dailyArchive = Archives(root, "daily.log").Single();
        Assert(File.ReadAllText(dailyArchive) == "previous day\n" &&
               File.GetLastWriteTimeUtc(dailyArchive) == previousDay,
            "Daily rotation must preserve the archived data and its age.");

        DiagnosticLogger.AppendLog(path, "a", 32, 2);
        Assert(new FileInfo(path).Length == 32,
            "An entry that fits exactly must stay in the current file.");
        DiagnosticLogger.AppendLog(path, "b\n", 32, 2);
        var nextArchive = Archives(root, "daily.log").Single(file => file != dailyArchive);
        Assert(File.ReadAllText(path) == "b\n" && File.ReadAllText(nextArchive) == entry + "a" &&
               File.ReadAllText(dailyArchive) == "previous day\n",
            "Same-day size rotation must retain each previous segment in order.");
    }

    private static void TestRetentionAndDirectoryLimit(string directory)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var activeNames = new[] { "application.log", "capture.log", "reverse-control.log", "startup.log" };
        foreach (var name in activeNames)
            CreateFile(directory, name, MiB, now);
        var expired = CreateFile(directory, "startup.log.1", MiB, now.AddDays(-15));
        var oldest = CreateFile(directory, "capture.log.1", 24 * MiB, now.AddDays(-4));
        var newer = CreateFile(directory, "application.log.1", 24 * MiB, now.AddDays(-3));
        var newest = CreateFile(directory, "reverse-control.log.1", 24 * MiB, now.AddDays(-2));
        var unrelated = CreateFile(directory, "keep.data", 30 * MiB, now.AddDays(-30));
        var unrelatedLog = CreateFile(directory, "other-program.log", 30 * MiB, now.AddDays(-30));
        var unrelatedArchive = CreateFile(directory, "application.log.backup", 30 * MiB, now.AddDays(-30));

        var result = DiagnosticLogger.CleanupDirectory(directory, now, includeActiveLogs: false);
        Assert(result.DeletedFiles == 2 && result.DeletedBytes == 25 * MiB,
            "Cleanup must remove the expired archive and then the oldest archive needed for the budget.");
        Assert(!File.Exists(expired) && !File.Exists(oldest) && File.Exists(newer) && File.Exists(newest),
            "Directory cleanup must preserve recent archives once the total fits.");
        Assert(activeNames.All(name => File.Exists(Path.Combine(directory, name))),
            "Automatic budget cleanup must preserve recent active logs.");
        Assert(File.Exists(unrelated) && File.Exists(unrelatedLog) && File.Exists(unrelatedArchive),
            "Unrelated files in a custom log directory must not count toward the log budget.");
        var inactive = Path.Combine(directory, "reverse-control.log");
        File.SetLastWriteTimeUtc(inactive, now.AddDays(-30).UtcDateTime);
        DiagnosticLogger.CleanupDirectory(directory, now, includeActiveLogs: false);
        Assert(!File.Exists(inactive), "An unused active log must expire.");
        DiagnosticLogger.CleanupDirectory(directory, now, includeActiveLogs: true);
        Assert(File.Exists(unrelatedLog) && File.Exists(unrelatedArchive),
            "Manual cleanup must preserve unrelated files in a custom log directory.");
    }

    private static async Task TestLockedManagedArchiveAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var path = CreateFile(directory, "application.log", 128, now);
        var oldest = CreateFile(directory, "application.log.4", 16, now.AddDays(-4));
        for (var index = 1; index <= 3; ++index)
            CreateFile(directory, $"application.log.{index}", 16, now.AddDays(-index));
        using (var handle = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            DiagnosticLogger.AppendLog(path, "current log\n", 128, 4, maintainDirectory: true);
            Assert(File.ReadAllText(path) == "current log\n" &&
                   Archives(directory, "application.log").Count() == 5,
                "A locked managed archive must not prevent rotation or discard younger archives.");
            File.WriteAllText(path, new string('x', 128));
            DiagnosticLogger.AppendLog(path, "another current log\n", 128, 4, maintainDirectory: true);
            Assert(File.ReadAllText(path) == "another current log\n" &&
                   Archives(directory, "application.log").Count() == 5,
                "Further rotations must stay bounded while the oldest archive is locked.");

            var remainingLocks = Archives(directory, "application.log").Where(file => file != oldest)
                .Select(file => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                .ToArray();
            try
            {
                var fullLog = new string('x', 128);
                File.WriteAllText(path, fullLog);
                try
                {
                    DiagnosticLogger.AppendLog(path, "extra log\n", 128, 4, maintainDirectory: true);
                    throw new InvalidOperationException("Rotation should fail when every archive slot is locked.");
                }
                catch (IOException)
                {
                    Assert(File.ReadAllText(path) == fullLog &&
                           Archives(directory, "application.log").Count() == 5,
                        "Full locked archive slots must preserve the active log and the file limit.");
                }
            }
            finally
            {
                foreach (var archiveLock in remainingLocks) archiveLock.Dispose();
            }
        }
        await using var maintenance = DiagnosticLogger.StartDirectoryMaintenance(directory, TimeSpan.FromMilliseconds(25));
        await EventuallyAsync(() => !File.Exists(oldest),
            "The background sweep must retry excess archives after their locks release.");
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

    private static IEnumerable<string> Archives(string directory, string activeName) =>
        Directory.EnumerateFiles(directory, activeName + ".*", new EnumerationOptions());

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
