using System.Collections.Generic;

namespace WSGM.DeviceLab.Inventory;

/// <summary>Hard allocation and enumeration bounds for one automatic inventory sweep.</summary>
internal static class InventoryLimits
{
    /// <summary>Maximum text retained from any one machine observation.</summary>
    public const int MaximumTextCharacters = 2048;

    /// <summary>Maximum endpoints retained in one transport or API lane.</summary>
    public const int MaximumEndpointsPerLane = 256;

    /// <summary>Maximum independent backend/API projections in one inventory.</summary>
    public const int MaximumInputBackendViews = 15;

    /// <summary>Maximum relevant processes, services, tasks, providers, or binaries per lane.</summary>
    public const int MaximumSystemEntriesPerLane = 512;

    /// <summary>Maximum passive serial framing candidates per endpoint.</summary>
    public const int MaximumFramingCandidates = 16;

    /// <summary>Maximum supported sensor intervals retained per endpoint.</summary>
    public const int MaximumSensorIntervals = 64;

    /// <summary>Maximum native binary size hashed by automatic inventory.</summary>
    public const long MaximumNativeBinaryBytes = 512L * 1024 * 1024;

    /// <summary>Maximum export names retained from one native binary.</summary>
    public const int MaximumNativeExports = 4096;
}

/// <summary>One display adapter identity used only for catalog matching.</summary>
internal sealed record GraphicsAdapterInventory
{
    /// <summary>PnP instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Adapter marketing name.</summary>
    public string? Name { get; init; }

    /// <summary>PCI vendor identifier.</summary>
    public string? VendorId { get; init; }

    /// <summary>PCI device identifier.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Installed driver version.</summary>
    public string? DriverVersion { get; init; }
}

/// <summary>Access result for an enumerated passive endpoint.</summary>
internal enum InventoryAccess
{
    /// <summary>The endpoint metadata was available.</summary>
    Available,

    /// <summary>The endpoint exists but metadata access was denied.</summary>
    AccessDenied,

    /// <summary>The endpoint disappeared during enumeration.</summary>
    Disconnected,

    /// <summary>The platform cannot expose this property without active probing.</summary>
    Unsupported,

    /// <summary>The endpoint returned structurally invalid or excessive metadata.</summary>
    Malformed,

    /// <summary>A read-only open was rejected by an existing incompatible share or lease.</summary>
    ExclusiveAccessDenied,
}

/// <summary>A serial-port framing value reported by the installed driver.</summary>
/// <remarks>This is a passive observation, not an instruction to transmit with these settings.</remarks>
internal sealed record SerialFramingCandidate
{
    /// <summary>Baud rate reported by the provider.</summary>
    public uint? BaudRate { get; init; }

    /// <summary>Number of data bits.</summary>
    public byte? DataBits { get; init; }

    /// <summary>Provider parity value.</summary>
    public byte? Parity { get; init; }

    /// <summary>Provider stop-bit value.</summary>
    public byte? StopBits { get; init; }

    /// <summary>Where this candidate came from.</summary>
    public required string Source { get; init; }
}

/// <summary>One passively enumerated COM endpoint.</summary>
internal sealed record SerialEndpointInventory
{
    /// <summary>PnP instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Windows port name, such as <c>COM4</c>.</summary>
    public string? PortName { get; init; }

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Provider or device manufacturer.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Physical location when available.</summary>
    public string? LocationPath { get; init; }

    /// <summary>Immediate PnP parent used only as a passive association basis.</summary>
    public string? AssociationId { get; init; }

    /// <summary>Whether the endpoint was present when enumerated.</summary>
    public bool Present { get; init; }

    /// <summary>Whether passive metadata was accessible.</summary>
    public required InventoryAccess Access { get; init; }

    /// <summary>Driver-reported framing candidates. No serial handle was opened.</summary>
    public IReadOnlyList<SerialFramingCandidate> FramingCandidates { get; init; } = [];
}

/// <summary>One sensor-like PnP endpoint and its passive association data.</summary>
internal sealed record SensorEndpointInventory
{
    /// <summary>PnP instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Friendly endpoint name.</summary>
    public string? Name { get; init; }

    /// <summary>Observed PnP class or sensor kind.</summary>
    public string? Kind { get; init; }

    /// <summary>Parent or container association, private until redacted.</summary>
    public string? AssociationId { get; init; }

    /// <summary>API or topology view that produced this observation.</summary>
    public SensorApiKind Api { get; init; }

    /// <summary>How the controller or parent association was established.</summary>
    public string? AssociationBasis { get; init; }

    /// <summary>Composite-level physical location used to correlate detachable endpoints.</summary>
    public string? DeviceLevelLocationPath { get; init; }

    /// <summary>Minimum supported report interval when passively published.</summary>
    public uint? MinimumReportIntervalMilliseconds { get; init; }

    /// <summary>Other passively published supported intervals in deterministic order.</summary>
    public IReadOnlyList<uint> SupportedReportIntervalsMilliseconds { get; init; } = [];

    /// <summary>Reported measurement unit, when published.</summary>
    public string? Unit { get; init; }

    /// <summary>Current metadata accessibility.</summary>
    public required InventoryAccess Access { get; init; }
}

/// <summary>Sensor API or association view.</summary>
internal enum SensorApiKind
{
    /// <summary>Passive PnP sensor or HID-sensor metadata.</summary>
    Pnp,

    /// <summary>Windows Runtime sensor projection.</summary>
    WinRt,

    /// <summary>Sensor endpoint associated with a controller topology.</summary>
    Controller,
}

/// <summary>Supported independent input views.</summary>
internal enum InputBackendKind
{
    /// <summary>Windows XInput slots.</summary>
    XInput,

    /// <summary>DirectInput-compatible PnP devices.</summary>
    DirectInput,

    /// <summary>SDL runtime discovery.</summary>
    Sdl,

    /// <summary>Win32 Raw Input devices.</summary>
    RawInput,

    /// <summary>Raw HID PnP interfaces.</summary>
    RawHid,
}

/// <summary>One endpoint visible through an input backend.</summary>
internal sealed record InputEndpointInventory
{
    /// <summary>Backend-local stable slot or session identifier.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Backend-reported display name.</summary>
    public string? Name { get; init; }

    /// <summary>Associated PnP instance when available.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Backend-specific device type.</summary>
    public string? DeviceType { get; init; }

    /// <summary>Backend-local metadata accessibility.</summary>
    public InventoryAccess Access { get; init; } = InventoryAccess.Available;

    /// <summary>USB vendor identifier when the backend exposes or correlates it.</summary>
    public string? VendorId { get; init; }

    /// <summary>USB product identifier when the backend exposes or correlates it.</summary>
    public string? ProductId { get; init; }

    /// <summary>Composite-level location association when passively available.</summary>
    public string? AssociationId { get; init; }

    /// <summary>Whether the endpoint may detach within the same logical device generation.</summary>
    public bool Detachable { get; init; }

    /// <summary>Whether passive HID descriptor metadata was structurally available.</summary>
    public InventoryAccess DescriptorAccess { get; init; } = InventoryAccess.Unsupported;

    /// <summary>SHA-256 of the report descriptor when an earlier passive observation supplied it.</summary>
    public string? ReportDescriptorSha256 { get; init; }

    /// <summary>Maximum input report length published by the passive descriptor view.</summary>
    public int? InputReportBytes { get; init; }

    /// <summary>Maximum output report length published by the passive descriptor view.</summary>
    public int? OutputReportBytes { get; init; }

    /// <summary>Maximum feature report length published by the passive descriptor view.</summary>
    public int? FeatureReportBytes { get; init; }

    /// <summary>Whether the endpoint was connected at enumeration time.</summary>
    public bool Connected { get; init; }
}

/// <summary>One independent input backend view.</summary>
internal sealed record InputBackendInventory
{
    /// <summary>Backend identity.</summary>
    public required InputBackendKind Backend { get; init; }

    /// <summary>Whether safe enumeration was available.</summary>
    public required InventoryAccess Access { get; init; }

    /// <summary>Whether this is a live API enumeration, compatibility projection, or runtime check.</summary>
    public InputBackendViewKind View { get; init; }

    /// <summary>Whether the backend runtime itself was present without implying endpoint access.</summary>
    public bool RuntimeAvailable { get; init; }

    /// <summary>Observed endpoints in deterministic order.</summary>
    public IReadOnlyList<InputEndpointInventory> Endpoints { get; init; } = [];

    /// <summary>Explicit limit of this view.</summary>
    public string? Limitation { get; init; }
}

/// <summary>How an input-backend view was obtained.</summary>
internal enum InputBackendViewKind
{
    /// <summary>The read-only API enumerated its own endpoints.</summary>
    LiveApi,

    /// <summary>PnP metadata was projected into a compatibility view without acquiring devices.</summary>
    PassiveCompatibility,

    /// <summary>Only runtime file availability was checked; no subsystem was initialized.</summary>
    RuntimeOnly,
}

/// <summary>Signature observation for a native file.</summary>
internal enum BinarySignatureState
{
    /// <summary>An embedded signer certificate was present.</summary>
    Signed,

    /// <summary>No embedded signer certificate was found.</summary>
    Unsigned,

    /// <summary>The signature could not be inspected.</summary>
    Unknown,
}

/// <summary>Native PE metadata read from disk without loading the binary.</summary>
internal sealed record NativeBinaryInventory
{
    /// <summary>Whether file metadata, signature, hash, and PE shape were accessible.</summary>
    public InventoryAccess Access { get; init; }

    /// <summary>Absolute file path; private captures only.</summary>
    public required string Path { get; init; }

    /// <summary>File name.</summary>
    public required string Name { get; init; }

    /// <summary>Exact file length included in the bounded inspection.</summary>
    public long FileBytes { get; init; }

    /// <summary>File version resource.</summary>
    public string? Version { get; init; }

    /// <summary>PE architecture.</summary>
    public string? Architecture { get; init; }

    /// <summary>Lowercase SHA-256 of the bytes on disk.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Embedded signature observation; verification is not inferred.</summary>
    public required BinarySignatureState Signature { get; init; }

    /// <summary>Signer certificate subject when present.</summary>
    public string? SignerSubject { get; init; }

    /// <summary>PE export names parsed from disk without invoking them.</summary>
    public IReadOnlyList<string> Exports { get; init; } = [];
}

/// <summary>Relevant running-process observation.</summary>
internal sealed record ProcessInventory
{
    /// <summary>Session-local process identifier.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Shareable session-local token replacing the private process ID.</summary>
    public string? SessionToken { get; init; }

    /// <summary>Executable name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether path, command-line, and module metadata were accessible.</summary>
    public InventoryAccess Access { get; init; }

    /// <summary>Executable path when accessible; private captures only.</summary>
    public string? Path { get; init; }

    /// <summary>Command line when accessible; private captures only.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Relevant native modules already loaded by the process.</summary>
    public IReadOnlyList<string> LoadedModulePaths { get; init; } = [];
}

/// <summary>Relevant Windows service observation.</summary>
internal sealed record ServiceInventory
{
    /// <summary>Service name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether service configuration metadata was accessible.</summary>
    public InventoryAccess Access { get; init; }

    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Observed service state.</summary>
    public string? State { get; init; }

    /// <summary>Configured binary path; private captures only.</summary>
    public string? PathName { get; init; }

    /// <summary>Process ID when running.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Shareable token correlating a running service with its process.</summary>
    public string? ProcessToken { get; init; }
}

/// <summary>Relevant scheduled-task observation.</summary>
internal sealed record ScheduledTaskInventory
{
    /// <summary>Task path and name.</summary>
    public required string Path { get; init; }

    /// <summary>Whether task metadata was accessible.</summary>
    public InventoryAccess Access { get; init; }

    /// <summary>Observed task state.</summary>
    public string? State { get; init; }

    /// <summary>Whether the task is enabled.</summary>
    public bool? Enabled { get; init; }
}

/// <summary>One configured or loaded provider observed without activating it.</summary>
internal sealed record ProviderInventory
{
    /// <summary>Provider kind, such as WMI registration or loaded native module.</summary>
    public required string Kind { get; init; }

    /// <summary>Provider or module name.</summary>
    public required string Name { get; init; }

    /// <summary>Namespace or host context when published.</summary>
    public string? Context { get; init; }

    /// <summary>Provider host process ID when reported.</summary>
    public int? HostProcessId { get; init; }

    /// <summary>Shareable session-local host-process token.</summary>
    public string? HostProcessToken { get; init; }

    /// <summary>Provider module path when already loaded and accessible.</summary>
    public string? ModulePath { get; init; }

    /// <summary>Whether the provider was observed loaded rather than merely registered.</summary>
    public bool Loaded { get; init; }

    /// <summary>Current metadata accessibility.</summary>
    public required InventoryAccess Access { get; init; }
}

/// <summary>Observed signal that another owner may conflict with a resource.</summary>
internal enum ConflictSignalKind
{
    /// <summary>A name match only; this is never ownership proof.</summary>
    PresenceOnly,

    /// <summary>The resource reported sharing or access denial during an allowlisted read.</summary>
    ExclusiveAccessDenied,

    /// <summary>The production owner explicitly reported an active lease.</summary>
    ReportedLease,
}

/// <summary>One potential or demonstrated resource conflict.</summary>
internal sealed record ResourceConflictInventory
{
    /// <summary>Semantic resource ID.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Observed possible owner.</summary>
    public required string Owner { get; init; }

    /// <summary>Observed conflict signal.</summary>
    public required ConflictSignalKind Signal { get; init; }

    /// <summary>Whether the signal demonstrates a current conflict.</summary>
    public bool Demonstrated => Signal is not ConflictSignalKind.PresenceOnly;
}

/// <summary>One bounded PnP topology observation tied to a monotonic device generation.</summary>
internal sealed record TopologyGenerationInventory
{
    /// <summary>Monotonic generation within this inventory or imported fixture.</summary>
    public required long Generation { get; init; }

    /// <summary>Baseline, arrival, removal, or metadata-change observation.</summary>
    public required TopologyChangeKind Change { get; init; }

    /// <summary>Private device instance identity.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Composite-level location continuing across detachable re-enumeration.</summary>
    public string? AssociationId { get; init; }

    /// <summary>Whether the endpoint was present after this observation.</summary>
    public bool Present { get; init; }
}

/// <summary>Kind of passive PnP topology observation.</summary>
internal enum TopologyChangeKind
{
    /// <summary>Initial sweep state.</summary>
    Baseline,

    /// <summary>An endpoint arrived.</summary>
    Arrival,

    /// <summary>An endpoint was removed.</summary>
    Removal,

    /// <summary>Metadata changed without treating it as a new physical association.</summary>
    Changed,
}
