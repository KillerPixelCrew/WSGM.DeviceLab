using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.DeviceLab.Preflight;

/// <summary>Severity and execution consequence of one doctor check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLabDoctorStatus>))]
internal enum DeviceLabDoctorStatus
{
    /// <summary>The checked prerequisite is ready.</summary>
    Pass,

    /// <summary>The environment remains usable, with a documented limitation.</summary>
    Warning,

    /// <summary>A required prerequisite prevents the requested workflow.</summary>
    Blocked,
}

/// <summary>One stable doctor check suitable for CLI and GUI rendering.</summary>
internal sealed record DeviceLabDoctorCheck
{
    /// <summary>Stable machine-readable check identifier.</summary>
    public required string Code { get; init; }

    /// <summary>Broad area such as environment, runtime, API, permission, or output.</summary>
    public required string Category { get; init; }

    /// <summary>Severity and workflow consequence.</summary>
    public required DeviceLabDoctorStatus Status { get; init; }

    /// <summary>Short operator-facing outcome.</summary>
    public required string Summary { get; init; }

    /// <summary>Optional bounded diagnostic detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>Complete, deterministic Device Lab doctor result.</summary>
internal sealed record DeviceLabDoctorReport
{
    /// <summary>Current doctor-report schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>UTC timestamp supplied by the caller.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Worst status across all checks.</summary>
    public required DeviceLabDoctorStatus Status { get; init; }

    /// <summary>Normalized output directory when path resolution succeeded.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Checks in stable policy order.</summary>
    public IReadOnlyList<DeviceLabDoctorCheck> Checks { get; init; } = [];
}

/// <summary>Availability of one required system DLL export.</summary>
internal sealed record WindowsApiAvailability
{
    /// <summary>Stable check name.</summary>
    public required string Name { get; init; }

    /// <summary>System DLL filename.</summary>
    public required string Library { get; init; }

    /// <summary>Required exported function.</summary>
    public required string Export { get; init; }

    /// <summary>Whether the system DLL and export were found.</summary>
    public required bool Available { get; init; }
}

/// <summary>Observed environment values consumed by the pure doctor evaluator.</summary>
internal sealed record DeviceLabDoctorSnapshot
{
    /// <summary>Whether the current OS identifies as Windows.</summary>
    public required bool IsWindows { get; init; }

    /// <summary>Whether the operating system is 64-bit.</summary>
    public required bool Is64BitOperatingSystem { get; init; }

    /// <summary>Whether the current process is 64-bit.</summary>
    public required bool Is64BitProcess { get; init; }

    /// <summary>Runtime major version.</summary>
    public required int RuntimeMajorVersion { get; init; }

    /// <summary>Human-readable runtime description.</summary>
    public required string RuntimeDescription { get; init; }

    /// <summary>Runtime identifier selected for this process.</summary>
    public required string RuntimeIdentifier { get; init; }

    /// <summary>Whether the current token is an elevated administrator token.</summary>
    public required bool IsElevated { get; init; }

    /// <summary>Whether this process has an interactive local user session.</summary>
    public required bool IsUserInteractive { get; init; }

    /// <summary>Whether a recognized CI environment marker is present.</summary>
    public required bool IsContinuousIntegration { get; init; }

    /// <summary>Availability of required read-only Windows API entry points.</summary>
    public IReadOnlyList<WindowsApiAvailability> RequiredApis { get; init; } = [];

    /// <summary>Whether the selected safe output directory or its nearest parent is writable.</summary>
    public required bool OutputPathWritable { get; init; }

    /// <summary>Bounded output-access diagnostic.</summary>
    public string? OutputAccessDetail { get; init; }
}
