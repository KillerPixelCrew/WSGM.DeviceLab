using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WSGM.DeviceLab.Capture;

/// <summary>Canonical paths in a shareable <c>.wsgmcap</c> archive.</summary>
internal static class CaptureBundleLayout
{
    /// <summary>Root bundle manifest.</summary>
    public const string ManifestPath = "manifest.json";

    /// <summary>Inert observe-only recipe.</summary>
    public const string RecipePath = "recipe.json";

    /// <summary>Sanitized machine inventory.</summary>
    public const string InventoryPath = "inventory.json";

    /// <summary>Redaction and quarantine report.</summary>
    public const string RedactionPath = "redaction.json";

    /// <summary>SHA-256 manifest.</summary>
    public const string HashesPath = "hashes.sha256";

    /// <summary>Whether a path is a canonical safe relative archive path.</summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><see langword="true"/> when the path cannot escape or alias an archive entry.</returns>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > CaptureSchema.MaximumArchivePathLength
            || path[0] is '/' or '\\'
            || path.Contains('\\')
            || path.Contains(':')
            || path.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = path.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not "..");
    }
}

/// <summary>One actionable validation failure in a capture artifact.</summary>
/// <param name="Path">Logical property or archive path.</param>
/// <param name="Message">Concrete reason the artifact is invalid.</param>
internal sealed record CaptureValidationError(string Path, string Message);

/// <summary>Validates capture schemas before any archive entry is created.</summary>
internal static class CaptureSchemaValidator
{
    /// <summary>Validates a shareable capture manifest independently of its archive.</summary>
    /// <param name="manifest">Manifest values to validate.</param>
    /// <returns>Every validation failure found.</returns>
    public static IReadOnlyList<CaptureValidationError> Validate(ShareableCaptureManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        List<CaptureValidationError> errors = [];
        ValidateManifest(manifest, errors);
        return errors;
    }

    /// <summary>Validates an imported observe-only recipe against its closed operation set.</summary>
    /// <param name="recipe">Recipe values to validate.</param>
    /// <returns>Every validation failure found.</returns>
    public static IReadOnlyList<CaptureValidationError> Validate(ObserveOnlyRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        List<CaptureValidationError> errors = [];
        ValidateRecipe(recipe, errors);
        return errors;
    }

    /// <summary>Validates a complete sanitized bundle.</summary>
    /// <param name="bundle">Bundle values to validate.</param>
    /// <param name="cancellationToken">Cancels validation of bounded collection content.</param>
    /// <returns>Every validation failure found.</returns>
    public static IReadOnlyList<CaptureValidationError> Validate(
        SanitizedCaptureBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        cancellationToken.ThrowIfCancellationRequested();

        List<CaptureValidationError> errors = [];
        ValidateManifest(bundle.Manifest, errors);
        ValidateRecipe(bundle.Recipe, errors);
        ValidateRedaction(bundle.Redaction, errors);

        Dictionary<string, ObservationStep> steps = bundle.Recipe.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.StepId))
            .GroupBy(step => step.StepId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        Dictionary<string, CaptureStreamDescriptor> streamDescriptors = bundle.Manifest.Streams
            .Where(stream => !string.IsNullOrWhiteSpace(stream.SourceId))
            .GroupBy(stream => stream.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        HashSet<long> globalSequences = [];
        HashSet<string> eventIds = new(StringComparer.Ordinal);

        foreach (CaptureStreamFile stream in bundle.Streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!streamDescriptors.TryGetValue(stream.SourceId, out CaptureStreamDescriptor? descriptor))
            {
                errors.Add(new("streams", $"Source '{stream.SourceId}' has no manifest descriptor."));
                continue;
            }

            if (descriptor.EventCount != stream.Events.Count)
            {
                errors.Add(new(descriptor.Path,
                    $"Manifest declares {descriptor.EventCount} events but stream contains {stream.Events.Count}."));
            }

            long previousSourceSequence = -1;
            foreach (CaptureStreamEvent captureEvent in stream.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateEvent(captureEvent, stream.SourceId, steps, errors);

                if (captureEvent.SourceSequence <= previousSourceSequence)
                {
                    errors.Add(new(captureEvent.EventId,
                        "Source sequence must increase strictly within its stream."));
                }

                previousSourceSequence = captureEvent.SourceSequence;

                if (!globalSequences.Add(captureEvent.GlobalSequence))
                {
                    errors.Add(new(captureEvent.EventId,
                        $"Global sequence {captureEvent.GlobalSequence} is duplicated."));
                }

                if (!eventIds.Add(captureEvent.EventId))
                {
                    errors.Add(new(captureEvent.EventId, "Event ID is duplicated."));
                }
            }
        }

        if (bundle.Streams.Count != streamDescriptors.Count)
        {
            errors.Add(new("streams", "Manifest descriptors and supplied stream files do not match one-to-one."));
        }

        Dictionary<string, CaptureAnalysisDescriptor> analysisDescriptors = bundle.Manifest.Analysis
            .Where(item => !string.IsNullOrWhiteSpace(item.AnalyzerId))
            .GroupBy(item => item.AnalyzerId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (CaptureAnalysisFile analysis in bundle.Analysis)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!analysisDescriptors.TryGetValue(
                    analysis.AnalyzerId,
                    out CaptureAnalysisDescriptor? descriptor))
            {
                errors.Add(new("analysis", $"Analyzer '{analysis.AnalyzerId}' has no manifest descriptor."));
                continue;
            }

            if (descriptor.ResultCount != analysis.Results.Count)
            {
                errors.Add(new(descriptor.Path,
                    $"Manifest declares {descriptor.ResultCount} results but stream contains {analysis.Results.Count}."));
            }

            foreach (CaptureAnalysisResult result in analysis.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateAnalysis(result, descriptor, eventIds, errors);
            }
        }

        if (bundle.Analysis.Count != analysisDescriptors.Count)
        {
            errors.Add(new("analysis", "Manifest descriptors and supplied analysis files do not match one-to-one."));
        }

        Dictionary<string, CaptureBlobDescriptor> blobDescriptors = bundle.Manifest.Blobs
            .Where(blob => !string.IsNullOrWhiteSpace(blob.BlobId))
            .GroupBy(blob => blob.BlobId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (CaptureBlobFile blob in bundle.Blobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBlob(blob, blobDescriptors, errors);
        }

        if (bundle.Blobs.Count != blobDescriptors.Count)
        {
            errors.Add(new("blobs", "Manifest descriptors and supplied blob files do not match one-to-one."));
        }

        return errors;
    }

    private static void ValidateManifest(
        ShareableCaptureManifest manifest,
        ICollection<CaptureValidationError> errors)
    {
        if (manifest.SchemaVersion != CaptureSchema.CurrentVersion)
        {
            errors.Add(new("manifest.schemaVersion", "Unsupported shareable capture schema version."));
        }

        ValidateId(manifest.BundleId, "manifest.bundleId", errors);
        ValidateId(manifest.ToolVersion, "manifest.toolVersion", errors);

        if (manifest.Privacy is not CapturePrivacy.ShareableSanitized)
        {
            errors.Add(new("manifest.privacy", "A .wsgmcap must be marked ShareableSanitized."));
        }

        if (manifest.CompletedAt < manifest.StartedAt)
        {
            errors.Add(new("manifest.completedAt", "Capture completion precedes its start."));
        }

        if (manifest.QpcFrequency <= 0)
        {
            errors.Add(new("manifest.qpcFrequency", "QPC frequency must be positive."));
        }

        ValidateFixedPath(manifest.RecipePath, CaptureBundleLayout.RecipePath, "manifest.recipePath", errors);
        ValidateFixedPath(manifest.InventoryPath, CaptureBundleLayout.InventoryPath, "manifest.inventoryPath", errors);
        ValidateFixedPath(manifest.RedactionPath, CaptureBundleLayout.RedactionPath, "manifest.redactionPath", errors);
        ValidateFixedPath(manifest.HashesPath, CaptureBundleLayout.HashesPath, "manifest.hashesPath", errors);

        if (manifest.Streams.Count > CaptureSchema.MaximumSources)
        {
            errors.Add(new("manifest.streams", $"At most {CaptureSchema.MaximumSources} streams are allowed."));
        }

        ValidateUniquePaths(manifest.Streams.Select(stream => stream.Path), errors);
        ValidateUniquePaths(manifest.Analysis.Select(analysis => analysis.Path), errors);
        ValidateUniquePaths(manifest.Blobs.Select(blob => blob.Path), errors);
        ValidateUniqueIds(manifest.Streams.Select(stream => stream.SourceId), "manifest.streams", errors);
        ValidateUniqueIds(manifest.Analysis.Select(analysis => analysis.AnalyzerId), "manifest.analysis", errors);
        ValidateUniqueIds(manifest.Blobs.Select(blob => blob.BlobId), "manifest.blobs", errors);

        foreach (CaptureStreamDescriptor stream in manifest.Streams)
        {
            ValidateId(stream.SourceId, "manifest.streams.sourceId", errors);
            ValidateFolderPath(stream.Path, "streams/", ".ndjson", errors);
            if (stream.EventCount < 0)
            {
                errors.Add(new(stream.Path, "Event count cannot be negative."));
            }
        }

        foreach (CaptureAnalysisDescriptor analysis in manifest.Analysis)
        {
            ValidateId(analysis.AnalyzerId, "manifest.analysis.analyzerId", errors);
            ValidateId(analysis.AnalyzerVersion, "manifest.analysis.analyzerVersion", errors);
            ValidateFolderPath(analysis.Path, "analysis/", ".ndjson", errors);
            if (analysis.ResultCount < 0)
            {
                errors.Add(new(analysis.Path, "Result count cannot be negative."));
            }
        }

        foreach (CaptureBlobDescriptor blob in manifest.Blobs)
        {
            ValidateId(blob.BlobId, "manifest.blobs.blobId", errors);
            ValidateFolderPath(blob.Path, "blobs/", null, errors);
            if (blob.Length < 0 || blob.Length > CaptureSchema.MaximumBlobBytes)
            {
                errors.Add(new(blob.Path, $"Blob length must be between 0 and {CaptureSchema.MaximumBlobBytes}."));
            }

            ValidateSha256(blob.Sha256, blob.Path, errors);
        }
    }

    private static void ValidateRecipe(
        ObserveOnlyRecipe recipe,
        ICollection<CaptureValidationError> errors)
    {
        if (recipe.SchemaVersion != CaptureSchema.CurrentVersion)
        {
            errors.Add(new("recipe.schemaVersion", "Unsupported observe-only recipe schema version."));
        }

        ValidateId(recipe.RecipeId, "recipe.recipeId", errors);
        ValidateText(recipe.DisplayName, "recipe.displayName", errors);

        if (recipe.Steps.Count > CaptureSchema.MaximumRecipeSteps)
        {
            errors.Add(new("recipe.steps", $"At most {CaptureSchema.MaximumRecipeSteps} steps are allowed."));
        }

        HashSet<string> stepIds = new(StringComparer.Ordinal);
        foreach (ObservationStep step in recipe.Steps)
        {
            ValidateId(step.StepId, "recipe.steps.stepId", errors);
            ValidateId(step.SourceId, "recipe.steps.sourceId", errors);
            if (!stepIds.Add(step.StepId))
            {
                errors.Add(new(step.StepId, "Recipe step ID is duplicated."));
            }

            if (step.DurationMilliseconds <= 0
                || step.DurationMilliseconds > CaptureSchema.MaximumStepDurationMilliseconds)
            {
                errors.Add(new(step.StepId,
                    $"Observation duration must be between 1 and {CaptureSchema.MaximumStepDurationMilliseconds} milliseconds."));
            }

            if (step.OperatorPrompt is not null)
            {
                ValidateText(step.OperatorPrompt, step.StepId, errors);
            }
        }
    }

    private static void ValidateEvent(
        CaptureStreamEvent captureEvent,
        string sourceId,
        IReadOnlyDictionary<string, ObservationStep> steps,
        ICollection<CaptureValidationError> errors)
    {
        if (captureEvent.SchemaVersion != CaptureSchema.CurrentVersion)
        {
            errors.Add(new(captureEvent.EventId, "Unsupported stream-event schema version."));
        }

        ValidateId(captureEvent.EventId, "event.eventId", errors);
        ValidateId(captureEvent.SourceId, captureEvent.EventId, errors);
        ValidateId(captureEvent.RecipeStepId, captureEvent.EventId, errors);

        if (!string.Equals(captureEvent.SourceId, sourceId, StringComparison.Ordinal))
        {
            errors.Add(new(captureEvent.EventId, "Event source does not match its stream."));
        }

        if (!steps.TryGetValue(captureEvent.RecipeStepId, out ObservationStep? step)
            || !string.Equals(step.SourceId, captureEvent.SourceId, StringComparison.Ordinal))
        {
            errors.Add(new(captureEvent.EventId, "Event does not reference a matching recipe step."));
        }

        if (captureEvent.SourceSequence < 0 || captureEvent.GlobalSequence < 0
            || captureEvent.QpcReceiptTime < 0 || captureEvent.ClockSegment < 0
            || captureEvent.DeviceGeneration < 0)
        {
            errors.Add(new(captureEvent.EventId, "Sequences, time, segment, and generation cannot be negative."));
        }

        if (captureEvent.SourceTime is { } sourceTime
            && (sourceTime.Frequency <= 0 || sourceTime.Value < 0))
        {
            errors.Add(new(captureEvent.EventId, "Source timestamp must have non-negative time and positive frequency."));
        }

        CapturedPayload payload = captureEvent.Payload;
        if (payload.Length < 0 || payload.Length > CaptureSchema.MaximumEventPayloadBytes)
        {
            errors.Add(new(captureEvent.EventId,
                $"Payload length must be between 0 and {CaptureSchema.MaximumEventPayloadBytes}."));
        }

        if (payload.Disposition is PayloadDisposition.Included)
        {
            if (payload.Bytes is null || payload.Bytes.Length != payload.Length)
            {
                errors.Add(new(captureEvent.EventId, "Included payload bytes must match the reported length."));
            }
            else
            {
                string expectedHash = CaptureHashFile.Hash(payload.Bytes);
                if (!string.Equals(payload.Sha256, expectedHash, StringComparison.Ordinal))
                {
                    errors.Add(new(captureEvent.EventId, "Included payload SHA-256 does not match its bytes."));
                }
            }
        }
        else if (payload.Bytes is not null || payload.Sha256 is not null)
        {
            errors.Add(new(captureEvent.EventId,
                "Redacted, absent, or quarantined payloads cannot retain bytes or a content hash."));
        }
    }

    private static void ValidateAnalysis(
        CaptureAnalysisResult result,
        CaptureAnalysisDescriptor descriptor,
        IReadOnlySet<string> eventIds,
        ICollection<CaptureValidationError> errors)
    {
        if (result.SchemaVersion != CaptureSchema.CurrentVersion)
        {
            errors.Add(new(result.ResultId, "Unsupported analysis-result schema version."));
        }

        ValidateId(result.ResultId, "analysis.resultId", errors);
        ValidateText(result.Meaning, result.ResultId, errors);

        if (!string.Equals(result.AnalyzerId, descriptor.AnalyzerId, StringComparison.Ordinal)
            || !string.Equals(result.AnalyzerVersion, descriptor.AnalyzerVersion, StringComparison.Ordinal))
        {
            errors.Add(new(result.ResultId, "Result analyzer identity does not match its stream."));
        }

        if (result.SupportingEventIds.Count == 0)
        {
            errors.Add(new(result.ResultId, "Derived analysis must reference at least one raw event."));
        }

        if (result.Values.Count > CaptureSchema.MaximumAnalysisValues)
        {
            errors.Add(new(result.ResultId,
                $"Analysis may contain at most {CaptureSchema.MaximumAnalysisValues} values."));
        }

        if (result.SupportingEventIds.Count + result.CounterexampleEventIds.Count
            > CaptureSchema.MaximumAnalysisEventReferences)
        {
            errors.Add(new(result.ResultId,
                $"Analysis may reference at most {CaptureSchema.MaximumAnalysisEventReferences} raw events."));
        }

        foreach (CaptureAnalysisValue value in result.Values)
        {
            ValidateId(value.Key, result.ResultId, errors);
            ValidateText(value.Value, result.ResultId, errors);
            if (value.Unit is not null)
            {
                ValidateId(value.Unit, result.ResultId, errors);
            }
        }

        foreach (string limitation in result.Limitations)
        {
            ValidateText(limitation, result.ResultId, errors);
        }

        foreach (string eventId in result.SupportingEventIds.Concat(result.CounterexampleEventIds))
        {
            if (!eventIds.Contains(eventId))
            {
                errors.Add(new(result.ResultId, $"Analysis references unknown event '{eventId}'."));
            }
        }
    }

    private static void ValidateBlob(
        CaptureBlobFile blob,
        IReadOnlyDictionary<string, CaptureBlobDescriptor> descriptors,
        ICollection<CaptureValidationError> errors)
    {
        if (!descriptors.TryGetValue(blob.Descriptor.BlobId, out CaptureBlobDescriptor? manifestBlob)
            || manifestBlob != blob.Descriptor)
        {
            errors.Add(new(blob.Descriptor.BlobId, "Blob descriptor does not match the manifest."));
            return;
        }

        if (blob.Bytes.LongLength != blob.Descriptor.Length)
        {
            errors.Add(new(blob.Descriptor.Path, "Blob bytes do not match the declared length."));
        }

        if (!string.Equals(
                CaptureHashFile.Hash(blob.Bytes),
                blob.Descriptor.Sha256,
                StringComparison.Ordinal))
        {
            errors.Add(new(blob.Descriptor.Path, "Blob SHA-256 does not match its bytes."));
        }
    }

    private static void ValidateRedaction(
        CaptureRedactionManifest redaction,
        ICollection<CaptureValidationError> errors)
    {
        if (redaction.SchemaVersion != CaptureSchema.CurrentVersion)
        {
            errors.Add(new("redaction.schemaVersion", "Unsupported redaction schema version."));
        }

        if (!redaction.DefaultRedactionApplied)
        {
            errors.Add(new("redaction.defaultRedactionApplied",
                "A shareable capture must pass the default redaction stage."));
        }

        foreach (QuarantinedCaptureArtifact artifact in redaction.Quarantined)
        {
            ValidateText(artifact.Name, "redaction.quarantined.name", errors);
            ValidateText(artifact.Reason, "redaction.quarantined.reason", errors);
            if (artifact.Length < 0)
            {
                errors.Add(new(artifact.Name, "Quarantined artifact length cannot be negative."));
            }
        }
    }

    private static void ValidateId(
        string? value,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > CaptureSchema.MaximumIdentifierLength)
        {
            errors.Add(new(path,
                $"Identifier must contain 1 to {CaptureSchema.MaximumIdentifierLength} characters."));
        }
    }

    private static void ValidateText(
        string? value,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > CaptureSchema.MaximumTextLength)
        {
            errors.Add(new(path,
                $"Text must contain 1 to {CaptureSchema.MaximumTextLength} characters."));
        }
    }

    private static void ValidateFixedPath(
        string path,
        string expected,
        string property,
        ICollection<CaptureValidationError> errors)
    {
        if (!string.Equals(path, expected, StringComparison.Ordinal))
        {
            errors.Add(new(property, $"Path must be '{expected}'."));
        }
    }

    private static void ValidateFolderPath(
        string path,
        string prefix,
        string? suffix,
        ICollection<CaptureValidationError> errors)
    {
        if (!CaptureBundleLayout.IsSafeRelativePath(path)
            || !path.StartsWith(prefix, StringComparison.Ordinal)
            || suffix is not null && !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            errors.Add(new(path, $"Path must be a canonical entry below '{prefix}'."));
        }
    }

    private static void ValidateUniquePaths(
        IEnumerable<string> paths,
        ICollection<CaptureValidationError> errors)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (!seen.Add(path))
            {
                errors.Add(new(path, "Archive path is duplicated."));
            }
        }
    }

    private static void ValidateUniqueIds(
        IEnumerable<string> ids,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (!seen.Add(id))
            {
                errors.Add(new(path, $"Identifier '{id}' is duplicated."));
            }
        }
    }

    private static void ValidateSha256(
        string hash,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            errors.Add(new(path, "SHA-256 must be 64 lowercase hexadecimal characters."));
        }
    }
}

/// <summary>Canonical encoding for the bundle's <c>hashes.sha256</c> file.</summary>
internal static class CaptureHashFile
{
    /// <summary>Computes a lowercase hexadecimal SHA-256 digest.</summary>
    /// <param name="content">Exact content bytes.</param>
    /// <returns>The digest.</returns>
    public static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>Serializes entries in path order using the conventional two-space separator.</summary>
    /// <param name="entries">Entries to serialize.</param>
    /// <returns>UTF-8 text with one entry per line and a final newline.</returns>
    public static string Serialize(IReadOnlyList<CaptureHashEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        StringBuilder builder = new();
        foreach (CaptureHashEntry entry in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            builder.Append(entry.Sha256).Append("  ").Append(entry.Path).Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary>Writes a deterministic sanitized capture archive without executing its recipe.</summary>
internal static class CaptureBundleWriter
{
    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Newline = [(byte)'\n'];

    /// <summary>
    /// Writes one validated <c>.wsgmcap</c> archive and leaves the destination stream open.
    /// </summary>
    /// <param name="destination">Seekable output stream.</param>
    /// <param name="bundle">Already-sanitized capture values.</param>
    /// <param name="cancellationToken">Cancels validation, serialization, or archive writing.</param>
    /// <exception cref="InvalidDataException">The bundle violates its schema.</exception>
    public static void Write(
        Stream destination,
        SanitizedCaptureBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(bundle);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<CaptureValidationError> errors = CaptureSchemaValidator.Validate(
            bundle,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                errors.Select(error => $"{error.Path}: {error.Message}")));
        }

        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException("Capture destination must be writable and seekable.", nameof(destination));
        }

        SortedDictionary<string, Action<Stream, IncrementalHash, CancellationToken>> entries =
            new(StringComparer.Ordinal)
            {
                [CaptureBundleLayout.ManifestPath] = (output, hash, token) => WriteJsonFile(
                    output,
                    hash,
                    bundle.Manifest,
                    DeviceLabJsonContext.Default.ShareableCaptureManifest,
                    token),
                [CaptureBundleLayout.RecipePath] = (output, hash, token) => WriteJsonFile(
                    output,
                    hash,
                    bundle.Recipe,
                    DeviceLabJsonContext.Default.ObserveOnlyRecipe,
                    token),
                [CaptureBundleLayout.InventoryPath] = (output, hash, token) => WriteJsonFile(
                    output,
                    hash,
                    bundle.Inventory,
                    DeviceLabJsonContext.Default.MachineInventory,
                    token),
                [CaptureBundleLayout.RedactionPath] = (output, hash, token) => WriteJsonFile(
                    output,
                    hash,
                    bundle.Redaction,
                    DeviceLabJsonContext.Default.CaptureRedactionManifest,
                    token),
            };

        foreach (CaptureStreamFile stream in bundle.Streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureStreamDescriptor descriptor = bundle.Manifest.Streams.Single(item =>
                string.Equals(item.SourceId, stream.SourceId, StringComparison.Ordinal));
            entries[descriptor.Path] = (output, hash, token) => WriteNdjson(
                output,
                hash,
                stream.Events,
                captureEvent => JsonSerializer.SerializeToUtf8Bytes(
                    captureEvent,
                    DeviceLabCompactJson.CaptureStreamEvent),
                token);
        }

        foreach (CaptureAnalysisFile analysis in bundle.Analysis)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureAnalysisDescriptor descriptor = bundle.Manifest.Analysis.Single(item =>
                string.Equals(item.AnalyzerId, analysis.AnalyzerId, StringComparison.Ordinal));
            entries[descriptor.Path] = (output, hash, token) => WriteNdjson(
                output,
                hash,
                analysis.Results,
                result => JsonSerializer.SerializeToUtf8Bytes(
                    result,
                    DeviceLabCompactJson.CaptureAnalysisResult),
                token);
        }

        foreach (CaptureBlobFile blob in bundle.Blobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries[blob.Descriptor.Path] = (output, hash, token) => WriteBytes(
                output,
                hash,
                blob.Bytes,
                token);
        }

        cancellationToken.ThrowIfCancellationRequested();
        destination.Position = 0;
        destination.SetLength(0);

        using ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        List<CaptureHashEntry> hashes = [];
        foreach ((string path, Action<Stream, IncrementalHash, CancellationToken> write) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            entry.LastWriteTime = DeterministicTimestamp;
            entry.ExternalAttributes = 0;
            using Stream output = entry.Open();
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            write(output, hash, cancellationToken);
            hashes.Add(new CaptureHashEntry(
                path,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }

        byte[] hashFile = Encoding.UTF8.GetBytes(CaptureHashFile.Serialize(hashes));
        ZipArchiveEntry hashesEntry = archive.CreateEntry(
            CaptureBundleLayout.HashesPath,
            CompressionLevel.NoCompression);
        hashesEntry.LastWriteTime = DeterministicTimestamp;
        hashesEntry.ExternalAttributes = 0;
        using Stream hashesOutput = hashesEntry.Open();
        WriteBytes(hashesOutput, hash: null, hashFile, cancellationToken);
    }

    private static void WriteJsonFile<T>(
        Stream output,
        IncrementalHash hash,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        WriteBytes(output, hash, json, cancellationToken);
        WriteBytes(output, hash, Newline, cancellationToken);
    }

    private static void WriteNdjson<T>(
        Stream output,
        IncrementalHash hash,
        IReadOnlyList<T> values,
        Func<T, byte[]> serialize,
        CancellationToken cancellationToken)
    {
        foreach (T value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] json = serialize(value);
            WriteBytes(output, hash, json, cancellationToken);
            WriteBytes(output, hash, Newline, cancellationToken);
        }
    }

    private static void WriteBytes(
        Stream output,
        IncrementalHash? hash,
        ReadOnlySpan<byte> content,
        CancellationToken cancellationToken)
    {
        const int MaximumChunkBytes = 64 * 1024;
        int offset = 0;
        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(MaximumChunkBytes, content.Length - offset);
            ReadOnlySpan<byte> chunk = content.Slice(offset, length);
            hash?.AppendData(chunk);
            output.Write(chunk);
            offset += length;
        }
    }
}
