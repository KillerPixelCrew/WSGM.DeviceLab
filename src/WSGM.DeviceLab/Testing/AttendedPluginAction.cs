using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;

namespace WSGM.DeviceLab.Testing;

/// <summary>The one semantic hardware operation selected for an attended plugin run.</summary>
internal enum AttendedPluginActionKind
{
    /// <summary>Apply one semantic capability value, then restore its verified original value.</summary>
    CapabilityValue,

    /// <summary>Send one bounded haptic pulse, then explicitly stop output and release the controller.</summary>
    HapticPulse,

    /// <summary>Acquire controller management once, then release and verify the restored topology.</summary>
    ControllerManagement,

    /// <summary>
    /// Interactive motor calibration: descending haptic sweeps stepped by the device's own A
    /// button, with B marking the perception boundary, producing the measured
    /// <c>MinimumStartIntensity</c> and <c>MinimumPulse</c> for the plugin's haptic capabilities.
    /// </summary>
    HapticSweep,
}

/// <summary>Direct input for one compiled attended plugin action.</summary>
internal sealed record AttendedPluginActionRequest
{
    /// <summary>Selected compiled action.</summary>
    public required AttendedPluginActionKind Kind { get; init; }

    /// <summary>Exact semantic capability ID for <see cref="AttendedPluginActionKind.CapabilityValue"/>.</summary>
    public string? CapabilityId { get; init; }

    /// <summary>Optional exact capability, controller-source, or haptic-sink instance discriminator.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Semantic value text parsed against the selected descriptor.</summary>
    public string? ValueText { get; init; }
}

/// <summary>Observed outcome and verified restoration of one attended semantic action.</summary>
internal sealed record AttendedPluginActionReport
{
    /// <summary>Selected compiled action.</summary>
    public required AttendedPluginActionKind Kind { get; init; }

    /// <summary>Whether the selected action and its mandatory restoration were verified.</summary>
    public required bool Passed { get; init; }

    /// <summary>Selected value, controller-source, or haptic-sink capability ID.</summary>
    public string? CapabilityId { get; init; }

    /// <summary>Selected value, controller-source, or haptic-sink instance.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Original hardware value captured before the selected change.</summary>
    public CapabilityValue? OriginalValue { get; init; }

    /// <summary>Whether the original value was verified before the selected change.</summary>
    public bool OriginalValueVerified { get; init; }

    /// <summary>Requested semantic value parsed against the live descriptor.</summary>
    public CapabilityValue? RequestedValue { get; init; }

    /// <summary>Idempotent command used to verify an initially observed original value, when needed.</summary>
    public CapabilityCommandResult? OriginalVerification { get; init; }

    /// <summary>Result of applying the selected semantic value.</summary>
    public CapabilityCommandResult? Apply { get; init; }

    /// <summary>Result of restoring the original semantic value.</summary>
    public CapabilityCommandResult? Restore { get; init; }

    /// <summary>The fixed, bounded pulse submitted to the plugin.</summary>
    public HapticOutputFrame? HapticPulse { get; init; }

    /// <summary>Whether the non-silent haptic submission completed.</summary>
    public bool HapticPulseSent { get; init; }

    /// <summary>Whether an explicit zero-output frame was attempted.</summary>
    public bool HapticStopAttempted { get; init; }

    /// <summary>Whether the explicit zero-output submission completed.</summary>
    public bool HapticStopSent { get; init; }

    /// <summary>Whether temporary controller acquisition completed.</summary>
    public bool ControllerManagementEnabled { get; init; }

    /// <summary>Whether the plugin published the acquired controller resource as available.</summary>
    public bool ControllerAvailabilityObserved { get; init; }

    /// <summary>Controller handoff result after the temporary action.</summary>
    public PluginControllerRelease? ControllerRelease { get; init; }

    /// <summary>Whether the changed value or controller topology was restored and verified.</summary>
    public required bool RestorationVerified { get; init; }

    /// <summary>Measured sweep boundaries for <see cref="AttendedPluginActionKind.HapticSweep"/>.</summary>
    public AttendedHapticSweepReport? HapticSweep { get; init; }

    /// <summary>Failure detail when the action or mandatory restoration was not verified.</summary>
    public string? Error { get; init; }
}

/// <summary>Perception boundaries measured by the attended haptic sweep.</summary>
/// <remarks>
/// Each boundary is the last value the operator confirmed feeling before pressing B; a null
/// boundary means the operator felt every step, so the true boundary lies below the sweep floor
/// and the smallest swept value is the honest declaration.
/// </remarks>
internal sealed record AttendedHapticSweepReport
{
    /// <summary>Weakest continuous drive the operator felt, 0..1. Informational only.</summary>
    public float? ContinuousFloor { get; init; }

    /// <summary>Weakest 30 ms tick the operator felt, 0..1 — the <c>MinimumStartIntensity</c>.</summary>
    public float? TickFloor { get; init; }

    /// <summary>Shortest full-strength pulse the operator felt — the <c>MinimumPulse</c>.</summary>
    public TimeSpan? MinimumPulse { get; init; }

    /// <summary>Whether every phase ran to an operator-confirmed boundary or sweep end.</summary>
    public required bool Completed { get; init; }
}

/// <summary>Executes the three explicit semantic actions available behind Device Lab's attended gate.</summary>
internal static class AttendedPluginActionRunner
{
    private static readonly TimeSpan ActionBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HapticPulseDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>Overall attended budget for the interactive haptic sweep.</summary>
    private static readonly TimeSpan SweepBudget = TimeSpan.FromMinutes(5);

    /// <summary>Descending drive levels swept by the strength phases, on the 0..1 output scale.</summary>
    private static readonly float[] SweepLevels = [.. new[]
    {
        255, 224, 192, 160, 128, 112, 96, 80, 64, 56, 48, 40, 32, 24, 16, 8,
    }.Select(static level => level / 255f)];

    /// <summary>Descending pulse lengths swept at full strength by the duration phase.</summary>
    private static readonly int[] SweepPulseMilliseconds =
        [200, 150, 120, 90, 70, 55, 45, 35, 28, 22, 17, 13, 10, 7, 5];

    /// <summary>Tick length used while sweeping the event floor.</summary>
    private const int TickPulseMilliseconds = 30;

    /// <summary>Executes one selected action and its action-specific verified restoration.</summary>
    /// <param name="plugin">Already-started exact-device plugin.</param>
    /// <param name="host">Recorder containing the plugin's live semantic publications.</param>
    /// <param name="request">Explicit action and value selection.</param>
    /// <param name="cancellationToken">Cancels the selected action, but never its cleanup.</param>
    /// <returns>Action, readback, and restoration details.</returns>
    public static Task<AttendedPluginActionReport> RunAsync(
        IDevicePlugin plugin,
        TestPluginHostAdapter host,
        AttendedPluginActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind switch
        {
            AttendedPluginActionKind.CapabilityValue => RunCapabilityValueAsync(
                plugin,
                host,
                request,
                cancellationToken),
            AttendedPluginActionKind.HapticPulse => RunControllerActionAsync(
                plugin,
                host,
                request,
                cancellationToken),
            AttendedPluginActionKind.ControllerManagement => RunControllerActionAsync(
                plugin,
                host,
                request,
                cancellationToken),
            AttendedPluginActionKind.HapticSweep => RunHapticSweepAsync(
                plugin,
                host,
                request,
                cancellationToken),
            _ => Task.FromResult(Failed(request.Kind, "The selected attended action is unsupported.")),
        };
    }

    private static async Task<AttendedPluginActionReport> RunCapabilityValueAsync(
        IDevicePlugin plugin,
        TestPluginHostAdapter host,
        AttendedPluginActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySelectCapability(host, request, out CapabilityDescriptorSet? descriptorSet,
            out CapabilityDescriptor? descriptor, out CapabilityState? state, out string? selectionError))
        {
            return Failed(request.Kind, selectionError!);
        }

        CapabilityDescriptorSet selectedSet = descriptorSet!;
        CapabilityDescriptor selectedDescriptor = descriptor!;
        CapabilityState selectedState = state!;
        if (!TryParseValue(selectedDescriptor, request.ValueText, out CapabilityValue? requested,
            out string? parseError))
        {
            return Failed(
                request.Kind,
                parseError!,
                selectedDescriptor.CapabilityId,
                selectedDescriptor.InstanceId);
        }

        CapabilityValue selectedValue = requested!;
        CapabilityValue original = selectedState.ObservedValue!;
        if (ValuesEqual(original, selectedValue))
        {
            return Failed(
                request.Kind,
                "The requested value equals the captured original; select a materially different value.",
                selectedDescriptor.CapabilityId,
                selectedDescriptor.InstanceId,
                original,
                selectedValue);
        }

        bool originalVerified = selectedState.Quality is HardwareStateQuality.Verified;
        bool applyVerified = false;
        bool restoreRequired = false;
        bool restorationVerified = false;
        CapabilityCommandResult? originalVerification = null;
        CapabilityCommandResult? apply = null;
        CapabilityCommandResult? restore = null;
        string? error = null;
        try
        {
            if (!originalVerified)
            {
                restoreRequired = true;
                CapabilityCommand verifyCommand = Command(selectedSet, selectedDescriptor, original);
                originalVerification = await plugin.ExecuteCommandAsync(verifyCommand, cancellationToken)
                    .ConfigureAwait(false);
                originalVerified = IsVerified(originalVerification, verifyCommand.CommandId, original);
                if (!originalVerified)
                {
                    AppendError(ref error, DescribeResult(
                        "Original-value verification",
                        originalVerification,
                        verifyCommand.CommandId,
                        original));
                }
            }

            if (originalVerified)
            {
                restoreRequired = true;
                CapabilityCommand applyCommand = Command(selectedSet, selectedDescriptor, selectedValue);
                apply = await plugin.ExecuteCommandAsync(applyCommand, cancellationToken)
                    .ConfigureAwait(false);
                applyVerified = IsVerified(apply, applyCommand.CommandId, selectedValue);
                if (!applyVerified)
                {
                    AppendError(ref error, DescribeResult(
                        "Requested-value apply",
                        apply,
                        applyCommand.CommandId,
                        selectedValue));
                }
            }
        }
        catch (Exception exception)
        {
            AppendError(ref error, $"Capability action failed: {exception.Message}");
        }
        finally
        {
            if (restoreRequired)
            {
                try
                {
                    using CancellationTokenSource cleanup = Deadline();
                    CapabilityCommand restoreCommand = Command(selectedSet, selectedDescriptor, original);
                    restore = await plugin.ExecuteCommandAsync(restoreCommand, cleanup.Token)
                        .ConfigureAwait(false);
                    restorationVerified = IsVerified(restore, restoreCommand.CommandId, original);
                    if (!restorationVerified)
                    {
                        AppendError(ref error, DescribeResult(
                            "Original-value restore",
                            restore,
                            restoreCommand.CommandId,
                            original));
                    }
                }
                catch (Exception exception)
                {
                    AppendError(ref error, $"Original-value restore failed: {exception.Message}");
                }
            }
        }

        return new AttendedPluginActionReport
        {
            Kind = request.Kind,
            Passed = originalVerified
                && applyVerified
                && restorationVerified
                && error is null,
            CapabilityId = selectedDescriptor.CapabilityId,
            InstanceId = selectedDescriptor.InstanceId,
            OriginalValue = original,
            OriginalValueVerified = originalVerified,
            RequestedValue = selectedValue,
            OriginalVerification = originalVerification,
            Apply = apply,
            Restore = restore,
            RestorationVerified = restorationVerified,
            Error = error,
        };
    }

    private static async Task<AttendedPluginActionReport> RunControllerActionAsync(
        IDevicePlugin plugin,
        TestPluginHostAdapter host,
        AttendedPluginActionRequest request,
        CancellationToken cancellationToken)
    {
        AttendedPluginActionKind kind = request.Kind;
        CapabilityRole requiredRole = kind is AttendedPluginActionKind.HapticPulse
            ? CapabilityRole.HapticSink
            : CapabilityRole.ControllerSource;
        if (!TrySelectRole(host, requiredRole, request.InstanceId,
            out CapabilityDescriptorSet? descriptorSet,
            out CapabilityDescriptor? descriptor, out string? selectionError))
        {
            return Failed(kind, selectionError!);
        }

        long controllerGeneration;
        try
        {
            controllerGeneration = NextCycleGeneration(host);
        }
        catch (OverflowException)
        {
            return Failed(kind, "A fresh controller cycle generation could not be allocated.");
        }

        bool managementAttempted = false;
        bool managementEnabled = false;
        bool availabilityObserved = false;
        bool pulseSent = false;
        bool stopAttempted = false;
        bool stopSent = false;
        bool restorationVerified = false;
        PluginControllerRelease? release = null;
        HapticOutputFrame? pulse = null;
        string? error = null;
        try
        {
            managementAttempted = true;
            await plugin.SetControllerManagementAsync(
                new PluginControllerManagementContext(
                    Enabled: true,
                    controllerGeneration,
                    DateTimeOffset.UtcNow + ActionBudget),
                cancellationToken).ConfigureAwait(false);
            managementEnabled = true;
            availabilityObserved = IsAvailableAtGeneration(
                host,
                descriptorSet!,
                descriptor!,
                controllerGeneration);
            if (!availabilityObserved)
            {
                AppendError(ref error,
                    $"The plugin did not publish {requiredRole} as available for controller generation {controllerGeneration}.");
            }

            if (kind is AttendedPluginActionKind.HapticPulse && availabilityObserved)
            {
                pulse = new HapticOutputFrame
                {
                    TargetGeneration = controllerGeneration,
                    LowFrequency = 0.35F,
                    HighFrequency = 0.35F,
                    Timestamp = DateTimeOffset.UtcNow,
                };
                await plugin.ApplyHapticOutputAsync(pulse, cancellationToken).ConfigureAwait(false);
                pulseSent = true;
                await Task.Delay(HapticPulseDuration, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            AppendError(ref error, $"{ActionLabel(kind)} failed: {exception.Message}");
        }
        finally
        {
            if (kind is AttendedPluginActionKind.HapticPulse)
            {
                stopAttempted = true;
                try
                {
                    using CancellationTokenSource stopDeadline = Deadline();
                    await plugin.ApplyHapticOutputAsync(
                        HapticOutputFrame.Stop(controllerGeneration, DateTimeOffset.UtcNow),
                        stopDeadline.Token).ConfigureAwait(false);
                    stopSent = true;
                }
                catch (Exception exception)
                {
                    AppendError(ref error, $"Haptic zero-output cleanup failed: {exception.Message}");
                }
            }

            if (managementAttempted)
            {
                try
                {
                    using CancellationTokenSource releaseDeadline = Deadline();
                    release = await plugin.ReleaseControllerAsync(
                        new PluginControllerReleaseContext(
                            HandoffScope.ControllerOnly,
                            DateTimeOffset.UtcNow + ActionBudget),
                        releaseDeadline.Token).ConfigureAwait(false);
                    restorationVerified = IsVerifiedRelease(release);
                    if (!restorationVerified)
                    {
                        AppendError(ref error,
                            $"Controller topology restore was not verified ({release.Step}, {release.Result}).");
                    }
                }
                catch (Exception exception)
                {
                    AppendError(ref error, $"Controller topology restore failed: {exception.Message}");
                }
            }
        }

        bool actionPassed = managementEnabled && availabilityObserved;
        if (kind is AttendedPluginActionKind.HapticPulse)
        {
            actionPassed = actionPassed && pulseSent && stopSent;
        }

        return new AttendedPluginActionReport
        {
            Kind = kind,
            Passed = actionPassed && restorationVerified && error is null,
            CapabilityId = descriptor!.CapabilityId,
            InstanceId = descriptor.InstanceId,
            HapticPulse = pulse,
            HapticPulseSent = pulseSent,
            HapticStopAttempted = stopAttempted,
            HapticStopSent = stopSent,
            ControllerManagementEnabled = managementEnabled,
            ControllerAvailabilityObserved = availabilityObserved,
            ControllerRelease = release,
            RestorationVerified = restorationVerified,
            Error = error,
        };
    }

    private static bool TrySelectCapability(
        TestPluginHostAdapter host,
        AttendedPluginActionRequest request,
        out CapabilityDescriptorSet? descriptorSet,
        out CapabilityDescriptor? descriptor,
        out CapabilityState? state,
        out string? error)
    {
        descriptorSet = host.DescriptorSets.LastOrDefault();
        descriptor = null;
        state = null;
        if (descriptorSet is null)
        {
            error = "The plugin published no capability descriptors.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.CapabilityId))
        {
            error = "Capability value action requires an exact capability ID.";
            return false;
        }

        CapabilityDescriptor[] matches = [.. descriptorSet.Descriptors.Where(candidate =>
            string.Equals(candidate.CapabilityId, request.CapabilityId, StringComparison.Ordinal)
            && (request.InstanceId is null
                || string.Equals(candidate.InstanceId, request.InstanceId, StringComparison.Ordinal)))];
        if (matches.Length == 0)
        {
            error = "The selected capability or instance is not present in the latest descriptor set.";
            return false;
        }

        if (matches.Length != 1)
        {
            error = "The selected capability has multiple instances; select one exact instance ID.";
            return false;
        }

        CapabilityDescriptor selectedDescriptor = matches[0];
        descriptor = selectedDescriptor;
        if (!selectedDescriptor.SupportsRead || !selectedDescriptor.SupportsWrite
            || selectedDescriptor.ValueKind is CapabilityValueKind.None)
        {
            error = "The selected capability must support both value readback and writes.";
            return false;
        }

        CapabilityDescriptorSet selectedSet = descriptorSet;
        state = host.CapabilityStates.LastOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, selectedDescriptor.CapabilityId, StringComparison.Ordinal)
            && string.Equals(candidate.InstanceId, selectedDescriptor.InstanceId, StringComparison.Ordinal)
            && candidate.DescriptorGeneration == selectedSet.Generation
            && candidate.CycleGeneration == selectedSet.CycleGeneration);
        if (state is null || !state.Available || state.ObservedValue is null
            || state.ObservedValue.Kind != selectedDescriptor.ValueKind
            || state.Quality is HardwareStateQuality.Unknown
                or HardwareStateQuality.Stale
                or HardwareStateQuality.Faulted)
        {
            error = "The selected capability has no current readable original hardware value.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Runs the interactive three-phase motor calibration behind the attended gate.</summary>
    /// <remarks>
    /// Self-paced on the device's own controls, because perception is the instrument: A steps the
    /// sweep (confirming the previous step was felt), B marks the boundary. Phase one holds a
    /// continuous drive at descending levels (informational — the host never floors continuous
    /// output), phase two fires 30 ms ticks at descending levels (the
    /// <c>MinimumStartIntensity</c>), phase three fires full-strength pulses of descending length
    /// (the <c>MinimumPulse</c>). Prompts go to standard error like every other attended
    /// diagnostic; the measured boundaries travel in the report.
    /// </remarks>
    private static async Task<AttendedPluginActionReport> RunHapticSweepAsync(
        IDevicePlugin plugin,
        TestPluginHostAdapter host,
        AttendedPluginActionRequest request,
        CancellationToken cancellationToken)
    {
        const AttendedPluginActionKind kind = AttendedPluginActionKind.HapticSweep;
        if (!TrySelectRole(host, CapabilityRole.HapticSink, request.InstanceId,
            out CapabilityDescriptorSet? descriptorSet,
            out CapabilityDescriptor? descriptor, out string? selectionError))
        {
            return Failed(kind, selectionError!);
        }

        long controllerGeneration;
        try
        {
            controllerGeneration = NextCycleGeneration(host);
        }
        catch (OverflowException)
        {
            return Failed(kind, "A fresh controller cycle generation could not be allocated.");
        }

        bool managementAttempted = false;
        bool managementEnabled = false;
        bool availabilityObserved = false;
        bool stopAttempted = false;
        bool stopSent = false;
        bool restorationVerified = false;
        PluginControllerRelease? release = null;
        AttendedHapticSweepReport? sweep = null;
        string? error = null;
        try
        {
            managementAttempted = true;
            await plugin.SetControllerManagementAsync(
                new PluginControllerManagementContext(
                    Enabled: true,
                    controllerGeneration,
                    DateTimeOffset.UtcNow + SweepBudget),
                cancellationToken).ConfigureAwait(false);
            managementEnabled = true;
            availabilityObserved = IsAvailableAtGeneration(
                host,
                descriptorSet!,
                descriptor!,
                controllerGeneration);
            if (!availabilityObserved)
            {
                AppendError(ref error,
                    $"The plugin did not publish {CapabilityRole.HapticSink} as available for controller generation {controllerGeneration}.");
            }
            else
            {
                sweep = await RunSweepPhasesAsync(plugin, host, controllerGeneration, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            AppendError(ref error, $"{ActionLabel(kind)} failed: {exception.Message}");
        }
        finally
        {
            stopAttempted = true;
            try
            {
                using CancellationTokenSource stopDeadline = Deadline();
                await plugin.ApplyHapticOutputAsync(
                    HapticOutputFrame.Stop(controllerGeneration, DateTimeOffset.UtcNow),
                    stopDeadline.Token).ConfigureAwait(false);
                stopSent = true;
            }
            catch (Exception exception)
            {
                AppendError(ref error, $"Haptic zero-output cleanup failed: {exception.Message}");
            }

            if (managementAttempted)
            {
                try
                {
                    using CancellationTokenSource releaseDeadline = Deadline();
                    release = await plugin.ReleaseControllerAsync(
                        new PluginControllerReleaseContext(
                            HandoffScope.ControllerOnly,
                            DateTimeOffset.UtcNow + ActionBudget),
                        releaseDeadline.Token).ConfigureAwait(false);
                    restorationVerified = IsVerifiedRelease(release);
                    if (!restorationVerified)
                    {
                        AppendError(ref error,
                            $"Controller topology restore was not verified ({release.Step}, {release.Result}).");
                    }
                }
                catch (Exception exception)
                {
                    AppendError(ref error, $"Controller topology restore failed: {exception.Message}");
                }
            }
        }

        return new AttendedPluginActionReport
        {
            Kind = kind,
            Passed = managementEnabled && availabilityObserved && sweep is { Completed: true }
                && stopSent && restorationVerified && error is null,
            CapabilityId = descriptor!.CapabilityId,
            InstanceId = descriptor.InstanceId,
            HapticStopAttempted = stopAttempted,
            HapticStopSent = stopSent,
            ControllerManagementEnabled = managementEnabled,
            ControllerAvailabilityObserved = availabilityObserved,
            ControllerRelease = release,
            RestorationVerified = restorationVerified,
            HapticSweep = sweep,
            Error = error,
        };
    }

    private static async Task<AttendedHapticSweepReport> RunSweepPhasesAsync(
        IDevicePlugin plugin,
        TestPluginHostAdapter host,
        long controllerGeneration,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + SweepBudget;
        SweepStepReader steps = new(host);
        Task Apply(float level) => plugin.ApplyHapticOutputAsync(
            new HapticOutputFrame
            {
                TargetGeneration = controllerGeneration,
                LowFrequency = level,
                HighFrequency = level,
                Timestamp = DateTimeOffset.UtcNow,
            },
            cancellationToken).AsTask();
        Task Stop() => plugin.ApplyHapticOutputAsync(
            HapticOutputFrame.Stop(controllerGeneration, DateTimeOffset.UtcNow),
            cancellationToken).AsTask();

        Console.Error.WriteLine();
        Console.Error.WriteLine("Haptic sweep: A on the device steps the sweep, B marks the boundary.");

        // Phase 1: continuous drive, descending. A means "I feel this, go weaker"; B means "I no
        // longer feel it", so the boundary is the last A-confirmed level.
        Console.Error.WriteLine();
        Console.Error.WriteLine("Phase 1/3 - continuous strength. Rumble is running; A = felt, step weaker; B = can no longer feel it.");
        float? continuousFloor = null;
        for (int index = 0; index < SweepLevels.Length; index++)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"  level {index + 1}/{SweepLevels.Length}: {SweepLevels[index]:F3}"));
            await Apply(SweepLevels[index]).ConfigureAwait(false);
            if (await steps.WaitAsync(deadline, cancellationToken).ConfigureAwait(false) == SweepStep.Boundary)
            {
                break;
            }

            continuousFloor = SweepLevels[index];
        }

        await Stop().ConfigureAwait(false);

        // Phases 2 and 3: A fires the next pulse (confirming the previous one was felt); B marks
        // the previous pulse as the last one felt.
        Console.Error.WriteLine();
        Console.Error.WriteLine("Phase 2/3 - 30 ms ticks. A = fire next weaker tick; B = did NOT feel the last one.");
        float? tickFloor = await RunPulsePhaseAsync(
            steps,
            SweepLevels,
            level => FirePulseAsync(Apply, Stop, TimeSpan.FromMilliseconds(TickPulseMilliseconds), level, cancellationToken),
            static (levels, lastFelt) => lastFelt >= 0 ? levels[lastFelt] : (float?)null,
            deadline,
            cancellationToken).ConfigureAwait(false);

        Console.Error.WriteLine();
        Console.Error.WriteLine("Phase 3/3 - full-strength pulses. A = fire next shorter pulse; B = did NOT feel the last one.");
        float? pulseBoundary = await RunPulsePhaseAsync(
            steps,
            [.. SweepPulseMilliseconds.Select(static ms => (float)ms)],
            ms => FirePulseAsync(Apply, Stop, TimeSpan.FromMilliseconds(ms), 1f, cancellationToken),
            static (milliseconds, lastFelt) => lastFelt >= 0 ? milliseconds[lastFelt] : (float?)null,
            deadline,
            cancellationToken).ConfigureAwait(false);

        AttendedHapticSweepReport report = new()
        {
            ContinuousFloor = continuousFloor,
            TickFloor = tickFloor,
            MinimumPulse = pulseBoundary is { } milliseconds
                ? TimeSpan.FromMilliseconds(milliseconds)
                : null,
            Completed = true,
        };
        Console.Error.WriteLine();
        Console.Error.WriteLine("Sweep complete. Declare in HapticCapabilities:");
        Console.Error.WriteLine(FormattableString.Invariant(
            $"  MinimumStartIntensity = {report.TickFloor ?? SweepLevels[^1]:F3}f"));
        Console.Error.WriteLine(FormattableString.Invariant(
            $"  MinimumPulse = TimeSpan.FromMilliseconds({(report.MinimumPulse ?? TimeSpan.FromMilliseconds(SweepPulseMilliseconds[^1])).TotalMilliseconds:F0})"));
        return report;
    }

    private static async Task<float?> RunPulsePhaseAsync(
        SweepStepReader steps,
        float[] values,
        Func<float, Task> fire,
        Func<float[], int, float?> boundary,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        int lastFired = -1;
        while (lastFired < values.Length - 1)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"  press A for step {lastFired + 2}/{values.Length} ({values[lastFired + 1]:F3})"));
            if (await steps.WaitAsync(deadline, cancellationToken).ConfigureAwait(false) == SweepStep.Boundary)
            {
                // The boundary refers to the last fired pulse; everything before it was felt.
                return boundary(values, lastFired - 1);
            }

            lastFired++;
            await fire(values[lastFired]).ConfigureAwait(false);
        }

        // The operator felt every pulse the sweep offered.
        return boundary(values, lastFired);
    }

    private static async Task FirePulseAsync(
        Func<float, Task> apply,
        Func<Task> stop,
        TimeSpan duration,
        float level,
        CancellationToken cancellationToken)
    {
        await apply(level).ConfigureAwait(false);
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stop().ConfigureAwait(false);
        }
    }

    private enum SweepStep
    {
        Advance,
        Boundary,
    }

    /// <summary>Turns the plugin's own published controller samples into sweep steps.</summary>
    /// <remarks>
    /// Polls the recording host adapter for fresh samples and reports rising edges of A and B.
    /// The device under calibration is also the input device — that is the point: no keyboard
    /// reach, and the samples prove the controller path is alive while the sweep runs.
    /// </remarks>
    private sealed class SweepStepReader(TestPluginHostAdapter host)
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
        private readonly TestPluginHostAdapter _host = host;
        private int _consumed = host.ControllerSamples.Count;
        private bool _aHeld;
        private bool _bHeld;

        public async Task<SweepStep> WaitAsync(
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                IReadOnlyList<CanonicalControllerSample> samples = _host.ControllerSamples;
                for (; _consumed < samples.Count; _consumed++)
                {
                    CanonicalButtons buttons = samples[_consumed].Buttons;
                    bool a = (buttons & CanonicalButtons.A) != 0;
                    bool b = (buttons & CanonicalButtons.B) != 0;
                    bool aEdge = a && !_aHeld;
                    bool bEdge = b && !_bHeld;
                    _aHeld = a;
                    _bHeld = b;
                    if (bEdge)
                    {
                        _consumed++;
                        return SweepStep.Boundary;
                    }

                    if (aEdge)
                    {
                        _consumed++;
                        return SweepStep.Advance;
                    }
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The haptic sweep timed out waiting for an A or B press.");
        }
    }

    private static bool TrySelectRole(
        TestPluginHostAdapter host,
        CapabilityRole role,
        string? instanceId,
        out CapabilityDescriptorSet? descriptorSet,
        out CapabilityDescriptor? descriptor,
        out string? error)
    {
        descriptorSet = host.DescriptorSets.LastOrDefault();
        descriptor = null;
        if (descriptorSet is null)
        {
            error = "The plugin published no capability descriptors.";
            return false;
        }

        CapabilityDescriptor[] matches = [.. descriptorSet.Descriptors.Where(candidate =>
            candidate.Role == role
            && (instanceId is null
                || string.Equals(candidate.InstanceId, instanceId, StringComparison.Ordinal)))];
        if (matches.Length == 0)
        {
            error = instanceId is null
                ? $"The plugin did not publish a {role} capability."
                : $"The plugin did not publish {role} for instance '{instanceId}'.";
            return false;
        }

        if (matches.Length != 1)
        {
            error = $"The plugin published multiple {role} instances; select one exact instance ID.";
            return false;
        }

        descriptor = matches[0];
        error = null;
        return true;
    }

    private static bool IsAvailableAtGeneration(
        TestPluginHostAdapter host,
        CapabilityDescriptorSet descriptorSet,
        CapabilityDescriptor descriptor,
        long controllerGeneration)
    {
        CapabilityState? state = host.CapabilityStates.LastOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, descriptor.CapabilityId, StringComparison.Ordinal)
            && string.Equals(candidate.InstanceId, descriptor.InstanceId, StringComparison.Ordinal)
            && candidate.DescriptorGeneration == descriptorSet.Generation
            && candidate.CycleGeneration == controllerGeneration);
        if (state is not { Available: true }
            || state.Quality is not (HardwareStateQuality.Observed or HardwareStateQuality.Verified))
        {
            return false;
        }

        return descriptor.ValueKind switch
        {
            CapabilityValueKind.None => state.ObservedValue is null
                || state.ObservedValue.Kind is CapabilityValueKind.None,
            CapabilityValueKind.Boolean => state.ObservedValue is
            { Kind: CapabilityValueKind.Boolean, BooleanValue: true },
            CapabilityValueKind.Choice => state.ObservedValue is
            { Kind: CapabilityValueKind.Choice, ChoiceValue: "plugin" },
            _ => false,
        };
    }

    private static bool TryParseValue(
        CapabilityDescriptor descriptor,
        string? text,
        out CapabilityValue? value,
        out string? error)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Capability value action requires an explicit semantic value.";
            return false;
        }

        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Boolean:
                if (!bool.TryParse(text, out bool boolean))
                {
                    error = "Boolean capability values must be true or false.";
                    return false;
                }

                value = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Boolean,
                    BooleanValue = boolean,
                };
                break;
            case CapabilityValueKind.Integer:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
                {
                    error = "Integer capability values must use invariant whole-number syntax.";
                    return false;
                }

                int minimum = descriptor.Minimum ?? int.MinValue;
                int maximum = descriptor.Maximum ?? int.MaxValue;
                if (integer < minimum || integer > maximum)
                {
                    error = $"The requested value must be between {minimum} and {maximum}.";
                    return false;
                }

                if (descriptor.Step is > 0
                    && ((long)integer - (descriptor.Minimum ?? 0)) % descriptor.Step.Value != 0)
                {
                    error = $"The requested value must align to step {descriptor.Step.Value}.";
                    return false;
                }

                value = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Integer,
                    IntegerValue = integer,
                };
                break;
            case CapabilityValueKind.Choice:
                if (!descriptor.Choices.Any(choice => string.Equals(
                    choice.Value,
                    text,
                    StringComparison.Ordinal)))
                {
                    error = "The requested choice is not present in the current descriptor.";
                    return false;
                }

                value = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Choice,
                    ChoiceValue = text,
                };
                break;
            case CapabilityValueKind.Color:
                string colorText = text.StartsWith('#') ? text[1..] : text;
                if (colorText.Length != 6
                    || !int.TryParse(colorText, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out int color))
                {
                    error = "Color capability values must use RRGGBB or #RRGGBB.";
                    return false;
                }

                value = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Color,
                    ColorValue = color,
                };
                break;
            case CapabilityValueKind.Curve:
                if (!TryParseCurve(text, out IReadOnlyList<CurvePoint>? points))
                {
                    error = "Curve values must be 1-32 strictly ordered input:output pairs separated by commas.";
                    return false;
                }

                value = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Curve,
                    CurveValue = points,
                };
                break;
            case CapabilityValueKind.Text:
                string? textError = null;
                if (descriptor.MaximumLength is not > 0
                    || !PlainText.TryValidate(
                        text,
                        descriptor.MaximumLength.Value,
                        "value",
                        out textError))
                {
                    error = textError ?? "The text capability did not declare a valid length bound.";
                    return false;
                }

                value = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Text,
                    TextValue = text,
                };
                break;
            case CapabilityValueKind.None:
            default:
                error = "The selected capability does not accept a semantic value.";
                return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseCurve(string text, out IReadOnlyList<CurvePoint> points)
    {
        string[] pairs = text.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pairs.Length is < 1 or > 32)
        {
            points = [];
            return false;
        }

        var parsed = new List<CurvePoint>(pairs.Length);
        int? previousInput = null;
        foreach (string pair in pairs)
        {
            string[] values = pair.Split(':', StringSplitOptions.TrimEntries);
            if (values.Length != 2
                || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int input)
                || !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int output)
                || previousInput is not null && input <= previousInput.Value)
            {
                points = [];
                return false;
            }

            parsed.Add(new CurvePoint(input, output));
            previousInput = input;
        }

        points = parsed;
        return true;
    }

    private static CapabilityCommand Command(
        CapabilityDescriptorSet descriptorSet,
        CapabilityDescriptor descriptor,
        CapabilityValue value) => new()
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            RequestedValue = value,
            ExpectedDescriptorGeneration = descriptorSet.Generation,
            ExpectedCycleGeneration = descriptorSet.CycleGeneration,
            Deadline = DateTimeOffset.UtcNow + ActionBudget,
        };

    private static bool IsVerified(
        CapabilityCommandResult result,
        Guid commandId,
        CapabilityValue expected) =>
        result.CommandId == commandId
        && result.Outcome is CommandOutcome.AppliedVerified
        && result.ReadbackValue is not null
        && ValuesEqual(result.ReadbackValue, expected);

    private static bool ValuesEqual(CapabilityValue left, CapabilityValue right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }

        return left.Kind switch
        {
            CapabilityValueKind.Boolean => left.BooleanValue == right.BooleanValue,
            CapabilityValueKind.Integer => left.IntegerValue == right.IntegerValue,
            CapabilityValueKind.Choice => string.Equals(
                left.ChoiceValue,
                right.ChoiceValue,
                StringComparison.Ordinal),
            CapabilityValueKind.Color => left.ColorValue == right.ColorValue,
            CapabilityValueKind.Curve => left.CurveValue.SequenceEqual(right.CurveValue),
            CapabilityValueKind.Text => string.Equals(
                left.TextValue,
                right.TextValue,
                StringComparison.Ordinal),
            CapabilityValueKind.None => true,
            _ => false,
        };
    }

    private static long NextCycleGeneration(TestPluginHostAdapter host)
    {
        long generation = host.CycleGeneration;
        foreach (CapabilityDescriptorSet descriptors in host.DescriptorSets)
        {
            generation = Math.Max(generation, descriptors.CycleGeneration);
        }

        foreach (CapabilityState state in host.CapabilityStates)
        {
            generation = Math.Max(generation, state.CycleGeneration);
        }

        return checked(generation + 1);
    }

    private static bool IsVerifiedRelease(PluginControllerRelease release) =>
        release.Result is ControllerHandoffResult.ReleasedVerified
        && release.Step is ControllerHandoffStep.TopologyVerified
            or ControllerHandoffStep.WsgmStateRemoved;

    private static string DescribeResult(
        string operation,
        CapabilityCommandResult result,
        Guid expectedCommandId,
        CapabilityValue expectedValue)
    {
        if (result.CommandId != expectedCommandId)
        {
            return $"{operation} returned the wrong command ID.";
        }

        if (result.Outcome is not CommandOutcome.AppliedVerified)
        {
            return $"{operation} was not verified ({result.Outcome}): {result.Reason?.Detail ?? "no detail"}.";
        }

        return result.ReadbackValue is null || !ValuesEqual(result.ReadbackValue, expectedValue)
            ? $"{operation} returned a mismatched readback value."
            : $"{operation} was not verified.";
    }

    private static string ActionLabel(AttendedPluginActionKind kind) => kind switch
    {
        AttendedPluginActionKind.HapticPulse => "Haptic pulse",
        AttendedPluginActionKind.ControllerManagement => "Controller management",
        _ => "Attended action",
    };

    private static CancellationTokenSource Deadline()
    {
        var deadline = new CancellationTokenSource();
        deadline.CancelAfter(ActionBudget);
        return deadline;
    }

    private static void AppendError(ref string? error, string detail) =>
        error = error is null ? detail : $"{error} {detail}";

    private static AttendedPluginActionReport Failed(
        AttendedPluginActionKind kind,
        string error,
        string? capabilityId = null,
        string? instanceId = null,
        CapabilityValue? original = null,
        CapabilityValue? requested = null) => new()
        {
            Kind = kind,
            Passed = false,
            CapabilityId = capabilityId,
            InstanceId = instanceId,
            OriginalValue = original,
            RequestedValue = requested,
            RestorationVerified = false,
            Error = error,
        };
}
