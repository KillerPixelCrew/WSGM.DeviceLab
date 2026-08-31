using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Testing;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Cli;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Probes;
using WSGM.DeviceLab.Testing;

namespace WSGM.Device.Tests;

public sealed class DeviceLabTests
{
    [Fact]
    public async Task PublicationSummary_UsesTheSdkOwnedTestHostAdapterForEveryChannel()
    {
        TestPluginHostAdapter host = new(cycleGeneration: 3);
        await host.PublishDescriptorsAsync(
            new CapabilityDescriptorSet { Generation = 1, CycleGeneration = 3 },
            CancellationToken.None);
        await host.PublishCapabilityStateAsync(
            new CapabilityState
            {
                CapabilityId = "dock.beacon",
                Available = true,
                Quality = HardwareStateQuality.Verified,
                DescriptorGeneration = 1,
                CycleGeneration = 3,
            },
            CancellationToken.None);
        await host.PublishPhysicalDevicesAsync([], output: null, CancellationToken.None);
        await host.PublishControllerSampleAsync(
            CanonicalControllerSample.Neutral(1, 3, DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await host.PublishOemControlsAsync([], CancellationToken.None);
        await host.PublishOemEventAsync(
            new OemControlEvent(
                "dock-button",
                OemPressKind.Short,
                3,
                DateTimeOffset.UnixEpoch,
                "synthetic-press"),
            CancellationToken.None);

        PluginPublicationSummary summary = PluginPublicationSummary.From(host);

        Assert.Equal(1, summary.DescriptorSets);
        Assert.Equal(1, summary.CapabilityStates);
        Assert.Equal(1, summary.PhysicalDeviceSets);
        Assert.Equal(1, summary.ControllerSamples);
        Assert.Equal(1, summary.OemControlSets);
        Assert.Equal(1, summary.OemEvents);
    }

    [Fact]
    public async Task SyntheticDockFixture_ExercisesTheMateriallyDifferentPluginLifecycle()
    {
        SyntheticPluginFixtureReport report = await SyntheticPluginFixture.RunAsync(
            CancellationToken.None);
        string[] expected =
        [
            "different-device-rejected",
            "synthetic-dock-exact-match",
            "partial-capability-availability",
            "canonical-input-published",
            "boolean-command-readback",
            "canonical-output-applied",
            "cancellation-observed",
            "stale-generation-rejected",
            "stop-restores-original-state-and-output",
            "cleanup-diagnostics-reported",
        ];

        Assert.True(report.Passed);
        Assert.Equal(expected, report.Checks);
    }

    [Fact]
    public void KnownMsiClaw_ExactFingerprintOwnsExactlyFiveCompiledReadProbes()
    {
        KnownDeviceFingerprint fingerprint = KnownMsiClaw.Create();
        CandidateAssessment exact = KnownDeviceMatcher.Assess(
            Inventory(fingerprint),
            fingerprint,
            fingerprint.DeviceId);

        Assert.True(exact.ExactMatch, string.Join(Environment.NewLine, exact.Explanations));
        Assert.Equal(5, fingerprint.ReadProbes.Count);
        Assert.Equal(5, fingerprint.ReadProbes.Select(probe => probe.Id).Distinct().Count());
        Assert.All(fingerprint.ReadProbes, probe =>
        {
            Assert.Empty(ReadProbeMetadataPolicy.Validate(probe));
            Assert.True(BuiltInReadProbeRegistry.TryResolve(probe.Id, probe.Version, out _));
        });

        MachineInventory mismatch = Inventory(fingerprint) with
        {
            Firmware = Inventory(fingerprint).Firmware with { BaseboardProduct = "NOT-MS-1T52" },
        };
        Assert.False(KnownDeviceMatcher.Assess(
            mismatch,
            fingerprint,
            fingerprint.DeviceId).ExactMatch);
    }

    [Fact]
    public void DeviceLabCli_RejectsAMisspelledRedactionFlag()
    {
        string? error = DeviceLabCli.ValidateArguments(
            ["inventory", "--out-dir", "capture", "--sharable"]);

        Assert.Contains("--sharable", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FanRpmProbe_AllowsLiveTachometerMovementAcrossReads()
    {
        ReadProbeMetadata metadata = KnownMsiClaw.Create().ReadProbes.Single(
            probe => probe.Id.EndsWith("fan-rpm", StringComparison.Ordinal));
        ReadProbeWorkerResponse response = new()
        {
            SchemaVersion = 1,
            ProbeId = metadata.Id,
            ProbeVersion = metadata.Version,
            Status = ReadProbeWorkerStatus.Completed,
            Samples =
            [
                FanSample("3000,3100", "3010,3090"),
                FanSample("3030,3120", "3020,3110"),
            ],
            HardwareMutationObserved = false,
        };

        Assert.False(metadata.ExpectedResponse.MustBeStable);
        Assert.Equal(ReadProbeCrossCheckKind.Present, metadata.CrossCheck.Kind);
        Assert.True(ReadProbeResponseValidator.Validate(metadata, response).Accepted);
    }

    [Fact]
    public void ReadProbeSupervisor_OutlivesTheWorkersSemanticDeadline()
    {
        ReadProbeMetadata metadata = KnownMsiClaw.Create().ReadProbes[0];

        Assert.True(
            ReadProbeWorkerSupervisor.ProcessDeadline(metadata)
            > TimeSpan.FromMilliseconds(metadata.TimeoutMilliseconds));
    }

    [Fact]
    public void KnownMsiClaw_UnavailableWmiNamespaceDoesNotSatisfyTheExactProviderGate()
    {
        KnownDeviceFingerprint fingerprint = KnownMsiClaw.Create();
        MachineInventory inventory = Inventory(fingerprint);
        MachineInventory unavailable = inventory with
        {
            WmiClasses =
            [
                inventory.WmiClasses[0] with { Access = WmiAccess.NamespaceUnavailable },
            ],
        };
        MachineInventory accessDenied = inventory with
        {
            WmiClasses =
            [
                inventory.WmiClasses[0] with { Access = WmiAccess.AccessDenied },
            ],
        };

        Assert.False(KnownDeviceMatcher.Assess(
            unavailable,
            fingerprint,
            fingerprint.DeviceId).ExactMatch);
        Assert.True(KnownDeviceMatcher.Assess(
            accessDenied,
            fingerprint,
            fingerprint.DeviceId).ExactMatch);
    }

    [Fact]
    public void PluginIdentity_UnavailableWmiNamespaceDoesNotClaimAProviderSignature()
    {
        KnownDeviceFingerprint fingerprint = KnownMsiClaw.Create();
        MachineInventory inventory = Inventory(fingerprint);
        MachineInventory unavailable = inventory with
        {
            WmiClasses =
            [
                inventory.WmiClasses[0] with { Access = WmiAccess.NamespaceUnavailable },
            ],
        };
        MachineInventory accessDenied = inventory with
        {
            WmiClasses =
            [
                inventory.WmiClasses[0] with { Access = WmiAccess.AccessDenied },
            ],
        };

        string[] expected = [$"{fingerprint.WmiNamespace}:{fingerprint.WmiClass}"];
        Assert.Empty(DeviceLabApplication.ToPluginIdentity(unavailable).WmiProviderSignatures);
        Assert.Equal(expected, DeviceLabApplication.ToPluginIdentity(accessDenied).WmiProviderSignatures);
    }

    [Fact]
    public void ReadProbeResponse_MutationOrMissingCrossCheck_IsRejected()
    {
        ReadProbeMetadata metadata = KnownMsiClaw.Create().ReadProbes.Single(
            probe => probe.Id.EndsWith("charge-limit", StringComparison.Ordinal));
        ReadProbeSample sample = new()
        {
            ValueKind = ReadProbeValueKind.Integer,
            StatusCode = 1,
            Length = 2,
            NumericValue = 80,
            NormalizedValue = "80",
            ElapsedMilliseconds = 5,
            CrossCheckValue = "80",
            CrossCheckNumericValue = 80,
        };
        ReadProbeWorkerResponse response = new()
        {
            SchemaVersion = 1,
            ProbeId = metadata.Id,
            ProbeVersion = metadata.Version,
            Status = ReadProbeWorkerStatus.Completed,
            Samples = [sample, sample],
            HardwareMutationObserved = false,
        };

        Assert.True(ReadProbeResponseValidator.Validate(metadata, response).Accepted);
        Assert.Equal(
            "response.mutation",
            ReadProbeResponseValidator.Validate(
                metadata,
                response with { HardwareMutationObserved = true }).Code);
        Assert.Equal(
            "response.cross-check",
            ReadProbeResponseValidator.Validate(
                metadata,
                response with
                {
                    Samples = [sample with { CrossCheckValue = "79" }, sample],
                }).Code);
    }

    private static ReadProbeSample FanSample(string value, string crossCheck) => new()
    {
        ValueKind = ReadProbeValueKind.Text,
        StatusCode = 1,
        Length = 5,
        NormalizedValue = value,
        ElapsedMilliseconds = 5,
        CrossCheckValue = crossCheck,
    };

    [Fact]
    public void AttendedHardwareAction_RequiresImmediateConfirmationAndNoProductionOwner()
    {
        DeviceLabOperationRequirements requirements = new()
        {
            OperationId = "plugin.hardware-test",
            ResourceId = "wsgm.device.synthetic.dock-x1",
            Access = DeviceLabOperationAccess.AttendedPluginAction,
            ExactDeviceMatched = true,
            RequiresElevation = true,
        };
        DeviceLabSafetySnapshot snapshot = new()
        {
            OwnerDiscovery = DeviceOwnerDiscoveryState.Absent,
            IsElevated = true,
            IsUserInteractive = true,
            IsContinuousIntegration = false,
            AttendedActionConfirmed = false,
        };

        DeviceLabPreflightDecision unconfirmed = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            snapshot);
        DeviceLabPreflightDecision confirmed = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            snapshot with { AttendedActionConfirmed = true });
        DeviceLabPreflightDecision owned = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            snapshot with
            {
                AttendedActionConfirmed = true,
                OwnerDiscovery = DeviceOwnerDiscoveryState.Present,
            });

        Assert.Equal(DeviceLabAccessRoute.None, unconfirmed.Route);
        Assert.Contains(unconfirmed.Checks, check => check.Code == "attended.confirmation");
        Assert.Equal(DeviceLabAccessRoute.DirectAttended, confirmed.Route);
        Assert.Equal(DeviceLabAccessRoute.None, owned.Route);
        Assert.Contains(owned.Checks, check => check.Code == "owner.active");
    }

    [Fact]
    public void CaptureRedaction_KeepsHardwareFingerprintAndRemovesUnitIdentity()
    {
        const string UnitPath = @"USB\VID_0DB0&PID_1901\00006F64096B22E7";

        string redacted = new CaptureRedactor().Redact(UnitPath);

        Assert.Contains("VID_0DB0&PID_1901", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("00006F64096B22E7", redacted, StringComparison.Ordinal);
    }

    private static MachineInventory Inventory(KnownDeviceFingerprint fingerprint) => new()
    {
        SchemaVersion = 1,
        Firmware = new FirmwareInventory
        {
            SystemManufacturer = fingerprint.SystemManufacturer,
            BaseboardProduct = fingerprint.BaseboardProduct,
            SystemSku = fingerprint.SystemSku,
        },
        UsbInterfaces =
        [
            new UsbInterfaceInventory
            {
                InstanceId = "synthetic-instance",
                VendorId = fingerprint.UsbVendorId,
                ProductId = fingerprint.UsbProductIds[0],
                DeviceRelease = fingerprint.UsbDeviceRelease,
                Present = true,
            },
        ],
        WmiClasses =
        [
            new WmiClassInventory
            {
                Namespace = fingerprint.WmiNamespace,
                ClassName = fingerprint.WmiClass,
                Access = WmiAccess.Available,
            },
        ],
        CapturedAt = DateTimeOffset.UnixEpoch,
    };
}
