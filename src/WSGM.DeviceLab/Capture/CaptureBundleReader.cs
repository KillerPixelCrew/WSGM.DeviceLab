using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
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
    private const int MaximumHashManifestBytes = 2 * 1024 * 1024;

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

            if (!TryVerifyHashes(
                entries,
                cancellationToken,
                out Dictionary<string, string> hashes,
                out string? hashError))
            {
                return Failure(CaptureBundleReadFailure.HashMismatch, hashError);
            }

            ShareableCaptureManifest manifest = Deserialize(
                entries[CaptureBundleLayout.ManifestPath],
                DeviceLabJsonContext.Default.ShareableCaptureManifest,
                cancellationToken);
            ObserveOnlyRecipe recipe = Deserialize(
                entries[CaptureBundleLayout.RecipePath],
                DeviceLabJsonContext.Default.ObserveOnlyRecipe,
                cancellationToken);
            MachineInventory inventory = Deserialize(
                entries[CaptureBundleLayout.InventoryPath],
                DeviceLabJsonContext.Default.MachineInventory,
                cancellationToken);
            CaptureRedactionManifest redaction = Deserialize(
                entries[CaptureBundleLayout.RedactionPath],
                DeviceLabJsonContext.Default.CaptureRedactionManifest,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            List<CaptureStreamFile> streams = [];
            foreach (CaptureStreamDescriptor descriptor in manifest.Streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(descriptor.Path, out ZipArchiveEntry? entry))
                {
                    return Failure(CaptureBundleReadFailure.MissingEntry, $"Stream '{descriptor.Path}' is absent.");
                }

                streams.Add(new CaptureStreamFile
                {
                    SourceId = descriptor.SourceId,
                    Events = DeserializeLines(
                        entry,
                        DeviceLabCompactJson.CaptureStreamEvent,
                        cancellationToken),
                });
            }

            List<CaptureAnalysisFile> analysis = [];
            foreach (CaptureAnalysisDescriptor descriptor in manifest.Analysis)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(descriptor.Path, out ZipArchiveEntry? entry))
                {
                    return Failure(CaptureBundleReadFailure.MissingEntry, $"Analysis '{descriptor.Path}' is absent.");
                }

                analysis.Add(new CaptureAnalysisFile
                {
                    AnalyzerId = descriptor.AnalyzerId,
                    Results = DeserializeLines(
                        entry,
                        DeviceLabCompactJson.CaptureAnalysisResult,
                        cancellationToken),
                });
            }

            List<CaptureBlobFile> blobs = [];
            foreach (CaptureBlobDescriptor descriptor in manifest.Blobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(descriptor.Path, out ZipArchiveEntry? entry))
                {
                    return Failure(CaptureBundleReadFailure.MissingEntry, $"Blob '{descriptor.Path}' is absent.");
                }

                blobs.Add(new CaptureBlobFile
                {
                    Descriptor = descriptor,
                    Bytes = ReadEntry(entry, cancellationToken),
                });
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
            if (!declared.SetEquals(entries.Keys))
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
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        CancellationToken cancellationToken,
        out Dictionary<string, string> hashes,
        out string? error)
    {
        hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        string text;
        try
        {
            ZipArchiveEntry hashEntry = entries[CaptureBundleLayout.HashesPath];
            if (hashEntry.Length > MaximumHashManifestBytes)
            {
                error = "Hash manifest exceeds its decode budget.";
                return false;
            }

            text = new UTF8Encoding(false, true).GetString(
                ReadEntry(hashEntry, cancellationToken));
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

        string[] expectedPaths = [.. entries.Keys
            .Where(path => !string.Equals(path, CaptureBundleLayout.HashesPath, StringComparison.OrdinalIgnoreCase))];
        if (!hashes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedPaths))
        {
            error = "Hash manifest does not cover every archive entry exactly once.";
            return false;
        }

        foreach ((string path, string expected) in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(HashEntry(entries[path], cancellationToken), expected,
                StringComparison.Ordinal))
            {
                error = $"Content hash mismatch for '{path}'.";
                return false;
            }
        }

        return true;
    }

    private static string HashEntry(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using Stream input = entry.Open();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > entry.Length)
            {
                throw new InvalidDataException("Archive entry exceeded its declared length.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
        }

        if (total != entry.Length)
        {
            throw new InvalidDataException("Archive entry ended before its declared length.");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static T Deserialize<T>(
        ZipArchiveEntry entry,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        using Stream input = entry.Open();
        return JsonSerializer.DeserializeAsync(input, typeInfo, cancellationToken)
            .AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidDataException("A required JSON entry decoded to null.");
    }

    private static IReadOnlyList<T> DeserializeLines<T>(
        ZipArchiveEntry entry,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        List<T> values = [];
        using Stream input = entry.Open();
        using MemoryStream line = new(capacity: Math.Min(MaximumJsonLineBytes, 64 * 1024));
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > entry.Length)
            {
                throw new InvalidDataException("Archive entry exceeded its declared length.");
            }

            int start = 0;
            for (int offset = 0; offset < read; offset++)
            {
                if (buffer[offset] != (byte)'\n')
                {
                    continue;
                }

                AppendLineBytes(line, buffer.AsSpan(start, offset - start));
                DecodeLine(line, values, typeInfo);
                line.SetLength(0);
                start = offset + 1;
            }

            AppendLineBytes(line, buffer.AsSpan(start, read - start));
        }

        if (total != entry.Length)
        {
            throw new InvalidDataException("Archive entry ended before its declared length.");
        }

        DecodeLine(line, values, typeInfo);
        return values;
    }

    private static void AppendLineBytes(MemoryStream line, ReadOnlySpan<byte> bytes)
    {
        if (line.Length + bytes.Length > MaximumJsonLineBytes)
        {
            throw new InvalidDataException("NDJSON line exceeds its decode budget.");
        }

        line.Write(bytes);
    }

    private static void DecodeLine<T>(
        MemoryStream line,
        ICollection<T> values,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        int length = checked((int)line.Length);
        if (length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = line.GetBuffer().AsSpan(0, length);
        if (bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        if (!bytes.IsEmpty)
        {
            values.Add(JsonSerializer.Deserialize(bytes, typeInfo)
                ?? throw new InvalidDataException("NDJSON line decoded to null."));
        }
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
