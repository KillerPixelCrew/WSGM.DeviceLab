using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Capture;

/// <summary>High-resolution receipt clock injected into a passive capture timeline.</summary>
internal interface ICaptureReceiptClock
{
    /// <summary>Clock frequency in ticks per second.</summary>
    long Frequency { get; }

    /// <summary>Reads the current monotonic receipt time.</summary>
    /// <returns>Current clock ticks.</returns>
    long GetTimestamp();
}

/// <summary>QueryPerformanceCounter-backed production receipt clock.</summary>
internal sealed class QpcCaptureReceiptClock : ICaptureReceiptClock
{
    /// <inheritdoc/>
    public long Frequency => Stopwatch.Frequency;

    /// <inheritdoc/>
    public long GetTimestamp() => Stopwatch.GetTimestamp();
}

/// <summary>One raw observation submitted by a passive source before receipt sequencing.</summary>
internal sealed record PassiveObservation
{
    /// <summary>Observer emitting the event.</summary>
    public required string SourceId { get; init; }

    /// <summary>Active observe-only recipe step.</summary>
    public required string RecipeStepId { get; init; }

    /// <summary>Sequence assigned by the source.</summary>
    public required long SourceSequence { get; init; }

    /// <summary>Optional source-owned timestamp.</summary>
    public CaptureSourceTimestamp? SourceTime { get; init; }

    /// <summary>Observed device generation.</summary>
    public required long DeviceGeneration { get; init; }

    /// <summary>Exact passive payload or explicit omission.</summary>
    public required CapturedPayload Payload { get; init; }

    /// <summary>Loss explicitly reported by the source.</summary>
    public EventLossState Loss { get; init; }

    /// <summary>Discontinuity explicitly reported by the source.</summary>
    public EventDiscontinuity Discontinuity { get; init; }

    /// <summary>Whether the bounded step timed out.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Whether the source was available.</summary>
    public EventAccessState Access { get; init; } = EventAccessState.Available;
}

/// <summary>Thread-safe QPC-aligned timeline retaining source and receipt ordering.</summary>
internal sealed class PassiveCaptureTimeline
{
    private readonly object _gate = new();
    private readonly ICaptureReceiptClock _clock;
    private readonly List<CaptureStreamEvent> _events = [];
    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.Ordinal);
    private long _globalSequence;
    private long _maximumQpc;
    private long? _deviceGeneration;
    private int _clockSegment;
    private EventDiscontinuity _pendingDiscontinuity;

    /// <summary>Creates an empty capture timeline.</summary>
    /// <param name="clock">Receipt clock shared by every source.</param>
    public PassiveCaptureTimeline(ICaptureReceiptClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Receipt-clock frequency recorded in the capture manifest.</summary>
    public long QpcFrequency => _clock.Frequency;

    /// <summary>Records one passive observation and assigns its global sequence and segment.</summary>
    /// <param name="observation">Source observation.</param>
    /// <returns>The immutable raw event retained by the timeline.</returns>
    public CaptureStreamEvent Record(PassiveObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        long qpc = _clock.GetTimestamp();

        lock (_gate)
        {
            SourceState source = _sources.TryGetValue(observation.SourceId, out SourceState? existing)
                ? existing
                : new SourceState();
            EventLossState loss = observation.Loss;
            if (loss is EventLossState.None
                && source.HasSequence
                && observation.SourceSequence > source.LastSequence + 1)
            {
                loss = EventLossState.SequenceGap;
            }

            EventDiscontinuity discontinuity = observation.Discontinuity;
            bool explicitSegment = discontinuity is EventDiscontinuity.SourceRestarted
                or EventDiscontinuity.ClockReset
                or EventDiscontinuity.SuspendResume
                or EventDiscontinuity.DeviceGenerationChanged;
            bool sourceClockReset = source.LastSourceTime is { } previousSourceTime
                && observation.SourceTime is { } currentSourceTime
                && string.Equals(previousSourceTime.ClockId, currentSourceTime.ClockId, StringComparison.Ordinal)
                && currentSourceTime.Value < previousSourceTime.Value;
            bool generationChanged = _deviceGeneration is { } generation
                && observation.DeviceGeneration != generation;

            if (_pendingDiscontinuity is not EventDiscontinuity.None)
            {
                discontinuity = _pendingDiscontinuity;
                _pendingDiscontinuity = EventDiscontinuity.None;
            }
            else if (sourceClockReset)
            {
                discontinuity = EventDiscontinuity.ClockReset;
                explicitSegment = true;
            }
            else if (generationChanged)
            {
                discontinuity = EventDiscontinuity.DeviceGenerationChanged;
                explicitSegment = true;
            }
            else if (qpc < _maximumQpc)
            {
                discontinuity = EventDiscontinuity.LateArrival;
            }

            if (explicitSegment)
            {
                _clockSegment++;
            }

            _maximumQpc = Math.Max(_maximumQpc, qpc);
            _deviceGeneration = observation.DeviceGeneration;
            source.HasSequence = true;
            source.LastSequence = observation.SourceSequence;
            source.LastSourceTime = observation.SourceTime;
            _sources[observation.SourceId] = source;

            CaptureStreamEvent captureEvent = new()
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                EventId = $"event-{++_globalSequence:D10}",
                SourceId = observation.SourceId,
                RecipeStepId = observation.RecipeStepId,
                SourceSequence = observation.SourceSequence,
                GlobalSequence = _globalSequence,
                QpcReceiptTime = qpc,
                SourceTime = observation.SourceTime,
                ClockSegment = _clockSegment,
                DeviceGeneration = observation.DeviceGeneration,
                Payload = ClonePayload(observation.Payload),
                Loss = loss,
                Discontinuity = discontinuity,
                TimedOut = observation.TimedOut,
                Access = observation.Access,
            };
            _events.Add(captureEvent);
            return captureEvent;
        }
    }

    /// <summary>Begins a new segment for the next received event after suspend/resume.</summary>
    public void MarkSuspendResume()
    {
        lock (_gate)
        {
            _clockSegment++;
            _pendingDiscontinuity = EventDiscontinuity.SuspendResume;
        }
    }

    /// <summary>Returns raw events in receipt order.</summary>
    /// <returns>An immutable snapshot.</returns>
    public IReadOnlyList<CaptureStreamEvent> SnapshotByReceipt()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }

    /// <summary>Returns a QPC view without discarding original global receipt sequence.</summary>
    /// <returns>Events ordered by segment, QPC, then receipt sequence.</returns>
    public IReadOnlyList<CaptureStreamEvent> SnapshotByQpc()
    {
        lock (_gate)
        {
            return [.. _events
                .OrderBy(captureEvent => captureEvent.ClockSegment)
                .ThenBy(captureEvent => captureEvent.QpcReceiptTime)
                .ThenBy(captureEvent => captureEvent.GlobalSequence)];
        }
    }

    private static CapturedPayload ClonePayload(CapturedPayload payload) => new()
    {
        Length = payload.Length,
        Disposition = payload.Disposition,
        Bytes = payload.Bytes is null ? null : [.. payload.Bytes],
        Sha256 = payload.Sha256,
    };

    private sealed class SourceState
    {
        public bool HasSequence { get; set; }

        public long LastSequence { get; set; }

        public CaptureSourceTimestamp? LastSourceTime { get; set; }
    }
}

/// <summary>One passive source that can feed the shared capture timeline.</summary>
internal interface IPassiveCaptureSource
{
    /// <summary>Stable source identifier used by recipe steps.</summary>
    string SourceId { get; }

    /// <summary>Observes one bounded step and emits only passive events.</summary>
    /// <param name="step">Observe-only step.</param>
    /// <param name="emit">Callback into the shared timeline.</param>
    /// <param name="cancellationToken">Step deadline or caller cancellation.</param>
    Task ObserveAsync(
        ObservationStep step,
        Func<PassiveObservation, ValueTask> emit,
        CancellationToken cancellationToken);
}

/// <summary>Runs inert recipe steps through registered passive sources.</summary>
internal sealed class PassiveCaptureCoordinator
{
    private readonly IReadOnlyDictionary<string, IPassiveCaptureSource> _sources;
    private readonly PassiveCaptureTimeline _timeline;

    /// <summary>Creates a coordinator over explicitly supplied observation sources.</summary>
    /// <param name="sources">Passive sources, one per stable ID.</param>
    /// <param name="timeline">Shared QPC timeline.</param>
    public PassiveCaptureCoordinator(
        IReadOnlyList<IPassiveCaptureSource> sources,
        PassiveCaptureTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        Dictionary<string, IPassiveCaptureSource> indexed = new(StringComparer.Ordinal);
        foreach (IPassiveCaptureSource source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.SourceId) || !indexed.TryAdd(source.SourceId, source))
            {
                throw new ArgumentException("Passive source identifiers must be nonempty and unique.", nameof(sources));
            }
        }

        _sources = indexed;
    }

    /// <summary>Runs every recipe step under its own hard duration bound.</summary>
    /// <param name="recipe">Inert observe-only recipe.</param>
    /// <param name="cancellationToken">Whole-session cancellation.</param>
    public async Task RunAsync(ObserveOnlyRecipe recipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.SchemaVersion != CaptureSchema.CurrentVersion
            || recipe.Steps.Count > CaptureSchema.MaximumRecipeSteps
            || recipe.Steps.Any(step => step.DurationMilliseconds is <= 0 or > CaptureSchema.MaximumStepDurationMilliseconds))
        {
            throw new InvalidDataException("Passive capture recipe failed its closed schema or duration bounds.");
        }

        foreach (ObservationStep step in recipe.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sources.TryGetValue(step.SourceId, out IPassiveCaptureSource? source))
            {
                RecordUnavailable(step);
                continue;
            }

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(step.DurationMilliseconds);
            try
            {
                await source.ObserveAsync(
                    step,
                    observation =>
                    {
                        if (!string.Equals(observation.SourceId, source.SourceId, StringComparison.Ordinal)
                            || !string.Equals(observation.RecipeStepId, step.StepId, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("Passive source emitted outside its assigned source or step.");
                        }

                        _timeline.Record(observation);
                        return ValueTask.CompletedTask;
                    },
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RecordTimedOut(step);
            }
        }
    }

    private void RecordUnavailable(ObservationStep step) => _timeline.Record(new PassiveObservation
    {
        SourceId = step.SourceId,
        RecipeStepId = step.StepId,
        SourceSequence = 0,
        DeviceGeneration = 0,
        Payload = EmptyPayload(),
        Access = EventAccessState.Unavailable,
    });

    private void RecordTimedOut(ObservationStep step) => _timeline.Record(new PassiveObservation
    {
        SourceId = step.SourceId,
        RecipeStepId = step.StepId,
        SourceSequence = long.MaxValue,
        DeviceGeneration = 0,
        Payload = EmptyPayload(),
        TimedOut = true,
    });

    private static CapturedPayload EmptyPayload() => new()
    {
        Length = 0,
        Disposition = PayloadDisposition.NotCaptured,
    };
}

/// <summary>Closed guided operator actions that may be placed on the passive timeline.</summary>
internal enum GuidedOperatorMarkerKind
{
    /// <summary>Begin a quiet baseline.</summary>
    Baseline,

    /// <summary>Press one named button.</summary>
    ButtonPress,

    /// <summary>Release that named button.</summary>
    ButtonRelease,

    /// <summary>Move one axis to a named position.</summary>
    AxisPosition,

    /// <summary>Place the device on one of six faces.</summary>
    MotionFace,

    /// <summary>Attach a detachable component.</summary>
    Attach,

    /// <summary>Detach a detachable component.</summary>
    Detach,

    /// <summary>Record state before one externally performed OEM-utility change.</summary>
    OemSettingBefore,

    /// <summary>Record state after that externally performed OEM-utility change.</summary>
    OemSettingAfter,
}

/// <summary>Closed six-face orientations for a guided motion observation.</summary>
internal enum GuidedMotionFace
{
    /// <summary>Display facing upward.</summary>
    FaceUp,

    /// <summary>Display facing downward.</summary>
    FaceDown,

    /// <summary>Top edge facing upward.</summary>
    TopUp,

    /// <summary>Bottom edge facing upward.</summary>
    BottomUp,

    /// <summary>Left edge facing upward.</summary>
    LeftUp,

    /// <summary>Right edge facing upward.</summary>
    RightUp,
}

/// <summary>Closed axis positions used by guided capture.</summary>
internal enum GuidedAxisPosition
{
    /// <summary>Negative or minimum extent.</summary>
    Minimum,

    /// <summary>Neutral or center position.</summary>
    Center,

    /// <summary>Positive or maximum extent.</summary>
    Maximum,
}

/// <summary>Creates and validates passive operator-marker observations.</summary>
internal static class GuidedOperatorMarkers
{
    /// <summary>Stable source ID for guided operator markers.</summary>
    public const string SourceId = "operator.marker";

    /// <summary>Creates one marker payload; it never invokes the OEM utility or hardware.</summary>
    /// <param name="stepId">Recipe step receiving the marker.</param>
    /// <param name="actionId">Stable action correlation ID.</param>
    /// <param name="kind">Closed guided action.</param>
    /// <param name="label">Bounded operator label such as button or axis name.</param>
    /// <param name="sourceSequence">Operator-marker sequence.</param>
    /// <param name="deviceGeneration">Current observed generation.</param>
    /// <returns>A passive observation ready for the shared timeline.</returns>
    public static PassiveObservation Create(
        string stepId,
        string actionId,
        GuidedOperatorMarkerKind kind,
        string label,
        long sourceSequence,
        long deviceGeneration)
    {
        ValidateToken(stepId, nameof(stepId));
        ValidateToken(actionId, nameof(actionId));
        ValidateToken(label, nameof(label));
        string encoded = string.Join("\t", "v1", kind.ToString(), actionId, label);
        byte[] bytes = Encoding.UTF8.GetBytes(encoded);
        return new PassiveObservation
        {
            SourceId = SourceId,
            RecipeStepId = stepId,
            SourceSequence = sourceSequence,
            DeviceGeneration = deviceGeneration,
            Payload = new CapturedPayload
            {
                Length = bytes.Length,
                Disposition = PayloadDisposition.Included,
                Bytes = bytes,
                Sha256 = CaptureHashFile.Hash(bytes),
            },
        };
    }

    /// <summary>Creates a marker for one axis at a closed minimum, center, or maximum position.</summary>
    /// <param name="stepId">Recipe step receiving the marker.</param>
    /// <param name="actionId">Stable axis-action ID.</param>
    /// <param name="axisId">Bounded axis name.</param>
    /// <param name="position">Closed guided position.</param>
    /// <param name="sourceSequence">Operator-marker sequence.</param>
    /// <param name="deviceGeneration">Current observed generation.</param>
    /// <returns>Passive axis marker.</returns>
    public static PassiveObservation CreateAxis(
        string stepId,
        string actionId,
        string axisId,
        GuidedAxisPosition position,
        long sourceSequence,
        long deviceGeneration) => Create(
            stepId,
            actionId,
            GuidedOperatorMarkerKind.AxisPosition,
            $"{axisId}:{position}",
            sourceSequence,
            deviceGeneration);

    /// <summary>Creates one of the six closed device-orientation markers.</summary>
    /// <param name="stepId">Recipe step receiving the marker.</param>
    /// <param name="actionId">Stable motion-action ID.</param>
    /// <param name="face">One of six physical orientations.</param>
    /// <param name="sourceSequence">Operator-marker sequence.</param>
    /// <param name="deviceGeneration">Current observed generation.</param>
    /// <returns>Passive motion marker.</returns>
    public static PassiveObservation CreateMotionFace(
        string stepId,
        string actionId,
        GuidedMotionFace face,
        long sourceSequence,
        long deviceGeneration) => Create(
            stepId,
            actionId,
            GuidedOperatorMarkerKind.MotionFace,
            face.ToString(),
            sourceSequence,
            deviceGeneration);

    /// <summary>Decodes a marker emitted by <see cref="Create"/>.</summary>
    /// <param name="captureEvent">Raw event.</param>
    /// <param name="kind">Decoded kind.</param>
    /// <param name="actionId">Decoded correlation ID.</param>
    /// <param name="label">Decoded label.</param>
    /// <returns>True only for a well-formed operator marker.</returns>
    public static bool TryDecode(
        CaptureStreamEvent captureEvent,
        out GuidedOperatorMarkerKind kind,
        out string actionId,
        out string label)
    {
        kind = default;
        actionId = string.Empty;
        label = string.Empty;
        if (!string.Equals(captureEvent.SourceId, SourceId, StringComparison.Ordinal)
            || captureEvent.Payload.Disposition is not PayloadDisposition.Included
            || captureEvent.Payload.Bytes is not { Length: > 0 } bytes)
        {
            return false;
        }

        string[] parts = Encoding.UTF8.GetString(bytes).Split('\t');
        if (parts.Length != 4
            || parts[0] != "v1"
            || !Enum.TryParse(parts[1], ignoreCase: false, out kind)
            || parts[2].Length == 0
            || parts[3].Length == 0)
        {
            return false;
        }

        actionId = parts[2];
        label = parts[3];
        return true;
    }

    /// <summary>Reports duplicate and unpaired action markers without discarding them.</summary>
    /// <param name="events">Raw timeline.</param>
    /// <returns>Deterministic ambiguity descriptions.</returns>
    public static IReadOnlyList<string> Validate(IReadOnlyList<CaptureStreamEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var markers = events
            .Select(captureEvent => TryDecode(captureEvent, out GuidedOperatorMarkerKind kind, out string action, out _)
                ? new { Event = captureEvent, Kind = kind, Action = action }
                : null)
            .Where(marker => marker is not null)
            .ToArray();
        List<string> errors = [];

        foreach (var duplicate in markers
            .GroupBy(marker => (marker!.Action, marker.Kind))
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key.Action, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Kind))
        {
            errors.Add($"Duplicate {duplicate.Key.Kind} marker for action '{duplicate.Key.Action}'.");
        }

        foreach (var action in markers.GroupBy(marker => marker!.Action).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            bool press = action.Any(marker => marker!.Kind is GuidedOperatorMarkerKind.ButtonPress);
            bool release = action.Any(marker => marker!.Kind is GuidedOperatorMarkerKind.ButtonRelease);
            if (press != release)
            {
                errors.Add($"Button action '{action.Key}' requires exactly one press and release marker.");
            }
        }

        return errors;
    }

    private static void ValidateToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > CaptureSchema.MaximumIdentifierLength
            || value.Contains('\t')
            || value.Contains('\r')
            || value.Contains('\n'))
        {
            throw new ArgumentException("Marker tokens must be bounded single-line values without tabs.", parameterName);
        }
    }
}

/// <summary>Explicit limitations attached to passive Device Lab observations.</summary>
internal static class PassiveCaptureLimitations
{
    /// <summary>Platform limitations that every passive correlation report must retain.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "User-mode HID capture cannot observe every output transfer.",
        "USB ETW is bounded and may lose events.",
        "WMI Activity is incomplete and not every provider emits useful events.",
        "ETW coverage is not universal across OEM transports.",
        "Low-level hook events cannot always be attributed to one physical device.",
        "There is no safe generic ACPI, EC, SMBus, or I2C observation path.",
        "Multi-source snapshots are not atomic.",
        "Timing correlation is a candidate relationship, not proof of causality.",
    ];
}
