using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        if (File.Exists(arguments.ResultPath))
        {
            Console.Error.WriteLine("The read-probe worker refuses to overwrite an existing result file.");
            return ExitRejected;
        }

        ReadProbeWorkerRequest? request = await ReadRequestAsync(arguments.RequestPath, cancellationToken)
            .ConfigureAwait(false);
        if (request is null
            || request.SchemaVersion != 1
            || !string.Equals(request.ProbeId, arguments.ProbeId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The read-probe request identity did not match its command envelope.");
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
        await WriteResultAsync(arguments.ResultPath, response, cancellationToken).ConfigureAwait(false);
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
                || args[index] is not ("--probe" or "--request" or "--result")
                || !values.TryAdd(args[index], args[index + 1]))
            {
                parsed = null;
                error = "The read-probe worker requires exactly --probe, --request, and --result once each.";
                return false;
            }
        }

        if (values.Count != 3
            || !values.TryGetValue("--probe", out string? probeId)
            || !values.TryGetValue("--request", out string? requestPath)
            || !values.TryGetValue("--result", out string? resultPath)
            || string.IsNullOrWhiteSpace(probeId)
            || !File.Exists(requestPath)
            || string.IsNullOrWhiteSpace(resultPath))
        {
            parsed = null;
            error = "The read-probe worker arguments were incomplete or malformed.";
            return false;
        }

        parsed = new Arguments(probeId, requestPath, resultPath);
        error = null;
        return true;
    }

    private sealed record Arguments(
        string ProbeId,
        string RequestPath,
        string ResultPath);
}
