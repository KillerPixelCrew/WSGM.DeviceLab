using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.DeviceLab.Probes;

/// <summary>Validates compiled probe metadata before it can run.</summary>
internal static class ReadProbeMetadataPolicy
{
    /// <summary>Validates identity, rate, deadline, structure, and cross-check bounds.</summary>
    /// <param name="metadata">Compiled metadata to inspect.</param>
    /// <returns>Every defect in deterministic field order.</returns>
    public static IReadOnlyList<string> Validate(ReadProbeMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        List<string> errors = [];

        Required(metadata.Id, "Probe ID", errors);
        Required(metadata.FamilyId, "Family ID", errors);
        Required(metadata.EndpointId, "Endpoint ID", errors);
        Required(metadata.ResourceId, "Resource ID", errors);
        Required(metadata.CrossCheck.Id, "Cross-check ID", errors);

        if (metadata.Version <= 0)
        {
            errors.Add("Probe version must be positive.");
        }

        if (metadata.MaximumReadsPerSecond is < 1 or > 20)
        {
            errors.Add("Read rate must be between 1 and 20 calls per second.");
        }

        if (metadata.TimeoutMilliseconds is < 50 or > 30_000)
        {
            errors.Add("Probe deadline must be between 50 and 30000 milliseconds.");
        }

        if (metadata.Repetitions is < 1 or > 10)
        {
            errors.Add("Probe repetitions must be between 1 and 10.");
        }

        ReadProbeResponseExpectation expected = metadata.ExpectedResponse;
        if (expected.MinimumLength < 0
            || expected.MaximumLength < expected.MinimumLength
            || expected.MaximumLength > 65_536)
        {
            errors.Add("Expected response length must be ordered and no larger than 65536 bytes.");
        }

        if (expected.AllowedStatusCodes.Count == 0)
        {
            errors.Add("At least one response status code must be allowlisted.");
        }

        if (expected.MinimumValue is { } minimum
            && expected.MaximumValue is { } maximum
            && minimum > maximum)
        {
            errors.Add("Expected numeric range is reversed.");
        }

        if (metadata.CrossCheck.Kind is ReadProbeCrossCheckKind.InRange
            && (metadata.CrossCheck.MinimumValue is null
                || metadata.CrossCheck.MaximumValue is null
                || metadata.CrossCheck.MinimumValue > metadata.CrossCheck.MaximumValue))
        {
            errors.Add("An in-range cross-check requires an ordered numeric range.");
        }

        return errors;
    }

    private static void Required(string value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label} is required.");
        }
    }
}

/// <summary>Validates every structural and semantic dimension of a worker response.</summary>
internal static class ReadProbeResponseValidator
{
    /// <summary>Validates response identity, mutation, count, type, length, status, range, timing, stability, and cross-check.</summary>
    /// <param name="metadata">Compiled contract.</param>
    /// <param name="response">Disposable-worker response.</param>
    /// <returns>Accepted only when every invariant holds.</returns>
    public static ReadProbeValidationResult Validate(
        ReadProbeMetadata metadata,
        ReadProbeWorkerResponse response)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(response);

        if (response.SchemaVersion != 1
            || !string.Equals(response.ProbeId, metadata.Id, StringComparison.Ordinal)
            || response.ProbeVersion != metadata.Version)
        {
            return Reject("response.identity", "Response schema or probe identity did not match the request.");
        }

        if (response.HardwareMutationObserved)
        {
            return Reject("response.mutation", "A read probe reported hardware mutation and is rejected.");
        }

        if (response.Status is not ReadProbeWorkerStatus.Completed)
        {
            return Reject($"worker.{response.Status.ToString().ToLowerInvariant()}", response.Error ?? "Read-probe worker did not complete.");
        }

        if (response.Samples.Count != metadata.Repetitions)
        {
            return Reject("response.repetitions", "Response did not contain the required repetitions.");
        }

        string? stableValue = null;
        foreach (ReadProbeSample sample in response.Samples)
        {
            ReadProbeResponseExpectation expected = metadata.ExpectedResponse;
            if (sample.ValueKind != expected.ValueKind)
            {
                return Reject("response.type", "Response value type was not the compiled type.");
            }

            if (sample.Length < expected.MinimumLength || sample.Length > expected.MaximumLength)
            {
                return Reject("response.length", "Response length was outside the compiled bounds.");
            }

            if (!expected.AllowedStatusCodes.Contains(sample.StatusCode))
            {
                return Reject("response.status", "Response status was not allowlisted.");
            }

            if (sample.ElapsedMilliseconds < 0 || sample.ElapsedMilliseconds > metadata.TimeoutMilliseconds)
            {
                return Reject("response.timing", "A response exceeded the whole-probe deadline.");
            }

            if (expected.ValueKind is ReadProbeValueKind.Integer
                && (sample.NumericValue is null
                    || expected.MinimumValue is { } minimum && sample.NumericValue.Value < minimum
                    || expected.MaximumValue is { } maximum && sample.NumericValue.Value > maximum))
            {
                return Reject("response.range", "Numeric response was absent or outside the compiled range.");
            }

            ReadProbeValidationResult crossCheck = ValidateCrossCheck(metadata.CrossCheck, sample);
            if (!crossCheck.Accepted)
            {
                return crossCheck;
            }

            if (expected.MustBeStable
                && stableValue is not null
                && !string.Equals(stableValue, sample.NormalizedValue, StringComparison.Ordinal))
            {
                return Reject("response.unstable", "Repeated responses were not stable.");
            }

            stableValue ??= sample.NormalizedValue;
        }

        return new ReadProbeValidationResult
        {
            Accepted = true,
            Code = "accepted",
            Message = $"Validated {response.Samples.Count} response repetition(s) and their independent cross-checks.",
        };
    }

    private static ReadProbeValidationResult ValidateCrossCheck(
        ReadProbeCrossCheck crossCheck,
        ReadProbeSample sample)
    {
        bool accepted = crossCheck.Kind switch
        {
            ReadProbeCrossCheckKind.Equal => string.Equals(
                sample.NormalizedValue,
                sample.CrossCheckValue,
                StringComparison.Ordinal),
            ReadProbeCrossCheckKind.SameStatus => string.Equals(
                sample.NormalizedValue,
                sample.CrossCheckValue,
                StringComparison.OrdinalIgnoreCase),
            ReadProbeCrossCheckKind.Present => !string.IsNullOrWhiteSpace(sample.CrossCheckValue),
            ReadProbeCrossCheckKind.InRange => sample.CrossCheckNumericValue is { } numeric
                && crossCheck.MinimumValue is { } minimum
                && crossCheck.MaximumValue is { } maximum
                && numeric >= minimum
                && numeric <= maximum,
            _ => false,
        };

        return accepted
            ? new ReadProbeValidationResult
            {
                Accepted = true,
                Code = "cross-check.accepted",
                Message = "Independent cross-check accepted.",
            }
            : Reject("response.cross-check", $"Independent cross-check '{crossCheck.Id}' did not corroborate the response.");
    }

    private static ReadProbeValidationResult Reject(string code, string message) => new()
    {
        Accepted = false,
        Code = code,
        Message = message,
    };
}
