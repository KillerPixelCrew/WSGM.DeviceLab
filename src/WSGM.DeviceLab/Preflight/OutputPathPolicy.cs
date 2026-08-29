using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.DeviceLab.Preflight;

/// <summary>Kind of filesystem target requested by a Device Lab workflow.</summary>
internal enum DeviceLabOutputTargetKind
{
    /// <summary>A directory that may contain several generated artifacts.</summary>
    Directory,

    /// <summary>One new output file.</summary>
    NewFile,
}

/// <summary>Closed reason an output path was refused.</summary>
internal enum DeviceLabOutputPathRisk
{
    /// <summary>No unsafe condition was found.</summary>
    None,

    /// <summary>The requested path was empty or malformed.</summary>
    Malformed,

    /// <summary>The path resolves to a drive root.</summary>
    DriveRoot,

    /// <summary>The path resolves to a broad user-profile directory.</summary>
    BroadHomeDirectory,

    /// <summary>The path resolves to the repository root.</summary>
    RepositoryRoot,

    /// <summary>The path resolves inside the live WSGM data directory.</summary>
    LiveDataDirectory,

    /// <summary>An existing reparse point makes the final target ambiguous.</summary>
    ReparsePoint,

    /// <summary>A new file target would overwrite an existing file or directory.</summary>
    ExistingTarget,

    /// <summary>A directory target names an existing file.</summary>
    NotDirectory,
}

/// <summary>Environment-owned paths that Device Lab must not use as broad output targets.</summary>
internal sealed record DeviceLabPathBoundaries
{
    /// <summary>Live per-user WSGM state directory.</summary>
    public required string LiveDataDirectory { get; init; }

    /// <summary>Repository root when the tool is running from a source checkout.</summary>
    public string? RepositoryRoot { get; init; }

    /// <summary>Broad profile directories that require a more specific child path.</summary>
    public IReadOnlyList<string> BroadHomeDirectories { get; init; } = [];

    /// <summary>Builds boundaries for the current user and optional source checkout.</summary>
    /// <param name="repositoryRoot">Detected repository root, or <see langword="null"/>.</param>
    /// <returns>Normalized boundaries used only for rejection.</returns>
    public static DeviceLabPathBoundaries ForCurrentUser(string? repositoryRoot)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> broadHomeDirectories =
        [
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ];

        if (!string.IsNullOrWhiteSpace(profile))
        {
            broadHomeDirectories.Add(Path.Combine(profile, "Downloads"));
        }

        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive))
        {
            broadHomeDirectories.Add(oneDrive);
        }

        // wsgm-allow-live-data-path: this resolves the live directory only so every Device Lab
        // output policy can refuse it before opening or creating anything there.
        string liveDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSGM");

        return new DeviceLabPathBoundaries
        {
            LiveDataDirectory = liveDataDirectory,
            RepositoryRoot = repositoryRoot,
            BroadHomeDirectories = broadHomeDirectories
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }
}

/// <summary>Result of resolving and evaluating one explicit output path.</summary>
internal sealed record DeviceLabOutputPathDecision
{
    /// <summary>Whether Device Lab may use the target.</summary>
    public required bool IsAllowed { get; init; }

    /// <summary>Normalized absolute path when resolution succeeded.</summary>
    public string? FullPath { get; init; }

    /// <summary>Closed rejection category.</summary>
    public required DeviceLabOutputPathRisk Risk { get; init; }

    /// <summary>Operator-facing reason for rejection.</summary>
    public string? Reason { get; init; }
}

/// <summary>Central output-path firewall shared by every Device Lab surface.</summary>
internal static class DeviceLabOutputPathPolicy
{
    /// <summary>Resolves and validates one explicit output target.</summary>
    /// <param name="path">Requested path.</param>
    /// <param name="kind">Whether the path names a directory or one new file.</param>
    /// <param name="boundaries">Environment paths that must be protected.</param>
    /// <returns>An allow decision with a normalized path, or a closed rejection.</returns>
    public static DeviceLabOutputPathDecision Evaluate(
        string? path,
        DeviceLabOutputTargetKind kind,
        DeviceLabPathBoundaries boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);

        if (string.IsNullOrWhiteSpace(path))
        {
            return Reject(DeviceLabOutputPathRisk.Malformed, "An explicit output path is required.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return Reject(DeviceLabOutputPathRisk.Malformed, "The output path is malformed.");
        }

        string normalized = NormalizeDirectory(fullPath);
        string? root = Path.GetPathRoot(fullPath);
        if (root is not null && PathsEqual(normalized, NormalizeDirectory(root)))
        {
            return Reject(
                DeviceLabOutputPathRisk.DriveRoot,
                "A drive root is too broad for Device Lab output.",
                fullPath);
        }

        if (IsUnderneath(fullPath, boundaries.LiveDataDirectory))
        {
            return Reject(
                DeviceLabOutputPathRisk.LiveDataDirectory,
                "The live WSGM data directory is never a Device Lab output target.",
                fullPath);
        }

        if (kind is DeviceLabOutputTargetKind.Directory
            && boundaries.BroadHomeDirectories.Any(directory =>
                PathsEqual(normalized, NormalizeDirectory(directory))))
        {
            return Reject(
                DeviceLabOutputPathRisk.BroadHomeDirectory,
                "Choose a specific child directory instead of a broad home directory.",
                fullPath);
        }

        if (kind is DeviceLabOutputTargetKind.Directory
            && boundaries.RepositoryRoot is { Length: > 0 } repositoryRoot
            && PathsEqual(normalized, NormalizeDirectory(repositoryRoot)))
        {
            return Reject(
                DeviceLabOutputPathRisk.RepositoryRoot,
                "Choose a dedicated output directory instead of the repository root.",
                fullPath);
        }

        if (HasExistingReparsePoint(fullPath))
        {
            return Reject(
                DeviceLabOutputPathRisk.ReparsePoint,
                "An existing path component is a reparse point, so the target is ambiguous.",
                fullPath);
        }

        if (kind is DeviceLabOutputTargetKind.NewFile
            && (File.Exists(fullPath) || Directory.Exists(fullPath)))
        {
            return Reject(
                DeviceLabOutputPathRisk.ExistingTarget,
                "Device Lab will not overwrite an existing output target.",
                fullPath);
        }

        if (kind is DeviceLabOutputTargetKind.Directory && File.Exists(fullPath))
        {
            return Reject(
                DeviceLabOutputPathRisk.NotDirectory,
                "The requested output directory is an existing file.",
                fullPath);
        }

        return new DeviceLabOutputPathDecision
        {
            IsAllowed = true,
            FullPath = fullPath,
            Risk = DeviceLabOutputPathRisk.None,
        };
    }

    private static bool HasExistingReparsePoint(string path)
    {
        string? current = File.Exists(path) || Directory.Exists(path)
            ? path
            : Path.GetDirectoryName(path);

        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    private static bool IsUnderneath(string candidate, string directory)
    {
        string normalizedCandidate = NormalizeDirectory(candidate);
        string normalizedDirectory = NormalizeDirectory(directory);
        return PathsEqual(normalizedCandidate, normalizedDirectory)
            || normalizedCandidate.StartsWith(
                normalizedDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static DeviceLabOutputPathDecision Reject(
        DeviceLabOutputPathRisk risk,
        string reason,
        string? fullPath = null) => new()
        {
            IsAllowed = false,
            FullPath = fullPath,
            Risk = risk,
            Reason = reason,
        };
}

/// <summary>Finds the WSGM source root without assuming the process started there.</summary>
internal static class DeviceLabRepositoryLocator
{
    /// <summary>Walks upward for the WSGM solution marker.</summary>
    /// <param name="startPath">File or directory path to start from.</param>
    /// <returns>The repository root, or <see langword="null"/> outside a checkout.</returns>
    public static string? Find(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(startPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }

        DirectoryInfo? directory = File.Exists(fullPath)
            ? new FileInfo(fullPath).Directory
            : new DirectoryInfo(fullPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WSGM.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
