using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Fixtures;

/// <summary>Extracts plain simulator-only fixtures from validated sanitized captures.</summary>
internal static class FixtureExtractionWorkflow
{
    /// <summary>Current deterministic extractor identity.</summary>
    public const string ExtractorVersion = "wsgm-device-fixture@1";

    /// <summary>Writes a new reviewable fixture directory without invoking hardware.</summary>
    /// <param name="bundle">Validated sanitized source bundle.</param>
    /// <param name="sourceCaptureSha256">Hash of the exact source archive.</param>
    /// <param name="fixtureId">Stable fixture ID.</param>
    /// <param name="outputDirectory">New explicit fixture directory.</param>
    /// <param name="boundaries">Protected filesystem boundaries.</param>
    /// <param name="cancellationToken">Cancels serialization or atomic publication.</param>
    /// <returns>Written simulator-only manifest.</returns>
    public static FixtureManifest Extract(
        SanitizedCaptureBundle bundle,
        string sourceCaptureSha256,
        string fixtureId,
        string outputDirectory,
        DeviceLabPathBoundaries boundaries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCaptureSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureId);
        ArgumentNullException.ThrowIfNull(boundaries);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Fixture output must be a new directory.");
        }

        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            outputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            throw new IOException(decision.Reason ?? "Fixture output path was rejected.");
        }

        SortedDictionary<string, byte[]> inputs = new(StringComparer.Ordinal)
        {
            ["input/inventory.json"] = WithNewline(JsonSerializer.SerializeToUtf8Bytes(
                bundle.Inventory,
                DeviceLabJsonContext.Default.MachineInventory)),
            ["input/recipe.json"] = WithNewline(JsonSerializer.SerializeToUtf8Bytes(
                bundle.Recipe,
                DeviceLabJsonContext.Default.ObserveOnlyRecipe)),
        };
        foreach (CaptureStreamFile stream in bundle.Streams.OrderBy(stream => stream.SourceId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            inputs[$"input/streams/{SafeName(stream.SourceId)}.ndjson"] = Ndjson(
                stream.Events,
                captureEvent => JsonSerializer.SerializeToUtf8Bytes(
                    captureEvent,
                    DeviceLabCompactJson.CaptureStreamEvent),
                cancellationToken);
        }

        SortedDictionary<string, byte[]> expected = new(StringComparer.Ordinal);
        foreach (CaptureAnalysisFile analysis in bundle.Analysis.OrderBy(item => item.AnalyzerId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            expected[$"expected/analysis/{SafeName(analysis.AnalyzerId)}.ndjson"] = Ndjson(
                analysis.Results,
                result => JsonSerializer.SerializeToUtf8Bytes(
                    result,
                    DeviceLabCompactJson.CaptureAnalysisResult),
                cancellationToken);
        }

        FixtureManifest manifest = new()
        {
            SchemaVersion = FixtureSchema.CurrentVersion,
            FixtureId = fixtureId,
            SourceCaptureSha256 = sourceCaptureSha256,
            ExtractorVersion = ExtractorVersion,
            ReplayPolicy = FixtureReplayPolicy.SimulatorOnly,
            Inputs = [.. inputs.Select(pair => Artifact(pair.Key, pair.Value))],
            ExpectedOutputs = [.. expected.Select(pair => Artifact(pair.Key, pair.Value))],
        };
        IReadOnlyList<CaptureValidationError> errors = FixtureSchemaValidator.Validate(manifest);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(errors[0].Message);
        }

        string parent = Path.GetDirectoryName(decision.FullPath)
            ?? throw new IOException("Fixture output has no parent directory.");
        string temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(decision.FullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(parent);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(temporary);
            DeviceLabOutputPathDecision recheck = DeviceLabOutputPathPolicy.Evaluate(
                decision.FullPath,
                DeviceLabOutputTargetKind.Directory,
                boundaries);
            if (!recheck.IsAllowed)
            {
                throw new IOException(recheck.Reason ?? "Fixture path changed before write.");
            }

            foreach ((string path, byte[] bytes) in inputs.Concat(expected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteNew(temporary, path, bytes, cancellationToken);
            }

            WriteNew(
                temporary,
                FixtureSchema.ManifestPath,
                WithNewline(JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    DeviceLabJsonContext.Default.FixtureManifest)),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(decision.FullPath) || File.Exists(decision.FullPath))
            {
                throw new IOException("Fixture output was created before publication.");
            }
            Directory.Move(temporary, decision.FullPath);
        }
        catch
        {
            TryDeleteTemporaryDirectory(temporary);
            throw;
        }
        return manifest;
    }

    private static FixtureArtifact Artifact(string path, byte[] bytes) => new()
    {
        Path = path,
        MediaType = path.EndsWith(".ndjson", StringComparison.Ordinal)
            ? "application/x-ndjson"
            : "application/json",
        Length = bytes.Length,
        Sha256 = CaptureHashFile.Hash(bytes),
    };

    private static void WriteNew(
        string root,
        string relative,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Fixture artifact escaped its output directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(64 * 1024, bytes.Length - offset);
            output.Write(bytes, offset, length);
            offset += length;
        }
        output.Flush(flushToDisk: true);
    }

    /// <summary>Turns one source or analyzer identifier into a distinct filesystem-safe name.</summary>
    /// <param name="value">The identifier as the capture recorded it.</param>
    /// <returns>A sanitized name that is unique to that exact identifier.</returns>
    /// <remarks>
    /// The hash suffix is what makes it injective. Sanitizing alone maps distinct identifiers such
    /// as <c>pad/a</c> and <c>pad?a</c> onto the same name, and the dictionary assignment then
    /// silently replaced the first stream: the fixture still validated while omitting source data
    /// and expected results, so replay no longer represented the capture it came from.
    /// </remarks>
    private static string SafeName(string value)
    {
        string sanitized = string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-'));
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "unknown";
        }

        string digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        return $"{sanitized}-{digest}";
    }

    private static byte[] WithNewline(byte[] bytes)
    {
        byte[] output = new byte[bytes.Length + 1];
        bytes.CopyTo(output, 0);
        output[^1] = (byte)'\n';
        return output;
    }

    private static byte[] Ndjson<T>(
        IReadOnlyList<T> values,
        Func<T, byte[]> serializer,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        foreach (T value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(serializer(value));
            output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Preserve the original extraction failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original extraction failure.
        }
    }
}
