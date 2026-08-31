using System;
using System.Collections.Generic;

namespace WSGM.DeviceLab.Inventory;

/// <summary>
/// Everything the sweep observed about one machine, before any interpretation.
/// </summary>
/// <remarks>
/// Raw observation kept separate from candidate matching, so a capture taken today can be re-matched
/// against a catalog that grows later. Nothing here opens a device for writing, invokes an unknown
/// method, or transmits on a serial port — inventory is enumeration only.
/// </remarks>
internal sealed record MachineInventory
{
    /// <summary>Schema version of this inventory.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>SMBIOS and firmware identity.</summary>
    public required FirmwareInventory Firmware { get; init; }

    /// <summary>Processor identity, used only for matching catalog predicates.</summary>
    public ProcessorInventory? Processor { get; init; }

    /// <summary>Display adapters, used only for matching catalog predicates.</summary>
    public IReadOnlyList<GraphicsAdapterInventory> GraphicsAdapters { get; init; } = [];

    /// <summary>USB and HID endpoints present.</summary>
    public IReadOnlyList<UsbInterfaceInventory> UsbInterfaces { get; init; } = [];

    /// <summary>WMI classes found present, without any method being invoked.</summary>
    public IReadOnlyList<WmiClassInventory> WmiClasses { get; init; } = [];

    /// <summary>Serial endpoints and passive framing observations; no bytes were transmitted.</summary>
    public IReadOnlyList<SerialEndpointInventory> SerialEndpoints { get; init; } = [];

    /// <summary>Sensor endpoints observed through passive PnP metadata.</summary>
    public IReadOnlyList<SensorEndpointInventory> Sensors { get; init; } = [];

    /// <summary>Independent controller/input backend views.</summary>
    public IReadOnlyList<InputBackendInventory> InputBackends { get; init; } = [];

    /// <summary>Relevant native binaries inspected as files without loading them.</summary>
    public IReadOnlyList<NativeBinaryInventory> NativeBinaries { get; init; } = [];

    /// <summary>Relevant processes observed without treating presence as resource ownership.</summary>
    public IReadOnlyList<ProcessInventory> Processes { get; init; } = [];

    /// <summary>Relevant Windows services observed without changing service state.</summary>
    public IReadOnlyList<ServiceInventory> Services { get; init; } = [];

    /// <summary>Relevant scheduled tasks observed without running or modifying them.</summary>
    public IReadOnlyList<ScheduledTaskInventory> ScheduledTasks { get; init; } = [];

    /// <summary>Relevant registered or loaded providers without activation.</summary>
    public IReadOnlyList<ProviderInventory> Providers { get; init; } = [];

    /// <summary>Potential or demonstrated ownership conflicts with their observed signal.</summary>
    public IReadOnlyList<ResourceConflictInventory> ResourceConflicts { get; init; } = [];

    /// <summary>Bounded passive topology generations for baseline and imported change fixtures.</summary>
    public IReadOnlyList<TopologyGenerationInventory> TopologyGenerations { get; init; } = [];

    /// <summary>Inventory lanes that could not be queried, distinct from a successful empty result.</summary>
    public IReadOnlyList<InventoryCollectionIssue> CollectionIssues { get; init; } = [];

    /// <summary>When the sweep ran, in UTC.</summary>
    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>A read-only inventory lane that failed before it could report observations.</summary>
internal sealed record InventoryCollectionIssue
{
    /// <summary>Stable lane name, such as <c>graphics</c> or <c>usb</c>.</summary>
    public required string Lane { get; init; }

    /// <summary>Stable failure category without machine-specific exception text.</summary>
    public required string Error { get; init; }
}

/// <summary>SMBIOS and firmware identity as read from the machine.</summary>
/// <remarks>
/// System and baseboard fields are recorded separately and never merged. On the reference handheld
/// the exact board identifier lives in the baseboard product while the system product carries only a
/// marketing string, so a schema with one "product" field would silently lose the useful half.
/// </remarks>
internal sealed record FirmwareInventory
{
    /// <summary>SMBIOS Type 1 manufacturer.</summary>
    public string? SystemManufacturer { get; init; }

    /// <summary>SMBIOS Type 1 product — marketing text.</summary>
    public string? SystemProduct { get; init; }

    /// <summary>SMBIOS Type 1 SKU number.</summary>
    public string? SystemSku { get; init; }

    /// <summary>SMBIOS Type 1 family.</summary>
    public string? SystemFamily { get; init; }

    /// <summary>SMBIOS Type 2 baseboard product — the exact board.</summary>
    public string? BaseboardProduct { get; init; }

    /// <summary>SMBIOS Type 2 baseboard version.</summary>
    public string? BaseboardVersion { get; init; }

    /// <summary>BIOS version string.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>
    /// EC firmware version as SMBIOS reports it, which is frequently useless.
    /// </summary>
    /// <remarks>
    /// Recorded as observed, including the <c>255</c> "unknown" encoding that the reference handheld
    /// returns for both major and minor. Storing the useless value rather than dropping it is what
    /// lets a matcher tell "SMBIOS says unknown" apart from "nobody looked", and the real version
    /// comes from the vendor provider instead.
    /// </remarks>
    public string? EmbeddedControllerVersion { get; init; }
}

/// <summary>Processor identity.</summary>
internal sealed record ProcessorInventory
{
    /// <summary>Marketing name.</summary>
    public string? Name { get; init; }

    /// <summary>CPUID family.</summary>
    public int? Family { get; init; }

    /// <summary>CPUID model.</summary>
    public int? Model { get; init; }

    /// <summary>CPUID stepping.</summary>
    public int? Stepping { get; init; }

    /// <summary>Physical core count.</summary>
    public int? Cores { get; init; }

    /// <summary>Normalized <c>family-model-stepping</c> form used for matching.</summary>
    public string? NormalizedIdentity =>
        Family is { } family && Model is { } model && Stepping is { } stepping
            ? $"{family}-{model}-{stepping}"
            : null;
}

/// <summary>One USB or HID interface present on the machine.</summary>
internal sealed record UsbInterfaceInventory
{
    /// <summary>Device instance path.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Setup class the interface is registered under.</summary>
    public string? DeviceClass { get; init; }

    /// <summary>USB vendor ID, four uppercase hexadecimal digits.</summary>
    public string? VendorId { get; init; }

    /// <summary>USB product ID, four uppercase hexadecimal digits.</summary>
    public string? ProductId { get; init; }

    /// <summary>USB <c>bcdDevice</c> value from a <c>REV_</c> hardware ID.</summary>
    public string? DeviceRelease { get; init; }

    /// <summary>Interface number of a composite device.</summary>
    public int? InterfaceNumber { get; init; }

    /// <summary>
    /// Physical USB location path.
    /// </summary>
    /// <remarks>
    /// Captured because it is the only identifier verified stable across a controller mode switch on
    /// the reference hardware. It belongs to this machine and is redacted from shareable output.
    /// </remarks>
    public string? LocationPath { get; init; }

    /// <summary>
    /// The composite device this interface belongs to, as a location path.
    /// </summary>
    /// <remarks>
    /// <see cref="LocationPath"/> with any trailing interface component removed. This is the value
    /// hotplug continuation keys on: the composite-level prefix was verified byte-identical across a
    /// full controller mode switch, while the interface index it drops is not established as stable
    /// across that same event — a mode switch is precisely what rearranges the interfaces.
    /// </remarks>
    public string? DeviceLevelLocationPath { get; init; }

    /// <summary>Whether the device is currently present and started.</summary>
    public bool Present { get; init; }
}

/// <summary>One WMI class found present.</summary>
/// <remarks>
/// Presence and method <em>signatures</em> only. Enumerating a vendor method never authorizes calling
/// it: an unknown method may write, and the whole inventory stage is read-only by construction.
/// </remarks>
internal sealed record WmiClassInventory
{
    /// <summary>WMI namespace, for example <c>root\WMI</c>.</summary>
    public required string Namespace { get; init; }

    /// <summary>Class name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Whether the class could be enumerated with the current rights.</summary>
    public required WmiAccess Access { get; init; }

    /// <summary>Number of instances found, when enumeration succeeded.</summary>
    public int? InstanceCount { get; init; }

    /// <summary>Names of methods the class declares. Never invoked.</summary>
    public IReadOnlyList<string> MethodNames { get; init; } = [];
}

/// <summary>Whether a WMI class could be reached.</summary>
/// <remarks>
/// <see cref="AccessDenied"/> is distinct from <see cref="NotFound"/> on purpose, and the difference
/// is load-bearing: on the reference handheld the vendor provider returns access-denied from a
/// medium-integrity process and enumerates fine when elevated. Recording both as "absent" would
/// diagnose a rights problem as a missing provider.
/// </remarks>
internal enum WmiAccess
{
    /// <summary>Enumerated successfully.</summary>
    Available,

    /// <summary>Present, but the current process lacks rights.</summary>
    AccessDenied,

    /// <summary>Not present in that namespace.</summary>
    NotFound,

    /// <summary>The namespace itself could not be reached.</summary>
    NamespaceUnavailable,
}
