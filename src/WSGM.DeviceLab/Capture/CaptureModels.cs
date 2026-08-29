using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Capture;

/// <summary>One current version and hard bounds for Device Lab capture artifacts.</summary>
internal static class CaptureSchema
{
    /// <summary>Current version shared by the complete capture bundle.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Largest identifier accepted in a capture.</summary>
    public const int MaximumIdentifierLength = 128;

    /// <summary>Largest operator-facing prompt or limitation.</summary>
    public const int MaximumTextLength = 2048;

    /// <summary>Largest canonical path stored in a capture archive.</summary>
    public const int MaximumArchivePathLength = 512;

    /// <summary>Largest payload embedded directly in one stream event.</summary>
    public const int MaximumEventPayloadBytes = 1024 * 1024;

    /// <summary>Largest individual blob admitted to a shareable capture.</summary>
    public const long MaximumBlobBytes = 64L * 1024 * 1024;

    /// <summary>Largest total uncompressed shareable capture.</summary>
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;

    /// <summary>Largest number of entries accepted in one shareable archive.</summary>
    public const int MaximumArchiveEntries = 4096;

    /// <summary>Maximum number of observation sources in one capture.</summary>
    public const int MaximumSources = 128;

    /// <summary>Maximum number of steps in one observe-only recipe.</summary>
    public const int MaximumRecipeSteps = 2048;

    /// <summary>Longest single passive observation step.</summary>
    public const int MaximumStepDurationMilliseconds = 60 * 60 * 1000;

    /// <summary>Maximum number of derived values in one analysis result.</summary>
    public const int MaximumAnalysisValues = 1024;

    /// <summary>Maximum number of raw-event references in one analysis result.</summary>
    public const int MaximumAnalysisEventReferences = 8192;
}

/// <summary>The privacy boundary represented by a capture manifest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapturePrivacy>))]
internal enum CapturePrivacy
{
    /// <summary>Unredacted local capture that must never be exported as a <c>.wsgmcap</c>.</summary>
    PrivateWorking,

    /// <summary>Redacted capture explicitly prepared for sharing.</summary>
    ShareableSanitized,
}

/// <summary>
/// Manifest for an unredacted working session kept separately from shareable bundles.
/// </summary>
internal sealed record PrivateCaptureManifest
{
    /// <summary>Schema version of this manifest.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable identifier for this local capture session.</summary>
    public required string CaptureId { get; init; }

    /// <summary>Tool build that produced the capture.</summary>
    public required string ToolVersion { get; init; }

    /// <summary>Explicit private-working marker.</summary>
    public CapturePrivacy Privacy { get; init; } = CapturePrivacy.PrivateWorking;

    /// <summary>UTC time at which capture began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC time at which capture ended.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>QueryPerformanceCounter frequency used by every event in this capture.</summary>
    public required long QpcFrequency { get; init; }

    /// <summary>Recipe recording the steps that ran.</summary>
    public required string RecipePath { get; init; }

    /// <summary>Unredacted inventory for the local machine.</summary>
    public required string InventoryPath { get; init; }

    /// <summary>Event streams in the working session.</summary>
    public IReadOnlyList<CaptureStreamDescriptor> Streams { get; init; } = [];

    /// <summary>Derived-analysis streams kept apart from raw observations.</summary>
    public IReadOnlyList<CaptureAnalysisDescriptor> Analysis { get; init; } = [];

    /// <summary>Raw or derived blobs in the private working directory.</summary>
    public IReadOnlyList<CaptureBlobDescriptor> Blobs { get; init; } = [];
}

/// <summary>Manifest at the root of a sanitized, shareable <c>.wsgmcap</c> bundle.</summary>
internal sealed record ShareableCaptureManifest
{
    /// <summary>Schema version of this manifest.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Identifier created for the sanitized bundle, not copied from a machine identifier.</summary>
    public required string BundleId { get; init; }

    /// <summary>Tool build that produced the sanitized bundle.</summary>
    public required string ToolVersion { get; init; }

    /// <summary>Explicit marker that this manifest describes the shareable projection.</summary>
    public CapturePrivacy Privacy { get; init; } = CapturePrivacy.ShareableSanitized;

    /// <summary>UTC time at which observation began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC time at which observation ended.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>QueryPerformanceCounter frequency used by every event in this bundle.</summary>
    public required long QpcFrequency { get; init; }

    /// <summary>Path of the inert observe-only recipe.</summary>
    public string RecipePath { get; init; } = CaptureBundleLayout.RecipePath;

    /// <summary>Path of the sanitized inventory.</summary>
    public string InventoryPath { get; init; } = CaptureBundleLayout.InventoryPath;

    /// <summary>Raw event streams included in the bundle.</summary>
    public IReadOnlyList<CaptureStreamDescriptor> Streams { get; init; } = [];

    /// <summary>Derived-analysis streams included in the bundle.</summary>
    public IReadOnlyList<CaptureAnalysisDescriptor> Analysis { get; init; } = [];

    /// <summary>Blobs included in the bundle.</summary>
    public IReadOnlyList<CaptureBlobDescriptor> Blobs { get; init; } = [];

    /// <summary>Path of the redaction report.</summary>
    public string RedactionPath { get; init; } = CaptureBundleLayout.RedactionPath;

    /// <summary>Path of the content-hash manifest.</summary>
    public string HashesPath { get; init; } = CaptureBundleLayout.HashesPath;
}

/// <summary>One raw-event stream and its archive path.</summary>
internal sealed record CaptureStreamDescriptor
{
    /// <summary>Stable source identifier carried by every event in the stream.</summary>
    public required string SourceId { get; init; }

    /// <summary>Relative archive path under <c>streams/</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Number of newline-delimited events in the stream.</summary>
    public required long EventCount { get; init; }
}

/// <summary>One derived-analysis stream and its archive path.</summary>
internal sealed record CaptureAnalysisDescriptor
{
    /// <summary>Stable analyzer identifier.</summary>
    public required string AnalyzerId { get; init; }

    /// <summary>Exact analyzer version.</summary>
    public required string AnalyzerVersion { get; init; }

    /// <summary>Relative archive path under <c>analysis/</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Number of newline-delimited results in the stream.</summary>
    public required long ResultCount { get; init; }
}

/// <summary>One blob that is present in a capture.</summary>
internal sealed record CaptureBlobDescriptor
{
    /// <summary>Stable identifier used by events and analyses.</summary>
    public required string BlobId { get; init; }

    /// <summary>Relative archive path under <c>blobs/</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Media type, or <c>application/octet-stream</c> when unknown.</summary>
    public required string MediaType { get; init; }

    /// <summary>Exact number of bytes.</summary>
    public required long Length { get; init; }

    /// <summary>Lowercase hexadecimal SHA-256 digest.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>A closed record of the observe-only steps a capture performs.</summary>
internal sealed record ObserveOnlyRecipe
{
    /// <summary>Schema version of this recipe.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable recipe identifier.</summary>
    public required string RecipeId { get; init; }

    /// <summary>Human-readable recipe name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Ordered observation steps.</summary>
    public IReadOnlyList<ObservationStep> Steps { get; init; } = [];
}

/// <summary>One bounded observation or operator marker in a capture recipe.</summary>
internal sealed record ObservationStep
{
    /// <summary>Stable step identifier carried by resulting events.</summary>
    public required string StepId { get; init; }

    /// <summary>Source expected to emit events for this step.</summary>
    public required string SourceId { get; init; }

    /// <summary>Closed observe-only operation.</summary>
    public required ObservationStepKind Kind { get; init; }

    /// <summary>Optional operator instruction, never a command sent to hardware.</summary>
    public string? OperatorPrompt { get; init; }

    /// <summary>Maximum observation duration, in milliseconds.</summary>
    public required int DurationMilliseconds { get; init; }
}

/// <summary>Closed vocabulary of passive capture operations.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ObservationStepKind>))]
internal enum ObservationStepKind
{
    /// <summary>Record the read-only machine inventory.</summary>
    InventorySnapshot,

    /// <summary>Observe Plug and Play arrival and removal.</summary>
    PnpEvents,

    /// <summary>Observe HID input reports without opening an output path.</summary>
    HidInput,

    /// <summary>Observe device-identified Raw Input.</summary>
    RawInput,

    /// <summary>Observe low-level keyboard or mouse hook events with explicit device-ambiguity limits.</summary>
    LowLevelHook,

    /// <summary>Observe WMI device events.</summary>
    WmiEvents,

    /// <summary>Observe WMI Activity metadata.</summary>
    WmiActivity,

    /// <summary>Observe state through a controller API.</summary>
    ControllerState,

    /// <summary>Observe XInput state.</summary>
    XInputState,

    /// <summary>Observe DirectInput state.</summary>
    DirectInputState,

    /// <summary>Observe SDL controller state.</summary>
    SdlState,

    /// <summary>Observe a sensor stream.</summary>
    SensorState,

    /// <summary>Observe an already identified serial stream without transmitting data.</summary>
    SerialState,

    /// <summary>Observe relevant process lifecycle.</summary>
    ProcessEvents,

    /// <summary>Observe an already-running plugin operation.</summary>
    PluginEvents,

    /// <summary>Record an operator-supplied timeline marker.</summary>
    OperatorMarker,

    /// <summary>Observe already-available telemetry or readback.</summary>
    TelemetryReadback,
}

/// <summary>One timestamp supplied by an observed source.</summary>
internal sealed record CaptureSourceTimestamp
{
    /// <summary>Raw source-clock value.</summary>
    public required long Value { get; init; }

    /// <summary>Ticks per second for the source clock.</summary>
    public required long Frequency { get; init; }

    /// <summary>Identity of the clock domain.</summary>
    public required string ClockId { get; init; }
}

/// <summary>Exact event payload, or an explicit reason it is absent.</summary>
internal sealed record CapturedPayload
{
    /// <summary>Exact length reported by the source, including when bytes are omitted.</summary>
    public required int Length { get; init; }

    /// <summary>Whether exact bytes are present and why not when they are absent.</summary>
    public required PayloadDisposition Disposition { get; init; }

    /// <summary>Exact payload bytes when sharing is permitted.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Lowercase hexadecimal SHA-256 digest when exact bytes are present.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>Why an event payload is present or absent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PayloadDisposition>))]
internal enum PayloadDisposition
{
    /// <summary>Exact bytes are included.</summary>
    Included,

    /// <summary>Bytes were removed by the sanitization pass.</summary>
    Redacted,

    /// <summary>The source reported a length but did not expose the bytes.</summary>
    NotCaptured,

    /// <summary>Opaque bytes were excluded because they could not be safely rewritten.</summary>
    Quarantined,
}

/// <summary>One raw observation in a QPC-aligned capture stream.</summary>
internal sealed record CaptureStreamEvent
{
    /// <summary>Schema version of this event.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable identifier used by derived analyses.</summary>
    public required string EventId { get; init; }

    /// <summary>Observer that emitted the event.</summary>
    public required string SourceId { get; init; }

    /// <summary>Recipe step active when the event was received.</summary>
    public required string RecipeStepId { get; init; }

    /// <summary>Sequence assigned by this source.</summary>
    public required long SourceSequence { get; init; }

    /// <summary>Sequence assigned across every source in the capture.</summary>
    public required long GlobalSequence { get; init; }

    /// <summary>QueryPerformanceCounter receipt time.</summary>
    public required long QpcReceiptTime { get; init; }

    /// <summary>Timestamp supplied by the source, when one exists.</summary>
    public CaptureSourceTimestamp? SourceTime { get; init; }

    /// <summary>Clock segment; incremented after reset, suspend, or an unbridgeable clock gap.</summary>
    public required int ClockSegment { get; init; }

    /// <summary>Device generation current when the event arrived.</summary>
    public required long DeviceGeneration { get; init; }

    /// <summary>Exact bytes, or an explicit omission state.</summary>
    public required CapturedPayload Payload { get; init; }

    /// <summary>Whether this event reports lost observations.</summary>
    public required EventLossState Loss { get; init; }

    /// <summary>Whether this event begins a discontinuous segment.</summary>
    public required EventDiscontinuity Discontinuity { get; init; }

    /// <summary>Whether the observation step exceeded its deadline.</summary>
    public required bool TimedOut { get; init; }

    /// <summary>Whether the source was available to the current process.</summary>
    public required EventAccessState Access { get; init; }
}

/// <summary>Loss state carried by every capture event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventLossState>))]
internal enum EventLossState
{
    /// <summary>No loss was observed.</summary>
    None,

    /// <summary>A gap was found in source sequence numbers.</summary>
    SequenceGap,

    /// <summary>The source explicitly reported loss.</summary>
    SourceReported,

    /// <summary>The bounded capture queue overflowed.</summary>
    QueueOverflow,
}

/// <summary>Discontinuity state carried by every capture event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventDiscontinuity>))]
internal enum EventDiscontinuity
{
    /// <summary>The event continues the current segment.</summary>
    None,

    /// <summary>The observation source restarted.</summary>
    SourceRestarted,

    /// <summary>The source clock reset.</summary>
    ClockReset,

    /// <summary>The machine suspended and resumed.</summary>
    SuspendResume,

    /// <summary>The observed device re-enumerated into a new generation.</summary>
    DeviceGenerationChanged,

    /// <summary>The event arrived after a later-QPC event and was retained rather than reordered away.</summary>
    LateArrival,
}

/// <summary>Access state carried by every capture event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventAccessState>))]
internal enum EventAccessState
{
    /// <summary>The source was available.</summary>
    Available,

    /// <summary>The source existed but denied access.</summary>
    AccessDenied,

    /// <summary>The source or prerequisite was unavailable.</summary>
    Unavailable,
}

/// <summary>One derived interpretation that links back to raw event IDs.</summary>
internal sealed record CaptureAnalysisResult
{
    /// <summary>Schema version of this result.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable result identifier.</summary>
    public required string ResultId { get; init; }

    /// <summary>Analyzer that produced the result.</summary>
    public required string AnalyzerId { get; init; }

    /// <summary>Exact analyzer version.</summary>
    public required string AnalyzerVersion { get; init; }

    /// <summary>Plain-language interpretation.</summary>
    public required string Meaning { get; init; }

    /// <summary>Structured derived values.</summary>
    public IReadOnlyList<CaptureAnalysisValue> Values { get; init; } = [];

    /// <summary>Raw observations supporting this interpretation.</summary>
    public IReadOnlyList<string> SupportingEventIds { get; init; } = [];

    /// <summary>Raw observations contradicting this interpretation.</summary>
    public IReadOnlyList<string> CounterexampleEventIds { get; init; } = [];

    /// <summary>Known limits on what this result establishes.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>One reviewable key/value produced by an analyzer.</summary>
internal sealed record CaptureAnalysisValue
{
    /// <summary>Stable semantic key.</summary>
    public required string Key { get; init; }

    /// <summary>Invariant textual representation of the value.</summary>
    public required string Value { get; init; }

    /// <summary>Optional physical unit.</summary>
    public string? Unit { get; init; }
}

/// <summary>What sanitization removed or refused to include.</summary>
internal sealed record CaptureRedactionManifest
{
    /// <summary>Schema version of this report.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Whether the standard shareable redaction pass ran.</summary>
    public required bool DefaultRedactionApplied { get; init; }

    /// <summary>Identifying values replaced with stable bundle-local tokens.</summary>
    public IReadOnlyList<RedactionSummary> Replacements { get; init; } = [];

    /// <summary>Opaque artifacts excluded because they could not be safely rewritten.</summary>
    public IReadOnlyList<QuarantinedCaptureArtifact> Quarantined { get; init; } = [];
}

/// <summary>An artifact deliberately absent from a shareable bundle.</summary>
internal sealed record QuarantinedCaptureArtifact
{
    /// <summary>Original logical artifact name, after path redaction.</summary>
    public required string Name { get; init; }

    /// <summary>Media type, when known.</summary>
    public string? MediaType { get; init; }

    /// <summary>Observed byte length.</summary>
    public required long Length { get; init; }

    /// <summary>Why it could not safely be shared.</summary>
    public required string Reason { get; init; }
}

/// <summary>One content-hash entry in <c>hashes.sha256</c>.</summary>
/// <param name="Path">Canonical relative archive path.</param>
/// <param name="Sha256">Lowercase hexadecimal SHA-256 digest.</param>
internal sealed record CaptureHashEntry(string Path, string Sha256);

/// <summary>An in-memory raw-event stream ready for sanitized bundle writing.</summary>
internal sealed record CaptureStreamFile
{
    /// <summary>Source identifier shared by all events.</summary>
    public required string SourceId { get; init; }

    /// <summary>Raw events in source-sequence order.</summary>
    public IReadOnlyList<CaptureStreamEvent> Events { get; init; } = [];
}

/// <summary>An in-memory analysis stream ready for sanitized bundle writing.</summary>
internal sealed record CaptureAnalysisFile
{
    /// <summary>Analyzer identifier shared by all results.</summary>
    public required string AnalyzerId { get; init; }

    /// <summary>Derived results in deterministic order.</summary>
    public IReadOnlyList<CaptureAnalysisResult> Results { get; init; } = [];
}

/// <summary>An included blob and its exact bytes.</summary>
internal sealed record CaptureBlobFile
{
    /// <summary>Descriptor recorded in the bundle manifest.</summary>
    public required CaptureBlobDescriptor Descriptor { get; init; }

    /// <summary>Exact sanitized bytes.</summary>
    public required byte[] Bytes { get; init; }
}

/// <summary>All sanitized values required to write one deterministic <c>.wsgmcap</c>.</summary>
internal sealed record SanitizedCaptureBundle
{
    /// <summary>Root shareable manifest.</summary>
    public required ShareableCaptureManifest Manifest { get; init; }

    /// <summary>Inert record of the observe-only recipe.</summary>
    public required ObserveOnlyRecipe Recipe { get; init; }

    /// <summary>Sanitized inventory projection.</summary>
    public required MachineInventory Inventory { get; init; }

    /// <summary>Raw event streams.</summary>
    public IReadOnlyList<CaptureStreamFile> Streams { get; init; } = [];

    /// <summary>Derived analysis streams.</summary>
    public IReadOnlyList<CaptureAnalysisFile> Analysis { get; init; } = [];

    /// <summary>Included sanitized blobs.</summary>
    public IReadOnlyList<CaptureBlobFile> Blobs { get; init; } = [];

    /// <summary>Redaction and quarantine report.</summary>
    public required CaptureRedactionManifest Redaction { get; init; }
}
