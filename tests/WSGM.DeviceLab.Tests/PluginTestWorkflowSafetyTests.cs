using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Plugin;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Testing;

namespace WSGM.Device.Tests;

public sealed class PluginTestWorkflowSafetyTests
{
    [Fact]
    public async Task RunAttended_UnconfirmedActionRefusesBeforeLoadingTheDeclaredAssembly()
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackageWithUnresolvableEntryType(temporary);
        string stateDirectory = temporary.GetPath("new-state");
        int reservationAttempts = 0;

        PluginTestReport report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            stateDirectory,
            Action(),
            confirmed: false,
            Boundaries(temporary),
            SafetyEnvironment(() =>
            {
                reservationAttempts++;
                return Reserved(new CallbackDisposable(static () => { }));
            }),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Null(report.Detection);
        Assert.False(report.Started);
        Assert.Contains(report.Preflight!.Checks, check => check.Code == "attended.confirmation");
        Assert.Contains("before plugin loading", report.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(stateDirectory));
        Assert.Equal(0, reservationAttempts);
    }

    [Fact]
    public async Task RunAttended_ExistingStateDirectoryRefusesBeforeLoadingTheDeclaredAssembly()
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackageWithUnresolvableEntryType(temporary);
        string stateDirectory = temporary.GetPath("existing-state");
        Directory.CreateDirectory(stateDirectory);
        int reservationAttempts = 0;

        PluginTestReport report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            stateDirectory,
            Action(),
            confirmed: true,
            Boundaries(temporary),
            SafetyEnvironment(() =>
            {
                reservationAttempts++;
                return Reserved(new CallbackDisposable(static () => { }));
            }),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Null(report.Detection);
        Assert.False(report.Started);
        Assert.Equal("The plugin state directory must be new.", report.Error);
        Assert.Equal(0, reservationAttempts);
    }

    [Fact]
    public async Task RunAttended_ExistingProductionOwnerRefusesBeforeLoadingTheDeclaredAssembly()
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackageWithUnresolvableEntryType(temporary);
        string stateDirectory = temporary.GetPath("new-state");
        int reservationAttempts = 0;

        PluginTestReport report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            stateDirectory,
            Action(),
            confirmed: true,
            Boundaries(temporary),
            SafetyEnvironment(() =>
            {
                reservationAttempts++;
                return OwnerPresent();
            }),
            CancellationToken.None);

        Assert.Equal(1, reservationAttempts);
        Assert.False(report.Passed);
        Assert.Null(report.Detection);
        Assert.False(report.Started);
        Assert.Contains(report.Preflight!.Checks, check => check.Code == "owner.active");
        Assert.Contains("before plugin loading", report.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(stateDirectory));
    }

    [Fact]
    public async Task RunAttended_OwnerReservationOutlivesPluginDisposal()
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackage(
            temporary,
            OwnerReservationLifetimePlugin.Id,
            typeof(OwnerReservationLifetimePlugin).FullName!);
        string marker = Path.Combine(package, OwnerReservationLifetimePlugin.DisposalMarker);
        string stateDirectory = temporary.GetPath("new-state");
        bool reservationDisposed = false;
        var handle = new CallbackDisposable(() =>
        {
            Assert.True(
                File.Exists(marker),
                "LocalPluginPackage must dispose the plugin before the owner reservation closes.");
            reservationDisposed = true;
        });

        PluginTestReport report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            stateDirectory,
            Action(),
            confirmed: true,
            Boundaries(temporary),
            SafetyEnvironment(() => Reserved(handle)),
            CancellationToken.None);

        Assert.False(report.Passed);
        PluginDetectionResult detection = Assert.IsType<PluginDetectionResult>(report.Detection);
        Assert.False(detection.Matched);
        Assert.False(report.Started);
        Assert.Contains(report.Preflight!.Checks, check => check.Code == "identity.mismatch");
        Assert.True(File.Exists(marker));
        Assert.True(reservationDisposed);
        Assert.False(Directory.Exists(stateDirectory));
    }

    [Fact]
    public async Task LocalPluginPackage_ThrowingDisposalUnloadsAndRetainsOwnerForProcessLifetime()
    {
        string ownerName = $@"Local\WSGM.DeviceOwner.DisposeFailure.{Guid.NewGuid():N}";
        DeviceLabOwnerReservationResult owner = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Absent, owner.Inspection.State);
        DeviceLabOwnerReservation reservation =
            Assert.IsType<DeviceLabOwnerReservation>(owner.Reservation);
        using (reservation)
        {
            var disposalFailure = new InvalidOperationException("plugin disposal failed");
            bool unloaded = false;

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocalPluginPackage.DisposePluginAndUnloadAsync(
                    () => ValueTask.FromException(disposalFailure),
                    () => unloaded = true,
                    reservation).AsTask());

            Assert.Same(disposalFailure, thrown);
            Assert.True(unloaded);
        }

        DeviceLabOwnerReservationResult competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task RunAttended_ThrowingPluginDisposalKeepsTheAtomicOwnerReservationUnavailable()
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackage(
            temporary,
            ThrowingDisposePlugin.Id,
            typeof(ThrowingDisposePlugin).FullName!);
        string ownerName = $@"Local\WSGM.DeviceOwner.WorkflowDisposeFailure.{Guid.NewGuid():N}";

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PluginTestWorkflow.RunAttendedAsync(
                package,
                new DeviceIdentitySnapshot(),
                temporary.GetPath("new-state"),
                Action(),
                confirmed: true,
                Boundaries(temporary),
                SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
                CancellationToken.None));

        Assert.Contains("plugin disposal failed", failure.Message, StringComparison.Ordinal);
        DeviceLabOwnerReservationResult competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task RunAttended_ThrowingPluginConstructorKeepsTheAtomicOwnerReservationUnavailable()
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackage(
            temporary,
            ThrowingConstructorPlugin.Id,
            typeof(ThrowingConstructorPlugin).FullName!);
        string ownerName = $@"Local\WSGM.DeviceOwner.ConstructorFailure.{Guid.NewGuid():N}";

        string? failure = await CaptureFailureAsync(() => PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            temporary.GetPath("new-state"),
            Action(),
            confirmed: true,
            Boundaries(temporary),
            SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
            CancellationToken.None));
        Assert.NotNull(failure);
        CollectPluginLoadContexts();

        Assert.True(File.Exists(Path.Combine(package, ThrowingConstructorPlugin.ConstructorMarker)));
        DeviceLabOwnerReservationResult competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task RunAttended_ThrowingPackageIdAndDisposalKeepsOwnerReservationUnavailable()
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackage(
            temporary,
            ThrowingPackageIdPlugin.Id,
            typeof(ThrowingPackageIdPlugin).FullName!);
        string ownerName = $@"Local\WSGM.DeviceOwner.PackageIdFailure.{Guid.NewGuid():N}";

        string? failure = await CaptureFailureAsync(() => PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            temporary.GetPath("new-state"),
            Action(),
            confirmed: true,
            Boundaries(temporary),
            SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
            CancellationToken.None));
        Assert.NotNull(failure);
        CollectPluginLoadContexts();

        Assert.True(File.Exists(Path.Combine(package, ThrowingPackageIdPlugin.DisposalMarker)));
        DeviceLabOwnerReservationResult competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task RunAttended_ThrowingPackageIdWithCleanDisposalKeepsOwnerReservationUnavailable()
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackage(
            temporary,
            ThrowingPackageIdCleanDisposePlugin.Id,
            typeof(ThrowingPackageIdCleanDisposePlugin).FullName!);
        string ownerName = $@"Local\WSGM.DeviceOwner.CleanDisposePackageIdFailure.{Guid.NewGuid():N}";

        string? failure = await CaptureFailureAsync(() => PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            temporary.GetPath("new-state"),
            Action(),
            confirmed: true,
            Boundaries(temporary),
            SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
            CancellationToken.None));
        Assert.NotNull(failure);
        CollectPluginLoadContexts();

        Assert.True(File.Exists(Path.Combine(package, OwnerReservationLifetimePlugin.DisposalMarker)));
        DeviceLabOwnerReservationResult competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Theory]
    [InlineData(
        UnverifiedStopPlugin.Id,
        typeof(UnverifiedStopPlugin),
        "synthetic restoration was unverified")]
    [InlineData(
        FailedStopPlugin.Id,
        typeof(FailedStopPlugin),
        "synthetic restoration failed")]
    [InlineData(
        ThrowingStopPlugin.Id,
        typeof(ThrowingStopPlugin),
        "synthetic Stop threw")]
    public async Task RunAttended_PostStartUnverifiedOutcomeWithCleanDisposalKeepsOwnerUnavailable(
        string packageId,
        Type pluginType,
        string expectedError)
    {
        using TemporaryDirectory temporary = new();
        string package = CreatePackage(
            temporary,
            packageId,
            pluginType.FullName!);
        string ownerName = $@"Local\WSGM.DeviceOwner.UnverifiedAttendedStop.{Guid.NewGuid():N}";

        PluginTestReport report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            temporary.GetPath("new-state"),
            Action(),
            confirmed: true,
            Boundaries(temporary),
            SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
            CancellationToken.None);

        Assert.True(report.Started);
        Assert.False(report.CleanedUp);
        Assert.Contains(expectedError, report.Error, StringComparison.Ordinal);
        DeviceLabOwnerReservationResult competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task OwnerReservation_UsesAtomicNamedObjectLifetimeWithoutThreadOwnership()
    {
        string ownerName = $@"Local\WSGM.DeviceOwner.Test.{Guid.NewGuid():N}";
        DeviceLabOwnerReservationResult first = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Absent, first.Inspection.State);
        DeviceLabOwnerReservation firstReservation = Assert.IsType<DeviceLabOwnerReservation>(first.Reservation);
        try
        {
            DeviceLabOwnerReservationResult concurrent = DeviceLabOwnerInspector.Reserve(ownerName);
            Assert.Equal(DeviceOwnerDiscoveryState.Present, concurrent.Inspection.State);
            Assert.Null(concurrent.Reservation);

            await Task.Run(firstReservation.Dispose);
            DeviceLabOwnerReservationResult afterRelease = DeviceLabOwnerInspector.Reserve(ownerName);
            Assert.Equal(DeviceOwnerDiscoveryState.Absent, afterRelease.Inspection.State);
            DeviceLabOwnerReservation afterReleaseReservation =
                Assert.IsType<DeviceLabOwnerReservation>(afterRelease.Reservation);
            afterReleaseReservation.Dispose();
        }
        finally
        {
            firstReservation.Dispose();
        }
    }

    [Fact]
    public void OwnerReservation_UsesTheExactMachineWideProductionMarker()
    {
        Assert.Equal(@"Global\WSGM.DeviceOwner", DeviceLabOwnerInspector.OwnerObjectName());
    }

    private static AttendedPluginSafetyEnvironment SafetyEnvironment(
        Func<DeviceLabOwnerReservationResult> reserveOwner) => new()
        {
            ReserveOwner = reserveOwner,
            IsElevated = true,
            IsUserInteractive = true,
            IsContinuousIntegration = false,
        };

    private static DeviceLabOwnerReservationResult Reserved(IDisposable handle) => new()
    {
        Inspection = new DeviceLabOwnerInspection
        {
            State = DeviceOwnerDiscoveryState.Absent,
        },
        Reservation = new DeviceLabOwnerReservation(handle),
    };

    private static DeviceLabOwnerReservationResult OwnerPresent() => new()
    {
        Inspection = new DeviceLabOwnerInspection
        {
            State = DeviceOwnerDiscoveryState.Present,
        },
    };

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose()
        {
            Action? callback = Interlocked.Exchange(ref _callback, null);
            callback?.Invoke();
        }
    }

    private static string CreatePackageWithUnresolvableEntryType(TemporaryDirectory temporary)
        => CreatePackage(
            temporary,
            "wsgm.device.synthetic.preflight-order",
            "WSGM.Device.Tests.ThisTypeMustNeverBeResolved");

    private static string CreatePackage(
        TemporaryDirectory temporary,
        string packageId,
        string entryType)
    {
        string package = temporary.GetPath("package");
        Directory.CreateDirectory(package);
        string assemblyName = Path.GetFileName(typeof(PluginTestWorkflowSafetyTests).Assembly.Location);
        File.Copy(
            typeof(PluginTestWorkflowSafetyTests).Assembly.Location,
            Path.Combine(package, assemblyName));
        var manifest = new PluginManifest
        {
            Id = packageId,
            Name = "Synthetic Preflight Order",
            Version = "1.0.0",
            ApiVersion = DeviceApi.Version,
            EntryAssembly = assemblyName,
            EntryType = entryType,
        };
        File.WriteAllBytes(
            Path.Combine(package, PluginPackageWorkflow.ManifestPath),
            PluginManifestFixture.Serialize(manifest));
        return package;
    }

    private static AttendedPluginActionRequest Action() => new()
    {
        Kind = AttendedPluginActionKind.ControllerManagement,
    };

    private static async Task<string?> CaptureFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception.ToString();
        }
    }

    private static void CollectPluginLoadContexts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static DeviceLabPathBoundaries Boundaries(TemporaryDirectory temporary) => new()
    {
        LiveDataDirectory = temporary.GetPath("never-live-data"),
        BroadHomeDirectories = [],
    };
}

public class OwnerReservationLifetimePlugin : IDevicePlugin
{
    public const string Id = "wsgm.device.synthetic.owner-reservation-lifetime";
    public const string DisposalMarker = "plugin-disposed.marker";

    public virtual string PackageId => Id;

    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = false,
        });

    public ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken) => throw new InvalidOperationException(
            "A mismatched plugin must never start.");

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SuspendAsync(
        PluginQuiesceContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public virtual ValueTask DisposeAsync()
    {
        string packageDirectory = Path.GetDirectoryName(typeof(OwnerReservationLifetimePlugin).Assembly.Location)!;
        File.WriteAllText(Path.Combine(packageDirectory, DisposalMarker), "disposed");
        return ValueTask.CompletedTask;
    }
}

public sealed class ThrowingDisposePlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-dispose";

    public override string PackageId => Id;

    public override ValueTask DisposeAsync() =>
        ValueTask.FromException(new InvalidOperationException("plugin disposal failed"));
}

public sealed class ThrowingConstructorPlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-constructor";
    public const string ConstructorMarker = "plugin-constructor-entered.marker";

    public ThrowingConstructorPlugin()
    {
        string packageDirectory = Path.GetDirectoryName(
            typeof(ThrowingConstructorPlugin).Assembly.Location)!;
        File.WriteAllText(Path.Combine(packageDirectory, ConstructorMarker), "entered");
        throw new InvalidOperationException("plugin constructor failed");
    }

    public override string PackageId => Id;
}

public sealed class ThrowingPackageIdPlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-package-id";
    public new const string DisposalMarker = "throwing-package-id-disposed.marker";

    public override string PackageId => throw new InvalidOperationException("package ID failed");

    public override ValueTask DisposeAsync()
    {
        string packageDirectory = Path.GetDirectoryName(
            typeof(ThrowingPackageIdPlugin).Assembly.Location)!;
        File.WriteAllText(Path.Combine(packageDirectory, DisposalMarker), "attempted");
        return ValueTask.FromException(new InvalidOperationException("plugin disposal failed"));
    }
}

public sealed class ThrowingPackageIdCleanDisposePlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-package-id-clean-dispose";

    public override string PackageId => throw new InvalidOperationException("package ID failed");
}

public class UnverifiedStopPlugin : IDevicePlugin
{
    public const string Id = "wsgm.device.synthetic.unverified-stop";

    public virtual string PackageId => Id;

    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = true,
            DeviceDefinitionId = "synthetic-device",
        });

    public ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStartResult
        {
            State = PluginOperationalState.Active,
        });

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SuspendAsync(
        PluginQuiesceContext context,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStartResult
        {
            State = PluginOperationalState.Active,
        });

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginDiagnostics());

    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginControllerRelease
        {
            Step = ControllerHandoffStep.TopologyVerified,
            Result = ControllerHandoffResult.ReleasedVerified,
        });

    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public virtual ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStopResult
        {
            Status = PluginStopStatus.Unverified,
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "synthetic restoration was unverified"),
        });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FailedStopPlugin : UnverifiedStopPlugin
{
    public new const string Id = "wsgm.device.synthetic.failed-stop";

    public override string PackageId => Id;

    public override ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStopResult
        {
            Status = PluginStopStatus.Failed,
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "synthetic restoration failed"),
        });
}

public sealed class ThrowingStopPlugin : UnverifiedStopPlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-stop";

    public override string PackageId => Id;

    public override ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken) => ValueTask.FromException<PluginStopResult>(
            new InvalidOperationException("synthetic Stop threw"));
}
