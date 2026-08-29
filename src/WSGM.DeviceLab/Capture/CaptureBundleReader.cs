using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Capture;

/// <summary>Closed reason an imported shareable capture was rejected.</summary>
internal enum CaptureBundleReadFailure
{
    /// <summary>The archive and every content hash passed validation.</summary>
    None,

    /// <summary>The archive structure, path, count, or size was unsafe.</summary>
    UnsafeArchive,

    /// <summary>A required canonical entry was absent.</summary>
    MissingEntry,

    /// <summary>The hash file was malformed, incomplete, or did not match content.</summary>
    HashMismatch,

    /// <summary>A JSON or NDJSON entry was malformed or exceeded its line budget.</summary>
    MalformedContent,

    /// <summary>The typed bundle violated its semantic schema.</summary>
    InvalidSchema,

    /// <summary>The input could not be read.</summary>
    Unreadable,
}

/// <summary>Bounded result of importing one <c>.wsgmcap</c>.</summary>
internal sealed record CaptureBundleReadResult
{
    /// <summary>Closed import outcome.</summary>
    public required CaptureBundleReadFailure Failure { get; init; }

    /// <summary>Validated bundle, present only on success.</summary>
    public SanitizedCaptureBundle? Bundle { get; init; }

    /// <summary>Verified entry hashes, useful for deterministic diffing.</summary>
    public IReadOnlyDictionary<string, string> EntryHashes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Bounded failure detail without imported content.</summary>
    public string? Detail { get; init; }

    /// <summary>Whether a validated bundle is available.</summary>
    public bool Succeeded => Failure is CaptureBundleReadFailure.None && Bundle is not null;
}

/// <summary>Reads and verifies an untrusted shareable capture under hard archive and decode budgets.</summary>
internal static class CaptureBundleReader
{
    private const int MaximumJsonLineBytes = 4 * 1024 * 1024;

    /// <summary>Reads one archive from a seekable stream and leaves the stream open.</summary>
    /// <param name="source">Untrusted archive stream.</param>
    /// <param name="cancellationToken">Cancels bounded archive verification and decoding.</param>
    /// <returns>A validated bundle or a closed rejection.</returns>
    public static CaptureBundleReadResult Read(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek)
        {
            return Failure(CaptureBundleReadFailure.Unreadable, "Capture input must be readable and seekable.");
        }

        try
        {
            using ZipArchive archive = new(source, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
            if (archive.Entries.Count is 0 or > CaptureSchema.MaximumArchiveEntries)
            {
                return Failure(CaptureBundleReadFailure.UnsafeArchive, "Archive entry count is outside the allowed range.");
            }

            Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);
            long uncompressedTotal = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CaptureBundleLayout.IsSafeRelativePath(entry.FullName)
                    || !entries.TryAdd(entry.FullName, entry)
                    || entry.Length < 0)
                {
                    return Failure(CaptureBundleReadFailure.UnsafeArchive, "Archive contains an unsafe or duplicate path.");
                }

                uncompressedTotal = checked(uncompressedTotal + entry.Length);
                if (uncompressedTotal > CaptureSchema.MaximumArchiveBytes
                    || entry.Length > CaptureSchema.MaximumArchiveBytes
                    || entry.CompressedLength > 0 && entry.Length > 1024 * 1024
                        && entry.Length / entry.CompressedLength > 100)
                {
                    return Failure(CaptureBundleReadFailure.UnsafeArchive, "Archive exceeds its size or expansion budget.");
                }
            }

            foreach (string required in RequiredRootEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.ContainsKey(required))
                {
                    return Failure(CaptureBundleReadFailure.MissingEntry, $"Required entry '{required}' is absent.");
                }
            }

            Dictionary<string, byte[]> content = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, ZipArchiveEntry entry) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                content[path] = ReadEntry(entry, cancellationToken);
            }

            if (!TryVerifyHashes(
                content,
                cancellationToken,
                out Dictionary<string, string> hashes,
                out string? hashError))
            {
                return Failure(CaptureBundleReadFailure.HashMismatch, hashError);
            }

            ShareableCaptureManifest manifest = Deserialize(
                content[CaptureBundleLayout.ManifestPath],
                DeviceLabJsonContext.Default.ShareableCaptureManifest);
            ObserveOnlyRecipe recipe = Deserialize(
                content[CaptureBundleLayout.RecipePath],
                DeviceLabJsonContext.Default.ObserveOnlyRecipe);
            MachineInventory inventory = Deserialize(
                content[CaptureBundleLayout.InventoryPath],
                DeviceLabJsonContext.Default.MachineInventory);
            CaptureRedactionManifest redaction = Deserialize(
                content[CaptureBundleLayout.RedactionPath],
                DeviceLabJsonContext.Default.CaptureRedactionManifest);
            cancellationToken.ThrowIfCancellationRequested();

            List<CaptureStreamFile> streams = [];
            foreach (CaptureStreamDescriptor descriptor in manifest.Streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!content.TryGetValue(descriptor.Path, out byte[]? bytes))
                {
                    return Failure(CaptureBundleReadFailure.MissingEntry, $"Stream '{descriptor.Path}' is absent.");
                }

                streams.Add(new CaptureStreamFile
                {
                    SourceId = descriptor.SourceId,
                    Events = DeserializeLines(
                        bytes,
                        DeviceLabCompactJson.CaptureStreamEvent,
                        cancellationToken),
                });
            }

            List<CaptureAnalysisFile> analysis = [];
            foreach (CaptureAnalysisDescriptor descriptor in manifest.Analysis)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!content.TryGetValue(descriptor.Path, out byte[]? bytes))
                {
                    return Failure(CaptureBundleReadFailure.MissingEntry, $"Analysis '{descriptor.Path}' is absent.");
                }

                analysis.Add(new CaptureAnalysisFile
                {
                    AnalyzerId = descriptor.AnalyzerId,
                    Results = DeserializeLines(
                        bytes,
                        DeviceLabCompactJson.CaptureAnalysisResult,
                        cancellationToken),
                });
            }

            List<CaptureBlobFile> blobs = [];
            foreach (CaptureBlobDescriptor descriptor in manifest.Blobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!content.TryGetValue(descriptor.Path, out byte[]? bytes))
                {
                    return Failure(CaptureBundleReadFailure.MissingEntry, $"Blob '{descriptor.Path}' is absent.");
                }

                blobs.Add(new CaptureBlobFile { Descriptor = descriptor, Bytes = bytes });
            }

            HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase)
            {
                CaptureBundleLayout.ManifestPath,
                CaptureBundleLayout.RecipePath,
                CaptureBundleLayout.InventoryPath,
                CaptureBundleLayout.RedactionPath,
                CaptureBundleLayout.HashesPath,
            };
            declared.UnionWith(manifest.Streams.Select(stream => stream.Path));
            declared.UnionWith(manifest.Analysis.Select(item => item.Path));
            declared.UnionWith(manifest.Blobs.Select(blob => blob.Path));
            cancellationToken.ThrowIfCancellationRequested();
            if (!declared.SetEquals(content.Keys))
            {
                return Failure(CaptureBundleReadFailure.InvalidSchema, "Archive contains undeclared entries.");
            }

            SanitizedCaptureBundle bundle = new()
            {
                Manifest = manifest,
                Recipe = recipe,
                Inventory = inventory,
                Streams = streams,
                Analysis = analysis,
                Blobs = blobs,
                Redaction = redaction,
            };
            IReadOnlyList<CaptureValidationError> errors = CaptureSchemaValidator.Validate(
                bundle,
                cancellationToken);
            return errors.Count == 0
                ? new CaptureBundleReadResult
                {
                    Failure = CaptureBundleReadFailure.None,
                    Bundle = bundle,
                    EntryHashes = hashes,
                }
                : Failure(CaptureBundleReadFailure.InvalidSchema, errors[0].Message);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
            or UnauthorizedAccessException or JsonException or OverflowException
            or ArgumentException or NotSupportedException)
        {
            return Failure(
                exception is JsonException or InvalidDataException
                    ? CaptureBundleReadFailure.MalformedContent
                    : CaptureBundleReadFailure.Unreadable,
                exception.GetType().Name);
        }
    }

    private static string[] RequiredRootEntries() =>
    [
        CaptureBundleLayout.ManifestPath,
        CaptureBundleLayout.RecipePath,
        CaptureBundleLayout.InventoryPath,
        CaptureBundleLayout.RedactionPath,
        CaptureBundleLayout.HashesPath,
    ];

    private static byte[] ReadEntry(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > int.MaxValue)
        {
            throw new InvalidDataException("Entry exceeds the in-memory decode budget.");
        }

        byte[] bytes = new byte[(int)entry.Length];
        using Stream input = entry.Open();
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException("Archive entry ended before its declared length.");
            }

            offset += read;
        }

        if (input.ReadByte() != -1)
        {
            throw new InvalidDataException("Archive entry exceeded its declared length.");
        }

        return bytes;
    }

    private static bool TryVerifyHashes(
        IReadOnlyDictionary<string, byte[]> content,
        CancellationToken cancellationToken,
        out Dictionary<string, string> hashes,
        out string? error)
    {
        hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content[CaptureBundleLayout.HashesPath]);
        }
        catch (DecoderFallbackException)
        {
            error = "Hash manifest is not valid UTF-8.";
            return false;
        }

        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length < 67 || line[64..66] != "  ")
            {
                error = "Hash manifest contains a malformed line.";
                return false;
            }

            string hash = line[..64];
            string path = line[66..].TrimEnd('\r');
            if (hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || !CaptureBundleLayout.IsSafeRelativePath(path)
                || string.Equals(path, CaptureBundleLayout.HashesPath, StringComparison.OrdinalIgnoreCase)
                || !hashes.TryAdd(path, hash))
            {
                error = "Hash manifest contains an invalid hash or path.";
                return false;
            }
        }

        string[] expectedPaths = [.. content.Keys
            .Where(path => !string.Equals(path, CaptureBundleLayout.HashesPath, StringComparison.OrdinalIgnoreCase))];
        if (!hashes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedPaths))
        {
            error = "Hash manifest does not cover every archive entry exactly once.";
            return false;
        }

        foreach ((string path, string expected) in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(CaptureHashFile.Hash(content[path]), expected, StringComparison.Ordinal))
            {
                error = $"Content hash mismatch for '{path}'.";
                return false;
            }
        }

        return true;
    }

    private static T Deserialize<T>(byte[] bytes, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class => JsonSerializer.Deserialize(bytes, typeInfo)
        ?? throw new InvalidDataException("A required JSON entry decoded to null.");

    private static IReadOnlyList<T> DeserializeLines<T>(
        byte[] bytes,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        List<T> values = [];
        int start = 0;
        for (int offset = 0; offset <= bytes.Length; offset++)
        {
            if ((offset & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (offset != bytes.Length && bytes[offset] != (byte)'\n')
            {
                continue;
            }

            int length = offset - start;
            if (length > MaximumJsonLineBytes)
            {
                throw new InvalidDataException("NDJSON line exceeds its decode budget.");
            }

            if (length > 0)
            {
                ReadOnlySpan<byte> line = bytes.AsSpan(start, length);
                if (line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                values.Add(JsonSerializer.Deserialize(line, typeInfo)
                    ?? throw new InvalidDataException("NDJSON line decoded to null."));
            }

            start = offset + 1;
        }

        return values;
    }

    private static CaptureBundleReadResult Failure(CaptureBundleReadFailure failure, string? detail) => new()
    {
        Failure = failure,
        Detail = detail,
    };
}

/// <summary>Stable summary returned by capture inspection.</summary>
internal sealed record CaptureInspection
{
    /// <summary>Sanitized bundle ID.</summary>
    public required string BundleId { get; init; }

    /// <summary>Number of raw streams.</summary>
    public required int StreamCount { get; init; }

    /// <summary>Number of raw events.</summary>
    public required long EventCount { get; init; }

    /// <summary>Number of derived results.</summary>
    public required long AnalysisCount { get; init; }

    /// <summary>Number of included sanitized blobs.</summary>
    public required int BlobCount { get; init; }

    /// <summary>Explicit platform limitations carried by the capture.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Read-only inspection and deterministic entry-level diff operations.</summary>
internal static class CaptureWorkbench
{
    /// <summary>Summarizes a validated capture.</summary>
    /// <param name="bundle">Validated capture.</param>
    /// <returns>Stable capture summary.</returns>
    public static CaptureInspection Inspect(SanitizedCaptureBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return new CaptureInspection
        {
            BundleId = bundle.Manifest.BundleId,
            StreamCount = bundle.Streams.Count,
            EventCount = bundle.Streams.Sum(stream => (long)stream.Events.Count),
            AnalysisCount = bundle.Analysis.Sum(stream => (long)stream.Results.Count),
            BlobCount = bundle.Blobs.Count,
            Limitations = [.. bundle.Analysis.SelectMany(stream => stream.Results)
                .SelectMany(result => result.Limitations)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)],
        };
    }

    /// <summary>Compares verified archive-entry hashes without inferring meaning from similarity.</summary>
    /// <param name="left">First verified entry hashes.</param>
    /// <param name="right">Second verified entry hashes.</param>
    /// <returns>Every added, removed, or changed canonical path.</returns>
    public static IReadOnlyList<CaptureEntryDifference> Diff(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        SortedSet<string> paths = new(left.Keys, StringComparer.Ordinal);
        paths.UnionWith(right.Keys);
        List<CaptureEntryDifference> differences = [];
        foreach (string path in paths)
        {
            bool hasLeft = left.TryGetValue(path, out string? leftHash);
            bool hasRight = right.TryGetValue(path, out string? rightHash);
            if (!hasLeft || !hasRight || !string.Equals(leftHash, rightHash, StringComparison.Ordinal))
            {
                differences.Add(new CaptureEntryDifference(path, leftHash, rightHash));
            }
        }

        return differences;
    }
}

/// <summary>One deterministic archive-entry difference.</summary>
/// <param name="Path">Canonical entry path.</param>
/// <param name="LeftSha256">Left hash, or null when absent.</param>
/// <param name="RightSha256">Right hash, or null when absent.</param>
internal sealed record CaptureEntryDifference(string Path, string? LeftSha256, string? RightSha256);
