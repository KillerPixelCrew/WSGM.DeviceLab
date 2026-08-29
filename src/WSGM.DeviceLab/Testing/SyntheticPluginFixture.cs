using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;

namespace WSGM.DeviceLab.Testing;

/// <summary>Result of the built-in, hardware-free plugin API fixture.</summary>
internal sealed record SyntheticPluginFixtureReport
{
    /// <summary>Whether every exact detection and lifecycle check passed.</summary>
    public required bool Passed { get; init; }

    /// <summary>Named checks in execution order.</summary>
    public IReadOnlyList<string> Checks { get; init; } = [];
}

/// <summary>Exercises the public plugin API with a non-handheld synthetic dock accessory.</summary>
internal static class SyntheticPluginFixture
{
    /// <summary>Runs exact detection, partial publication, canonical I/O, cancellation, and cleanup.</summary>
    /// <param name="cancellationToken">Cancels the hardware-free fixture.</param>
    /// <returns>Named checks and aggregate result.</returns>
    public static async Task<SyntheticPluginFixtureReport> RunAsync(CancellationToken cancellationToken)
    {
        List<string> checks = [];
        await using SyntheticDockPlugin plugin = new();
        PluginDetectionResult mismatch = await plugin.DetectAsync(
            new PluginDetectionContext
            {
                Identity = new DeviceIdentitySnapshot
                {
                    SystemManufacturer = "Micro-Star International Co., Ltd.",
                    BaseboardProduct = "MS-1T52",
                },
            },
            cancellationToken).ConfigureAwait(false);
        Check(!mismatch.Matched, "different-device-rejected", checks);

        PluginDetectionResult exact = await plugin.DetectAsync(
            new PluginDetectionContext
            {
                Identity = SyntheticDockPlugin.Identity,
            },
            cancellationToken).ConfigureAwait(false);
        Check(exact.Matched && exact.DeviceDefinitionId == SyntheticDockPlugin.DeviceId,
            "synthetic-dock-exact-match", checks);

        TestPluginHostAdapter host = new(cycleGeneration: 7);
        PluginStartResult start = await plugin.StartAsync(
            new PluginStartContext
            {
                Host = host,
                CycleGeneration = 7,
                DeviceDefinitionId = SyntheticDockPlugin.DeviceId,
                StateDirectory = PathForFixtureOnly(),
                ControllerManagementEnabled = false,
            },
            cancellationToken).ConfigureAwait(false);
        PluginPublicationSummary activation = PluginPublicationSummary.From(host);
        Check(start.State is PluginOperationalState.Degraded
            && start.Reason?.Code is CapabilityReasonCode.PrerequisiteMissing
            && activation.DescriptorSets == 1
            && activation.CapabilityStates == 2
            && host.CapabilityStates.Any(state => !state.Available
                && state.Reason?.Code is CapabilityReasonCode.PrerequisiteMissing),
            "partial-capability-availability", checks);
        Check(activation.ControllerSamples == 1
            && host.ControllerSamples[0].Buttons.HasFlag(CanonicalButtons.A)
            && host.ControllerSamples[0].Motion is { HasGyro: true },
            "canonical-input-published", checks);

        CapabilityCommandResult applied = await plugin.ExecuteCommandAsync(
            Command(expectedDeviceGeneration: 7),
            cancellationToken).ConfigureAwait(false);
        Check(applied.Outcome is CommandOutcome.AppliedVerified
            && applied.ReadbackValue?.BooleanValue is true,
            "boolean-command-readback", checks);

        HapticOutputFrame output = new()
        {
            TargetGeneration = 4,
            LowFrequency = 0.75f,
            HighFrequency = 0.25f,
            LeftTrigger = 0.5f,
            Timestamp = DateTimeOffset.UtcNow,
        };
        await plugin.ApplyHapticOutputAsync(output, cancellationToken).ConfigureAwait(false);
        Check(plugin.LastHapticOutput is
        {
            LowFrequency: 0.75f,
            HighFrequency: 0.25f,
            LeftTrigger: 0,
            RightTrigger: 0,
        },
            "canonical-output-applied", checks);

        using (CancellationTokenSource cancelled = new())
        {
            cancelled.Cancel();
            bool observed = false;
            try
            {
                _ = await plugin.GetDiagnosticsAsync(cancelled.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                observed = true;
            }

            Check(observed, "cancellation-observed", checks);
        }

        CapabilityCommandResult stale = await plugin.ExecuteCommandAsync(
            Command(expectedDeviceGeneration: 6),
            cancellationToken).ConfigureAwait(false);
        Check(stale.Outcome is CommandOutcome.Rejected
            && stale.Reason?.Code is CapabilityReasonCode.GenerationChanged,
            "stale-generation-rejected", checks);

        PluginStopResult stop = await plugin.StopAsync(
            new PluginStopContext(
                PluginStopReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(5)),
            cancellationToken).ConfigureAwait(false);
        PluginDiagnostics diagnostics = await plugin.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        Check(stop.Status is PluginStopStatus.Clean
            && host.CapabilityStates[^1].ObservedValue?.BooleanValue is false
            && plugin.LastHapticOutput?.IsSilent is true,
            "stop-restores-original-state-and-output", checks);
        Check(diagnostics.Values.TryGetValue("state", out string? state) && state == "stopped"
            && diagnostics.Values.TryGetValue("restorations", out string? restorations)
            && restorations == "1",
            "cleanup-diagnostics-reported", checks);

        return new SyntheticPluginFixtureReport
        {
            Passed = checks.Count == 10,
            Checks = checks,
        };
    }

    private static CapabilityCommand Command(long expectedDeviceGeneration) => new()
    {
        CommandId = Guid.NewGuid(),
        CapabilityId = SyntheticDockPlugin.BeaconCapabilityId,
        RequestedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Boolean,
            BooleanValue = true,
        },
        ExpectedDescriptorGeneration = 1,
        ExpectedCycleGeneration = expectedDeviceGeneration,
        Deadline = DateTimeOffset.UtcNow.AddSeconds(5),
    };

    private static void Check(bool condition, string name, ICollection<string> checks)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Synthetic plugin check failed: {name}.");
        }

        checks.Add(name);
    }

    private static string PathForFixtureOnly() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsgm-device-synthetic-state-not-written");
}

internal sealed class SyntheticDockPlugin : IDevicePlugin
{
    internal const string DeviceId = "synthetic.dock-x1";
    internal const string BeaconCapabilityId = "dock.beacon";
    internal const string UnavailableSensorCapabilityId = "dock.ambient-temperature";

    private static readonly HapticCapabilities OutputCapabilities = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        LeftTrigger = OutputChannelSupport.Unsupported,
        RightTrigger = OutputChannelSupport.Unsupported,
    };

    private TestPluginHostAdapter? _host;
    private long _cycleGeneration;
    private bool _beaconValue;
    private bool _capturedBeaconValue;
    private bool _active;
    private int _restorationCount;

    internal HapticOutputFrame? LastHapticOutput { get; private set; }

    internal static DeviceIdentitySnapshot Identity { get; } = new()
    {
        SystemManufacturer = "Contoso Devices",
        BaseboardProduct = "DOCK-X1",
        SystemSku = "DOCK-X1-DEV",
        BiosVersion = "1.0.0",
        UsbEndpoints =
        [
            new UsbEndpointObservation
            {
                VendorId = "CAFE",
                ProductId = "BEEF",
                DeviceRelease = "0100",
            },
        ],
    };

    public string PackageId => "wsgm.device.synthetic.dock-x1";

    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool matched = IdentityText.Matches(context.Identity.SystemManufacturer, Identity.SystemManufacturer)
            && IdentityText.Matches(context.Identity.BaseboardProduct, Identity.BaseboardProduct)
            && context.Identity.UsbEndpoints.Count == 1
            && string.Equals(context.Identity.UsbEndpoints[0].VendorId, "CAFE", StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.Identity.UsbEndpoints[0].ProductId, "BEEF", StringComparison.OrdinalIgnoreCase);
        return ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = matched,
            DeviceDefinitionId = matched ? DeviceId : null,
            Reason = matched ? null : new CapabilityReason(CapabilityReasonCode.Unsupported),
        });
    }

    public async ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(context.DeviceDefinitionId, DeviceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Synthetic dock activation requires its exact definition.");
        }

        _host = context.Host as TestPluginHostAdapter
            ?? throw new InvalidOperationException("Synthetic fixture requires the Device Lab test adapter.");
        _cycleGeneration = context.CycleGeneration;
        _capturedBeaconValue = false;
        _beaconValue = _capturedBeaconValue;
        LastHapticOutput = null;
        _restorationCount = 0;
        _active = true;
        await context.Host.PublishDescriptorsAsync(
            new CapabilityDescriptorSet
            {
                Generation = 1,
                CycleGeneration = context.CycleGeneration,
                Descriptors =
                [
                    new CapabilityDescriptor
                    {
                        CapabilityId = BeaconCapabilityId,
                        Role = CapabilityRole.GenericToggle,
                        ValueKind = CapabilityValueKind.Boolean,
                        Display = new CapabilityDisplay
                        {
                            Key = DisplayKey.Custom,
                            CustomLabel = "Dock beacon",
                        },
                        SupportsRead = true,
                        SupportsWrite = true,
                        Persistence = CapabilityPersistence.Volatile,
                    },
                    new CapabilityDescriptor
                    {
                        CapabilityId = UnavailableSensorCapabilityId,
                        Role = CapabilityRole.Telemetry,
                        ValueKind = CapabilityValueKind.Integer,
                        Display = new CapabilityDisplay
                        {
                            Key = DisplayKey.Custom,
                            CustomLabel = "Ambient temperature",
                        },
                        SupportsRead = true,
                        Unit = CapabilityUnit.Celsius,
                        Persistence = CapabilityPersistence.Volatile,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);
        await context.Host.PublishCapabilityStateAsync(State(_beaconValue), cancellationToken).ConfigureAwait(false);
        await context.Host.PublishCapabilityStateAsync(
            new CapabilityState
            {
                CapabilityId = UnavailableSensorCapabilityId,
                Available = false,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.PrerequisiteMissing,
                    "Synthetic dock temperature provider is intentionally absent."),
                Quality = HardwareStateQuality.Unknown,
                DescriptorGeneration = 1,
                CycleGeneration = _cycleGeneration,
            },
            cancellationToken).ConfigureAwait(false);
        await context.Host.PublishControllerSampleAsync(
            new CanonicalControllerSample
            {
                Sequence = 1,
                CycleGeneration = _cycleGeneration,
                Timestamp = DateTimeOffset.UtcNow,
                Buttons = CanonicalButtons.A,
                LeftStickX = 0.25f,
                Motion = new MotionSample
                {
                    HasGyro = true,
                    GyroZ = 12.5f,
                    SensorTimestamp = DateTimeOffset.UtcNow,
                },
            },
            cancellationToken).ConfigureAwait(false);
        return new PluginStartResult
        {
            State = PluginOperationalState.Degraded,
            Reason = new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The optional synthetic temperature provider is intentionally absent."),
        };
    }

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_active
            || command.ExpectedCycleGeneration != _cycleGeneration
            || command.ExpectedDescriptorGeneration != 1)
        {
            return Result(command, CommandOutcome.Rejected, CapabilityReasonCode.GenerationChanged);
        }

        if (!string.Equals(command.CapabilityId, BeaconCapabilityId, StringComparison.Ordinal)
            || command.RequestedValue is not { Kind: CapabilityValueKind.Boolean, BooleanValue: not null })
        {
            return Result(command, CommandOutcome.Rejected, CapabilityReasonCode.Unsupported);
        }

        _beaconValue = command.RequestedValue.BooleanValue.Value;
        await _host!.PublishCapabilityStateAsync(State(_beaconValue), cancellationToken).ConfigureAwait(false);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedVerified,
            ReadbackValue = command.RequestedValue,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    public ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cycleGeneration = context.CycleGeneration;
        _active = true;
        return ValueTask.FromResult(new PluginStartResult
        {
            State = PluginOperationalState.Degraded,
            Reason = new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The optional synthetic temperature provider is intentionally absent."),
        });
    }

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginDiagnostics
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["state"] = _active ? "active" : "stopped",
                ["beacon"] = _beaconValue ? "on" : "off",
                ["haptic-output"] = LastHapticOutput is null
                    ? "not-observed"
                    : LastHapticOutput.IsSilent ? "silent" : "active",
                ["restorations"] = _restorationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        LastHapticOutput = OutputCapabilities.Clamp(frame);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginControllerRelease
        {
            Step = ControllerHandoffStep.WsgmStateRemoved,
            Result = ControllerHandoffResult.ReleasedVerified,
        });
    }

    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_active && _host is not null)
        {
            _beaconValue = _capturedBeaconValue;
            await _host.PublishCapabilityStateAsync(State(_beaconValue), cancellationToken)
                .ConfigureAwait(false);
            long targetGeneration = LastHapticOutput?.TargetGeneration ?? 0;
            LastHapticOutput = HapticOutputFrame.Stop(targetGeneration, DateTimeOffset.UtcNow);
            _restorationCount++;
        }

        _active = false;
        return new PluginStopResult { Status = PluginStopStatus.Clean };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private CapabilityState State(bool value) => new()
    {
        CapabilityId = BeaconCapabilityId,
        Available = true,
        ObservedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Boolean,
            BooleanValue = value,
        },
        Quality = HardwareStateQuality.Verified,
        ObservedAt = DateTimeOffset.UtcNow,
        DescriptorGeneration = 1,
        CycleGeneration = _cycleGeneration,
    };

    private static CapabilityCommandResult Result(
        CapabilityCommand command,
        CommandOutcome outcome,
        CapabilityReasonCode reason) => new()
        {
            CommandId = command.CommandId,
            Outcome = outcome,
            Reason = new CapabilityReason(reason),
            CompletedAt = DateTimeOffset.UtcNow,
        };
}
