using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace WSGM.DeviceLab.Capture;

/// <summary>Request for baseline/action/release correlation around one guided button action.</summary>
internal sealed record PassiveCorrelationRequest
{
    /// <summary>Stable analysis identifier.</summary>
    public required string AnalysisId { get; init; }

    /// <summary>Guided marker action ID.</summary>
    public required string ActionId { get; init; }

    /// <summary>Only sources plausibly associated with the target device.</summary>
    public required IReadOnlySet<string> ExpectedSourceIds { get; init; }

    /// <summary>Raw timeline, retained separately from the derived result.</summary>
    public required IReadOnlyList<CaptureStreamEvent> Events { get; init; }

    /// <summary>QPC ticks inspected before press and after release.</summary>
    public required long ContextWindowTicks { get; init; }
}

/// <summary>Derived correlation candidate for one byte position in one expected source.</summary>
internal sealed record PassiveCorrelationFinding
{
    /// <summary>Source carrying the candidate byte.</summary>
    public required string SourceId { get; init; }

    /// <summary>Zero-based byte position.</summary>
    public required int ByteOffset { get; init; }

    /// <summary>Score from zero to one after stability and loss penalties.</summary>
    public required double Score { get; init; }

    /// <summary>Explicit result kind; never causality.</summary>
    public string CorrelationKind => "correlation-only";

    /// <summary>Stable baseline byte.</summary>
    public required byte BaselineValue { get; init; }

    /// <summary>Stable action byte.</summary>
    public required byte ActionValue { get; init; }

    /// <summary>Stable release byte.</summary>
    public required byte ReleaseValue { get; init; }

    /// <summary>Raw event IDs supporting the finding.</summary>
    public IReadOnlyList<string> SupportingEventIds { get; init; } = [];

    /// <summary>Raw event IDs contradicting the finding.</summary>
    public IReadOnlyList<string> CounterexampleEventIds { get; init; } = [];
}

/// <summary>Correlates raw byte observations with guided markers without claiming causality.</summary>
internal static class PassiveCorrelationAnalyzer
{
    /// <summary>Finds stable baseline-to-action changes which return on release.</summary>
    /// <param name="request">Raw timeline, expected device sources, and context window.</param>
    /// <param name="cancellationToken">Cancels bounded correlation analysis.</param>
    /// <returns>Deterministically ordered correlation-only findings.</returns>
    public static IReadOnlyList<PassiveCorrelationFinding> Analyze(
        PassiveCorrelationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ContextWindowTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Context window must be positive.");
        }

        var markers = WithCancellation(request.Events, cancellationToken)
            .Where(captureEvent => GuidedOperatorMarkers.TryDecode(
                captureEvent,
                out _,
                out string action,
                out _) && string.Equals(action, request.ActionId, StringComparison.Ordinal))
            .Select(captureEvent =>
            {
                _ = GuidedOperatorMarkers.TryDecode(captureEvent, out GuidedOperatorMarkerKind kind, out _, out _);
                return new { Event = captureEvent, Kind = kind };
            })
            .ToArray();
        CaptureStreamEvent[] presses = [.. markers
            .Where(marker => marker.Kind is GuidedOperatorMarkerKind.ButtonPress)
            .Select(marker => marker.Event)];
        CaptureStreamEvent[] releases = [.. markers
            .Where(marker => marker.Kind is GuidedOperatorMarkerKind.ButtonRelease)
            .Select(marker => marker.Event)];
        if (presses.Length != 1 || releases.Length != 1)
        {
            return [];
        }

        long press = presses[0].QpcReceiptTime;
        long release = releases[0].QpcReceiptTime;
        if (release <= press)
        {
            return [];
        }

        IEnumerable<CaptureStreamEvent> usable = WithCancellation(request.Events, cancellationToken).Where(captureEvent =>
            request.ExpectedSourceIds.Contains(captureEvent.SourceId)
            && captureEvent.Payload.Disposition is PayloadDisposition.Included
            && captureEvent.Payload.Bytes is not null
            && captureEvent.ClockSegment == presses[0].ClockSegment
            && captureEvent.ClockSegment == releases[0].ClockSegment);
        List<PassiveCorrelationFinding> findings = [];

        foreach (IGrouping<string, CaptureStreamEvent> source in usable
            .GroupBy(captureEvent => captureEvent.SourceId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureStreamEvent[] baseline = [.. WithCancellation(source, cancellationToken).Where(captureEvent =>
                captureEvent.QpcReceiptTime >= press - request.ContextWindowTicks
                && captureEvent.QpcReceiptTime < press)];
            CaptureStreamEvent[] action = [.. WithCancellation(source, cancellationToken).Where(captureEvent =>
                captureEvent.QpcReceiptTime >= press
                && captureEvent.QpcReceiptTime < release)];
            CaptureStreamEvent[] released = [.. WithCancellation(source, cancellationToken).Where(captureEvent =>
                captureEvent.QpcReceiptTime >= release
                && captureEvent.QpcReceiptTime <= release + request.ContextWindowTicks)];
            int width = MinimumWidth(baseline, action, released);
            for (int offset = 0; offset < width; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] baselineValues = [.. WithCancellation(baseline, cancellationToken)
                    .Select(captureEvent => captureEvent.Payload.Bytes![offset])];
                byte[] actionValues = [.. WithCancellation(action, cancellationToken)
                    .Select(captureEvent => captureEvent.Payload.Bytes![offset])];
                byte[] releaseValues = [.. WithCancellation(released, cancellationToken)
                    .Select(captureEvent => captureEvent.Payload.Bytes![offset])];
                if (!TrySingle(baselineValues, out byte baselineValue)
                    || !TrySingle(actionValues, out byte actionValue)
                    || !TrySingle(releaseValues, out byte releaseValue)
                    || baselineValue == actionValue
                    || baselineValue != releaseValue)
                {
                    continue;
                }

                CaptureStreamEvent[] supporting = [.. baseline.Concat(action).Concat(released)];
                double repetition = Math.Min(0.20, Math.Max(0, supporting.Length - 3) * 0.025);
                double penalty = supporting.Count(IsDegraded) * 0.15;
                double score = Math.Clamp(0.75 + repetition - penalty, 0, 1);
                findings.Add(new PassiveCorrelationFinding
                {
                    SourceId = source.Key,
                    ByteOffset = offset,
                    Score = score,
                    BaselineValue = baselineValue,
                    ActionValue = actionValue,
                    ReleaseValue = releaseValue,
                    SupportingEventIds = [.. supporting.Select(captureEvent => captureEvent.EventId)],
                });
            }
        }

        return [.. findings
            .OrderByDescending(finding => finding.Score)
            .ThenBy(finding => finding.SourceId, StringComparer.Ordinal)
            .ThenBy(finding => finding.ByteOffset)];
    }

    /// <summary>Projects one finding into the versioned derived-analysis schema with raw links.</summary>
    /// <param name="analysisId">Stable result ID.</param>
    /// <param name="finding">Correlation-only candidate.</param>
    /// <returns>Reviewable derived result.</returns>
    public static CaptureAnalysisResult ToAnalysisResult(
        string analysisId,
        PassiveCorrelationFinding finding) => new()
        {
            SchemaVersion = CaptureSchema.CurrentVersion,
            ResultId = analysisId,
            AnalyzerId = "passive-byte-correlation",
            AnalyzerVersion = "1.0.0",
            Meaning = $"Byte {finding.ByteOffset} on '{finding.SourceId}' correlated with the guided action and returned on release.",
            Values =
        [
            new CaptureAnalysisValue { Key = "correlation-kind", Value = finding.CorrelationKind },
            new CaptureAnalysisValue { Key = "source", Value = finding.SourceId },
            new CaptureAnalysisValue { Key = "offset", Value = finding.ByteOffset.ToString(CultureInfo.InvariantCulture), Unit = "byte" },
            new CaptureAnalysisValue { Key = "baseline", Value = finding.BaselineValue.ToString("x2", CultureInfo.InvariantCulture) },
            new CaptureAnalysisValue { Key = "action", Value = finding.ActionValue.ToString("x2", CultureInfo.InvariantCulture) },
            new CaptureAnalysisValue { Key = "release", Value = finding.ReleaseValue.ToString("x2", CultureInfo.InvariantCulture) },
            new CaptureAnalysisValue { Key = "score", Value = finding.Score.ToString("0.000", CultureInfo.InvariantCulture) },
        ],
            SupportingEventIds = finding.SupportingEventIds,
            CounterexampleEventIds = finding.CounterexampleEventIds,
            Limitations = PassiveCaptureLimitations.All,
        };

    private static int MinimumWidth(params CaptureStreamEvent[][] phases)
    {
        if (phases.Any(phase => phase.Length == 0))
        {
            return 0;
        }

        return phases.SelectMany(phase => phase)
            .Min(captureEvent => captureEvent.Payload.Bytes!.Length);
    }

    private static bool TrySingle(IReadOnlyList<byte> values, out byte value)
    {
        value = values.Count == 0 ? default : values[0];
        byte expected = value;
        return values.Count != 0 && values.All(candidate => candidate == expected);
    }

    private static bool IsDegraded(CaptureStreamEvent captureEvent) =>
        captureEvent.Loss is not EventLossState.None
        || captureEvent.Discontinuity is not EventDiscontinuity.None
        || captureEvent.TimedOut
        || captureEvent.Access is not EventAccessState.Available;

    private static IEnumerable<T> WithCancellation<T>(
        IEnumerable<T> source,
        CancellationToken cancellationToken)
    {
        foreach (T item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }
}
