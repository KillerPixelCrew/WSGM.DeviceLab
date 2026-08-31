using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WSGM.DeviceLab.Packaging;

/// <summary>
/// Pins one package tree and every accepted file so validation and packing consume identical bytes.
/// </summary>
internal sealed class DeviceLabPackageSnapshot : IDisposable
{
    private readonly NoFollowPackageSource _source;
    private readonly Dictionary<string, DeviceLabPackageFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _canonicalPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICollection<PluginPackageValidationIssue> _issues;
    private bool _disposed;

    private DeviceLabPackageSnapshot(
        NoFollowPackageSource source,
        ICollection<PluginPackageValidationIssue> issues)
    {
        _source = source;
        _issues = issues;
    }

    /// <summary>Accepted regular package files keyed by canonical relative path.</summary>
    internal IReadOnlyList<DeviceLabPackageFile> Files =>
        [.. _files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal)];

    /// <summary>Structural issues observed while pinning the package tree.</summary>
    internal IReadOnlyList<PluginPackageValidationIssue> Issues => [.. _issues];

    /// <summary>Captures a bounded no-follow view of an existing package directory.</summary>
    internal static DeviceLabPackageSnapshot Capture(
        string root,
        ICollection<PluginPackageValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);
        NoFollowPackageSource source = NoFollowPackageSource.Open(root);
        DeviceLabPackageSnapshot snapshot = new(source, issues);
        try
        {
            Stack<string> pending = new();
            pending.Push(source.RootPath);
            int entryCount = 0;
            int fileCount = 0;
            long totalBytes = 0;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                IReadOnlyList<string> entries = TakeBoundedEntries(
                    Directory.EnumerateFileSystemEntries(directory),
                    PluginPackageWorkflow.MaximumPackageEntries - entryCount,
                    cancellationToken,
                    out bool exceeded);
                if (exceeded)
                {
                    string relative = Path.GetRelativePath(source.RootPath, directory).Replace('\\', '/');
                    issues.Add(new PluginPackageValidationIssue(
                        "package-too-many-entries",
                        relative is "." ? string.Empty : relative,
                        $"Package contains more than {PluginPackageWorkflow.MaximumPackageEntries} filesystem entries."));
                    return snapshot;
                }

                foreach (string path in entries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entryCount++;
                    string relative = Path.GetRelativePath(source.RootPath, path).Replace('\\', '/');
                    using NoFollowPackageSourceEntry entry = source.OpenEntry(path);
                    if (entry.IsReparsePoint)
                    {
                        issues.Add(new PluginPackageValidationIssue(
                            "reparse-path",
                            relative,
                            "Package paths may not contain links or reparse points."));
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        source.RetainDirectory(entry);
                        pending.Push(path);
                        continue;
                    }

                    string? violation = PluginPackageWorkflow.PackageBudgetViolation(
                        fileCount,
                        totalBytes,
                        entry.Length);
                    if (violation is not null)
                    {
                        issues.Add(new PluginPackageValidationIssue(
                            violation,
                            relative,
                            PluginPackageWorkflow.PackageBudgetMessage(violation)));
                        return snapshot;
                    }

                    DeviceLabPackageFile file = new(relative, entry.TakeHandle(), entry.Length);
                    if (!snapshot._canonicalPaths.Add(relative)
                        || !snapshot._files.TryAdd(relative, file))
                    {
                        file.Dispose();
                        issues.Add(new PluginPackageValidationIssue(
                            "duplicate-path",
                            relative,
                            "Package contains duplicate canonical file paths."));
                        continue;
                    }

                    fileCount++;
                    totalBytes += entry.Length;
                }

            }

            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    /// <summary>Looks up one captured file without reopening its source path.</summary>
    internal bool TryGetFile(string relativePath, out DeviceLabPackageFile file) =>
        _files.TryGetValue(relativePath.Replace('\\', '/'), out file!);

    /// <summary>
    /// Takes at most the remaining entry budget plus one overflow observation before sorting.
    /// </summary>
    internal static IReadOnlyList<string> TakeBoundedEntries(
        IEnumerable<string> entries,
        int remaining,
        CancellationToken cancellationToken,
        out bool exceeded)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(remaining);
        List<string> accepted = new(Math.Min(remaining, 256));
        using IEnumerator<string> enumerator = entries.GetEnumerator();
        while (accepted.Count < remaining)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                exceeded = false;
                return accepted;
            }
            accepted.Add(enumerator.Current);
        }

        cancellationToken.ThrowIfCancellationRequested();
        exceeded = enumerator.MoveNext();
        return accepted;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (DeviceLabPackageFile file in _files.Values)
        {
            file.Dispose();
        }
        _files.Clear();
        _canonicalPaths.Clear();
        _source.Dispose();
    }
}

/// <summary>One regular package file retained with write and delete sharing denied.</summary>
internal sealed class DeviceLabPackageFile : IDisposable
{
    private readonly FileStream _stream;

    internal DeviceLabPackageFile(string relativePath, SafeFileHandle handle, long length)
    {
        RelativePath = relativePath;
        Length = length;
        _stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
    }

    /// <summary>Canonical package-relative path.</summary>
    internal string RelativePath { get; }

    /// <summary>Stable file length observed from the retained handle.</summary>
    internal long Length { get; }

    /// <summary>Retained seekable stream. Call <see cref="Rewind"/> before each read.</summary>
    internal Stream Stream => _stream;

    /// <summary>Rewinds the retained stream to the start.</summary>
    internal void Rewind() => _stream.Position = 0;

    /// <summary>Reads stable owned bytes without reopening the path.</summary>
    internal bool TryReadAllBytes(int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (Length < 0 || Length > maximumBytes)
        {
            return false;
        }

        Rewind();
        byte[] owned = new byte[(int)Length];
        _stream.ReadExactly(owned);
        bytes = owned;
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();
}

/// <summary>Locks source ancestors and opens each enumerated entry without following its final link.</summary>
internal sealed partial class NoFollowPackageSource : IDisposable
{
    private readonly List<SafeFileHandle> _directoryHandles = [];
    private bool _disposed;

    private NoFollowPackageSource(string rootPath)
    {
        RootPath = rootPath;
    }

    /// <summary>Canonical source root held against rename and deletion.</summary>
    internal string RootPath { get; }

    /// <summary>Opens and pins every existing source ancestor through the package root.</summary>
    internal static NoFollowPackageSource Open(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Stack<string> ancestors = [];
        DirectoryInfo? current = new(root);
        while (current is not null)
        {
            ancestors.Push(current.FullName);
            current = current.Parent;
        }

        NoFollowPackageSource source = new(root);
        try
        {
            while (ancestors.Count > 0)
            {
                string ancestor = ancestors.Pop();
                using NoFollowPackageSourceEntry entry = OpenEntryCore(ancestor);
                if (entry.IsReparsePoint)
                {
                    throw new InvalidDataException("Package source may not traverse a link or reparse point.");
                }
                if (!entry.IsDirectory)
                {
                    throw new InvalidDataException("Package source root and ancestors must be directories.");
                }
                source._directoryHandles.Add(entry.TakeHandle());
            }
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Opens one enumerated entry without following a final reparse point.</summary>
    internal NoFollowPackageSourceEntry OpenEntry(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return OpenEntryCore(path);
    }

    /// <summary>Keeps one opened ordinary directory stable through traversal and packing.</summary>
    internal void RetainDirectory(NoFollowPackageSourceEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsDirectory || entry.IsReparsePoint)
        {
            throw new InvalidDataException("Only ordinary package directories may be retained.");
        }
        _directoryHandles.Add(entry.TakeHandle());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int index = _directoryHandles.Count - 1; index >= 0; index--)
        {
            _directoryHandles[index].Dispose();
        }
        _directoryHandles.Clear();
    }

    private static NoFollowPackageSourceEntry OpenEntryCore(string path)
    {
        SafeFileHandle? probe = OpenPath(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (probe.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            probe.Dispose();
            throw NativeIoException("open", path, error);
        }

        try
        {
            NativeEntryInformation probeInformation = ReadInformation(probe, path);
            bool isDirectory = (probeInformation.Attributes & FileAttributeDirectory) != 0;
            bool isReparsePoint = (probeInformation.Attributes & FileAttributeReparsePoint) != 0;
            if (isDirectory || isReparsePoint)
            {
                NoFollowPackageSourceEntry result = new(
                    probe,
                    isDirectory,
                    isReparsePoint,
                    length: 0);
                probe = null;
                return result;
            }

            SafeFileHandle? readHandle = OpenPath(
                path,
                GenericRead,
                FileShareRead,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagSequentialScan);
            try
            {
                if (readHandle.IsInvalid)
                {
                    throw NativeIoException("open for reading", path, Marshal.GetLastPInvokeError());
                }

                NativeEntryInformation readInformation = ReadInformation(readHandle, path);
                if (readInformation.Identity != probeInformation.Identity
                    || (readInformation.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                {
                    throw new InvalidDataException(
                        $"Package source entry changed while it was being secured: '{path}'.");
                }

                NoFollowPackageSourceEntry result = new(
                    readHandle,
                    isDirectory: false,
                    isReparsePoint: false,
                    readInformation.Length);
                readHandle = null;
                return result;
            }
            finally
            {
                readHandle?.Dispose();
            }
        }
        finally
        {
            probe?.Dispose();
        }
    }

    private static NativeEntryInformation ReadInformation(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw NativeIoException("inspect", path, Marshal.GetLastPInvokeError());
        }

        ulong length = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (length > long.MaxValue)
        {
            throw new InvalidDataException($"Package source entry is too large: '{path}'.");
        }

        return new NativeEntryInformation(
            information.FileAttributes,
            new PackagePathIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow),
            (long)length);
    }

    private static Exception NativeIoException(string operation, string path, int error)
    {
        string message = $"Could not {operation} package source path '{path}'.";
        return error switch
        {
            2 or 3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message, new Win32Exception(error)),
            _ => new IOException(message, new Win32Exception(error)),
        };
    }

    private readonly record struct NativeEntryInformation(
        uint Attributes,
        PackagePathIdentity Identity,
        long Length);

    private readonly record struct PackagePathIdentity(uint VolumeSerialNumber, ulong FileIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    private static SafeFileHandle OpenPath(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint flags) => CreateFileW(path, desiredAccess, shareMode, 0, OpenExisting, flags, 0);
}

/// <summary>One no-follow source entry with a stable native identity.</summary>
internal sealed class NoFollowPackageSourceEntry : IDisposable
{
    private SafeFileHandle? _handle;

    internal NoFollowPackageSourceEntry(
        SafeFileHandle handle,
        bool isDirectory,
        bool isReparsePoint,
        long length)
    {
        _handle = handle;
        IsDirectory = isDirectory;
        IsReparsePoint = isReparsePoint;
        Length = length;
    }

    /// <summary>Whether the opened entry is a directory.</summary>
    internal bool IsDirectory { get; }

    /// <summary>Whether the opened entry is a reparse point.</summary>
    internal bool IsReparsePoint { get; }

    /// <summary>Stable file length from the retained native handle.</summary>
    internal long Length { get; }

    /// <summary>Transfers the retained native handle.</summary>
    internal SafeFileHandle TakeHandle()
    {
        SafeFileHandle handle = _handle
            ?? throw new ObjectDisposedException(nameof(NoFollowPackageSourceEntry));
        _handle = null;
        return handle;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
