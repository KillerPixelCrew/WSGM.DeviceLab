using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace WSGM.DeviceLab.Preflight;

/// <summary>Collects and evaluates the read-only Device Lab environment preflight.</summary>
internal static class DeviceLabDoctor
{
    /// <summary>Current doctor-report schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly (string Name, string Library, string Export)[] RequiredWindowsApis =
    [
        ("configuration-manager", "cfgmgr32.dll", "CM_Get_Device_Interface_List_SizeW"),
        ("device-setup", "setupapi.dll", "SetupDiGetClassDevsW"),
        ("hid-descriptors", "hid.dll", "HidD_GetPreparsedData"),
        ("raw-input", "user32.dll", "RegisterRawInputDevices"),
        ("high-resolution-clock", "kernel32.dll", "QueryPerformanceCounter"),
    ];

    /// <summary>Runs doctor checks for one explicit output directory.</summary>
    /// <param name="outputDirectory">Directory selected by the operator.</param>
    /// <param name="capturedAt">Timestamp to record.</param>
    /// <param name="repositoryRoot">Detected repository root, when running from a checkout.</param>
    /// <returns>A deterministic report; blocked checks are values rather than exceptions.</returns>
    public static DeviceLabDoctorReport Run(
        string outputDirectory,
        DateTimeOffset capturedAt,
        string? repositoryRoot = null)
    {
        DeviceLabPathBoundaries boundaries = DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot);
        DeviceLabOutputPathDecision outputDecision = DeviceLabOutputPathPolicy.Evaluate(
            outputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        DeviceLabDoctorSnapshot snapshot = WindowsDoctorSnapshotCollector.Collect(outputDecision);
        return Evaluate(snapshot, outputDecision, capturedAt);
    }

    /// <summary>Purely evaluates an observed environment and output-path decision.</summary>
    /// <param name="snapshot">Observed environment values.</param>
    /// <param name="outputDecision">Central output-path policy result.</param>
    /// <param name="capturedAt">Timestamp to record.</param>
    /// <returns>Checks in stable policy order and their aggregate status.</returns>
    public static DeviceLabDoctorReport Evaluate(
        DeviceLabDoctorSnapshot snapshot,
        DeviceLabOutputPathDecision outputDecision,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(outputDecision);

        List<DeviceLabDoctorCheck> checks =
        [
            Check(
                "environment.windows",
                "environment",
                snapshot.IsWindows ? DeviceLabDoctorStatus.Pass : DeviceLabDoctorStatus.Blocked,
                snapshot.IsWindows ? "Windows is available." : "Device Lab requires Windows."),
            Check(
                "architecture.os",
                "architecture",
                snapshot.Is64BitOperatingSystem ? DeviceLabDoctorStatus.Pass : DeviceLabDoctorStatus.Blocked,
                snapshot.Is64BitOperatingSystem
                    ? "The operating system is 64-bit."
                    : "A 64-bit Windows installation is required."),
            Check(
                "architecture.process",
                "architecture",
                snapshot.Is64BitProcess ? DeviceLabDoctorStatus.Pass : DeviceLabDoctorStatus.Blocked,
                snapshot.Is64BitProcess
                    ? "The Device Lab process is 64-bit."
                    : "Run the 64-bit Device Lab build."),
            Check(
                "runtime.net",
                "runtime",
                snapshot.RuntimeMajorVersion >= 10
                    ? DeviceLabDoctorStatus.Pass
                    : DeviceLabDoctorStatus.Blocked,
                snapshot.RuntimeMajorVersion >= 10
                    ? ".NET 10 or newer is active."
                    : ".NET 10 or newer is required.",
                $"{snapshot.RuntimeDescription}; {snapshot.RuntimeIdentifier}"),
        ];

        foreach (WindowsApiAvailability api in snapshot.RequiredApis.OrderBy(api => api.Name, StringComparer.Ordinal))
        {
            checks.Add(Check(
                $"api.{api.Name}",
                "api",
                api.Available ? DeviceLabDoctorStatus.Pass : DeviceLabDoctorStatus.Blocked,
                api.Available
                    ? $"{api.Name} API is available."
                    : $"{api.Name} API is unavailable.",
                $"{api.Library}!{api.Export}"));
        }

        checks.Add(Check(
            "permissions.elevation",
            "permission",
            snapshot.IsElevated ? DeviceLabDoctorStatus.Pass : DeviceLabDoctorStatus.Warning,
            snapshot.IsElevated
                ? "The current token is elevated."
                : "The current token is not elevated; protected observations will report access denied."));

        DeviceLabDoctorStatus outputStatus = !outputDecision.IsAllowed || !snapshot.OutputPathWritable
            ? DeviceLabDoctorStatus.Blocked
            : DeviceLabDoctorStatus.Pass;
        checks.Add(Check(
            "output.path",
            "output",
            outputStatus,
            outputDecision.IsAllowed
                ? snapshot.OutputPathWritable
                    ? "The explicit output path is safe and writable."
                    : "The explicit output path is not writable."
                : outputDecision.Reason ?? "The explicit output path was refused.",
            snapshot.OutputAccessDetail));

        checks.Add(Check(
            "session.interactive",
            "environment",
            snapshot.IsUserInteractive && !snapshot.IsContinuousIntegration
                ? DeviceLabDoctorStatus.Pass
                : DeviceLabDoctorStatus.Warning,
            snapshot.IsUserInteractive && !snapshot.IsContinuousIntegration
                ? "An interactive local user session is available."
                : "This environment cannot run the attended plugin hardware action."));

        DeviceLabDoctorStatus status = checks.Any(check => check.Status is DeviceLabDoctorStatus.Blocked)
            ? DeviceLabDoctorStatus.Blocked
            : checks.Any(check => check.Status is DeviceLabDoctorStatus.Warning)
                ? DeviceLabDoctorStatus.Warning
                : DeviceLabDoctorStatus.Pass;

        return new DeviceLabDoctorReport
        {
            SchemaVersion = CurrentSchemaVersion,
            CapturedAt = capturedAt,
            Status = status,
            OutputDirectory = outputDecision.FullPath,
            Checks = checks,
        };
    }

    private static DeviceLabDoctorCheck Check(
        string code,
        string category,
        DeviceLabDoctorStatus status,
        string summary,
        string? detail = null) => new()
        {
            Code = code,
            Category = category,
            Status = status,
            Summary = summary,
            Detail = detail,
        };

    private static class WindowsDoctorSnapshotCollector
    {
        public static DeviceLabDoctorSnapshot Collect(DeviceLabOutputPathDecision outputDecision)
        {
            (bool outputWritable, string? outputDetail) = ProbeOutputAccess(outputDecision);
            return new DeviceLabDoctorSnapshot
            {
                IsWindows = OperatingSystem.IsWindows(),
                Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                Is64BitProcess = Environment.Is64BitProcess,
                RuntimeMajorVersion = Environment.Version.Major,
                RuntimeDescription = RuntimeInformation.FrameworkDescription,
                RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                IsElevated = IsElevated(),
                IsUserInteractive = Environment.UserInteractive,
                IsContinuousIntegration = IsContinuousIntegration(),
                RequiredApis = RequiredWindowsApis.Select(api => ProbeApi(api)).ToArray(),
                OutputPathWritable = outputWritable,
                OutputAccessDetail = outputDetail,
            };
        }

        private static WindowsApiAvailability ProbeApi(
            (string Name, string Library, string Export) api)
        {
            string libraryPath = Path.Combine(Environment.SystemDirectory, api.Library);
            IntPtr handle = IntPtr.Zero;
            bool available = false;
            try
            {
                available = NativeLibrary.TryLoad(libraryPath, out handle)
                    && NativeLibrary.TryGetExport(handle, api.Export, out _);
            }
            catch (Exception exception) when (exception is DllNotFoundException
                or BadImageFormatException or FileLoadException)
            {
                available = false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    NativeLibrary.Free(handle);
                }
            }

            return new WindowsApiAvailability
            {
                Name = api.Name,
                Library = api.Library,
                Export = api.Export,
                Available = available,
            };
        }

        private static bool IsElevated()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsContinuousIntegration()
        {
            string? ci = Environment.GetEnvironmentVariable("CI");
            string? githubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
            return IsTruthy(ci) || IsTruthy(githubActions);
        }

        private static bool IsTruthy(string? value) =>
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

        private static (bool Writable, string? Detail) ProbeOutputAccess(
            DeviceLabOutputPathDecision outputDecision)
        {
            if (!outputDecision.IsAllowed || outputDecision.FullPath is not { Length: > 0 } outputPath)
            {
                return (false, outputDecision.Reason);
            }

            string? probeDirectory = Directory.Exists(outputPath)
                ? outputPath
                : FindExistingParent(outputPath);
            if (probeDirectory is null)
            {
                return (false, "No existing parent directory could be found.");
            }

            string probePath = Path.Combine(
                probeDirectory,
                $".wsgm-device-doctor-{Guid.NewGuid():N}.tmp");
            try
            {
                using (FileStream stream = new(
                    probePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough))
                {
                    stream.WriteByte(0);
                    stream.Flush(flushToDisk: true);
                }

                File.Delete(probePath);
                return (true, Directory.Exists(outputPath)
                    ? "The output directory is writable."
                    : "The nearest existing parent is writable; the output directory was not created.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or NotSupportedException)
            {
                return (false, exception.GetType().Name);
            }
        }

        private static string? FindExistingParent(string path)
        {
            string? current = Path.GetDirectoryName(path);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                current = parent;
            }

            return null;
        }
    }
}
