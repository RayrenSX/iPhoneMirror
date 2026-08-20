using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IPhoneMirror.Shared.Security;

internal sealed class ElevationPathLock : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInfoClass = 9;

    private readonly List<FileStream> _files;
    private readonly List<SafeFileHandle> _directories;
    private bool _disposed;

    private ElevationPathLock(List<FileStream> files,
        List<SafeFileHandle> directories)
    {
        _files = files;
        _directories = directories;
    }

    internal static ElevationPathLock Acquire(params string[] filePaths)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Elevation path locks are only supported on Windows.");
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Length == 0)
            throw new ArgumentException("At least one file path is required.",
                nameof(filePaths));

        var files = filePaths.Select(path =>
            Path.GetFullPath(RequirePath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var root = Path.GetPathRoot(file);
            for (var directory = Path.GetDirectoryName(file);
                 !string.IsNullOrEmpty(directory) &&
                 !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase);
                 directory = Path.GetDirectoryName(directory))
                directoryPaths.Add(directory);
        }

        var directoryHandles = new List<SafeFileHandle>();
        var fileStreams = new List<FileStream>();
        try
        {
            foreach (var directory in directoryPaths.OrderBy(path => path.Length))
            {
                var handle = OpenPath(directory, FileReadAttributes,
                    FileShare.Read | FileShare.Write,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint);
                uint attributes;
                try { attributes = GetAttributes(handle, directory); }
                catch
                {
                    handle.Dispose();
                    throw;
                }
                if ((attributes & FileAttributeDirectory) == 0 ||
                    (attributes & FileAttributeReparsePoint) != 0)
                {
                    handle.Dispose();
                    throw new IOException(
                        $"An elevation path directory is invalid or a reparse point: {directory}");
                }
                directoryHandles.Add(handle);
            }

            foreach (var file in files)
            {
                var handle = OpenPath(file, GenericRead, FileShare.Read,
                    FileFlagOpenReparsePoint);
                uint attributes;
                try { attributes = GetAttributes(handle, file); }
                catch
                {
                    handle.Dispose();
                    throw;
                }
                if ((attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                {
                    handle.Dispose();
                    throw new IOException(
                        $"An elevation payload is invalid or a reparse point: {file}");
                }
                try
                {
                    fileStreams.Add(new FileStream(handle, FileAccess.Read));
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            return new ElevationPathLock(fileStreams, directoryHandles);
        }
        catch
        {
            for (var index = fileStreams.Count - 1; index >= 0; --index)
                fileStreams[index].Dispose();
            for (var index = directoryHandles.Count - 1; index >= 0; --index)
                directoryHandles[index].Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var index = _files.Count - 1; index >= 0; --index)
            _files[index].Dispose();
        for (var index = _directories.Count - 1; index >= 0; --index)
            _directories[index].Dispose();
    }

    private static SafeFileHandle OpenPath(string path, uint desiredAccess,
        FileShare shareMode, uint flags)
    {
        var handle = CreateFileW(path, desiredAccess, shareMode, 0,
            OpenExisting, flags, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error,
            $"Windows could not lock an elevation path: {path}");
    }

    private static string RequirePath(string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path;
    }

    private static uint GetAttributes(SafeFileHandle handle, string path)
    {
        if (GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass,
                out var information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            return information.FileAttributes;
        throw new Win32Exception(Marshal.GetLastWin32Error(),
            $"Windows could not inspect an elevation path: {path}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName,
        uint desiredAccess, FileShare shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle, int fileInformationClass,
        out FileAttributeTagInformation fileInformation, uint bufferSize);
}
