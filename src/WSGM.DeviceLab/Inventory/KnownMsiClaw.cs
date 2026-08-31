using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Probes;

namespace WSGM.DeviceLab.Inventory;

/// <summary>One exact known-device fingerprint and the reviewed observations available for it.</summary>
internal sealed record KnownDeviceFingerprint
{
    /// <summary>Stable logical device ID.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Human-readable device name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Required SMBIOS system manufacturer.</summary>
    public required string SystemManufacturer { get; init; }

    /// <summary>Required SMBIOS baseboard product.</summary>
    public required string BaseboardProduct { get; init; }

    /// <summary>Required SMBIOS system SKU.</summary>
    public required string SystemSku { get; init; }

    /// <summary>Required USB vendor identifier.</summary>
    public required string UsbVendorId { get; init; }

    /// <summary>Allowed exact USB product identifiers.</summary>
    public IReadOnlyList<string> UsbProductIds { get; init; } = [];

    /// <summary>Required USB device release.</summary>
    public required string UsbDeviceRelease { get; init; }

    /// <summary>Required WMI namespace.</summary>
    public required string WmiNamespace { get; init; }

    /// <summary>Required WMI class.</summary>
    public required string WmiClass { get; init; }

    /// <summary>Reviewed read-only probes compiled into Device Lab.</summary>
    public IReadOnlyList<ReadProbeMetadata> ReadProbes { get; init; } = [];

    /// <summary>Device-specific facts that a new plugin must re-establish.</summary>
    public IReadOnlyList<string> NonInheritableValues { get; init; } = [];
}

/// <summary>Explained exact comparison against one known-device fingerprint.</summary>
internal sealed record CandidateAssessment
{
    /// <summary>Known device that was compared.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Human-readable known-device name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether every exact fingerprint field matched.</summary>
    public required bool ExactMatch { get; init; }

    /// <summary>One deterministic pass or mismatch explanation per fingerprint field.</summary>
    public IReadOnlyList<string> Explanations { get; init; } = [];

    /// <summary>Device-specific values that are not implied by a match.</summary>
    public IReadOnlyList<string> NonInheritableValues { get; init; } = [];
}

/// <summary>Performs deterministic exact matching with explained mismatches.</summary>
internal static class KnownDeviceMatcher
{
    /// <summary>Compares one inventory with one known fingerprint.</summary>
    /// <param name="inventory">Observed machine inventory.</param>
    /// <param name="fingerprint">Known exact device.</param>
    /// <param name="targetDeviceId">Logical device ID requested by the caller.</param>
    /// <returns>Explained exact match result.</returns>
    public static CandidateAssessment Assess(
        MachineInventory inventory,
        KnownDeviceFingerprint fingerprint,
        string targetDeviceId)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);

        List<string> explanations = [];
        bool exact = Check(
            "logical device ID",
            targetDeviceId,
            fingerprint.DeviceId,
            explanations);
        exact &= Check(
            "SMBIOS system manufacturer",
            inventory.Firmware.SystemManufacturer,
            fingerprint.SystemManufacturer,
            explanations);
        exact &= Check(
            "SMBIOS baseboard product",
            inventory.Firmware.BaseboardProduct,
            fingerprint.BaseboardProduct,
            explanations);
        exact &= Check(
            "SMBIOS system SKU",
            inventory.Firmware.SystemSku,
            fingerprint.SystemSku,
            explanations);

        bool usb = inventory.UsbInterfaces.Any(endpoint =>
            string.Equals(endpoint.VendorId, fingerprint.UsbVendorId, StringComparison.OrdinalIgnoreCase)
            && fingerprint.UsbProductIds.Contains(endpoint.ProductId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            && string.Equals(endpoint.DeviceRelease, fingerprint.UsbDeviceRelease, StringComparison.OrdinalIgnoreCase));
        explanations.Add(usb
            ? $"USB endpoint matched {fingerprint.UsbVendorId}:[{string.Join(", ", fingerprint.UsbProductIds)}] release {fingerprint.UsbDeviceRelease}."
            : $"USB endpoint mismatch: expected {fingerprint.UsbVendorId}:[{string.Join(", ", fingerprint.UsbProductIds)}] release {fingerprint.UsbDeviceRelease}.");
        exact &= usb;

        bool wmi = inventory.WmiClasses.Any(provider =>
            provider.Access is WmiAccess.Available or WmiAccess.AccessDenied
            && string.Equals(provider.Namespace, fingerprint.WmiNamespace, StringComparison.OrdinalIgnoreCase)
            && string.Equals(provider.ClassName, fingerprint.WmiClass, StringComparison.Ordinal));
        explanations.Add(wmi
            ? $"WMI provider matched {fingerprint.WmiNamespace}:{fingerprint.WmiClass}."
            : $"WMI provider mismatch: expected {fingerprint.WmiNamespace}:{fingerprint.WmiClass}.");
        exact &= wmi;

        return new CandidateAssessment
        {
            DeviceId = fingerprint.DeviceId,
            DisplayName = fingerprint.DisplayName,
            ExactMatch = exact,
            Explanations = explanations,
            NonInheritableValues = fingerprint.NonInheritableValues,
        };
    }

    private static bool Check(
        string label,
        string? actual,
        string expected,
        ICollection<string> explanations)
    {
        bool matched = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        explanations.Add(matched
            ? $"{label} matched '{expected}'."
            : $"{label} mismatch: expected '{expected}', observed '{actual ?? "<missing>"}'.");
        return matched;
    }
}

/// <summary>The one known-device fingerprint built into Device Lab.</summary>
internal static class KnownMsiClaw
{
    /// <summary>Creates the MS-1T52 fingerprint with five compiled MSI read probes.</summary>
    /// <returns>The exact known-device fingerprint.</returns>
    public static KnownDeviceFingerprint Create() => new()
    {
        DeviceId = "ms-1t52",
        DisplayName = "MSI Claw 8 AI+ A2VM",
        SystemManufacturer = "Micro-Star International Co., Ltd.",
        BaseboardProduct = "MS-1T52",
        SystemSku = "1T52.1",
        UsbVendorId = "0DB0",
        UsbProductIds = ["1901", "1902"],
        UsbDeviceRelease = "0229",
        WmiNamespace = "root\\WMI",
        WmiClass = "MSI_ACPI",
        ReadProbes = MsiReadProbes(),
        NonInheritableValues =
        [
            "WMI addresses and response offsets",
            "power limits and scenario policy",
            "fan table width, conversion, and safe minimum duty",
            "controller profile-memory offsets and mode topology",
            "RGB zone order and persistence",
        ],
    };

    private static IReadOnlyList<ReadProbeMetadata> MsiReadProbes() =>
    [
        Probe("msi.claw-a2vm.wmi-version", ReadProbeFamily.Version,
            "root/WMI:MSI_ACPI.Get_WMI", "vendor-wmi", ReadProbeValueKind.Version, 4, 4,
            0, 255),
        Probe("msi.claw-a2vm.ec-version", ReadProbeFamily.EmbeddedController,
            "root/WMI:MSI_ACPI.Get_EC", "vendor-wmi", ReadProbeValueKind.Bytes, 32, 32,
            null, null),
        Probe("msi.claw-a2vm.scenario-status", ReadProbeFamily.WmiStatus,
            "root/WMI:MSI_ACPI.Get_Data:0xd2", "power-policy", ReadProbeValueKind.Integer, 2, 2,
            0, 255),
        Probe("msi.claw-a2vm.fan-rpm", ReadProbeFamily.FanRpm,
            "root/WMI:MSI_ACPI.Get_Fan:0", "fan-control", ReadProbeValueKind.Text, 5, 5,
            null, null, stable: false, crossCheck: ReadProbeCrossCheckKind.Present),
        Probe("msi.claw-a2vm.charge-limit", ReadProbeFamily.ChargeState,
            "root/WMI:MSI_ACPI.Get_Data:0xd7", "charge-policy", ReadProbeValueKind.Integer, 2, 2,
            0, 100),
    ];

    private static ReadProbeMetadata Probe(
        string id,
        ReadProbeFamily family,
        string endpoint,
        string resource,
        ReadProbeValueKind kind,
        int minimumLength,
        int maximumLength,
        long? minimum = null,
        long? maximum = null,
        bool stable = true,
        ReadProbeCrossCheckKind crossCheck = ReadProbeCrossCheckKind.Equal) => new()
        {
            Id = id,
            Version = 1,
            FamilyId = "msi.claw-a2vm.ms-1t52",
            EndpointId = endpoint,
            ResourceId = resource,
            Family = family,
            MaximumReadsPerSecond = 2,
            TimeoutMilliseconds = 5_000,
            Repetitions = 2,
            ExpectedResponse = new ReadProbeResponseExpectation
            {
                ValueKind = kind,
                MinimumLength = minimumLength,
                MaximumLength = maximumLength,
                AllowedStatusCodes = [1],
                MinimumValue = minimum,
                MaximumValue = maximum,
                MustBeStable = stable,
            },
            CrossCheck = new ReadProbeCrossCheck
            {
                Id = $"{id}.repeat-read",
                Kind = crossCheck,
            },
            RequiresElevation = true,
        };
}
