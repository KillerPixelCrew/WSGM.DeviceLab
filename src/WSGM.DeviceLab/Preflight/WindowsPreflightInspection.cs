using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace WSGM.DeviceLab.Preflight;

/// <summary>Result of inspecting the machine-wide production device owner.</summary>
internal sealed record DeviceLabOwnerInspection
{
    /// <summary>Owner discovery outcome.</summary>
    public required DeviceOwnerDiscoveryState State { get; init; }

    /// <summary>Bounded diagnostic detail when inspection was inconclusive.</summary>
    public string? Detail { get; init; }
}

/// <summary>Handle-held reservation of the exact machine-wide production owner object.</summary>
internal sealed class DeviceLabOwnerReservation : IDisposable
{
    private static readonly object RetainedReservationGate = new();
    private static readonly List<IDisposable> RetainedReservations = [];
    private IDisposable? _handle;

    internal DeviceLabOwnerReservation(IDisposable handle)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    /// <summary>Closes an unretained handle so another process may create the owner object.</summary>
    public void Dispose()
    {
        IDisposable? handle = Interlocked.Exchange(ref _handle, null);
        handle?.Dispose();
    }

    internal void RetainForProcessLifetime()
    {
        IDisposable? handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null)
        {
            return;
        }

        // Any plugin-code path without a verified clean Stop leaves hardware/resource cleanup
        // unverified. Rooting the raw handle until process exit prevents WSGM or another Device Lab
        // action from starting a competing hardware cycle after the surrounding using unwinds.
        lock (RetainedReservationGate)
        {
            RetainedReservations.Add(handle);
        }
    }
}

/// <summary>Atomic owner-slot reservation outcome for one attended Device Lab run.</summary>
internal sealed record DeviceLabOwnerReservationResult
{
    /// <summary>Whether the exact owner object was absent, present, or inaccessible.</summary>
    public required DeviceLabOwnerInspection Inspection { get; init; }

    /// <summary>Handle that keeps an absent owner object reserved through verified plugin disposal.</summary>
    public DeviceLabOwnerReservation? Reservation { get; init; }
}

/// <summary>Finds the production owner without starting, stopping, or contacting it.</summary>
internal static class DeviceLabOwnerInspector
{
    internal const string ProductionOwnerName = @"Global\WSGM.DeviceOwner";

    /// <summary>Returns the exact machine-wide production owner object name.</summary>
    public static string OwnerObjectName() => ProductionOwnerName;

    /// <summary>Checks whether WSGM already owns device integration on this machine.</summary>
    /// <returns>Fail-closed owner presence.</returns>
    public static DeviceLabOwnerInspection Inspect()
    {
        try
        {
            bool present = Mutex.TryOpenExisting(OwnerObjectName(), out Mutex? owner);
            owner?.Dispose();
            return new DeviceLabOwnerInspection
            {
                State = present ? DeviceOwnerDiscoveryState.Present : DeviceOwnerDiscoveryState.Absent,
            };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or WaitHandleCannotBeOpenedException
            or IOException)
        {
            return new DeviceLabOwnerInspection
            {
                State = DeviceOwnerDiscoveryState.Unknown,
                Detail = exception.GetType().Name,
            };
        }
    }

    /// <summary>Atomically reserves the machine-wide production owner object when absent.</summary>
    /// <returns>An absent result with a handle-held reservation, or a fail-closed refusal.</returns>
    public static DeviceLabOwnerReservationResult Reserve()
    {
        return Reserve(OwnerObjectName());
    }

    /// <summary>Atomically reserves one explicit owner object name.</summary>
    /// <param name="ownerObjectName">Exact named-mutex object.</param>
    /// <returns>An absent result with a handle-held reservation, or a fail-closed refusal.</returns>
    /// <remarks>
    /// The mutex is deliberately created unowned. WSGM elects its one coordinator from named-object
    /// creation, so keeping this handle open is the lease; waiting or releasing would add thread
    /// affinity across asynchronous plugin cleanup without improving exclusion.
    /// </remarks>
    internal static DeviceLabOwnerReservationResult Reserve(string ownerObjectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerObjectName);
        try
        {
            Mutex handle = new(initiallyOwned: false, ownerObjectName, out bool createdNew);
            if (!createdNew)
            {
                handle.Dispose();
                return ReservationResult(DeviceOwnerDiscoveryState.Present);
            }

            return ReservationResult(
                DeviceOwnerDiscoveryState.Absent,
                new DeviceLabOwnerReservation(handle));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or WaitHandleCannotBeOpenedException
            or IOException)
        {
            return ReservationResult(DeviceOwnerDiscoveryState.Unknown, detail: exception.GetType().Name);
        }
    }

    private static DeviceLabOwnerReservationResult ReservationResult(
        DeviceOwnerDiscoveryState state,
        DeviceLabOwnerReservation? reservation = null,
        string? detail = null) => new()
        {
            Inspection = new DeviceLabOwnerInspection
            {
                State = state,
                Detail = detail,
            },
            Reservation = reservation,
        };
}
