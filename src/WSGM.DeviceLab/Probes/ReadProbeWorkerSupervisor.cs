using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Probes;

/// <summary>Starts exactly one disposable Device Lab self-worker under a hard deadline.</summary>
internal interface IReadProbeProcessLauncher
{
    /// <summary>Runs one worker and kills its full process tree if the deadline expires.</summary>
    /// <param name="executablePath">Current Device Lab executable.</param>
    /// <param name="arguments">Fixed hidden self-worker arguments.</param>
    /// <param name="timeout">Hard process deadline.</param>
    /// <param name="resultPath">Result file which must not exist before launch.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>Observed process lifecycle.</returns>
    Task<ReadProbeProcessOutcome> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string resultPath,
        CancellationToken cancellationToken);
}

/// <summary>Production process launcher for the disposable Device Lab self-worker.</summary>
internal sealed class SystemReadProbeProcessLauncher : IReadProbeProcessLauncher
{
    private const int MaximumErrorLength = 16_384;

    /// <inheritdoc/>
    public async Task<ReadProbeProcessOutcome> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string resultPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failed("Device Lab's read-probe worker did not start.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Failed(exception.Message);
        }

        Task<string> errorRead = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ReadProbeProcessOutcome
            {
                Started = true,
                TimedOut = true,
                ResultProduced = File.Exists(resultPath),
                Error = "Device Lab's read-probe worker exceeded its deadline and was killed.",
            };
        }
        catch (OperationCanceledException)
        {
            // Closing the GUI or pressing Ctrl+C must not leave the disposable worker holding its
            // endpoint. Kill the complete tree before the caller observes cancellation.
            TryKill(process);
            using CancellationTokenSource exitDeadline = new(TimeSpan.FromSeconds(2));
            try
            {
                await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Kill already made the strongest available teardown request. Preserve the
                // caller's cancellation rather than replacing it with a cleanup timeout.
            }
            throw;
        }

        string error = await errorRead.ConfigureAwait(false);
        return new ReadProbeProcessOutcome
        {
            Started = true,
            TimedOut = false,
            ExitCode = process.ExitCode,
            ResultProduced = File.Exists(resultPath),
            Error = string.IsNullOrWhiteSpace(error)
                ? null
                : error[..Math.Min(error.Length, MaximumErrorLength)],
        };
    }

    private static ReadProbeProcessOutcome Failed(string error) => new()
    {
        Started = false,
        TimedOut = false,
        ResultProduced = false,
        Error = error,
    };

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the deadline firing and the kill. There is no durable host state.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The supervisor still reports a deadline failure. The OS owns final process teardown.
        }
    }
}

/// <summary>Runs the preflight, disposable self-worker, and response-validation sequence.</summary>
internal static class ReadProbeWorkerSupervisor
{
    private const int MaximumResponseBytes = 1_048_576;

    /// <summary>Executes one compiled read probe in a fresh worker process.</summary>
    /// <param name="metadata">Compiled probe contract.</param>
    /// <param name="preflight">Already evaluated ownership and safety decision.</param>
    /// <param name="executablePath">Current Device Lab executable.</param>
    /// <param name="sessionDirectory">New, explicit output directory for request and result files.</param>
    /// <param name="launcher">Disposable process launcher.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>Classified run result.</returns>
    public static async Task<ReadProbeRunResult> RunAsync(
        ReadProbeMetadata metadata,
        DeviceLabPreflightDecision preflight,
        string executablePath,
        string sessionDirectory,
        IReadProbeProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(launcher);

        IReadOnlyList<string> metadataErrors = ReadProbeMetadataPolicy.Validate(metadata);
        if (metadataErrors.Count != 0)
        {
            return Result(ReadProbeRunStatus.Rejected, string.Join(" ", metadataErrors));
        }

        if (preflight.Status is DeviceLabDoctorStatus.Blocked
            || preflight.Route is not DeviceLabAccessRoute.DirectReadOnly
            || !string.Equals(preflight.ResourceId, metadata.ResourceId, StringComparison.Ordinal))
        {
            return Result(
                ReadProbeRunStatus.Rejected,
                "Safety preflight did not authorize direct read-only access to the exact resource.");
        }

        if (!File.Exists(executablePath))
        {
            return Result(ReadProbeRunStatus.LaunchFailed, "The current Device Lab executable is unavailable.");
        }

        if (Directory.Exists(sessionDirectory) || File.Exists(sessionDirectory))
        {
            return Result(ReadProbeRunStatus.Rejected, "Probe session output must be a new directory.");
        }

        DeviceLabOutputPathDecision output = DeviceLabOutputPathPolicy.Evaluate(
            sessionDirectory,
            DeviceLabOutputTargetKind.Directory,
            DeviceLabPathBoundaries.ForCurrentUser(
                DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory)));
        if (!output.IsAllowed || output.FullPath is null)
        {
            return Result(ReadProbeRunStatus.Rejected, output.Reason ?? "Probe session output was rejected.");
        }

        Directory.CreateDirectory(output.FullPath);
        string requestPath = Path.Combine(output.FullPath, "probe-request.json");
        string resultPath = Path.Combine(output.FullPath, "probe-result.json");
        if (File.Exists(requestPath) || File.Exists(resultPath))
        {
            return Result(ReadProbeRunStatus.Rejected, "Probe session files already exist; overwrite is forbidden.");
        }

        ReadProbeWorkerRequest request = new()
        {
            SchemaVersion = 1,
            ProbeId = metadata.Id,
            ProbeVersion = metadata.Version,
            FamilyId = metadata.FamilyId,
            EndpointId = metadata.EndpointId,
            Family = metadata.Family,
            MaximumReadsPerSecond = metadata.MaximumReadsPerSecond,
            TimeoutMilliseconds = metadata.TimeoutMilliseconds,
            Repetitions = metadata.Repetitions,
        };
        await File.WriteAllTextAsync(
            requestPath,
            DeviceLabJson.Serialize(request),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        string[] arguments =
        [
            ReadProbeWorker.Mode,
            "--probe", metadata.Id,
            "--request", requestPath,
            "--result", resultPath,
        ];
        ReadProbeProcessOutcome process = await launcher.RunAsync(
            executablePath,
            arguments,
            // The worker owns the semantic deadline and needs a short interval after cancelling to
            // serialize its DeadlineExceeded response. The supervisor is only the final process
            // containment boundary; giving both the same deadline made graceful timeout reports
            // unreachable and misclassified every slow WMI call as a killed worker.
            ProcessDeadline(metadata),
            resultPath,
            cancellationToken).ConfigureAwait(false);

        ReadProbeRunResult? processFailure = ReadProbeOutcomeClassifier.ClassifyProcess(process);
        if (processFailure is not null)
        {
            return processFailure;
        }

        ReadProbeWorkerResponse? response;
        try
        {
            FileInfo resultInfo = new(resultPath);
            if (resultInfo.Length > MaximumResponseBytes)
            {
                return Result(ReadProbeRunStatus.MalformedResponse, "Read-probe worker response exceeded the size limit.");
            }

            await using FileStream stream = new(
                resultPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            response = await JsonSerializer.DeserializeAsync(
                stream,
                DeviceLabJsonContext.Default.ReadProbeWorkerResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result(ReadProbeRunStatus.MalformedResponse, exception.Message);
        }

        if (response is null)
        {
            return Result(ReadProbeRunStatus.MalformedResponse, "Read-probe worker response was empty.");
        }

        return ReadProbeOutcomeClassifier.ClassifyResponse(metadata, response);
    }

    /// <summary>Whole-process deadline including time for the worker to publish its own timeout.</summary>
    /// <param name="metadata">Compiled semantic worker deadline.</param>
    /// <returns>The outer containment deadline.</returns>
    internal static TimeSpan ProcessDeadline(ReadProbeMetadata metadata) =>
        TimeSpan.FromMilliseconds(metadata.TimeoutMilliseconds + 2_000);

    private static ReadProbeRunResult Result(ReadProbeRunStatus status, string message) => new()
    {
        Status = status,
        Message = message,
    };
}

/// <summary>Maps process and typed-response failure modes to stable Device Lab results.</summary>
internal static class ReadProbeOutcomeClassifier
{
    /// <summary>Classifies launch, crash, hang, and missing-result states.</summary>
    /// <param name="process">Observed disposable-process lifecycle.</param>
    /// <returns>A terminal failure, or null when the response document should be read.</returns>
    public static ReadProbeRunResult? ClassifyProcess(ReadProbeProcessOutcome process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.Started)
        {
            return Result(ReadProbeRunStatus.LaunchFailed, process.Error ?? "Read-probe worker did not start.");
        }

        if (process.TimedOut)
        {
            return Result(ReadProbeRunStatus.WorkerHung, process.Error ?? "Read-probe worker exceeded its deadline.");
        }

        if (process.ExitCode != 0)
        {
            return Result(ReadProbeRunStatus.WorkerCrashed, process.Error ?? $"Read-probe worker exited with code {process.ExitCode}.");
        }

        return process.ResultProduced
            ? null
            : Result(ReadProbeRunStatus.MalformedResponse, "Read-probe worker exited without a result document.");
    }

    /// <summary>Classifies typed endpoint failures and validates completed responses.</summary>
    /// <param name="metadata">Compiled response contract.</param>
    /// <param name="response">Parsed worker response.</param>
    /// <returns>Stable run result.</returns>
    public static ReadProbeRunResult ClassifyResponse(
        ReadProbeMetadata metadata,
        ReadProbeWorkerResponse response)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(response);
        if (response.Status is ReadProbeWorkerStatus.AccessDenied)
        {
            return WithResponse(ReadProbeRunStatus.AccessDenied, response.Error ?? "Read-probe worker was denied access.", response);
        }

        if (response.Status is ReadProbeWorkerStatus.Disconnected)
        {
            return WithResponse(ReadProbeRunStatus.Disconnected, response.Error ?? "The exact endpoint disconnected.", response);
        }

        ReadProbeValidationResult validation = ReadProbeResponseValidator.Validate(metadata, response);
        return WithResponse(
            validation.Accepted ? ReadProbeRunStatus.Accepted : ReadProbeRunStatus.MalformedResponse,
            validation.Message,
            response);
    }

    private static ReadProbeRunResult Result(ReadProbeRunStatus status, string message) => new()
    {
        Status = status,
        Message = message,
    };

    private static ReadProbeRunResult WithResponse(
        ReadProbeRunStatus status,
        string message,
        ReadProbeWorkerResponse response) => new()
        {
            Status = status,
            Message = message,
            Response = response,
        };
}
