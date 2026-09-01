using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;

namespace WSGM.DeviceLab.Probes;

/// <summary>The allowlisted semantic family implemented by a reviewed read probe.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeFamily>))]
internal enum ReadProbeFamily
{
    /// <summary>A provider, protocol, firmware, or native-library version read.</summary>
    Version,

    /// <summary>A WMI getter whose exact method and response shape are compiled into Device Lab.</summary>
    WmiStatus,

    /// <summary>A known HID feature report read whose report ID and size are profile-scoped.</summary>
    HidFeature,

    /// <summary>A single allowlisted EC address read whose access path and address are profile-scoped.</summary>
    EmbeddedController,

    /// <summary>A controller mode or hardware profile read.</summary>
    ControllerMode,

    /// <summary>A current fan tachometer read.</summary>
    FanRpm,

    /// <summary>A current charge state or threshold read.</summary>
    ChargeState,

    /// <summary>Offline native-library version, architecture, hash, signer, or export inspection.</summary>
    NativeLibraryMetadata,
}

/// <summary>The scalar or byte representation expected from each probe repetition.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeValueKind>))]
internal enum ReadProbeValueKind
{
    /// <summary>A signed integral number.</summary>
    Integer,

    /// <summary>A true or false status.</summary>
    Boolean,

    /// <summary>A bounded UTF-8 string.</summary>
    Text,

    /// <summary>An exact or bounded byte sequence, represented as lower-case hexadecimal.</summary>
    Bytes,

    /// <summary>A dotted version string.</summary>
    Version,
}

/// <summary>How an independent observation must relate to the primary probe value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeCrossCheckKind>))]
internal enum ReadProbeCrossCheckKind
{
    /// <summary>The independent observation must equal the primary value.</summary>
    Equal,

    /// <summary>The independent observation must be present and fall within its declared range.</summary>
    InRange,

    /// <summary>The independent observation must report the same normalized status.</summary>
    SameStatus,

    /// <summary>The independent observation must be present, but may legitimately change.</summary>
    Present,
}

/// <summary>Structural and semantic invariants for one read-probe response.</summary>
internal sealed record ReadProbeResponseExpectation
{
    /// <summary>Expected representation.</summary>
    public required ReadProbeValueKind ValueKind { get; init; }

    /// <summary>Smallest encoded response length accepted.</summary>
    public required int MinimumLength { get; init; }

    /// <summary>Largest encoded response length accepted.</summary>
    public required int MaximumLength { get; init; }

    /// <summary>Allowlisted provider/protocol status values.</summary>
    public IReadOnlyList<int> AllowedStatusCodes { get; init; } = [0];

    /// <summary>Smallest numeric value accepted, when the representation is numeric.</summary>
    public long? MinimumValue { get; init; }

    /// <summary>Largest numeric value accepted, when the representation is numeric.</summary>
    public long? MaximumValue { get; init; }

    /// <summary>Whether all repetitions must return the same normalized value.</summary>
    public bool MustBeStable { get; init; } = true;
}

/// <summary>An independent read used to corroborate the primary response.</summary>
internal sealed record ReadProbeCrossCheck
{
    /// <summary>Stable identifier of the compiled cross-check.</summary>
    public required string Id { get; init; }

    /// <summary>Required relation between the primary and independent values.</summary>
    public required ReadProbeCrossCheckKind Kind { get; init; }

    /// <summary>Smallest accepted independent numeric value for <see cref="ReadProbeCrossCheckKind.InRange"/>.</summary>
    public long? MinimumValue { get; init; }

    /// <summary>Largest accepted independent numeric value for <see cref="ReadProbeCrossCheckKind.InRange"/>.</summary>
    public long? MaximumValue { get; init; }
}

/// <summary>Named, versioned metadata for one compiled read probe.</summary>
internal sealed record ReadProbeMetadata
{
    /// <summary>Stable probe identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Probe contract version.</summary>
    public required int Version { get; init; }

    /// <summary>Exact normalized hardware-family identifier.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Exact endpoint identifier within that family.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Resource whose ownership must be checked before execution.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Allowlisted semantic family.</summary>
    public required ReadProbeFamily Family { get; init; }

    /// <summary>Maximum calls per second, including repetitions and cross-checks.</summary>
    public required int MaximumReadsPerSecond { get; init; }

    /// <summary>Whole-probe deadline in milliseconds.</summary>
    public required int TimeoutMilliseconds { get; init; }

    /// <summary>Required number of repeated observations.</summary>
    public required int Repetitions { get; init; }

    /// <summary>Expected primary response invariants.</summary>
    public required ReadProbeResponseExpectation ExpectedResponse { get; init; }

    /// <summary>Required independent observation.</summary>
    public required ReadProbeCrossCheck CrossCheck { get; init; }

    /// <summary>Whether the compiled getter requires an elevated disposable host.</summary>
    public bool RequiresElevation { get; init; }
}

/// <summary>One bounded primary response and its independent observation.</summary>
internal sealed record ReadProbeSample
{
    /// <summary>Representation returned by the typed profile.</summary>
    public required ReadProbeValueKind ValueKind { get; init; }

    /// <summary>Provider or protocol status.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Encoded primary response length.</summary>
    public required int Length { get; init; }

    /// <summary>Signed numeric form, when applicable.</summary>
    public long? NumericValue { get; init; }

    /// <summary>Normalized text, version, boolean, or lower-case hexadecimal form.</summary>
    public required string NormalizedValue { get; init; }

    /// <summary>Elapsed time for this repetition in milliseconds.</summary>
    public required int ElapsedMilliseconds { get; init; }

    /// <summary>Normalized independent observation.</summary>
    public required string CrossCheckValue { get; init; }

    /// <summary>Numeric independent observation, when applicable.</summary>
    public long? CrossCheckNumericValue { get; init; }
}

/// <summary>Execution state reported by the disposable self-worker.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeWorkerStatus>))]
internal enum ReadProbeWorkerStatus
{
    /// <summary>The worker completed every bounded read.</summary>
    Completed,

    /// <summary>The typed profile could not open its resource because of access control.</summary>
    AccessDenied,

    /// <summary>The exact endpoint disappeared during execution.</summary>
    Disconnected,

    /// <summary>A compiled prerequisite was absent.</summary>
    PrerequisiteMissing,

    /// <summary>The profile rejected the request before opening the resource.</summary>
    Rejected,
}

/// <summary>Result document written once by a disposable Device Lab self-worker.</summary>
internal sealed record ReadProbeWorkerResponse
{
    /// <summary>Response schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Probe identifier actually executed.</summary>
    public required string ProbeId { get; init; }

    /// <summary>Probe version actually executed.</summary>
    public required int ProbeVersion { get; init; }

    /// <summary>Host execution state.</summary>
    public required ReadProbeWorkerStatus Status { get; init; }

    /// <summary>Bounded observations, one per requested repetition.</summary>
    public IReadOnlyList<ReadProbeSample> Samples { get; init; } = [];

    /// <summary>Structured failure detail without device identifiers or raw handles.</summary>
    public string? Error { get; init; }

    /// <summary>Must remain false for every read-only profile.</summary>
    public bool HardwareMutationObserved { get; init; }
}

/// <summary>Immutable invocation envelope consumed by one disposable Device Lab self-worker.</summary>
/// <remarks>
/// It contains no transport operation, address, method, report ID, library path, or arbitrary
/// parameter. Those remain compiled into the profile selected by <see cref="ProbeId"/>.
/// </remarks>
internal sealed record ReadProbeWorkerRequest
{
    /// <summary>Request schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable compiled profile identifier.</summary>
    public required string ProbeId { get; init; }

    /// <summary>Exact compiled profile version.</summary>
    public required int ProbeVersion { get; init; }

    /// <summary>Exact family already matched by Device Lab.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Exact endpoint already matched by Device Lab.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Allowlisted semantic profile family.</summary>
    public required ReadProbeFamily Family { get; init; }

    /// <summary>Rate ceiling that the compiled profile must not exceed.</summary>
    public required int MaximumReadsPerSecond { get; init; }

    /// <summary>Compiled whole-process deadline.</summary>
    public required int TimeoutMilliseconds { get; init; }

    /// <summary>Compiled repetition count.</summary>
    public required int Repetitions { get; init; }

    /// <summary>SHA-256 of the one-use secret delivered only through the inherited pipe.</summary>
    public string? AuthorizationSha256 { get; init; }
}

/// <summary>Observed lifecycle of one disposable Device Lab self-worker.</summary>
internal sealed record ReadProbeProcessOutcome
{
    /// <summary>Whether the process was started.</summary>
    public required bool Started { get; init; }

    /// <summary>Whether the supervisor killed it after its deadline.</summary>
    public required bool TimedOut { get; init; }

    /// <summary>Whether Windows confirmed that no worker or descendant remains contained.</summary>
    public required bool ContainmentVerified { get; init; }

    /// <summary>Exit code, when the process reached an exit state.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether a result document was produced.</summary>
    public required bool ResultProduced { get; init; }

    /// <summary>Bounded stderr detail.</summary>
    public string? Error { get; init; }
}

/// <summary>Caller cancellation observed only after bounded disposable-worker teardown.</summary>
internal sealed class DisposableWorkerCanceledException : OperationCanceledException
{
    internal DisposableWorkerCanceledException(
        bool containmentVerified,
        CancellationToken cancellationToken)
        : base("The disposable worker operation was cancelled.", innerException: null, cancellationToken)
    {
        ContainmentVerified = containmentVerified;
    }

    /// <summary>Whether Windows confirmed that every contained process exited before propagation.</summary>
    internal bool ContainmentVerified { get; }
}

/// <summary>End-to-end disposition of a supervised read-probe run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeRunStatus>))]
internal enum ReadProbeRunStatus
{
    /// <summary>The response passed every invariant.</summary>
    Accepted,

    /// <summary>Admission or safety preflight rejected execution.</summary>
    Rejected,

    /// <summary>The process could not be started.</summary>
    LaunchFailed,

    /// <summary>The process exited unexpectedly.</summary>
    WorkerCrashed,

    /// <summary>The process exceeded its deadline and was killed.</summary>
    WorkerHung,

    /// <summary>The typed endpoint rejected access.</summary>
    AccessDenied,

    /// <summary>The exact endpoint disconnected.</summary>
    Disconnected,

    /// <summary>The worker result was missing or failed structural validation.</summary>
    MalformedResponse,
}

/// <summary>Classified result exposed to Device Lab callers.</summary>
internal sealed record ReadProbeRunResult
{
    /// <summary>Stable run disposition.</summary>
    public required ReadProbeRunStatus Status { get; init; }

    /// <summary>Human-readable detail.</summary>
    public required string Message { get; init; }

    /// <summary>Validated response, present only when useful for diagnosis.</summary>
    public ReadProbeWorkerResponse? Response { get; init; }
}

/// <summary>Validation of a completed worker response against compiled invariants.</summary>
internal sealed record ReadProbeValidationResult
{
    /// <summary>Whether the response is usable.</summary>
    public required bool Accepted { get; init; }

    /// <summary>Stable validation code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable validation detail.</summary>
    public required string Message { get; init; }
}
