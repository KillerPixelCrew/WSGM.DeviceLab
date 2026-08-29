using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WSGM.DeviceLab.Preflight;

/// <summary>The only device-access paths exposed by Device Lab.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLabOperationAccess>))]
internal enum DeviceLabOperationAccess
{
    /// <summary>One compiled read-only probe for an exactly matched known device.</summary>
    ReadOnlyProbe,

    /// <summary>One locally confirmed plugin activation followed by mandatory cleanup.</summary>
    AttendedPluginAction,
}

/// <summary>The route selected after the small local safety gate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLabAccessRoute>))]
internal enum DeviceLabAccessRoute
{
    /// <summary>No device access is permitted.</summary>
    None,

    /// <summary>A compiled read-only probe may run in the hidden self-worker.</summary>
    DirectReadOnly,

    /// <summary>A locally selected plugin may run its ordinary activation and cleanup lifecycle.</summary>
    DirectAttended,
}

/// <summary>Whether the production WSGM session currently owns device integration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceOwnerDiscoveryState>))]
internal enum DeviceOwnerDiscoveryState
{
    /// <summary>No production owner object exists in this session.</summary>
    Absent,

    /// <summary>The production owner object exists.</summary>
    Present,

    /// <summary>The owner object could not be inspected safely.</summary>
    Unknown,
}

/// <summary>Inputs to the local gate shared by read probes and the attended plugin action.</summary>
internal sealed record DeviceLabOperationRequirements
{
    /// <summary>Stable operation identifier.</summary>
    public required string OperationId { get; init; }

    /// <summary>Resource or package being opened.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Requested access path.</summary>
    public required DeviceLabOperationAccess Access { get; init; }

    /// <summary>Whether exact local device detection succeeded.</summary>
    public required bool ExactDeviceMatched { get; init; }

    /// <summary>Whether the compiled endpoint exists on the matched device.</summary>
    public bool ExactEndpointMatched { get; init; } = true;

    /// <summary>Whether the operation requires an elevated Device Lab process.</summary>
    public bool RequiresElevation { get; init; }
}

/// <summary>Current local state used by the device-access gate.</summary>
internal sealed record DeviceLabSafetySnapshot
{
    /// <summary>Optional doctor report for workflows that already require an output directory.</summary>
    public DeviceLabDoctorReport? Doctor { get; init; }

    /// <summary>Production owner presence in the current session.</summary>
    public required DeviceOwnerDiscoveryState OwnerDiscovery { get; init; }

    /// <summary>Whether Device Lab currently has an elevated token.</summary>
    public required bool IsElevated { get; init; }

    /// <summary>Whether a human is driving a local interactive session.</summary>
    public required bool IsUserInteractive { get; init; }

    /// <summary>Whether the current process runs under continuous integration.</summary>
    public required bool IsContinuousIntegration { get; init; }

    /// <summary>Immediate confirmation for this one attended action.</summary>
    public bool AttendedActionConfirmed { get; init; }
}

/// <summary>One deterministic gate result.</summary>
internal sealed record DeviceLabPreflightCheck
{
    /// <summary>Stable check identifier.</summary>
    public required string Code { get; init; }

    /// <summary>Check severity.</summary>
    public required DeviceLabDoctorStatus Status { get; init; }

    /// <summary>Operator-facing detail.</summary>
    public required string Message { get; init; }
}

/// <summary>Complete access decision produced before Device Lab opens a device resource.</summary>
internal sealed record DeviceLabPreflightDecision
{
    /// <summary>Resource or package under review.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Overall gate status.</summary>
    public required DeviceLabDoctorStatus Status { get; init; }

    /// <summary>Permitted route, or <see cref="DeviceLabAccessRoute.None"/>.</summary>
    public required DeviceLabAccessRoute Route { get; init; }

    /// <summary>Every performed check in stable order.</summary>
    public IReadOnlyList<DeviceLabPreflightCheck> Checks { get; init; } = [];
}

/// <summary>Small fail-closed gate for the two ordinary compiled device operations.</summary>
internal static class DeviceLabSafetyPreflight
{
    /// <summary>Evaluates exact identity, owner exclusion, elevation, and immediate attendance.</summary>
    /// <param name="requirements">Operation being requested.</param>
    /// <param name="snapshot">Current local state.</param>
    /// <returns>The direct route when every mandatory check passes.</returns>
    public static DeviceLabPreflightDecision Evaluate(
        DeviceLabOperationRequirements requirements,
        DeviceLabSafetySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirements.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirements.ResourceId);

        List<DeviceLabPreflightCheck> checks = [];
        if (snapshot.Doctor?.Status is DeviceLabDoctorStatus.Blocked)
        {
            Block(checks, "doctor.blocked", "Device Lab doctor reported a blocking local condition.");
        }

        if (!requirements.ExactDeviceMatched)
        {
            Block(checks, "identity.mismatch", "The plugin or known-device fingerprint did not match exactly.");
        }

        if (!requirements.ExactEndpointMatched)
        {
            Block(checks, "endpoint.mismatch", "The compiled endpoint was not present on the matched device.");
        }

        if (requirements.RequiresElevation && !snapshot.IsElevated)
        {
            Block(checks, "permissions.elevation", "This operation requires an elevated Device Lab process.");
        }

        switch (snapshot.OwnerDiscovery)
        {
            case DeviceOwnerDiscoveryState.Present:
                Block(checks, "owner.active", "WSGM already owns device integration in this session.");
                break;
            case DeviceOwnerDiscoveryState.Unknown:
                Block(checks, "owner.unknown", "The production device owner could not be inspected safely.");
                break;
        }

        if (requirements.Access is DeviceLabOperationAccess.AttendedPluginAction)
        {
            if (!snapshot.IsUserInteractive || snapshot.IsContinuousIntegration)
            {
                Block(checks, "attended.interactive", "The hardware action requires a local interactive session and refuses CI.");
            }

            if (!snapshot.AttendedActionConfirmed)
            {
                Block(checks, "attended.confirmation", "Confirm this one hardware action immediately before it runs.");
            }
        }

        DeviceLabDoctorStatus status = checks.Any(check => check.Status is DeviceLabDoctorStatus.Blocked)
            ? DeviceLabDoctorStatus.Blocked
            : DeviceLabDoctorStatus.Pass;
        DeviceLabAccessRoute route = status is DeviceLabDoctorStatus.Blocked
            ? DeviceLabAccessRoute.None
            : requirements.Access is DeviceLabOperationAccess.ReadOnlyProbe
                ? DeviceLabAccessRoute.DirectReadOnly
                : DeviceLabAccessRoute.DirectAttended;

        if (status is DeviceLabDoctorStatus.Pass)
        {
            checks.Add(new DeviceLabPreflightCheck
            {
                Code = "access.allowed",
                Status = DeviceLabDoctorStatus.Pass,
                Message = route is DeviceLabAccessRoute.DirectReadOnly
                    ? "The exact compiled read may run in the disposable self-worker."
                    : "The exact local plugin may run once and must clean up before returning.",
            });
        }

        return new DeviceLabPreflightDecision
        {
            ResourceId = requirements.ResourceId,
            Status = status,
            Route = route,
            Checks = checks,
        };
    }

    private static void Block(
        ICollection<DeviceLabPreflightCheck> checks,
        string code,
        string message) => checks.Add(new DeviceLabPreflightCheck
        {
            Code = code,
            Status = DeviceLabDoctorStatus.Blocked,
            Message = message,
        });
}
