using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Capture;

/// <summary>Bounded projection of every lane that will enter the sanitized shareable bundle.</summary>
internal sealed record CapturePrivacyPreview
{
    private const int MaximumSamples = 64;
    private const int MaximumBlobPrefixBytes = 128;
    private static readonly byte[] Newline = [(byte)'\n'];

    public required ShareableCaptureManifest Manifest { get; init; }

    public required ObserveOnlyRecipe Recipe { get; init; }

    public required MachineInventory Inventory { get; init; }

    public required CaptureRedactionManifest Redaction { get; init; }

    public IReadOnlyList<CaptureLanePreview> Streams { get; init; } = [];

    public IReadOnlyList<CaptureLanePreview> Analysis { get; init; } = [];

    public IReadOnlyList<CaptureBlobPreview> Blobs { get; init; } = [];

    public required string Explanation { get; init; }

    internal static CapturePrivacyPreview Create(SanitizedCaptureBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        int remainingSamples = MaximumSamples;
        List<CaptureLanePreview> streams = [];
        foreach (CaptureStreamFile stream in bundle.Streams)
        {
            CaptureStreamDescriptor descriptor = bundle.Manifest.Streams.Single(candidate =>
                string.Equals(candidate.SourceId, stream.SourceId, StringComparison.Ordinal));
            streams.Add(Lane(
                descriptor.Path,
                stream.Events,
                value => JsonSerializer.SerializeToUtf8Bytes(
                    value,
                    DeviceLabCompactJson.CaptureStreamEvent),
                ref remainingSamples));
        }

        List<CaptureLanePreview> analysis = [];
        foreach (CaptureAnalysisFile file in bundle.Analysis)
        {
            CaptureAnalysisDescriptor descriptor = bundle.Manifest.Analysis.Single(candidate =>
                string.Equals(candidate.AnalyzerId, file.AnalyzerId, StringComparison.Ordinal));
            analysis.Add(Lane(
                descriptor.Path,
                file.Results,
                value => JsonSerializer.SerializeToUtf8Bytes(
                    value,
                    DeviceLabCompactJson.CaptureAnalysisResult),
                ref remainingSamples));
        }

        return new CapturePrivacyPreview
        {
            Manifest = bundle.Manifest,
            Recipe = bundle.Recipe,
            Inventory = bundle.Inventory,
            Redaction = bundle.Redaction,
            Streams = streams,
            Analysis = analysis,
            Blobs = [.. bundle.Blobs.Select(blob => new CaptureBlobPreview
            {
                Path = blob.Descriptor.Path,
                MediaType = blob.Descriptor.MediaType,
                ByteLength = blob.Bytes.LongLength,
                Sha256 = CaptureHashFile.Hash(blob.Bytes),
                Base64Prefix = Convert.ToBase64String(
                    blob.Bytes.AsSpan(0, Math.Min(blob.Bytes.Length, MaximumBlobPrefixBytes))),
                PrefixTruncated = blob.Bytes.Length > MaximumBlobPrefixBytes,
            })],
            Explanation = remainingSamples == 0
                ? $"Every shareable lane is listed with its exact item count, byte length, and hash. Content samples are capped globally at {MaximumSamples}; the sanitized root documents are shown in full."
                : "Every shareable lane is listed with its exact item count, byte length, and hash; the sanitized root documents and available content samples are shown.",
        };
    }

    private static CaptureLanePreview Lane<T>(
        string path,
        IReadOnlyList<T> values,
        Func<T, byte[]> serialize,
        ref int remainingSamples)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        List<JsonElement> samples = [];
        foreach (T value in values)
        {
            byte[] json = serialize(value);
            hash.AppendData(json);
            hash.AppendData(Newline);
            length = checked(length + json.Length + 1L);
            if (remainingSamples > 0)
            {
                using JsonDocument document = JsonDocument.Parse(json);
                samples.Add(document.RootElement.Clone());
                remainingSamples--;
            }
        }

        return new CaptureLanePreview
        {
            Path = path,
            ItemCount = values.Count,
            ByteLength = length,
            Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            Samples = samples,
            SamplesOmitted = values.Count - samples.Count,
        };
    }
}

internal sealed record CaptureLanePreview
{
    public required string Path { get; init; }

    public required int ItemCount { get; init; }

    public required long ByteLength { get; init; }

    public required string Sha256 { get; init; }

    public IReadOnlyList<JsonElement> Samples { get; init; } = [];

    public required int SamplesOmitted { get; init; }
}

internal sealed record CaptureBlobPreview
{
    public required string Path { get; init; }

    public required string MediaType { get; init; }

    public required long ByteLength { get; init; }

    public required string Sha256 { get; init; }

    public required string Base64Prefix { get; init; }

    public required bool PrefixTruncated { get; init; }
}
