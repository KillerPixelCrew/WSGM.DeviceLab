using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Probes;

namespace WSGM.DeviceLab.Testing;

internal sealed record PluginTestWorkerRequest
{
    public required int SchemaVersion { get; init; }

    public required PluginTestMode Mode { get; init; }

    public required string PackageDirectory { get; init; }

    public required DeviceIdentitySnapshot Identity { get; init; }

    public string? StateDirectory { get; init; }

    public AttendedPluginActionRequest? Action { get; init; }

    public bool Confirmed { get; init; }

    public bool ParentOwnerReserved { get; init; }

    public required string AuthorizationSha256 { get; init; }
}

internal sealed record PluginTestWorkerResponse
{
    public required int SchemaVersion { get; init; }

    public required string AuthorizationSha256 { get; init; }

    public PluginTestReport? Report { get; init; }

    public string? Error { get; init; }
}

/// <summary>Disposable hidden worker which is never trusted without its inherited one-use pipe.</summary>
internal static class PluginTestWorker
{
    internal const string Mode = "__plugin-test";
    internal const string RequestFileName = "plugin-request.json";
    internal const string ResultFileName = "plugin-result.json";

    private const int ExitSuccess = 0;
    private const int ExitInvalidArguments = 64;
    private const int ExitRejected = 65;
    private const int ExitFailure = 70;
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan AuthorizationDeadline = TimeSpan.FromSeconds(5);

    internal static int Run(IReadOnlyList<string> args)
    {
        if (!TryParseArguments(args, out Arguments? arguments, out string? error))
        {
            Console.Error.WriteLine(error);
            return ExitInvalidArguments;
        }

        try
        {
            return RunAsync(arguments!, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Device Lab plugin worker failed: {Bound(exception.Message)}");
            return ExitFailure;
        }
    }

    private static async Task<int> RunAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        using CancellationTokenSource authorization =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authorization.CancelAfter(AuthorizationDeadline);
        byte[]? authorizationSecret = await SelfWorkerAuthorization.ReadSecretAsync(
            arguments.AuthorizationHandle,
            authorization.Token).ConfigureAwait(false);
        if (authorizationSecret is null)
        {
            Console.Error.WriteLine("The plugin worker was not authorized by its supervisor.");
            return ExitRejected;
        }

        if (!SelfWorkerAuthorization.TryConstrainSessionFiles(
                arguments.RequestPath,
                arguments.ResultPath,
                RequestFileName,
                ResultFileName,
                out string? requestPath,
                out string? resultPath))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorizationSecret);
            Console.Error.WriteLine("The plugin worker session paths were rejected.");
            return ExitRejected;
        }

        PluginTestWorkerRequest? request = await ReadRequestAsync(requestPath!, cancellationToken)
            .ConfigureAwait(false);
        if (request is null
            || request.SchemaVersion != 1
            || request.Identity is null
            || string.IsNullOrWhiteSpace(request.PackageDirectory)
            || request.Mode is not (PluginTestMode.DetectionOnly or PluginTestMode.AttendedHardware)
            || request.Mode is PluginTestMode.AttendedHardware
                && (!request.ParentOwnerReserved
                    || request.Action is null
                    || string.IsNullOrWhiteSpace(request.StateDirectory)))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorizationSecret);
            Console.Error.WriteLine("The plugin worker request was malformed.");
            return ExitRejected;
        }

        bool authorized = SelfWorkerAuthorization.VerifySecret(
            authorizationSecret,
            request.AuthorizationSha256);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorizationSecret);
        if (!authorized)
        {
            Console.Error.WriteLine("The plugin worker was not authorized by its supervisor.");
            return ExitRejected;
        }

        PluginTestReport? report = null;
        string? failure = null;
        try
        {
            if (request.Mode is PluginTestMode.DetectionOnly)
            {
                report = await PluginTestWorkflow.TestDetectionInProcessAsync(
                    request.PackageDirectory,
                    request.Identity,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string? repositoryRoot = DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory)
                    ?? DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory);
                report = await PluginTestWorkflow.RunAttendedAsync(
                    request.PackageDirectory,
                    request.Identity,
                    request.StateDirectory!,
                    request.Action!,
                    request.Confirmed,
                    DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot),
                    ParentReservedSafetyEnvironment(),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = Bound(exception.Message);
        }

        await WriteResultAsync(
            resultPath!,
            new PluginTestWorkerResponse
            {
                SchemaVersion = 1,
                AuthorizationSha256 = request.AuthorizationSha256,
                Report = report,
                Error = failure,
            },
            cancellationToken).ConfigureAwait(false);
        return ExitSuccess;
    }

    private static AttendedPluginSafetyEnvironment ParentReservedSafetyEnvironment() => new()
    {
        ReserveOwner = static () => new DeviceLabOwnerReservationResult
        {
            Inspection = new DeviceLabOwnerInspection
            {
                State = DeviceOwnerDiscoveryState.Absent,
            },
            // The real machine-wide handle remains in the supervising process. This local handle
            // preserves the in-process lifetime ordering without pretending to own another mutex.
            Reservation = new DeviceLabOwnerReservation(new NoopDisposable()),
        },
        IsElevated = IsElevated(),
        IsUserInteractive = Environment.UserInteractive,
        IsContinuousIntegration = IsContinuousIntegration(),
    };

    private static async Task<PluginTestWorkerRequest?> ReadRequestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumRequestBytes)
        {
            return null;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<PluginTestWorkerRequest>(
            stream,
            PluginTestWorkerJson.Options,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResultAsync(
        string path,
        PluginTestWorkerResponse response,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            response,
            PluginTestWorkerJson.Options,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out Arguments? parsed,
        out string? error)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count
                || args[index] is not ("--request" or "--result" or "--authorization-handle")
                || !values.TryAdd(args[index], args[index + 1]))
            {
                parsed = null;
                error = "The plugin worker requires exactly --request, --result, and --authorization-handle once each.";
                return false;
            }
        }

        if (values.Count != 3
            || !values.TryGetValue("--request", out string? requestPath)
            || !values.TryGetValue("--result", out string? resultPath)
            || !values.TryGetValue("--authorization-handle", out string? authorizationHandle)
            || string.IsNullOrWhiteSpace(requestPath)
            || string.IsNullOrWhiteSpace(resultPath)
            || string.IsNullOrWhiteSpace(authorizationHandle))
        {
            parsed = null;
            error = "The plugin worker arguments were incomplete or malformed.";
            return false;
        }

        parsed = new Arguments(requestPath, resultPath, authorizationHandle);
        error = null;
        return true;
    }

    private static bool IsElevated()
    {
        using System.Security.Principal.WindowsIdentity identity =
            System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool IsContinuousIntegration() =>
        IsTruthy(Environment.GetEnvironmentVariable("CI"))
        || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static string Bound(string value) =>
        value[..Math.Min(value.Length, 16_384)];

    private sealed record Arguments(
        string RequestPath,
        string ResultPath,
        string AuthorizationHandle);

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal static class PluginTestWorkerJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Supervises all community plugin code behind a hard process-tree deadline.</summary>
internal static class PluginTestWorkerSupervisor
{
    private static readonly TimeSpan DetectionDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttendedDeadline = TimeSpan.FromSeconds(90);
    private const int MaximumResponseBytes = 8 * 1024 * 1024;

    internal static Task<PluginTestReport> TestDetectionAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        CancellationToken cancellationToken) => TestDetectionAsync(
            packageDirectory,
            identity,
            DeviceLabExecutable.CurrentPath,
            cancellationToken);

    internal static Task<PluginTestReport> TestDetectionAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        string executablePath,
        CancellationToken cancellationToken) => TestDetectionAsync(
            packageDirectory,
            identity,
            executablePath,
            DetectionDeadline,
            cancellationToken);

    internal static Task<PluginTestReport> TestDetectionAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        string executablePath,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(identity);
        return RunAsync(
            new PluginTestWorkerRequest
            {
                SchemaVersion = 1,
                Mode = PluginTestMode.DetectionOnly,
                PackageDirectory = Path.GetFullPath(packageDirectory),
                Identity = identity,
                AuthorizationSha256 = string.Empty,
            },
            ownerReservation: null,
            executablePath,
            deadline,
            cancellationToken);
    }

    internal static async Task<PluginTestReport> RunAttendedAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        string stateDirectory,
        AttendedPluginActionRequest action,
        bool confirmed,
        DeviceLabPathBoundaries boundaries,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(boundaries);
        PluginPackageValidationReport validation = PluginPackageWorkflow.ValidateOffline(packageDirectory);
        if (!validation.Valid)
        {
            return Failed(
                PluginTestMode.AttendedHardware,
                validation.PackageId,
                action,
                "Offline package validation failed.");
        }

        DeviceLabOutputPathDecision output = DeviceLabOutputPathPolicy.Evaluate(
            stateDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!output.IsAllowed || output.FullPath is null
            || Directory.Exists(output.FullPath) || File.Exists(output.FullPath))
        {
            return Failed(
                PluginTestMode.AttendedHardware,
                validation.PackageId,
                action,
                output.Reason ?? "The plugin state directory must be new.");
        }

        var requirements = new DeviceLabOperationRequirements
        {
            OperationId = "plugin.attended-run",
            ResourceId = validation.PackageId!,
            Access = DeviceLabOperationAccess.AttendedPluginAction,
            ExactDeviceMatched = true,
            RequiresElevation = true,
        };
        DeviceLabSafetySnapshot staticSnapshot = new()
        {
            OwnerDiscovery = DeviceOwnerDiscoveryState.Absent,
            IsElevated = IsElevated(),
            IsUserInteractive = Environment.UserInteractive,
            IsContinuousIntegration = IsContinuousIntegration(),
            AttendedActionConfirmed = confirmed,
        };
        DeviceLabPreflightDecision staticPreflight = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            staticSnapshot);
        if (staticPreflight.Route is not DeviceLabAccessRoute.DirectAttended)
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = validation.PackageId,
                Preflight = staticPreflight,
                Action = action,
                Error = "The attended hardware action was blocked before plugin loading.",
            };
        }

        DeviceLabOwnerReservationResult owner = DeviceLabOwnerInspector.Reserve();
        using DeviceLabOwnerReservation? ownerReservation = owner.Reservation;
        DeviceOwnerDiscoveryState ownerState = owner.Inspection.State;
        if ((ownerState is DeviceOwnerDiscoveryState.Absent) != (ownerReservation is not null))
        {
            ownerState = DeviceOwnerDiscoveryState.Unknown;
        }

        DeviceLabPreflightDecision ownerPreflight = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            staticSnapshot with { OwnerDiscovery = ownerState });
        if (ownerPreflight.Route is not DeviceLabAccessRoute.DirectAttended)
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = validation.PackageId,
                Preflight = ownerPreflight,
                Action = action,
                Error = "The attended hardware action was blocked before plugin loading.",
            };
        }

        PluginTestReport report = await RunAsync(
            new PluginTestWorkerRequest
            {
                SchemaVersion = 1,
                Mode = PluginTestMode.AttendedHardware,
                PackageDirectory = Path.GetFullPath(packageDirectory),
                Identity = identity,
                StateDirectory = output.FullPath,
                Action = action,
                Confirmed = confirmed,
                ParentOwnerReserved = true,
                AuthorizationSha256 = string.Empty,
            },
            ownerReservation,
            DeviceLabExecutable.CurrentPath,
            AttendedDeadline,
            cancellationToken).ConfigureAwait(false);

        if (report.Started && !report.CleanedUp)
        {
            ownerReservation?.RetainForProcessLifetime();
        }

        return report;
    }

    private static async Task<PluginTestReport> RunAsync(
        PluginTestWorkerRequest request,
        DeviceLabOwnerReservation? ownerReservation,
        string executablePath,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        byte[] authorizationSecret = SelfWorkerAuthorization.CreateSecret();
        request = request with
        {
            AuthorizationSha256 = SelfWorkerAuthorization.Hash(authorizationSecret),
        };

        string workersRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSGM Device Lab",
            "Workers"));
        string sessionId = $"plugin-{Guid.NewGuid():N}";
        string sessionDirectory = Path.Combine(workersRoot, sessionId);
        string requestPath = Path.Combine(sessionDirectory, PluginTestWorker.RequestFileName);
        string resultPath = Path.Combine(sessionDirectory, PluginTestWorker.ResultFileName);
        string markerPath = Path.Combine(sessionDirectory, ".device-lab-worker-session");
        bool sessionCleanupAllowed = true;
        try
        {
            Directory.CreateDirectory(workersRoot);
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllTextAsync(markerPath, sessionId, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            await using (FileStream requestStream = new(
                requestPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    requestStream,
                    request,
                    PluginTestWorkerJson.Options,
                    cancellationToken).ConfigureAwait(false);
                await requestStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!SelfWorkerAuthorization.TryConstrainSessionFiles(
                    requestPath,
                    resultPath,
                    PluginTestWorker.RequestFileName,
                    PluginTestWorker.ResultFileName,
                    out _,
                    out _))
            {
                throw new IOException("The plugin worker session path failed its confinement check.");
            }

            string[] arguments =
            [
                PluginTestWorker.Mode,
                "--request", requestPath,
                "--result", resultPath,
            ];
            ReadProbeProcessOutcome process;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                sessionCleanupAllowed = false;
                process = await new SystemReadProbeProcessLauncher().RunAsync(
                    executablePath,
                    arguments,
                    deadline,
                    resultPath,
                    authorizationSecret,
                    cancellationToken).ConfigureAwait(false);
                sessionCleanupAllowed = process.ContainmentVerified;
            }
            catch (DisposableWorkerCanceledException exception)
            {
                sessionCleanupAllowed = exception.ContainmentVerified;
                ownerReservation?.RetainForProcessLifetime();
                throw;
            }
            catch (OperationCanceledException)
            {
                ownerReservation?.RetainForProcessLifetime();
                throw;
            }

            if (!process.ContainmentVerified)
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    process.Error ?? "The plugin worker's complete descendant teardown could not be verified.");
            }

            if (!process.Started)
            {
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    process.Error ?? "The plugin worker did not start.");
            }

            if (process.TimedOut || process.ExitCode != 0 || !process.ResultProduced)
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    process.Error ?? (process.TimedOut
                        ? "The plugin worker exceeded its hard deadline and was killed."
                        : "The plugin worker did not complete cleanly."));
            }

            PluginTestWorkerResponse? response;
            try
            {
                FileInfo result = new(resultPath);
                if (result.Length is <= 0 or > MaximumResponseBytes)
                {
                    throw new InvalidDataException("The plugin worker response exceeded its size limit.");
                }

                await using FileStream resultStream = new(
                    resultPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                response = await JsonSerializer.DeserializeAsync<PluginTestWorkerResponse>(
                    resultStream,
                    PluginTestWorkerJson.Options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(request.Mode, null, request.Action, exception.Message);
            }

            if (response is null
                || response.SchemaVersion != 1
                || !string.Equals(
                    response.AuthorizationSha256,
                    request.AuthorizationSha256,
                    StringComparison.Ordinal))
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    "The plugin worker response did not match its authorized request.");
            }

            if (response.Report is null || response.Error is not null)
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    response.Error ?? "The plugin worker returned no report.");
            }

            return response.Report;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorizationSecret);
            if (sessionCleanupAllowed)
            {
                TryDeleteOwnedSession(workersRoot, sessionDirectory, markerPath, sessionId);
            }
        }
    }

    private static PluginTestReport Failed(
        PluginTestMode mode,
        string? packageId,
        AttendedPluginActionRequest? action,
        string error) => new()
        {
            Mode = mode,
            Passed = false,
            PackageId = packageId,
            Action = action,
            Error = error[..Math.Min(error.Length, 16_384)],
        };

    private static void TryDeleteOwnedSession(
        string workersRoot,
        string sessionDirectory,
        string markerPath,
        string sessionId)
    {
        try
        {
            if (!string.Equals(Path.GetDirectoryName(sessionDirectory), workersRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(sessionDirectory)
                || (File.GetAttributes(sessionDirectory) & FileAttributes.ReparsePoint) != 0
                || !File.Exists(markerPath)
                || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0
                || !string.Equals(File.ReadAllText(markerPath, Encoding.UTF8), sessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(markerPath),
                Path.Combine(sessionDirectory, PluginTestWorker.RequestFileName),
                Path.Combine(sessionDirectory, PluginTestWorker.ResultFileName),
            };
            int observed = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(sessionDirectory))
            {
                if (++observed > expected.Count
                    || !expected.Contains(Path.GetFullPath(path)))
                {
                    return;
                }

                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return;
                }
            }

            foreach (string path in expected)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            Directory.Delete(sessionDirectory, recursive: false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            // A suspicious or locked session is left for inspection instead of widening deletion.
        }
    }

    private static bool IsElevated()
    {
        using System.Security.Principal.WindowsIdentity identity =
            System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool IsContinuousIntegration() =>
        IsTruthy(Environment.GetEnvironmentVariable("CI"))
        || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
