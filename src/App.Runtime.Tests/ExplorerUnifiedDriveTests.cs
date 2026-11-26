using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using IPhoneMirror.App.Services.ExplorerMapping;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunExplorerUnifiedDriveTests()
    {
        RunAsync().GetAwaiter().GetResult();
        return 0;

        static async Task RunAsync()
        {
            var files = new Dictionary<string, byte[]>
            {
                ["media/open.txt"] = "media"u8.ToArray(), ["media/locked.txt"] = "locked"u8.ToArray(),
                ["media/date%3Aname%7C.txt"] = "escaped filename"u8.ToArray(),
                ["documents/app.txt"] = "application"u8.ToArray(), ["crashes/report.ips"] = "report"u8.ToArray(),
            };
            var directories = new HashSet<string> { "media/", "documents/", "crashes/" };
            var writes = 0;
            const long phoneTotal = 256_000_000_000;
            const long phoneAvailable = 175_450_000_000;
            Task<JsonElement> Request(string action, object? arguments, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                if (action == "device_info") return Task.FromResult(JsonSerializer.SerializeToElement(new
                { disk = new { available = true, values = new { TotalDiskCapacity = phoneTotal, TotalDataCapacity = 246_200_000_000L, TotalDataAvailable = phoneAvailable } } }));
                if (action == "explorer_roots") return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    roots = new object[] { new { root = "media" }, new { root = "crashes" },
                        new { root = "documents", bundleId = "com.test.app", name = "App" } },
                    total = 1, nextOffset = (int?)null,
                }));
                var args = JsonSerializer.SerializeToElement(arguments);
                var path = args.GetProperty("path").GetString()!;
                var root = args.GetProperty("root").GetString()!;
                var key = root + "/" + path;
                var directory = directories.Contains(key);
                JsonElement Metadata(string relative, bool folder, int length) => JsonSerializer.SerializeToElement(new
                { path = relative, name = relative[(relative.LastIndexOf('/') + 1)..], type = folder ? "directory" : "file", size = length, modified = 1,
                    readOnly = relative == "locked.txt" || root == "crashes" });
                if (action == "stat_file") return Task.FromResult(directory ? Metadata(path, true, 0) :
                    files.TryGetValue(key, out var data) ? Metadata(path, false, data.Length) : JsonSerializer.SerializeToElement<object?>(null));
                if (action == "list_dir")
                {
                    var entries = files.Where(file => file.Key.StartsWith(key, StringComparison.Ordinal) && file.Key != key &&
                        !file.Key[key.Length..].Contains('/')).Select(file => Metadata(file.Key[(root.Length + 1)..], false, file.Value.Length)).ToArray();
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { path, entries, total = entries.Length, nextOffset = (int?)null }));
                }
                if (action == "read_file")
                {
                    var data = files[key];
                    var offset = args.GetProperty("offset").GetInt32();
                    var length = Math.Min(args.GetProperty("length").GetInt32(), data.Length - offset);
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    { path, offset, size = data.Length, modified = 1, data = Convert.ToBase64String(data, offset, length), eof = offset + length == data.Length }));
                }
                Interlocked.Increment(ref writes);
                if (action == "make_directory") directories.Add(key);
                else if (action == "write_mapped_file") files[key] = File.ReadAllBytes(args.GetProperty("sourcePath").GetString()!);
                else throw new InvalidOperationException("Unexpected fixture mutation " + action);
                return Task.FromResult(Metadata(path, action == "make_directory", files.GetValueOrDefault(key)?.Length ?? 0));
            }

            const string deviceName = "Ray 的 iPhone";
            var store = await DeviceHubExplorerRootStore.CreateAsync(Request, deviceName, "媒体文件", "崩溃日志", "Temporary", "System logs", default);
            await using var server = ExplorerWebDavServer.Start(store);
            await using var drive = await WindowsExplorerDrive.ConnectAsync(server.BaseUri, displayName: deviceName);
            var displayName = new ExplorerShellFileInfo();
            if (ExplorerSHGetFileInfo(drive.RootPath, 0, ref displayName, (uint)Marshal.SizeOf<ExplorerShellFileInfo>(), 0x200) == 0 ||
                !displayName.DisplayName.Contains(deviceName, StringComparison.Ordinal) || writes != 0)
                throw new InvalidOperationException("Explorer did not use the device name as its drive label, or naming wrote to the phone: " + displayName.DisplayName);
            Console.WriteLine("PASS real Explorer device-name label: " + displayName.DisplayName);
            // WebClient ignores quota properties even when the server reports
            // valid phone bytes. Keep the actual native-API measurement visible;
            // the optional native projection must be verified separately.
            if (!ExplorerGetDiskFreeSpaceEx(drive.RootPath, out var available, out var total, out var free))
                throw new InvalidOperationException("Windows could not query the mapped drive.");
            Console.WriteLine($"OBSERVED WebClient capacity: total={total}, available={available}, free={free}; phone total={phoneTotal}, available={phoneAvailable}.");
            var media = Path.Combine(drive.RootPath, "媒体文件");
            if (File.ReadAllText(Path.Combine(media, "open.txt")) != "media" ||
                File.ReadAllText(Path.Combine(media, "date%3Aname%7C.txt")) != "escaped filename" ||
                File.ReadAllText(Path.Combine(drive.RootPath, "App", "app.txt")) != "application" ||
                File.ReadAllText(Path.Combine(drive.RootPath, "崩溃日志", "report.ips")) != "report")
                throw new InvalidOperationException("Windows read the wrong mapped service.");
            if ((File.GetAttributes(Path.Combine(media, "locked.txt")) & FileAttributes.ReadOnly) == 0)
                throw new InvalidOperationException("Windows lost the phone file's read-only attribute.");
            var previous = writes;
            try { File.WriteAllText(Path.Combine(media, "locked.txt"), "rejected"); throw new InvalidOperationException("Windows overwrote a read-only phone file."); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException) { }
            try { File.WriteAllText(Path.Combine(drive.RootPath, "崩溃日志", "new.txt"), "rejected"); throw new InvalidOperationException("Windows wrote a crash report."); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException) { }
            if (writes != previous) throw new InvalidOperationException("A denied write reached the fixture backend.");
            Directory.CreateDirectory(Path.Combine(media, "New"));
            File.WriteAllText(Path.Combine(media, "New", "created.txt"), "created");
            if (File.ReadAllText(Path.Combine(media, "New", "created.txt")) != "created")
                throw new InvalidOperationException("Windows could not write under the media folder.");
            var mountIdentity = drive.UncPath.Replace("\\DavWWWRoot\\", "\\", StringComparison.OrdinalIgnoreCase).Replace('\\', '#');
            await drive.DisposeAsync();
            using var mount = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\" + mountIdentity);
            if (mount?.GetValue("_LabelFromReg") is not null) throw new InvalidOperationException("An owned temporary drive label survived cleanup.");
            Console.WriteLine("PASS real Windows unified mapping: media/app/crash reads, phone read-only attributes, rejected writes, writable subfolders and temporary-drive cleanup.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ExplorerShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern nint ExplorerSHGetFileInfo(string path, uint attributes, ref ExplorerShellFileInfo info, uint size, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExplorerGetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}
