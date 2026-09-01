using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Testing;

/// <summary>Kind of local plugin test performed by Device Lab.</summary>
internal enum PluginTestMode
{
    /// <summary>Load the plugin and run exact detection only.</summary>
    DetectionOnly,

    /// <summary>Run one attended activation and guaranteed cleanup lifecycle.</summary>
    AttendedHardware,
}

/// <summary>Publications observed during one local plugin run.</summary>
internal sealed record PluginPublicationSummary
{
    /// <summary>Number of complete descriptor sets published.</summary>
    public required int DescriptorSets { get; init; }

    /// <summary>Number of capability states published.</summary>
    public required int CapabilityStates { get; init; }

    /// <summary>Number of physical-device sets published.</summary>
    public required int PhysicalDeviceSets { get; init; }

    /// <summary>Number of controller samples published.</summary>
    public required int ControllerSamples { get; init; }

    /// <summary>Number of OEM-control sets published.</summary>
    public required int OemControlSets { get; init; }

    /// <summary>Number of OEM events published.</summary>
    public required int OemEvents { get; init; }

    /// <summary>Summarizes the SDK-owned in-memory plugin host adapter.</summary>
    /// <param name="host">Adapter used for the local plugin run.</param>
    /// <returns>One count for each semantic publication channel.</returns>
    public static PluginPublicationSummary From(TestPluginHostAdapter host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new PluginPublicationSummary
        {
            DescriptorSets = host.DescriptorSets.Count,
            CapabilityStates = host.CapabilityStates.Count,
            PhysicalDeviceSets = host.PhysicalDeviceSets.Count,
            ControllerSamples = host.ControllerSamples.Count,
            OemControlSets = host.OemControlSets.Count,
            OemEvents = host.OemEvents.Count,
        };
    }
}

/// <summary>Result of loading and exercising one local plugin.</summary>
internal sealed record PluginTestReport
{
    /// <summary>Requested test mode.</summary>
    public required PluginTestMode Mode { get; init; }

    /// <summary>Whether package validation, loading, and the requested lifecycle succeeded.</summary>
    public required bool Passed { get; init; }

    /// <summary>Package identifier when its manifest was valid.</summary>
    public string? PackageId { get; init; }

    /// <summary>Exact detection result when plugin code loaded.</summary>
    public PluginDetectionResult? Detection { get; init; }

    /// <summary>Attended safety gate, present only for the hardware mode.</summary>
    public DeviceLabPreflightDecision? Preflight { get; init; }

    /// <summary>Exact semantic action selected for the attended hardware mode.</summary>
    public AttendedPluginActionRequest? Action { get; init; }

    /// <summary>Selected action, readback, and verified-restoration result.</summary>
    public AttendedPluginActionReport? ActionResult { get; init; }

    /// <summary>Whether plugin startup completed.</summary>
    public bool Started { get; init; }

    /// <summary>Aggregate state returned by startup.</summary>
    public PluginStartResult? Startup { get; init; }

    /// <summary>Bounded plugin diagnostics read after startup.</summary>
    public PluginDiagnostics? Diagnostics { get; init; }

    /// <summary>Whether deactivation completed after an activation attempt.</summary>
    public bool CleanedUp { get; init; }

    /// <summary>Semantic publications observed by the in-memory host adapter.</summary>
    public PluginPublicationSummary? Publications { get; init; }

    /// <summary>Failure detail when the requested workflow did not complete.</summary>
    public string? Error { get; init; }
}

/// <summary>Local facts and atomic owner reservation used by one attended activation.</summary>
internal sealed record AttendedPluginSafetyEnvironment
{
    /// <summary>Reserves the production owner slot without contacting a running owner.</summary>
    public required Func<DeviceLabOwnerReservationResult> ReserveOwner { get; init; }

    /// <summary>Whether Device Lab currently has an elevated token.</summary>
    public required bool IsElevated { get; init; }

    /// <summary>Whether a human is driving a local interactive session.</summary>
    public required bool IsUserInteractive { get; init; }

    /// <summary>Whether the current process runs under continuous integration.</summary>
    public required bool IsContinuousIntegration { get; init; }

    /// <summary>Combines the atomic reservation outcome with stable process facts.</summary>
    /// <param name="ownerDiscovery">Atomic owner-slot reservation outcome.</param>
    /// <param name="confirmed">Immediate confirmation for this one action.</param>
    /// <returns>One fail-closed preflight snapshot.</returns>
    public DeviceLabSafetySnapshot Capture(
        DeviceOwnerDiscoveryState ownerDiscovery,
        bool confirmed) => new()
        {
            OwnerDiscovery = ownerDiscovery,
            IsElevated = IsElevated,
            IsUserInteractive = IsUserInteractive,
            IsContinuousIntegration = IsContinuousIntegration,
            AttendedActionConfirmed = confirmed,
        };
}

/// <summary>Loads local plugin code and runs either detection or one attended lifecycle.</summary>
internal static class PluginTestWorkflow
{
    private static readonly TimeSpan LifecycleBudget = TimeSpan.FromSeconds(15);

    /// <summary>Loads a package and calls only its read-only exact detector.</summary>
    /// <param name="packageDirectory">Validated package directory.</param>
    /// <param name="identity">Current normalized machine identity.</param>
    /// <param name="cancellationToken">Cancels the local test.</param>
    /// <returns>Detection and load result.</returns>
    public static Task<PluginTestReport> TestDetectionAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        CancellationToken cancellationToken) => PluginTestWorkerSupervisor.TestDetectionAsync(
            packageDirectory,
            identity,
            cancellationToken);

    /// <summary>Worker-only detector implementation; community code must not call this in the UI process.</summary>
    internal static async Task<PluginTestReport> TestDetectionInProcessAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        PluginPackageValidationReport validation = PluginPackageWorkflow.ValidateOffline(packageDirectory);
        if (!validation.Valid)
        {
            return Failed(PluginTestMode.DetectionOnly, validation.PackageId, "Offline package validation failed.");
        }

        await using LocalPluginPackage package = LocalPluginPackage.Load(packageDirectory);
        PluginDetectionResult detection = await package.Plugin.DetectAsync(
            new PluginDetectionContext { Identity = identity },
            cancellationToken).ConfigureAwait(false);
        return new PluginTestReport
        {
            Mode = PluginTestMode.DetectionOnly,
            Passed = true,
            PackageId = package.Manifest.Id,
            Detection = detection,
        };
    }

    /// <summary>Runs one immediately confirmed plugin activation and mandatory deactivation.</summary>
    /// <param name="packageDirectory">Validated package directory.</param>
    /// <param name="identity">Current normalized machine identity.</param>
    /// <param name="stateDirectory">New explicit package state directory.</param>
    /// <param name="action">One explicit semantic action selected by the local operator.</param>
    /// <param name="confirmed">Immediate local confirmation for this action.</param>
    /// <param name="boundaries">Protected filesystem boundaries.</param>
    /// <param name="cancellationToken">Cancels the attended action.</param>
    /// <returns>Detection, gate, lifecycle, cleanup, and publication results.</returns>
    public static Task<PluginTestReport> RunAttendedAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        string stateDirectory,
        AttendedPluginActionRequest action,
        bool confirmed,
        DeviceLabPathBoundaries boundaries,
        CancellationToken cancellationToken) => PluginTestWorkerSupervisor.RunAttendedAsync(
            packageDirectory,
            identity,
            stateDirectory,
            action,
            confirmed,
            boundaries,
            cancellationToken);

    internal static async Task<PluginTestReport> RunAttendedAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        string stateDirectory,
        AttendedPluginActionRequest action,
        bool confirmed,
        DeviceLabPathBoundaries boundaries,
        AttendedPluginSafetyEnvironment safetyEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(boundaries);
        ArgumentNullException.ThrowIfNull(safetyEnvironment);
        PluginPackageValidationReport validation = PluginPackageWorkflow.ValidateOffline(packageDirectory);
        if (!validation.Valid)
        {
            return Failed(
                PluginTestMode.AttendedHardware,
                validation.PackageId,
                "Offline package validation failed.",
                action);
        }

        string packageId = validation.PackageId!;
        DeviceLabOutputPathDecision output = DeviceLabOutputPathPolicy.Evaluate(
            stateDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!IsNewStateDirectory(output))
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = packageId,
                Action = action,
                Error = output.Reason ?? "The plugin state directory must be new.",
            };
        }

        var requirements = new DeviceLabOperationRequirements
        {
            OperationId = "plugin.attended-run",
            ResourceId = packageId,
            Access = DeviceLabOperationAccess.AttendedPluginAction,
            // Static refusal checks must run before community plugin code is loaded. Exact identity
            // is replaced with the detector's result below, before activation can begin.
            ExactDeviceMatched = true,
            RequiresElevation = true,
        };
        DeviceLabSafetySnapshot preReservationSnapshot = safetyEnvironment.Capture(
            DeviceOwnerDiscoveryState.Absent,
            confirmed);
        DeviceLabPreflightDecision staticPreflight = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            preReservationSnapshot);
        if (staticPreflight.Route is not DeviceLabAccessRoute.DirectAttended)
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = packageId,
                Preflight = staticPreflight,
                Action = action,
                Error = "The attended hardware action was blocked before plugin loading.",
            };
        }

        DeviceLabOwnerReservationResult owner = safetyEnvironment.ReserveOwner();
        using DeviceLabOwnerReservation? ownerReservation = owner.Reservation;
        DeviceOwnerDiscoveryState ownerState = owner.Inspection.State;
        if ((ownerState is DeviceOwnerDiscoveryState.Absent) != (ownerReservation is not null))
        {
            ownerState = DeviceOwnerDiscoveryState.Unknown;
        }

        DeviceLabSafetySnapshot safetySnapshot = safetyEnvironment.Capture(ownerState, confirmed);
        DeviceLabPreflightDecision ownerPreflight = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            safetySnapshot);
        if (ownerPreflight.Route is not DeviceLabAccessRoute.DirectAttended)
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = packageId,
                Preflight = ownerPreflight,
                Action = action,
                Error = "The attended hardware action was blocked before plugin loading.",
            };
        }

        await using LocalPluginPackage package = LocalPluginPackage.Load(
            packageDirectory,
            ownerReservation);
        PluginDetectionResult detection = await package.Plugin.DetectAsync(
            new PluginDetectionContext { Identity = identity },
            cancellationToken).ConfigureAwait(false);
        bool exactDeviceMatched = detection.Matched
            && !string.IsNullOrWhiteSpace(detection.DeviceDefinitionId);
        // The exact named owner reservation stays live across this dynamic gate, activation,
        // cleanup, and LocalPluginPackage disposal. WSGM therefore cannot start a competing cycle.
        DeviceLabPreflightDecision preflight = DeviceLabSafetyPreflight.Evaluate(
            requirements with { ExactDeviceMatched = exactDeviceMatched },
            safetySnapshot);
        if (preflight.Route is not DeviceLabAccessRoute.DirectAttended)
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = packageId,
                Detection = detection,
                Preflight = preflight,
                Action = action,
                Error = "The attended hardware action was blocked before activation.",
            };
        }

        // Recheck the already-approved output immediately before creation so a filesystem race
        // cannot turn a new package state directory into an overwrite.
        output = DeviceLabOutputPathPolicy.Evaluate(
            stateDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!IsNewStateDirectory(output))
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = packageId,
                Detection = detection,
                Preflight = preflight,
                Action = action,
                Error = output.Reason ?? "The plugin state directory must be new.",
            };
        }

        string statePath = output.FullPath!;
        Directory.CreateDirectory(statePath);
        TestPluginHostAdapter host = new(cycleGeneration: 1);
        bool startAttempted = false;
        bool started = false;
        bool cleanedUp = false;
        PluginStartResult? startupResult = null;
        AttendedPluginActionReport? actionResult = null;
        PluginDiagnostics? diagnostics = null;
        string? error = null;
        try
        {
            using (CancellationTokenSource startup = Deadline(cancellationToken))
            {
                startAttempted = true;
                startupResult = await package.Plugin.StartAsync(
                    new PluginStartContext
                    {
                        Host = host,
                        CycleGeneration = 1,
                        DeviceDefinitionId = detection.DeviceDefinitionId!,
                        StateDirectory = statePath,
                        ControllerManagementEnabled = false,
                    },
                    startup.Token).ConfigureAwait(false);
            }

            started = true;
            using (CancellationTokenSource actionDeadline = Deadline(cancellationToken))
            {
                actionResult = await AttendedPluginActionRunner.RunAsync(
                    package.Plugin,
                    host,
                    action,
                    actionDeadline.Token).ConfigureAwait(false);
            }

            if (!actionResult.Passed)
            {
                error = $"Attended action failed: {actionResult.Error ?? "no detail"}";
            }

            using CancellationTokenSource diagnosticsDeadline = Deadline(cancellationToken);
            diagnostics = await package.Plugin.GetDiagnosticsAsync(diagnosticsDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            error = $"Activation failed: {exception.Message}";
        }
        finally
        {
            try
            {
                using CancellationTokenSource cleanup = Deadline(CancellationToken.None);
                PluginStopResult stop = await package.Plugin.StopAsync(
                    new PluginStopContext(
                        PluginStopReason.IntegrationDisabled,
                        DateTimeOffset.UtcNow + LifecycleBudget),
                    cleanup.Token).ConfigureAwait(false);
                cleanedUp = stop.Status is PluginStopStatus.Clean;
                if (!cleanedUp)
                {
                    string cleanupError = stop.Reason?.Detail ?? "Plugin cleanup was not verified clean.";
                    error = error is null ? cleanupError : $"{error} {cleanupError}";
                }
            }
            catch (Exception exception)
            {
                error = error is null
                    ? $"Cleanup failed: {exception.Message}"
                    : $"{error} Cleanup also failed: {exception.Message}";
            }

            if (startAttempted && !cleanedUp)
            {
                ownerReservation?.RetainForProcessLifetime();
            }
        }

        return new PluginTestReport
        {
            Mode = PluginTestMode.AttendedHardware,
            Passed = started && actionResult?.Passed is true && cleanedUp && error is null,
            PackageId = package.Manifest.Id,
            Detection = detection,
            Preflight = preflight,
            Action = action,
            ActionResult = actionResult,
            Started = started,
            Startup = startupResult,
            Diagnostics = diagnostics,
            CleanedUp = cleanedUp,
            Publications = PluginPublicationSummary.From(host),
            Error = error,
        };
    }

    private static bool IsNewStateDirectory(DeviceLabOutputPathDecision output) =>
        output.IsAllowed
        && output.FullPath is not null
        && !Directory.Exists(output.FullPath)
        && !File.Exists(output.FullPath);

    private static CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LifecycleBudget);
        return deadline;
    }

    private static bool IsElevated()
    {
        using System.Security.Principal.WindowsIdentity identity =
            System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool IsContinuousIntegration() =>
        IsTruthy(Environment.GetEnvironmentVariable("CI"))
        || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static PluginTestReport Failed(
        PluginTestMode mode,
        string? packageId,
        string error,
        AttendedPluginActionRequest? action = null) => new()
        {
            Mode = mode,
            Passed = false,
            PackageId = packageId,
            Action = action,
            Error = error,
        };
}

internal sealed class LocalPluginPackage : IAsyncDisposable
{
    private readonly PluginLoadContext _loadContext;
    private readonly DeviceLabOwnerReservation? _ownerReservation;

    private LocalPluginPackage(
        PluginManifest manifest,
        IDevicePlugin plugin,
        PluginLoadContext loadContext,
        DeviceLabOwnerReservation? ownerReservation)
    {
        Manifest = manifest;
        Plugin = plugin;
        _loadContext = loadContext;
        _ownerReservation = ownerReservation;
    }

    public PluginManifest Manifest { get; }

    public IDevicePlugin Plugin { get; }

    public static LocalPluginPackage Load(
        string packageDirectory,
        DeviceLabOwnerReservation? ownerReservation = null)
    {
        string root = Path.GetFullPath(packageDirectory);
        string manifestPath = Constrain(root, PluginPackageWorkflow.ManifestPath);
        if (!File.Exists(manifestPath) || PluginPackageWorkflow.IsLink(manifestPath))
        {
            throw new InvalidDataException("The plugin manifest is missing or is a link.");
        }

        PluginManifestReadResult manifestRead = PluginPackageWorkflow.ReadManifestBounded(manifestPath);
        PluginManifest manifest = manifestRead.IsValid && manifestRead.Manifest is not null
            ? manifestRead.Manifest
            : throw new InvalidDataException("The plugin manifest is invalid.");
        string entryPath = Constrain(root, manifest.EntryAssembly);
        if (!File.Exists(entryPath) || PluginPackageWorkflow.IsLink(entryPath))
        {
            throw new InvalidDataException("The plugin entry assembly is missing or is a link.");
        }

        PluginLoadContext context = new(root, entryPath);
        IDevicePlugin? plugin = null;
        bool activationAttempted = false;
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(entryPath);
            Type entryType;
            try
            {
                entryType = assembly.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false)!;
            }
            catch (TypeLoadException exception)
            {
                throw new InvalidDataException(
                    $"The declared plugin entry type could not be loaded: {exception.Message}",
                    exception);
            }

            if (!entryType.IsPublic
                || entryType.IsAbstract
                || entryType.IsInterface
                || entryType.ContainsGenericParameters
                || !typeof(IDevicePlugin).IsAssignableFrom(entryType)
                || entryType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidDataException(
                    "The entry type must be a public, concrete IDevicePlugin with a parameterless constructor.");
            }

            // From this point plugin code may have acquired resources even if construction or a
            // property getter fails before an instance can be returned to Device Lab.
            activationAttempted = true;
            plugin = Activator.CreateInstance(entryType) as IDevicePlugin
                ?? throw new InvalidDataException(
                    "The entry type did not create an IDevicePlugin instance.");
            if (!string.Equals(plugin.PackageId, manifest.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The plugin code and manifest package IDs differ.");
            }

            return new LocalPluginPackage(manifest, plugin, context, ownerReservation);
        }
        catch (Exception loadFailure)
        {
            Exception failure = loadFailure;
            if (activationAttempted)
            {
                // Loading has crossed into community plugin code without a lifecycle Stop result.
                // Disposal may release managed resources, but cannot verify hardware restoration.
                ownerReservation?.RetainForProcessLifetime();
            }
            if (plugin is not null)
            {
                try
                {
                    plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception disposalFailure)
                {
                    failure = CombineFailures(
                        "Plugin loading and disposal both failed.",
                        failure,
                        disposalFailure);
                }
            }

            try
            {
                context.Unload();
            }
            catch (Exception unloadFailure)
            {
                failure = CombineFailures(
                    "Plugin loading and load-context cleanup both failed.",
                    failure,
                    unloadFailure);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    public ValueTask DisposeAsync() => DisposePluginAndUnloadAsync(
        Plugin.DisposeAsync,
        _loadContext.Unload,
        _ownerReservation);

    internal static async ValueTask DisposePluginAndUnloadAsync(
        Func<ValueTask> disposePluginAsync,
        Action unload,
        DeviceLabOwnerReservation? ownerReservation)
    {
        ArgumentNullException.ThrowIfNull(disposePluginAsync);
        ArgumentNullException.ThrowIfNull(unload);
        Exception? failure = null;
        try
        {
            await disposePluginAsync().ConfigureAwait(false);
        }
        catch (Exception disposalFailure)
        {
            ownerReservation?.RetainForProcessLifetime();
            failure = disposalFailure;
        }
        finally
        {
            try
            {
                unload();
            }
            catch (Exception unloadFailure)
            {
                ownerReservation?.RetainForProcessLifetime();
                failure = CombineFailures(
                    "Plugin disposal and load-context cleanup were not both verified.",
                    failure,
                    unloadFailure);
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static Exception CombineFailures(
        string message,
        Exception? first,
        Exception next) => first is null
            ? next
            : new AggregateException(message, first, next);

    private static string Constrain(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Plugin package paths must be relative.");
        }

        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A plugin package path escaped its directory.");
        }

        return path;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly string SdkName = typeof(IDevicePlugin).Assembly.GetName().Name!;
        private readonly string _root;
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string root, string entryPath)
            : base($"WSGM.DeviceLab:{Path.GetFileName(root)}", isCollectible: true)
        {
            _root = root;
            _resolver = new AssemblyDependencyResolver(entryPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, SdkName, StringComparison.Ordinal))
            {
                return null;
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null && assemblyName.Name is { Length: > 0 } name)
            {
                string packageCandidate = Path.Combine(_root, $"{name}.dll");
                path = File.Exists(packageCandidate) ? packageCandidate : null;
            }

            // Null delegates framework/shared-contract resolution to the default context. Every
            // plugin-owned assembly that exists in the package is selected above and confined.
            if (path is null)
            {
                return null;
            }

            EnsureLocal(path);
            return LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null)
            {
                // Zero delegates to the runtime's normal OS-library search. A plugin may import a
                // Windows DLL without bundling a package copy; only a dependency the package
                // resolver actually selected needs the package-boundary check below.
                return 0;
            }

            EnsureLocal(path);
            return NativeLibrary.Load(path);
        }

        private void EnsureLocal(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || PluginPackageWorkflow.IsLink(fullPath))
            {
                throw new InvalidDataException("A plugin dependency escaped its package directory.");
            }
        }
    }
}
