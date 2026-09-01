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

    /// <summary>Failure detail when the action or mandatory restoration was not verified.</summary>
    public string? Error { get; init; }
}

/// <summary>Executes the three explicit semantic actions available behind Device Lab's attended gate.</summary>
internal static class AttendedPluginActionRunner
{
    private static readonly TimeSpan ActionBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HapticPulseDuration = TimeSpan.FromMilliseconds(250);

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
