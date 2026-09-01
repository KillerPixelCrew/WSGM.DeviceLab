using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Fixtures;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Probes;
using WSGM.DeviceLab.Scaffolding;
using WSGM.DeviceLab.Testing;

namespace WSGM.DeviceLab.Application;

/// <summary>Offline exact-device assessment and its eligible reviewed read probes.</summary>
internal sealed record DeviceLabCandidateResult
{
    /// <summary>Exact logical device ID used for device-scoped matching.</summary>
    public required string TargetDeviceId { get; init; }

    /// <summary>The known device comparison, including every exact mismatch.</summary>
    public IReadOnlyList<CandidateAssessment> Candidates { get; init; } = [];

    /// <summary>Reviewed read-only probes available only after an exact known-device match.</summary>
    public IReadOnlyList<ReadProbeMetadata> ReadOnlyProbes { get; init; } = [];

}

/// <summary>Correlation findings and the limits that constrain their meaning.</summary>
internal sealed record DeviceLabCorrelationResult
{
    /// <summary>Correlation-only findings linked to raw events.</summary>
    public IReadOnlyList<PassiveCorrelationFinding> Findings { get; init; } = [];

    /// <summary>Platform limitations retained alongside the findings.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

}

/// <summary>Safety preflight and disposable-worker result for one compiled read probe.</summary>
internal sealed record DeviceLabReadProbeExecutionResult
{
    /// <summary>Selected immutable probe metadata.</summary>
    public required ReadProbeMetadata Probe { get; init; }

    /// <summary>Safety decision taken before the disposable read-probe worker could open the resource.</summary>
    public required DeviceLabPreflightDecision Preflight { get; init; }

    /// <summary>Typed disposable-worker result, or null when preflight refused execution.</summary>
    public ReadProbeRunResult? Run { get; init; }

}

/// <summary>
/// Shared Device Lab application facade used by the GUI and CLI command surfaces.
/// </summary>
/// <remarks>Creates a facade rooted in the current checkout and running Device Lab executable.</remarks>
/// <param name="repositoryRoot">Repository root, or <see langword="null"/> outside a checkout.</param>
/// <param name="deviceLabPath">Path to the current Device Lab executable.</param>
internal sealed class DeviceLabApplication(string? repositoryRoot, string deviceLabPath)
{
    private const int MaximumInventoryBytes = 32 * 1024 * 1024;
    private readonly string? _repositoryRoot = repositoryRoot;
    private readonly string _deviceLabPath = Path.GetFullPath(deviceLabPath);

    /// <summary>Runs safe environment and output-path diagnostics.</summary>
    /// <param name="outputDirectory">Explicit output directory under review.</param>
    /// <param name="capturedAt">Timestamp to record.</param>
    /// <param name="cancellationToken">Cancels before or after diagnostics.</param>
    /// <returns>Structured doctor report.</returns>
    public DeviceLabDoctorReport Doctor(
        string outputDirectory,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeviceLabDoctorReport report = DeviceLabDoctor.Run(outputDirectory, capturedAt, _repositoryRoot);
        cancellationToken.ThrowIfCancellationRequested();
        return report;
    }

    /// <summary>Collects and persists one private or sanitized read-only inventory.</summary>
    /// <param name="outputDirectory">Explicit output directory.</param>
    /// <param name="shareable">Whether identifiers are redacted.</param>
    /// <param name="capturedAt">Timestamp to record.</param>
    /// <param name="cancellationToken">Cancels collection or publication.</param>
    /// <returns>Structured inventory workflow result.</returns>
    public DeviceLabInventoryResult Inventory(
        string outputDirectory,
        bool shareable,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default) => DeviceLabInventoryWorkflow.Run(
            new DeviceLabInventoryRequest { OutputDirectory = outputDirectory, Shareable = shareable },
            capturedAt,
            _repositoryRoot,
            cancellationToken);

    /// <summary>Compares the known MS-1T52 fingerprint and lists its reviewed probes without opening hardware.</summary>
    /// <param name="inventoryPath">Canonical inventory JSON.</param>
    /// <param name="targetDeviceId">Optional exact logical device ID.</param>
    /// <param name="cancellationToken">Cancels bounded inventory parsing or assessment.</param>
    /// <returns>Explained exact comparison and read-probe outputs.</returns>
    public DeviceLabCandidateResult Candidates(
        string inventoryPath,
        string? targetDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        MachineInventory inventory = ReadInventory(inventoryPath, cancellationToken);
        return Candidates(inventory, targetDeviceId, cancellationToken);
    }

    private static DeviceLabCandidateResult Candidates(
        MachineInventory inventory,
        string? targetDeviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = string.IsNullOrWhiteSpace(targetDeviceId) ? DeviceId(inventory) : targetDeviceId;
        KnownDeviceFingerprint fingerprint = KnownMsiClaw.Create();
        CandidateAssessment assessment = KnownDeviceMatcher.Assess(inventory, fingerprint, target);
        return new DeviceLabCandidateResult
        {
            TargetDeviceId = target,
            Candidates = [assessment],
            ReadOnlyProbes = assessment.ExactMatch
                ? [.. fingerprint.ReadProbes.OrderBy(probe => probe.Id, StringComparer.Ordinal)]
                : [],
        };
    }

    /// <summary>Reads and validates the exact inert recipe bytes an operator must review.</summary>
    /// <param name="recipePath">Imported recipe JSON.</param>
    /// <param name="cancellationToken">Cancels bounded recipe validation.</param>
    /// <returns>Closed observation steps and their approval hash.</returns>
    public ObserveOnlyRecipeReview ReviewCaptureRecipe(
        string recipePath,
        CancellationToken cancellationToken = default) =>
        ObserveOnlyCaptureWorkflow.Review(recipePath, cancellationToken);

    /// <summary>Runs one positively matched compiled read probe in a disposable self-worker.</summary>
    /// <param name="inventoryPath">Inventory used for exact candidate gates.</param>
    /// <param name="probeId">Reviewed built-in probe ID.</param>
    /// <param name="outputDirectory">Explicit safe root for the disposable session.</param>
    /// <param name="cancellationToken">Whole-probe cancellation.</param>
    /// <returns>Independent preflight and typed execution results.</returns>
    public async Task<DeviceLabReadProbeExecutionResult> RunReadProbeAsync(
        string inventoryPath,
        string probeId,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(probeId);
        MachineInventory inventory = ReadInventory(inventoryPath, cancellationToken);
        DeviceLabCandidateResult candidateResult = Candidates(
            inventory,
            targetDeviceId: null,
            cancellationToken: cancellationToken);
        ReadProbeMetadata probe = candidateResult.ReadOnlyProbes.SingleOrDefault(item =>
            string.Equals(item.Id, probeId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("The named probe is not a positively matched reviewed read probe.");
        DeviceLabDoctorReport doctor = Doctor(
            outputDirectory,
            DateTimeOffset.UtcNow,
            cancellationToken);
        DeviceLabOwnerInspection owner = DeviceLabOwnerInspector.Inspect();
        bool elevated = doctor.Checks.Any(check =>
            check.Code == "permissions.elevation" && check.Status is DeviceLabDoctorStatus.Pass);
        bool continuousIntegration = IsTruthy(Environment.GetEnvironmentVariable("CI"))
            || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        bool exactFamilyMatched = ProbeFamilyMatches(probe.FamilyId, candidateResult.TargetDeviceId);
        bool exactEndpointMatched = ProbeEndpointMatches(probe.EndpointId, inventory);
        DeviceLabPreflightDecision preflight = DeviceLabSafetyPreflight.Evaluate(
            new DeviceLabOperationRequirements
            {
                OperationId = probe.Id,
                ResourceId = probe.ResourceId,
                Access = DeviceLabOperationAccess.ReadOnlyProbe,
                ExactDeviceMatched = exactFamilyMatched,
                ExactEndpointMatched = exactEndpointMatched,
                RequiresElevation = probe.RequiresElevation,
            },
            new DeviceLabSafetySnapshot
            {
                Doctor = doctor,
                OwnerDiscovery = owner.State,
                IsElevated = elevated,
                IsUserInteractive = Environment.UserInteractive,
                IsContinuousIntegration = continuousIntegration,
            });
        if (preflight.Status is DeviceLabDoctorStatus.Blocked
            || preflight.Route is not DeviceLabAccessRoute.DirectReadOnly)
        {
            return new DeviceLabReadProbeExecutionResult { Probe = probe, Preflight = preflight };
        }

        string sessionDirectory = Path.Combine(
            Path.GetFullPath(outputDirectory),
            $"probe-{SafeFileName(probe.Id)}-{Guid.NewGuid():N}");
        ReadProbeRunResult run = await ReadProbeWorkerSupervisor.RunAsync(
            probe,
            preflight,
            _deviceLabPath,
            sessionDirectory,
            new SystemReadProbeProcessLauncher(),
            cancellationToken).ConfigureAwait(false);
        return new DeviceLabReadProbeExecutionResult { Probe = probe, Preflight = preflight, Run = run };
    }

    /// <summary>Prepares a private observe-only session and a not-yet-written privacy preview.</summary>
    /// <param name="request">Explicit recipe, path, and operator gates.</param>
    /// <param name="capturedAt">Session timestamp.</param>
    /// <param name="cancellationToken">Whole-session cancellation.</param>
    /// <returns>Prepared export or a closed refusal.</returns>
    public Task<ObserveOnlyCaptureResult> PrepareCaptureAsync(
        ObserveOnlyCaptureRequest request,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken) => ObserveOnlyCaptureWorkflow.PrepareAsync(
            request,
            capturedAt,
            _repositoryRoot,
            cancellationToken);

    /// <summary>Exports a prepared sanitized capture after separate privacy approval.</summary>
    /// <param name="plan">Prepared capture export.</param>
    /// <param name="exportPreviewConfirmed">Whether the actual preview was accepted.</param>
    /// <param name="cancellationToken">Cancels export before atomic publication.</param>
    /// <returns>Export result.</returns>
    public CaptureExportResult ExportCapture(
        CaptureExportPlan plan,
        bool exportPreviewConfirmed,
        CancellationToken cancellationToken = default) => ObserveOnlyCaptureWorkflow.Export(
            plan,
            exportPreviewConfirmed,
            _repositoryRoot,
            cancellationToken);

    /// <summary>Verifies and summarizes one sanitized capture.</summary>
    /// <param name="capturePath">Shareable capture path.</param>
    /// <param name="cancellationToken">Cancels bounded capture decoding.</param>
    /// <returns>Inspection linked to verified bundle entries.</returns>
    public CaptureInspection Inspect(
        string capturePath,
        CancellationToken cancellationToken = default)
    {
        CaptureBundleReadResult read = ReadCapture(capturePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CaptureWorkbench.Inspect(read.Bundle!);
    }

    /// <summary>Compares verified capture content hashes.</summary>
    /// <param name="leftPath">Left capture.</param>
    /// <param name="rightPath">Right capture.</param>
    /// <param name="cancellationToken">Cancels either bounded capture decode or comparison.</param>
    /// <returns>Entry additions, removals, and changes.</returns>
    public IReadOnlyList<CaptureEntryDifference> Diff(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken = default)
    {
        CaptureBundleReadResult left = ReadCapture(leftPath, cancellationToken);
        CaptureBundleReadResult right = ReadCapture(rightPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CaptureWorkbench.Diff(left.EntryHashes, right.EntryHashes);
    }

    /// <summary>Runs correlation-only analysis over a verified capture.</summary>
    /// <param name="capturePath">Shareable capture.</param>
    /// <param name="actionId">Operator action marker ID.</param>
    /// <param name="sourceIds">Expected source lanes.</param>
    /// <param name="cancellationToken">Cancels bounded decoding or correlation.</param>
    /// <returns>Findings that retain raw-event links and limitations.</returns>
    public DeviceLabCorrelationResult Correlate(
        string capturePath,
        string actionId,
        IReadOnlySet<string> sourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(sourceIds);
        CaptureBundleReadResult read = ReadCapture(capturePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CaptureStreamEvent> events = [.. read.Bundle!.Streams
            .SelectMany(stream => stream.Events)
            .OrderBy(captureEvent => captureEvent.GlobalSequence)];
        return new DeviceLabCorrelationResult
        {
            Findings = PassiveCorrelationAnalyzer.Analyze(
                new PassiveCorrelationRequest
                {
                    AnalysisId = $"correlate-{actionId}",
                    ActionId = actionId,
                    ExpectedSourceIds = sourceIds,
                    Events = events,
                    ContextWindowTicks = Math.Max(1, read.Bundle.Manifest.QpcFrequency * 2),
                },
                cancellationToken),
            Limitations = PassiveCaptureLimitations.All,
        };
    }

    /// <summary>Extracts a deterministic simulator-only fixture from a verified capture.</summary>
    /// <param name="capturePath">Shareable capture.</param>
    /// <param name="fixtureId">New fixture ID.</param>
    /// <param name="outputDirectory">New explicit output directory.</param>
    /// <param name="cancellationToken">Cancels bounded capture decoding or extraction.</param>
    /// <returns>Fixture extraction result.</returns>
    public FixtureManifest ExtractFixture(
        string capturePath,
        string fixtureId,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream input = new(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        CaptureBundleReadResult read = CaptureBundleReader.Read(input, cancellationToken);
        EnsureCapture(read);
        input.Position = 0;
        string sourceSha256 = HashStream(input, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return FixtureExtractionWorkflow.Extract(
            read.Bundle!,
            sourceSha256,
            fixtureId,
            outputDirectory,
            Boundaries(),
            cancellationToken);
    }

    /// <summary>Copies the checked-in minimal plugin template with exact captured identity.</summary>
    /// <param name="capturePath">Shareable capture.</param>
    /// <param name="outputDirectory">New scaffold directory.</param>
    /// <param name="cancellationToken">Cancels validation or atomic scaffold publication.</param>
    /// <param name="usbInstanceId">Exact endpoint selection when the capture contains more than one candidate.</param>
    /// <returns>Copied template files and identity.</returns>
    public PluginScaffoldResult Scaffold(
        string capturePath,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        string? usbInstanceId = null) => ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            outputDirectory,
            Boundaries(),
            cancellationToken,
            usbInstanceId);

    /// <summary>Runs the built-in hardware-free synthetic plugin fixture.</summary>
    /// <param name="cancellationToken">Cancels the fixture.</param>
    /// <returns>Named public-API checks.</returns>
    public Task<SyntheticPluginFixtureReport> TestSyntheticPluginAsync(
        CancellationToken cancellationToken) => SyntheticPluginFixture.RunAsync(cancellationToken);

    /// <summary>Loads one local plugin and runs only its exact detector.</summary>
    /// <param name="packageDirectory">Validated local package directory.</param>
    /// <param name="inventoryPath">Inventory JSON whose identity is supplied to detection.</param>
    /// <param name="cancellationToken">Cancels the detection test.</param>
    /// <returns>Local load and detection result.</returns>
    public Task<PluginTestReport> TestPluginAsync(
        string packageDirectory,
        string inventoryPath,
        CancellationToken cancellationToken) => PluginTestWorkflow.TestDetectionAsync(
            packageDirectory,
            ToPluginIdentity(ReadInventory(inventoryPath, cancellationToken)),
            cancellationToken);

    /// <summary>Runs one explicitly confirmed plugin activation and mandatory cleanup.</summary>
    /// <param name="packageDirectory">Validated local package directory.</param>
    /// <param name="inventoryPath">
    /// Operator-reviewed inventory JSON. The action recollects live identity and never uses this
    /// imported document as its activation identity.
    /// </param>
    /// <param name="stateDirectory">New explicit package state directory.</param>
    /// <param name="action">One explicit semantic action selected by the local operator.</param>
    /// <param name="confirmed">Immediate operator confirmation for this run.</param>
    /// <param name="cancellationToken">Cancels the attended lifecycle.</param>
    /// <returns>Detection, gate, activation, cleanup, and publication result.</returns>
    public Task<PluginTestReport> RunAttendedPluginAsync(
        string packageDirectory,
        string inventoryPath,
        string stateDirectory,
        AttendedPluginActionRequest action,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        // The imported inventory is useful operator context but cannot authorize a hardware action:
        // it may describe another machine or an earlier topology. Validate its shape, then recollect
        // identity from the machine that will actually run the plugin.
        _ = ReadInventory(inventoryPath, cancellationToken);
        DeviceIdentitySnapshot liveIdentity = ToPluginIdentity(WindowsInventoryCollector.Collect(
            DateTimeOffset.UtcNow,
            cancellationToken: cancellationToken));
        return PluginTestWorkflow.RunAttendedAsync(
            packageDirectory,
            liveIdentity,
            stateDirectory,
            action,
            confirmed,
            Boundaries(),
            cancellationToken);
    }

    /// <summary>Runs offline package validation without loading plugin code.</summary>
    /// <param name="packageDirectory">Package source directory.</param>
    /// <param name="cancellationToken">Cancels bounded source capture or validation.</param>
    /// <returns>Offline validation report.</returns>
    public PluginPackageValidationReport ValidateOffline(
        string packageDirectory,
        CancellationToken cancellationToken = default) =>
        PluginPackageWorkflow.ValidateOffline(packageDirectory, cancellationToken);

    /// <summary>Validates and deterministically packs a plugin.</summary>
    /// <param name="packageDirectory">Package source directory.</param>
    /// <param name="outputPath">New package archive path.</param>
    /// <param name="cancellationToken">Cancels validation or atomic archive publication.</param>
    /// <returns>The offline validation report for the packed source.</returns>
    public PluginPackageValidationReport Pack(
        string packageDirectory,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        PluginPackageWorkflow.Pack(packageDirectory, outputPath, Boundaries(), cancellationToken);

    /// <summary>Directly imports every package glyph profile through the SDK loader.</summary>
    /// <param name="packageDirectory">Package source containing profiles, artwork, and notices.</param>
    /// <param name="cancellationToken">Cancels bounded source capture or glyph import.</param>
    /// <returns>Accepted profiles and deterministic import failures.</returns>
    public GlyphPackageImportReport ImportGlyphs(
        string packageDirectory,
        CancellationToken cancellationToken = default) =>
        GlyphPackageImportWorkflow.Import(packageDirectory, cancellationToken);

    private DeviceLabPathBoundaries Boundaries() => DeviceLabPathBoundaries.ForCurrentUser(_repositoryRoot);

    private static MachineInventory ReadInventory(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = ReadBoundedFile(
            path,
            MaximumInventoryBytes,
            "Inventory is absent, empty, or oversized.",
            cancellationToken);
        MachineInventory? inventory = JsonSerializer.Deserialize(
            bytes,
            DeviceLabJsonContext.Default.MachineInventory);
        cancellationToken.ThrowIfCancellationRequested();
        if (inventory is null
            || inventory.Firmware is null
            || inventory.SchemaVersion != WindowsInventoryCollector.CurrentSchemaVersion)
        {
            throw new InvalidDataException("Inventory schema is unsupported.");
        }

        return MachineInventoryNormalizer.Normalize(inventory);
    }

    private static CaptureBundleReadResult ReadCapture(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CaptureBundleReadResult read = CaptureBundleReader.Read(input, cancellationToken);
        EnsureCapture(read);
        return read;
    }

    private static byte[] ReadBoundedFile(
        string path,
        int maximumBytes,
        string invalidMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 || input.Length > maximumBytes)
        {
            throw new InvalidDataException(invalidMessage);
        }

        byte[] bytes = new byte[(int)input.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException(invalidMessage);
            }
            offset += read;
        }
        return bytes;
    }

    private static string HashStream(Stream input, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void EnsureCapture(CaptureBundleReadResult read)
    {
        if (!read.Succeeded || read.Bundle is null)
        {
            throw new InvalidDataException($"Capture rejected ({read.Failure}): {read.Detail}");
        }
    }

    private static string DeviceId(MachineInventory inventory) =>
        string.Equals(inventory.Firmware.BaseboardProduct, "MS-1T52", StringComparison.OrdinalIgnoreCase)
            ? "ms-1t52"
            : $"observed-{(inventory.Firmware.BaseboardProduct ?? "unknown").ToLowerInvariant()}";

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    internal static DeviceIdentitySnapshot ToPluginIdentity(MachineInventory inventory) => new()
    {
        SystemManufacturer = IdentityText.Normalize(inventory.Firmware.SystemManufacturer),
        SystemProduct = IdentityText.Normalize(inventory.Firmware.SystemProduct),
        SystemSku = IdentityText.Normalize(inventory.Firmware.SystemSku),
        SystemFamily = IdentityText.Normalize(inventory.Firmware.SystemFamily),
        BaseboardProduct = IdentityText.Normalize(inventory.Firmware.BaseboardProduct),
        BaseboardVersion = IdentityText.Normalize(inventory.Firmware.BaseboardVersion),
        BiosVersion = IdentityText.Normalize(inventory.Firmware.BiosVersion),
        EcFirmwareVersion = IdentityText.Normalize(inventory.Firmware.EmbeddedControllerVersion),
        CpuIdentity = IdentityText.Normalize(inventory.Processor?.NormalizedIdentity),
        UsbEndpoints = [.. inventory.UsbInterfaces
            .Where(endpoint => endpoint.Present
                && endpoint.VendorId is not null
                && endpoint.ProductId is not null)
            .Select(endpoint => new UsbEndpointObservation
            {
                VendorId = endpoint.VendorId!,
                ProductId = endpoint.ProductId!,
                InterfaceNumber = endpoint.InterfaceNumber,
                DeviceRelease = endpoint.DeviceRelease,
                LocationPath = endpoint.LocationPath,
            })],
        WmiProviderSignatures = [.. inventory.WmiClasses
            .Where(provider => provider.Access is WmiAccess.Available or WmiAccess.AccessDenied)
            .Select(provider => $"{provider.Namespace}:{provider.ClassName}")
            .Order(StringComparer.Ordinal)],
    };

    private static string SafeFileName(string value) => string.Concat(value.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-'));

    private static bool ProbeFamilyMatches(string familyId, string targetDeviceId) => familyId switch
    {
        "msi.claw-a2vm.ms-1t52" => string.Equals(targetDeviceId, "ms-1t52", StringComparison.Ordinal),
        _ => false,
    };

    private static bool ProbeEndpointMatches(string endpointId, MachineInventory inventory)
    {
        int namespaceSeparator = endpointId.IndexOf(':');
        int methodSeparator = endpointId.IndexOf('.', namespaceSeparator + 1);
        if (namespaceSeparator <= 0 || methodSeparator <= namespaceSeparator + 1)
        {
            return false;
        }

        string wmiNamespace = endpointId[..namespaceSeparator].Replace('/', '\\');
        string className = endpointId[(namespaceSeparator + 1)..methodSeparator];
        int selectorSeparator = endpointId.IndexOf(':', methodSeparator + 1);
        string methodName = selectorSeparator < 0
            ? endpointId[(methodSeparator + 1)..]
            : endpointId[(methodSeparator + 1)..selectorSeparator];
        return inventory.WmiClasses.Any(item =>
            item.Access is WmiAccess.Available
            && string.Equals(item.Namespace, wmiNamespace, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ClassName, className, StringComparison.Ordinal)
            && item.MethodNames.Contains(methodName, StringComparer.Ordinal));
    }
}
