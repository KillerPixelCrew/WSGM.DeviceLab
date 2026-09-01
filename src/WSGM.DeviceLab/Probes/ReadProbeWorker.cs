using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Probes;

/// <summary>Entry point for Device Lab's disposable compatibility-probe self-worker.</summary>
/// <remarks>
/// The request can select only a profile compiled into this assembly. It carries no method, report
/// ID, address, native path, or arbitrary operation, so an imported file cannot turn the host into a
/// generic device-access broker.
/// </remarks>
internal static class ReadProbeWorker
{
    internal const string Mode = "__read-probe";

    private const int ExitSuccess = 0;
    private const int ExitInvalidArguments = 64;
    private const int ExitRejected = 65;
    private const int ExitFailure = 70;
    private const int MaximumRequestBytes = 262_144;
    private static readonly TimeSpan AuthorizationDeadline = TimeSpan.FromSeconds(5);

    internal static int Run(IReadOnlyList<string> args)
    {
        if (!TryParseArguments(args, out Arguments? parsed, out string? error))
        {
            Console.Error.WriteLine(error);
            return ExitInvalidArguments;
        }

        try
        {
            return RunAsync(parsed!, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Device Lab read-probe worker failed: {exception.Message}");
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
            Console.Error.WriteLine("The read-probe worker was not authorized by its supervisor.");
            return ExitRejected;
        }

        if (!SelfWorkerAuthorization.TryConstrainSessionFiles(
                arguments.RequestPath,
                arguments.ResultPath,
                "probe-request.json",
                "probe-result.json",
                out string? requestPath,
                out string? resultPath))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorizationSecret);
            Console.Error.WriteLine("The read-probe worker session paths were rejected.");
            return ExitRejected;
        }

        ReadProbeWorkerRequest? request = await ReadRequestAsync(requestPath!, cancellationToken)
            .ConfigureAwait(false);
        if (request is null
            || request.SchemaVersion != 1
            || !string.Equals(request.ProbeId, arguments.ProbeId, StringComparison.Ordinal))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorizationSecret);
            Console.Error.WriteLine("The read-probe request identity did not match its command envelope.");
            return ExitRejected;
        }

        bool authorized = SelfWorkerAuthorization.VerifySecret(
            authorizationSecret,
            request.AuthorizationSha256);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorizationSecret);
        if (!authorized)
        {
            Console.Error.WriteLine("The read-probe worker was not authorized by its supervisor.");
            return ExitRejected;
        }

        string mismatch = "The requested probe is not compiled into this Device Lab executable.";
        if (!BuiltInReadProbeRegistry.TryResolve(request.ProbeId, request.ProbeVersion, out IReadProbeProfile profile)
            || !profile.Descriptor.Matches(request, out mismatch))
        {
            Console.Error.WriteLine(mismatch);
            return ExitRejected;
        }

        ReadProbeWorkerResponse response = await ReadProbeExecutor.ExecuteAsync(
            profile,
            request,
            cancellationToken).ConfigureAwait(false);
        await WriteResultAsync(resultPath!, response, cancellationToken).ConfigureAwait(false);
        return ExitSuccess;
    }

    private static async Task<ReadProbeWorkerRequest?> ReadRequestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length > MaximumRequestBytes)
        {
            return null;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            DeviceLabJsonContext.Default.ReadProbeWorkerRequest,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResultAsync(
        string path,
        ReadProbeWorkerResponse response,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            response,
            DeviceLabJsonContext.Default.ReadProbeWorkerResponse,
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
                || args[index] is not ("--probe" or "--request" or "--result"
                    or "--authorization-handle")
                || !values.TryAdd(args[index], args[index + 1]))
            {
                parsed = null;
                error = "The read-probe worker requires exactly --probe, --request, --result, and --authorization-handle once each.";
                return false;
            }
        }

        if (values.Count != 4
            || !values.TryGetValue("--probe", out string? probeId)
            || !values.TryGetValue("--request", out string? requestPath)
            || !values.TryGetValue("--result", out string? resultPath)
            || !values.TryGetValue("--authorization-handle", out string? authorizationHandle)
            || string.IsNullOrWhiteSpace(probeId)
            || string.IsNullOrWhiteSpace(requestPath)
            || string.IsNullOrWhiteSpace(resultPath)
            || string.IsNullOrWhiteSpace(authorizationHandle))
        {
            parsed = null;
            error = "The read-probe worker arguments were incomplete or malformed.";
            return false;
        }

        parsed = new Arguments(probeId, requestPath, resultPath, authorizationHandle);
        error = null;
        return true;
    }

    private sealed record Arguments(
        string ProbeId,
        string RequestPath,
        string ResultPath,
        string AuthorizationHandle);
}
