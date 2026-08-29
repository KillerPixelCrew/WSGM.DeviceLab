using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Capture;

/// <summary>Closed outcomes for the shared observe-only capture workflow.</summary>
internal enum ObserveOnlyCaptureStatus
{
    /// <summary>The private session is complete and a sanitized export is ready for review.</summary>
    ReadyForExport,

    /// <summary>The operator or local-interactive safety gate refused the run.</summary>
    Refused,

    /// <summary>The imported recipe was absent, oversized, malformed, or outside the closed schema.</summary>
    InvalidRecipe,

    /// <summary>The explicit output path was unsafe.</summary>
    InvalidOutput,

    /// <summary>Inventory or passive observation could not complete.</summary>
    CaptureFailed,

    /// <summary>The private session could not be persisted.</summary>
    WriteFailed,
}

/// <summary>Inputs for one explicitly approved observe-only capture preparation.</summary>
internal sealed record ObserveOnlyCaptureRequest
{
    /// <summary>Imported inert recipe JSON.</summary>
    public required string RecipePath { get; init; }

    /// <summary>Explicit safe directory receiving physically separated private and export children.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>SHA-256 of the exact recipe bytes the operator reviewed.</summary>
    public required string ReviewedRecipeSha256 { get; init; }

    /// <summary>Whether the caller is a local interactive operator session.</summary>
    public required bool IsLocalInteractive { get; init; }

    /// <summary>Whether the operator reviewed and approved the observation scope.</summary>
    public required bool ObservationScopeConfirmed { get; init; }
}

/// <summary>Bounded inert recipe detail shown before an operator approves observation.</summary>
internal sealed record ObserveOnlyRecipeReview
{
    /// <summary>SHA-256 that binds later approval to these exact bytes.</summary>
    public required string RecipeSha256 { get; init; }

    /// <summary>Stable recipe identifier.</summary>
    public required string RecipeId { get; init; }

    /// <summary>Operator-facing recipe name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Closed observation steps and their prompts.</summary>
    public IReadOnlyList<ObservationStep> Steps { get; init; } = [];

}

/// <summary>A prepared capture and its not-yet-written sanitized export.</summary>
internal sealed record CaptureExportPlan
{
    /// <summary>Private working directory already persisted locally.</summary>
    public required string PrivateWorkingDirectory { get; init; }

    /// <summary>Proposed new shareable bundle path.</summary>
    public required string ShareableOutputPath { get; init; }

    /// <summary>Sanitized bundle retained until the operator accepts its preview.</summary>
    public required SanitizedCaptureBundle Bundle { get; init; }

    /// <summary>Privacy replacements and quarantined artifacts visible before export.</summary>
    public CaptureRedactionManifest Redaction => Bundle.Redaction;

    /// <summary>Observation prompts retained for operator review.</summary>
    public IReadOnlyList<string> Prompts { get; init; } = [];

    /// <summary>Honest platform limits attached to the prepared capture.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Result of preparing one capture without automatically exporting it.</summary>
internal sealed record ObserveOnlyCaptureResult
{
    /// <summary>Closed workflow outcome.</summary>
    public required ObserveOnlyCaptureStatus Status { get; init; }

    /// <summary>Prepared export when capture succeeded.</summary>
    public CaptureExportPlan? ExportPlan { get; init; }

    /// <summary>Bounded operator-facing failure detail.</summary>
    public string? Error { get; init; }
}

/// <summary>Result of the separate sanitized-export approval step.</summary>
internal sealed record CaptureExportResult
{
    /// <summary>Whether a new shareable bundle was written.</summary>
    public required bool Exported { get; init; }

    /// <summary>Absolute path of the completed bundle.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Reason export was refused or failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Prepares a private observe-only session and exports its sanitized projection only after a second
/// explicit approval.
/// </summary>
/// <remarks>
/// Recipe data selects only closed observation kinds. The only live source registered here records
/// the inventory snapshot that this workflow collected itself; every other source is represented as
/// unavailable until a separately reviewed local observer is compiled into Device Lab. Imported
/// recipe data therefore cannot open a device or authorize a write.
/// </remarks>
internal static class ObserveOnlyCaptureWorkflow
{
    private const int MaximumRecipeBytes = 2 * 1024 * 1024;

    /// <summary>Reads and validates one inert recipe for operator scope review.</summary>
    /// <param name="recipePath">Imported recipe JSON.</param>
    /// <param name="cancellationToken">Cancels bounded recipe validation.</param>
    /// <returns>Closed steps plus a hash that expires approval if the file changes.</returns>
    public static ObserveOnlyRecipeReview Review(
        string recipePath,
        CancellationToken cancellationToken = default)
    {
        (ObserveOnlyRecipe recipe, string hash) = ReadRecipe(recipePath, cancellationToken);
        return new ObserveOnlyRecipeReview
        {
            RecipeSha256 = hash,
            RecipeId = recipe.RecipeId,
            DisplayName = recipe.DisplayName,
            Steps = recipe.Steps,
        };
    }

    /// <summary>Prepares and persists a private capture, leaving shareable output unwritten.</summary>
    /// <param name="request">Explicit recipe, path, and operator gates.</param>
    /// <param name="capturedAt">Session timestamp.</param>
    /// <param name="repositoryRoot">Detected repository root, when running from a checkout.</param>
    /// <param name="cancellationToken">Whole-session cancellation.</param>
    /// <returns>A privacy-preview plan or a closed failure.</returns>
    public static async Task<ObserveOnlyCaptureResult> PrepareAsync(
        ObserveOnlyCaptureRequest request,
        DateTimeOffset capturedAt,
        string? repositoryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsLocalInteractive || !request.ObservationScopeConfirmed)
        {
            return Failure(
                ObserveOnlyCaptureStatus.Refused,
                "A local interactive operator must review and approve the observation scope.");
        }

        DeviceLabPathBoundaries boundaries = DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot);
        DeviceLabOutputPathDecision rootDecision = DeviceLabOutputPathPolicy.Evaluate(
            request.OutputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!rootDecision.IsAllowed || rootDecision.FullPath is null)
        {
            return Failure(ObserveOnlyCaptureStatus.InvalidOutput, rootDecision.Reason);
        }

        ObserveOnlyRecipe recipe;
        try
        {
            (recipe, string hash) = ReadRecipe(request.RecipePath, cancellationToken);
            if (!string.Equals(hash, request.ReviewedRecipeSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    ObserveOnlyCaptureStatus.Refused,
                    "The recipe changed after scope review; review the exact current bytes again.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException)
        {
            return Failure(ObserveOnlyCaptureStatus.InvalidRecipe, exception.Message);
        }

        string captureId = $"capture-{capturedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        string privateDirectory = Path.Combine(rootDecision.FullPath, "private", captureId);
        string shareablePath = Path.Combine(rootDecision.FullPath, "shareable", $"{captureId}.wsgmcap");
        DeviceLabOutputPathDecision privateDecision = DeviceLabOutputPathPolicy.Evaluate(
            privateDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        DeviceLabOutputPathDecision exportDecision = DeviceLabOutputPathPolicy.Evaluate(
            shareablePath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!privateDecision.IsAllowed || !exportDecision.IsAllowed)
        {
            return Failure(
                ObserveOnlyCaptureStatus.InvalidOutput,
                privateDecision.Reason ?? exportDecision.Reason);
        }

        MachineInventory privateInventory;
        PassiveCaptureTimeline timeline = new(new QpcCaptureReceiptClock());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            privateInventory = WindowsInventoryCollector.Collect(
                capturedAt,
                KnownInventoryClasses,
                cancellationToken);
            IPassiveCaptureSource[] sources =
            [
                .. recipe.Steps
                    .Select(step => step.SourceId)
                    .Distinct(StringComparer.Ordinal)
                    .Select(sourceId => new ClosedObserveOnlyCaptureSource(sourceId)),
            ];
            PassiveCaptureCoordinator coordinator = new(sources, timeline);
            await coordinator.RunAsync(recipe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            return Failure(ObserveOnlyCaptureStatus.CaptureFailed, exception.GetType().Name);
        }

        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<CaptureStreamEvent> events = timeline.SnapshotByReceipt();
        IReadOnlyList<CaptureStreamFile> streams = [.. events
            .GroupBy(captureEvent => captureEvent.SourceId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CaptureStreamFile
            {
                SourceId = group.Key,
                Events = [.. group.OrderBy(captureEvent => captureEvent.SourceSequence)],
            })];
        MachineInventory shareableInventory = InventoryRedaction.ToShareable(
            privateInventory,
            out IReadOnlyList<RedactionSummary> inventoryReplacements);
        CaptureRedactor recipeRedactor = new();
        ObserveOnlyRecipe shareableRecipe = recipe with
        {
            RecipeId = recipeRedactor.Redact(recipe.RecipeId),
            DisplayName = recipeRedactor.Redact(recipe.DisplayName),
            Steps = [.. recipe.Steps.Select(step => step with
            {
                StepId = recipeRedactor.Redact(step.StepId),
                SourceId = recipeRedactor.Redact(step.SourceId),
                OperatorPrompt = step.OperatorPrompt is null
                    ? null
                    : recipeRedactor.Redact(step.OperatorPrompt),
            })],
        };
        IReadOnlyList<CaptureStreamFile> shareableStreams = [.. streams.Select(stream => new CaptureStreamFile
        {
            SourceId = recipeRedactor.Redact(stream.SourceId),
            Events = [.. stream.Events.Select(captureEvent => captureEvent with
            {
                SourceId = recipeRedactor.Redact(captureEvent.SourceId),
                RecipeStepId = recipeRedactor.Redact(captureEvent.RecipeStepId),
            })],
        })];
        IReadOnlyList<RedactionSummary> replacements = MergeRedactions(
            inventoryReplacements,
            recipeRedactor.Summarize());
        CaptureRedactionManifest redaction = new()
        {
            SchemaVersion = CaptureSchema.CurrentVersion,
            DefaultRedactionApplied = true,
            Replacements = replacements,
            Quarantined = [],
        };
        SanitizedCaptureBundle bundle = CreateShareableBundle(
            captureId,
            capturedAt,
            completedAt,
            timeline.QpcFrequency,
            shareableRecipe,
            shareableInventory,
            shareableStreams,
            redaction);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistPrivateSession(
                privateDirectory,
                captureId,
                capturedAt,
                completedAt,
                timeline.QpcFrequency,
                recipe,
                privateInventory,
                streams,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return Failure(ObserveOnlyCaptureStatus.WriteFailed, exception.GetType().Name);
        }

        return new ObserveOnlyCaptureResult
        {
            Status = ObserveOnlyCaptureStatus.ReadyForExport,
            ExportPlan = new CaptureExportPlan
            {
                PrivateWorkingDirectory = privateDirectory,
                ShareableOutputPath = shareablePath,
                Bundle = bundle,
                Prompts = [.. recipe.Steps
                    .Where(step => !string.IsNullOrWhiteSpace(step.OperatorPrompt))
                    .Select(step => step.OperatorPrompt!)],
                Limitations = PassiveCaptureLimitations.All,
            },
        };
    }

    /// <summary>Writes the sanitized bundle only after the operator accepts its redaction preview.</summary>
    /// <param name="plan">Prepared in-memory export.</param>
    /// <param name="exportPreviewConfirmed">Whether the redaction preview was explicitly accepted.</param>
    /// <param name="repositoryRoot">Detected repository root, when running from a checkout.</param>
    /// <param name="cancellationToken">Cancels bundle generation before atomic publication.</param>
    /// <returns>Export result; refusal is a value and never writes a partial target.</returns>
    public static CaptureExportResult Export(
        CaptureExportPlan plan,
        bool exportPreviewConfirmed,
        string? repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        if (!exportPreviewConfirmed)
        {
            return new CaptureExportResult
            {
                Exported = false,
                Error = "The redaction and quarantine preview must be accepted before export.",
            };
        }

        DeviceLabPathBoundaries boundaries = DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot);
        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            plan.ShareableOutputPath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            return new CaptureExportResult { Exported = false, Error = decision.Reason };
        }

        string directory = Path.GetDirectoryName(decision.FullPath)!;
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(decision.FullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                CaptureBundleWriter.Write(output, plan.Bundle, cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, decision.FullPath);
            return new CaptureExportResult { Exported = true, OutputPath = decision.FullPath };
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidDataException)
        {
            TryDelete(temporaryPath);
            return new CaptureExportResult { Exported = false, Error = exception.Message };
        }
    }

    private static readonly (string Namespace, string ClassName)[] KnownInventoryClasses =
    [
        ("root\\WMI", "MSI_ACPI"),
        ("root\\WMI", "MSI_Event"),
        ("root\\WMI", "BatteryStatus"),
        ("root\\WMI", "MSAcpi_ThermalZoneTemperature"),
    ];

    private static (ObserveOnlyRecipe Recipe, string Sha256) ReadRecipe(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumRecipeBytes)
        {
            throw new InvalidDataException("Recipe is absent, empty, or oversized.");
        }

        byte[] bytes = new byte[(int)file.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = file.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Recipe ended before its inspected length.");
            }
            offset += read;
        }
        ObserveOnlyRecipe? recipe = JsonSerializer.Deserialize(
            bytes,
            DeviceLabJsonContext.Default.ObserveOnlyRecipe) ?? throw new InvalidDataException("Recipe could not be decoded.");
        IReadOnlyList<CaptureValidationError> errors = CaptureSchemaValidator.Validate(recipe);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                errors.Select(error => $"{error.Path}: {error.Message}")));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (recipe, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static SanitizedCaptureBundle CreateShareableBundle(
        string captureId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        long qpcFrequency,
        ObserveOnlyRecipe recipe,
        MachineInventory inventory,
        IReadOnlyList<CaptureStreamFile> streams,
        CaptureRedactionManifest redaction)
    {
        CaptureStreamDescriptor[] descriptors = [.. streams.Select((stream, index) => new CaptureStreamDescriptor
        {
            SourceId = stream.SourceId,
            Path = $"streams/{index:D3}-{SafeName(stream.SourceId)}.ndjson",
            EventCount = stream.Events.Count,
        })];
        return new SanitizedCaptureBundle
        {
            Manifest = new ShareableCaptureManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                BundleId = $"shareable-{captureId}",
                ToolVersion = typeof(ObserveOnlyCaptureWorkflow).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                QpcFrequency = qpcFrequency,
                Streams = descriptors,
                Analysis = [],
                Blobs = [],
            },
            Recipe = recipe,
            Inventory = inventory,
            Streams = streams,
            Analysis = [],
            Blobs = [],
            Redaction = redaction,
        };
    }

    private static IReadOnlyList<RedactionSummary> MergeRedactions(
        IReadOnlyList<RedactionSummary> left,
        IReadOnlyList<RedactionSummary> right) => [.. left
        .Concat(right)
        .GroupBy(summary => summary.Category)
        .OrderBy(group => group.Key)
        .Select(group => new RedactionSummary(group.Key, group.Sum(summary => summary.Occurrences)))];

    private static void PersistPrivateSession(
        string directory,
        string captureId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        long qpcFrequency,
        ObserveOnlyRecipe recipe,
        MachineInventory inventory,
        IReadOnlyList<CaptureStreamFile> streams,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string streamsDirectory = Path.Combine(directory, "streams");
        Directory.CreateDirectory(streamsDirectory);
        CaptureStreamDescriptor[] descriptors = [.. streams.Select((stream, index) => new CaptureStreamDescriptor
        {
            SourceId = stream.SourceId,
            Path = $"streams/{index:D3}-{SafeName(stream.SourceId)}.ndjson",
            EventCount = stream.Events.Count,
        })];
        PrivateCaptureManifest manifest = new()
        {
            SchemaVersion = CaptureSchema.CurrentVersion,
            CaptureId = captureId,
            ToolVersion = typeof(ObserveOnlyCaptureWorkflow).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            StartedAt = startedAt,
            CompletedAt = completedAt,
            QpcFrequency = qpcFrequency,
            RecipePath = CaptureBundleLayout.RecipePath,
            InventoryPath = CaptureBundleLayout.InventoryPath,
            Streams = descriptors,
            Analysis = [],
            Blobs = [],
        };

        WriteNew(Path.Combine(directory, CaptureBundleLayout.RecipePath), DeviceLabJson.Serialize(recipe));
        cancellationToken.ThrowIfCancellationRequested();
        WriteNew(Path.Combine(directory, CaptureBundleLayout.InventoryPath), DeviceLabJson.Serialize(inventory));
        for (int index = 0; index < streams.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureStreamFile stream = streams[index];
            StringBuilder ndjson = new();
            foreach (CaptureStreamEvent captureEvent in stream.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ndjson.Append(JsonSerializer.Serialize(
                    captureEvent,
                    DeviceLabCompactJson.CaptureStreamEvent)).Append('\n');
            }

            WriteNew(Path.Combine(directory, descriptors[index].Path.Replace('/', Path.DirectorySeparatorChar)), ndjson.ToString());
        }

        // Completion is published last. A process death can leave reviewable raw files, but never a
        // manifest that falsely labels a partial private session complete.
        cancellationToken.ThrowIfCancellationRequested();
        WriteNew(Path.Combine(directory, "private-manifest.json"), JsonSerializer.Serialize(
            manifest,
            DeviceLabJsonContext.Default.PrivateCaptureManifest));
    }

    private static void WriteNew(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
        writer.WriteLine();
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string SafeName(string sourceId)
    {
        StringBuilder name = new();
        foreach (char character in sourceId.ToLowerInvariant())
        {
            name.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return name.Length == 0 ? "source" : name.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed temporary-file cleanup is reported by the next new-file path check.
        }
    }

    private static ObserveOnlyCaptureResult Failure(ObserveOnlyCaptureStatus status, string? error) => new()
    {
        Status = status,
        Error = error ?? "The observe-only capture workflow could not complete.",
    };

    private sealed class ClosedObserveOnlyCaptureSource(string sourceId) : IPassiveCaptureSource
    {
        private long _sequence;

        public string SourceId { get; } = sourceId;

        public Task ObserveAsync(
            ObservationStep step,
            Func<PassiveObservation, ValueTask> emit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sequence = Interlocked.Increment(ref _sequence);
            if (step.Kind is not ObservationStepKind.InventorySnapshot)
            {
                return emit(new PassiveObservation
                {
                    SourceId = SourceId,
                    RecipeStepId = step.StepId,
                    SourceSequence = sequence,
                    DeviceGeneration = 0,
                    Payload = new CapturedPayload { Length = 0, Disposition = PayloadDisposition.NotCaptured },
                    Access = EventAccessState.Unavailable,
                }).AsTask();
            }

            byte[] payload = "inventory-snapshot-recorded"u8.ToArray();
            return emit(new PassiveObservation
            {
                SourceId = SourceId,
                RecipeStepId = step.StepId,
                SourceSequence = sequence,
                DeviceGeneration = 0,
                Payload = new CapturedPayload
                {
                    Length = payload.Length,
                    Disposition = PayloadDisposition.Included,
                    Bytes = payload,
                    Sha256 = CaptureHashFile.Hash(payload),
                },
            }).AsTask();
        }
    }
}
