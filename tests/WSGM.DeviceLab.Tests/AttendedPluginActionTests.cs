using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;
using WSGM.DeviceLab.Testing;

namespace WSGM.Device.Tests;

public sealed class AttendedPluginActionTests
{
    [Fact]
    public async Task CapabilityValue_ObservedOriginalIsVerifiedThenAppliedAndRestored()
    {
        TestPluginHostAdapter host = await CapabilityHostAsync(HardwareStateQuality.Observed);
        var plugin = new ActionTestPlugin(host);

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            CapabilityRequest("24"),
            CancellationToken.None);

        Assert.True(report.Passed, report.Error);
        Assert.True(report.OriginalValueVerified);
        Assert.NotNull(report.OriginalVerification);
        Assert.Equal(3, plugin.Commands.Count);
        Assert.Equal(18, plugin.Commands[0].RequestedValue!.IntegerValue);
        Assert.Equal(24, plugin.Commands[1].RequestedValue!.IntegerValue);
        Assert.Equal(18, plugin.Commands[2].RequestedValue!.IntegerValue);
        Assert.Equal(CommandOutcome.AppliedVerified, report.Apply!.Outcome);
        Assert.True(report.RestorationVerified);
    }

    [Fact]
    public async Task CapabilityValue_StaleGenerationRejectionStillRestoresOriginal()
    {
        TestPluginHostAdapter host = await CapabilityHostAsync(HardwareStateQuality.Verified);
        var plugin = new ActionTestPlugin(host)
        {
            CommandHandler = (command, call) => call == 1
                ? Result(
                    command,
                    CommandOutcome.Rejected,
                    null,
                    new CapabilityReason(
                        CapabilityReasonCode.GenerationChanged,
                        "Synthetic stale generation."))
                : Verified(command),
        };

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            CapabilityRequest("24"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(CommandOutcome.Rejected, report.Apply!.Outcome);
        Assert.True(report.RestorationVerified);
        Assert.Equal(2, plugin.Commands.Count);
        Assert.Equal(18, plugin.Commands[1].RequestedValue!.IntegerValue);
    }

    [Fact]
    public async Task CapabilityValue_AppliedUnverifiedFailsDespiteVerifiedRestore()
    {
        TestPluginHostAdapter host = await CapabilityHostAsync(HardwareStateQuality.Verified);
        var plugin = new ActionTestPlugin(host)
        {
            CommandHandler = (command, call) => call == 1
                ? Result(command, CommandOutcome.AppliedUnverified)
                : Verified(command),
        };

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            CapabilityRequest("24"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(CommandOutcome.AppliedUnverified, report.Apply!.Outcome);
        Assert.True(report.RestorationVerified);
        Assert.Contains("not verified", report.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapabilityValue_UnverifiedRestoreFailsTheAction()
    {
        TestPluginHostAdapter host = await CapabilityHostAsync(HardwareStateQuality.Verified);
        var plugin = new ActionTestPlugin(host)
        {
            CommandHandler = (command, call) => call == 1
                ? Verified(command)
                : Result(command, CommandOutcome.AppliedUnverified),
        };

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            CapabilityRequest("24"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.False(report.RestorationVerified);
        Assert.Equal(CommandOutcome.AppliedUnverified, report.Restore!.Outcome);
    }

    [Fact]
    public async Task HapticPulse_SendFailureStillSubmitsZeroOutputAndReleasesController()
    {
        TestPluginHostAdapter host = await RoleHostAsync(CapabilityRole.HapticSink);
        var plugin = new ActionTestPlugin(host) { ThrowOnNonSilentHaptic = true };

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            new AttendedPluginActionRequest { Kind = AttendedPluginActionKind.HapticPulse },
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(2, plugin.HapticFrames.Count);
        Assert.False(plugin.HapticFrames[0].IsSilent);
        Assert.True(plugin.HapticFrames[1].IsSilent);
        Assert.True(report.HapticStopAttempted);
        Assert.True(report.HapticStopSent);
        Assert.True(report.RestorationVerified);
        Assert.Equal(1, plugin.ControllerReleaseCalls);
    }

    [Fact]
    public async Task HapticPulse_SuccessRequiresPulseStopAndVerifiedRelease()
    {
        TestPluginHostAdapter host = await RoleHostAsync(CapabilityRole.HapticSink);
        var plugin = new ActionTestPlugin(host);

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            new AttendedPluginActionRequest { Kind = AttendedPluginActionKind.HapticPulse },
            CancellationToken.None);

        Assert.True(report.Passed, report.Error);
        Assert.True(report.HapticPulseSent);
        Assert.True(report.HapticStopSent);
        Assert.True(report.ControllerAvailabilityObserved);
        Assert.True(report.RestorationVerified);
    }

    [Fact]
    public async Task ControllerManagement_UnverifiedTopologyFailsTheAction()
    {
        TestPluginHostAdapter host = await RoleHostAsync(CapabilityRole.ControllerSource);
        var plugin = new ActionTestPlugin(host)
        {
            ControllerRelease = new PluginControllerRelease
            {
                Step = ControllerHandoffStep.TopologyUnverified,
                Result = ControllerHandoffResult.ReleasedUnverified,
            },
        };

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            new AttendedPluginActionRequest
            {
                Kind = AttendedPluginActionKind.ControllerManagement,
            },
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.True(report.ControllerManagementEnabled);
        Assert.False(report.RestorationVerified);
        Assert.Equal(1, plugin.ControllerReleaseCalls);
    }

    [Fact]
    public async Task ControllerManagement_VerifiedTopologyCompletesTheAction()
    {
        TestPluginHostAdapter host = await RoleHostAsync(CapabilityRole.ControllerSource);
        var plugin = new ActionTestPlugin(host);

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            new AttendedPluginActionRequest
            {
                Kind = AttendedPluginActionKind.ControllerManagement,
            },
            CancellationToken.None);

        Assert.True(report.Passed, report.Error);
        Assert.True(report.ControllerManagementEnabled);
        Assert.True(report.ControllerAvailabilityObserved);
        Assert.Equal(ControllerHandoffResult.ReleasedVerified, report.ControllerRelease!.Result);
        Assert.True(report.RestorationVerified);
    }

    [Fact]
    public async Task ControllerAction_RefusesAmbiguousRoleUntilAnExactInstanceIsSelected()
    {
        TestPluginHostAdapter host = await RoleHostAsync(
            CapabilityRole.HapticSink,
            "left",
            "right");
        var plugin = new ActionTestPlugin(host);

        AttendedPluginActionReport ambiguous = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            new AttendedPluginActionRequest { Kind = AttendedPluginActionKind.HapticPulse },
            CancellationToken.None);
        AttendedPluginActionReport selected = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            new AttendedPluginActionRequest
            {
                Kind = AttendedPluginActionKind.HapticPulse,
                InstanceId = "right",
            },
            CancellationToken.None);

        Assert.False(ambiguous.Passed);
        Assert.Contains("multiple", ambiguous.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(selected.Passed, selected.Error);
        Assert.Equal("right", selected.InstanceId);
    }

    [Fact]
    public async Task CapabilityValue_TextIsValidatedAppliedAndRestored()
    {
        var host = new TestPluginHostAdapter(cycleGeneration: 7);
        var descriptor = new CapabilityDescriptor
        {
            CapabilityId = "generic.label",
            Role = CapabilityRole.GenericText,
            SectionId = "general",
            ValueKind = CapabilityValueKind.Text,
            Display = Display("Label"),
            SupportsRead = true,
            SupportsWrite = true,
            MaximumLength = 16,
            Persistence = CapabilityPersistence.Volatile,
        };
        await host.PublishDescriptorsAsync(
            new CapabilityDescriptorSet
            {
                Generation = 4,
                CycleGeneration = 7,
                Descriptors = [descriptor],
            },
            CancellationToken.None);
        await host.PublishCapabilityStateAsync(
            new CapabilityState
            {
                CapabilityId = descriptor.CapabilityId,
                Available = true,
                ObservedValue = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Text,
                    TextValue = "Dock",
                },
                Quality = HardwareStateQuality.Verified,
                DescriptorGeneration = 4,
                CycleGeneration = 7,
            },
            CancellationToken.None);
        var plugin = new ActionTestPlugin(host);

        AttendedPluginActionReport report = await AttendedPluginActionRunner.RunAsync(
            plugin,
            host,
            new AttendedPluginActionRequest
            {
                Kind = AttendedPluginActionKind.CapabilityValue,
                CapabilityId = descriptor.CapabilityId,
                ValueText = "Desk",
            },
            CancellationToken.None);

        Assert.True(report.Passed, report.Error);
        Assert.Equal("Desk", report.Apply!.ReadbackValue!.TextValue);
        Assert.Equal("Dock", report.Restore!.ReadbackValue!.TextValue);
    }

    private static AttendedPluginActionRequest CapabilityRequest(string value) => new()
    {
        Kind = AttendedPluginActionKind.CapabilityValue,
        CapabilityId = "power.sustained",
        ValueText = value,
    };

    private static async Task<TestPluginHostAdapter> CapabilityHostAsync(HardwareStateQuality quality)
    {
        var host = new TestPluginHostAdapter(cycleGeneration: 7);
        await host.PublishDescriptorsAsync(
            new CapabilityDescriptorSet
            {
                Generation = 4,
                CycleGeneration = 7,
                Descriptors =
                [
                    new CapabilityDescriptor
                    {
                        CapabilityId = "power.sustained",
                        Role = CapabilityRole.PowerSustainedLimit,
                        ValueKind = CapabilityValueKind.Integer,
                        Display = Display("Synthetic power"),
                        SupportsRead = true,
                        SupportsWrite = true,
                        Minimum = 8,
                        Maximum = 30,
                        Step = 1,
                        Persistence = CapabilityPersistence.Volatile,
                    },
                ],
            },
            CancellationToken.None);
        await host.PublishCapabilityStateAsync(
            new CapabilityState
            {
                CapabilityId = "power.sustained",
                Available = true,
                ObservedValue = Integer(18),
                Quality = quality,
                ObservedAt = DateTimeOffset.UnixEpoch,
                DescriptorGeneration = 4,
                CycleGeneration = 7,
            },
            CancellationToken.None);
        return host;
    }

    private static async Task<TestPluginHostAdapter> RoleHostAsync(
        CapabilityRole role,
        params string?[] instanceIds)
    {
        var host = new TestPluginHostAdapter(cycleGeneration: 7);
        string capabilityId = role is CapabilityRole.HapticSink ? "controller.haptic" : "controller.source";
        if (instanceIds.Length == 0)
        {
            instanceIds = [null];
        }
        await host.PublishDescriptorsAsync(
            new CapabilityDescriptorSet
            {
                Generation = 4,
                CycleGeneration = 7,
                Descriptors = [.. instanceIds.Select(instanceId =>
                    new CapabilityDescriptor
                    {
                        CapabilityId = capabilityId,
                        InstanceId = instanceId,
                        Role = role,
                        ValueKind = role is CapabilityRole.HapticSink
                            ? CapabilityValueKind.None
                            : CapabilityValueKind.Choice,
                        Display = Display(role.ToString()),
                        SupportsRead = role is CapabilityRole.ControllerSource,
                        SupportsAction = role is CapabilityRole.HapticSink,
                        Choices = role is CapabilityRole.ControllerSource
                            ?
                            [
                                new CapabilityChoice("device", Display("Device")),
                                new CapabilityChoice("plugin", Display("Plugin")),
                                new CapabilityChoice("unavailable", Display("Unavailable")),
                            ]
                            : [],
                        Persistence = CapabilityPersistence.Volatile,
                    })],
            },
            CancellationToken.None);
        CapabilityDescriptor descriptor = host.DescriptorSets[^1].Descriptors[0];
        await host.PublishCapabilityStateAsync(
            State(descriptor, available: false, generation: 7),
            CancellationToken.None);
        return host;
    }

    private static CapabilityDisplay Display(string label) => new()
    {
        Key = DisplayKey.Custom,
        CustomLabel = label,
    };

    private static CapabilityValue Integer(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };

    private static CapabilityValue Boolean(bool value) => new()
    {
        Kind = CapabilityValueKind.Boolean,
        BooleanValue = value,
    };

    private static CapabilityValue RoleValue(CapabilityDescriptor descriptor, bool available) =>
        descriptor.ValueKind switch
        {
            CapabilityValueKind.Choice => new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = available ? "plugin" : "device",
            },
            CapabilityValueKind.None => new CapabilityValue { Kind = CapabilityValueKind.None },
            _ => Boolean(available),
        };

    private static CapabilityCommandResult Verified(CapabilityCommand command) => Result(
        command,
        CommandOutcome.AppliedVerified,
        command.RequestedValue);

    private static CapabilityCommandResult Result(
        CapabilityCommand command,
        CommandOutcome outcome,
        CapabilityValue? readback = null,
        CapabilityReason? reason = null) => new()
        {
            CommandId = command.CommandId,
            Outcome = outcome,
            Reason = reason,
            ReadbackValue = readback,
            CompletedAt = DateTimeOffset.UtcNow,
        };

    private static CapabilityState State(
        CapabilityDescriptor descriptor,
        bool available,
        long generation) => new()
        {
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            Available = available,
            ObservedValue = RoleValue(descriptor, available),
            Quality = HardwareStateQuality.Verified,
            ObservedAt = DateTimeOffset.UtcNow,
            DescriptorGeneration = 4,
            CycleGeneration = generation,
        };

    private sealed class ActionTestPlugin(TestPluginHostAdapter host) : IDevicePlugin
    {
        public string PackageId => "wsgm.device.test.attended-action";

        public List<CapabilityCommand> Commands { get; } = [];

        public List<HapticOutputFrame> HapticFrames { get; } = [];

        public Func<CapabilityCommand, int, CapabilityCommandResult>? CommandHandler { get; init; }

        public bool ThrowOnNonSilentHaptic { get; init; }

        public int ControllerReleaseCalls { get; private set; }

        public PluginControllerRelease ControllerRelease { get; init; } = new()
        {
            Step = ControllerHandoffStep.TopologyVerified,
            Result = ControllerHandoffResult.ReleasedVerified,
        };

        public ValueTask<PluginDetectionResult> DetectAsync(
            PluginDetectionContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(new PluginDetectionResult
            {
                Matched = true,
                DeviceDefinitionId = "synthetic-attended-action",
            });

        public ValueTask<PluginStartResult> StartAsync(
            PluginStartContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStartResult
            {
                State = PluginOperationalState.Active,
            });

        public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
            CapabilityCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            CapabilityCommandResult result = CommandHandler?.Invoke(command, Commands.Count)
                ?? Verified(command);
            return ValueTask.FromResult(result);
        }

        public ValueTask SuspendAsync(
            PluginQuiesceContext context,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<PluginStartResult> ResumeAsync(
            PluginResumeContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStartResult
            {
                State = PluginOperationalState.Active,
            });

        public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PluginDiagnostics());

        public ValueTask ApplyHapticOutputAsync(
            HapticOutputFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HapticFrames.Add(frame);
            if (!frame.IsSilent && ThrowOnNonSilentHaptic)
            {
                throw new InvalidOperationException("Synthetic haptic delivery failure.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
            PluginControllerReleaseContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ControllerReleaseCalls++;
            return ValueTask.FromResult(ControllerRelease);
        }

        public async ValueTask SetControllerManagementAsync(
            PluginControllerManagementContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapabilityDescriptorSet? descriptors = host.DescriptorSets.LastOrDefault();
            if (descriptors is null)
            {
                return;
            }

            foreach (CapabilityDescriptor descriptor in descriptors.Descriptors.Where(candidate =>
                candidate.Role is CapabilityRole.ControllerSource or CapabilityRole.HapticSink))
            {
                await host.PublishCapabilityStateAsync(
                    State(descriptor, context.Enabled, context.CycleGeneration),
                    cancellationToken);
            }
        }

        public ValueTask<PluginStopResult> StopAsync(
            PluginStopContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStopResult
            {
                Status = PluginStopStatus.Clean,
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
