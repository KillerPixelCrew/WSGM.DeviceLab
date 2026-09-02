using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Probes;
using WSGM.DeviceLab.Testing;

namespace WSGM.DeviceLab.Cli;

/// <summary>Thin command surface over the shared Device Lab application workflows.</summary>
internal static class DeviceLabCli
{
    private const int Success = 0;
    private const int Usage = 64;
    private const int Failed = 70;

    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            WriteUsage(Console.Out);
            return args.Length == 0 ? Usage : Success;
        }

        if (ValidateArguments(args) is { } argumentError)
        {
            return UsageError(argumentError);
        }

        try
        {
            return args[0] switch
            {
                "doctor" => RunDoctor(args.AsSpan(1)),
                "inventory" => RunInventory(args.AsSpan(1)),
                "candidates" => RunCandidates(args.AsSpan(1)),
                "probe-read" => await RunProbeReadAsync(args[1..]).ConfigureAwait(false),
                "capture" => await RunCaptureAsync(args.AsMemory(1)).ConfigureAwait(false),
                "inspect" => RunInspect(args.AsSpan(1)),
                "compare" => RunCompare(args.AsSpan(1)),
                "correlate" => RunCorrelate(args.AsSpan(1)),
                "fixture" => RunFixture(args.AsSpan(1)),
                "scaffold" => RunScaffold(args.AsSpan(1)),
                "glyph" => RunGlyph(args.AsSpan(1)),
                "validate" => RunValidate(args.AsSpan(1)),
                "test" => await RunTestAsync(args[1..]).ConfigureAwait(false),
                "pack" => RunPack(args.AsSpan(1)),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception exception)
        {
            // Community plugin code is intentionally outside WSGM's exception vocabulary. The CLI
            // boundary must still return a deterministic failure instead of losing the report and
            // process status to an arbitrary plugin exception.
            Console.Error.WriteLine($"Command failed: {exception.Message}");
            return Failed;
        }
    }

    private static int RunDoctor(ReadOnlySpan<string> args)
    {
        if (args.Length != 2 || args[0] is not ("--out-dir" or "-o"))
        {
            return UsageError("doctor requires exactly --out-dir <directory>.");
        }

        DeviceLabDoctorReport report = Application().Doctor(args[1], DateTimeOffset.UtcNow);
        Console.Out.WriteLine(DeviceLabJson.Serialize(report));
        return report.Status is DeviceLabDoctorStatus.Blocked ? Failed : Success;
    }

    private static int RunInventory(ReadOnlySpan<string> args)
    {
        string? output = Option(args, "--out-dir", "-o");
        if (output is null)
        {
            return UsageError("inventory requires --out-dir <directory> and accepts --shareable.");
        }

        DeviceLabInventoryResult result = Application().Inventory(
            output,
            Flag(args, "--shareable"),
            DateTimeOffset.UtcNow);
        if (result.Status is not DeviceLabInventoryStatus.Success || result.Json is null)
        {
            Console.Error.WriteLine($"Inventory failed ({result.Status}): {result.Error}");
            return Failed;
        }

        Console.Out.WriteLine(result.Json);
        Console.Error.WriteLine($"Inventory written to {result.OutputPath}");
        return Success;
    }

    private static int RunCandidates(ReadOnlySpan<string> args)
    {
        string? input = Option(args, "--from", "-f");
        if (input is null)
        {
            return UsageError("candidates requires --from <inventory.json>.");
        }

        WriteJson(Application().Candidates(input, Option(args, "--device-id")));
        return Success;
    }

    private static async Task<int> RunProbeReadAsync(string[] args)
    {
        ReadOnlySpan<string> options = args;
        string? input = Option(options, "--from", "-f");
        if (input is null)
        {
            return UsageError("probe-read requires --from <inventory.json> and optionally --run <probe-id> --out-dir <directory>.");
        }

        string executable = DeviceLabExecutable.CurrentPath;
        DeviceLabApplication application = new(RepositoryRoot(), executable);
        string? runId = Option(options, "--run");
        if (runId is not null)
        {
            string? output = Option(options, "--out-dir", "-o");
            if (output is null)
            {
                return UsageError("probe-read --run requires --out-dir <directory>.");
            }

            DeviceLabReadProbeExecutionResult execution = await application.RunReadProbeAsync(
                input,
                runId,
                output,
                CancellationToken.None).ConfigureAwait(false);
            WriteJson(execution);
            return execution.Run?.Status is ReadProbeRunStatus.Accepted ? Success : Failed;
        }

        DeviceLabCandidateResult result = application.Candidates(input);
        WriteJson(new
        {
            probes = result.ReadOnlyProbes,
            workerExecutable = executable,
            mode = "compiled-read-only",
        });
        return Success;
    }

    private static async Task<int> RunCaptureAsync(ReadOnlyMemory<string> arguments)
    {
        ReadOnlySpan<string> args = arguments.Span;
        if (args.Length == 0 || args[0] is not "run")
        {
            return UsageError("capture requires 'run --recipe <recipe.json> --out-dir <directory>'.");
        }

        ReadOnlySpan<string> options = args[1..];
        string? recipe = Option(options, "--recipe");
        string? output = Option(options, "--out-dir", "-o");
        if (recipe is null || output is null)
        {
            return UsageError("capture run requires --recipe <recipe.json> --out-dir <directory>.");
        }

        bool interactive = Environment.UserInteractive
            && !Console.IsInputRedirected
            && !Console.IsOutputRedirected
            && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        if (!interactive)
        {
            Console.Error.WriteLine("capture run refused: a local interactive terminal is mandatory.");
            return Failed;
        }

        ObserveOnlyRecipeReview review = Application().ReviewCaptureRecipe(recipe);
        Console.Error.WriteLine("Observe-only capture scope: read-only inventory and locally compiled passive observers only.");
        Console.Error.WriteLine("Unknown observers remain unavailable; imported recipe data cannot open a device or authorize mutation.");
        Console.Error.WriteLine(JsonSerializer.Serialize(review, OutputJson));
        Console.Error.Write("Type OBSERVE to prepare the private session: ");
        if (!string.Equals(Console.ReadLine(), "OBSERVE", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Capture cancelled before observation.");
            return Failed;
        }

        using CancellationTokenSource cancellation = new();
        void handler(object? _, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
        Console.CancelKeyPress += handler;
        ObserveOnlyCaptureResult prepared;
        try
        {
            prepared = await Application().PrepareCaptureAsync(
                new ObserveOnlyCaptureRequest
                {
                    RecipePath = recipe,
                    OutputDirectory = output,
                    ReviewedRecipeSha256 = review.RecipeSha256,
                    IsLocalInteractive = true,
                    ObservationScopeConfirmed = true,
                },
                DateTimeOffset.UtcNow,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Capture cancelled. No shareable bundle was written.");
            return Failed;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }

        if (prepared.Status is not ObserveOnlyCaptureStatus.ReadyForExport || prepared.ExportPlan is null)
        {
            Console.Error.WriteLine($"Capture failed ({prepared.Status}): {prepared.Error}");
            return Failed;
        }

        CaptureExportPlan plan = prepared.ExportPlan;
        Console.Error.WriteLine($"Private session: {plan.PrivateWorkingDirectory}");
        Console.Error.WriteLine("Sanitized shareable-content preview:");
        Console.Error.WriteLine(JsonSerializer.Serialize(
            CapturePrivacyPreview.Create(plan.Bundle),
            OutputJson));
        Console.Error.Write("Type EXPORT to write the sanitized .wsgmcap, or press Enter to keep it private: ");
        bool exportConfirmed = string.Equals(Console.ReadLine(), "EXPORT", StringComparison.Ordinal);
        CaptureExportResult exported = Application().ExportCapture(plan, exportConfirmed);
        WriteJson(new
        {
            prepared.Status,
            plan.PrivateWorkingDirectory,
            shareableOutputPath = exported.OutputPath,
            exported.Exported,
            exported.Error,
        });
        return exported.Exported ? Success : Failed;
    }

    private static int RunInspect(ReadOnlySpan<string> args)
    {
        if (args.Length != 1)
        {
            return UsageError("inspect requires one .wsgmcap path.");
        }

        WriteJson(Application().Inspect(args[0]));
        return Success;
    }

    private static int RunCompare(ReadOnlySpan<string> args)
    {
        if (args.Length != 2)
        {
            return UsageError("compare requires two .wsgmcap paths.");
        }

        WriteJson(new
        {
            differences = Application().Diff(args[0], args[1]),
        });
        return Success;
    }

    private static int RunCorrelate(ReadOnlySpan<string> args)
    {
        if (args.Length == 0)
        {
            return UsageError("correlate requires <capture> --action <id> --sources <id,id>.");
        }

        string? action = Option(args[1..], "--action");
        string? sources = Option(args[1..], "--sources");
        if (action is null || sources is null)
        {
            return UsageError("correlate requires <capture> --action <id> --sources <id,id>.");
        }

        WriteJson(Application().Correlate(
            args[0],
            action,
            sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal)));
        return Success;
    }

    private static int RunFixture(ReadOnlySpan<string> args)
    {
        if (args.Length == 0 || args[0] is not "extract")
        {
            return UsageError("fixture extract requires --from, --id, and --out-dir.");
        }

        ReadOnlySpan<string> options = args[1..];
        string? from = Option(options, "--from", "-f");
        string? id = Option(options, "--id");
        string? output = Option(options, "--out-dir", "-o");
        if (from is null || id is null || output is null)
        {
            return UsageError("fixture extract requires --from, --id, and --out-dir.");
        }

        WriteJson(Application().ExtractFixture(from, id, output));
        return Success;
    }

    private static int RunScaffold(ReadOnlySpan<string> args)
    {
        string? from = Option(args, "--from", "-f");
        string? output = Option(args, "--out-dir", "-o");
        string? usbInstance = Option(args, "--usb-instance");
        if (from is null || output is null)
        {
            return UsageError("scaffold requires --from <capture> --out-dir <new-directory>; use --usb-instance when the capture has multiple exact USB endpoints.");
        }

        WriteJson(Application().Scaffold(
            from,
            output,
            usbInstanceId: usbInstance));
        return Success;
    }

    private static int RunValidate(ReadOnlySpan<string> args)
    {
        if (args.Length != 1)
        {
            return UsageError("validate requires one package directory.");
        }

        PluginPackageValidationReport report = Application().ValidateOffline(args[0]);
        WriteJson(report);
        return report.Valid ? Success : Failed;
    }

    private static async Task<int> RunTestAsync(string[] args)
    {
        if (args.Length == 1 && args[0] is "sample")
        {
            var report = await Application().TestSyntheticPluginAsync(CancellationToken.None).ConfigureAwait(false);
            WriteJson(report);
            return report.Passed ? Success : Failed;
        }

        if (args.Length < 2 || args[0] is not ("plugin" or "hardware"))
        {
            return UsageError(
                "test requires 'sample', 'plugin <dir> --from <inventory>', or 'hardware <dir> --from <inventory> --state-dir <new-directory> --action capability|haptic|controller'.");
        }

        if (args[0] is "plugin")
        {
            string package = args[1];
            ReadOnlySpan<string> options = args.AsSpan(2);
            string? inventory = Option(options, "--from", "-f");
            if (inventory is null)
            {
                return UsageError("test plugin requires --from <inventory.json>.");
            }

            var report = await Application().TestPluginAsync(
                package,
                inventory,
                CancellationToken.None).ConfigureAwait(false);
            WriteJson(report);
            return report.Passed ? Success : Failed;
        }

        if (!HardwareTestCliArguments.TryParse(args.AsSpan(1), out HardwareTestCliArguments? parsed,
            out string parseError))
        {
            return UsageError(parseError);
        }

        HardwareTestCliArguments hardwareArguments = parsed!;
        AttendedPluginActionRequest action = hardwareArguments.Action;

        if (Console.IsInputRedirected || Console.IsOutputRedirected || !Environment.UserInteractive
            || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("test hardware refused: a local interactive terminal is mandatory.");
            return Failed;
        }

        Console.Error.WriteLine($"Selected action: {DescribeHardwareAction(action)}.");
        Console.Error.WriteLine("This loads the selected local plugin and may access or change matched hardware.");
        Console.Error.WriteLine("WSGM Device Integration must be stopped. Cleanup runs immediately after activation.");
        Console.Error.Write("Type RUN HARDWARE to continue: ");
        bool confirmed = string.Equals(Console.ReadLine(), "RUN HARDWARE", StringComparison.Ordinal);
        if (!confirmed)
        {
            Console.Error.WriteLine("Hardware action cancelled before plugin activation.");
            return Failed;
        }

        PluginTestReport hardware;
        try
        {
            hardware = await RunWithConsoleCancellationAsync(token =>
                Application().RunAttendedPluginAsync(
                    hardwareArguments.PackageDirectory,
                    hardwareArguments.InventoryPath,
                    hardwareArguments.StateDirectory,
                    action,
                    confirmed: true,
                    token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Hardware action cancelled after plugin cleanup completed.");
            return Failed;
        }
        WriteJson(hardware);
        return hardware.Passed ? Success : Failed;
    }

    private static async Task<T> RunWithConsoleCancellationAsync<T>(
        Func<CancellationToken, Task<T>> operation)
    {
        using CancellationTokenSource cancellation = new();
        void handler(object? _, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }

        Console.CancelKeyPress += handler;
        try
        {
            return await operation(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int RunGlyph(ReadOnlySpan<string> args)
    {
        if (args.Length != 2 || args[0] is not "import")
        {
            return UsageError("glyph import requires one package directory.");
        }

        GlyphPackageImportReport report = Application().ImportGlyphs(args[1]);
        WriteJson(report);
        return report.Valid ? Success : Failed;
    }

    private static int RunPack(ReadOnlySpan<string> args)
    {
        if (args.Length < 3 || Option(args[1..], "--out", "-o") is not { } output)
        {
            return UsageError("pack requires <package-directory> --out <new-package.wsgmpkg>.");
        }

        PluginPackageValidationReport report = Application().Pack(args[0], output);
        WriteJson(new
        {
            validation = report,
            output = report.Valid ? Path.GetFullPath(output) : null,
        });
        return report.Valid ? Success : Failed;
    }

    private static DeviceLabApplication Application() => new(RepositoryRoot(), DeviceLabExecutable.CurrentPath);

    private static string? RepositoryRoot() => DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory)
        ?? DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory);

    /// <summary>Rejects misspelled options before any workflow can observe their absence.</summary>
    /// <param name="args">Complete CLI arguments, including the command.</param>
    /// <returns>A usage error for the first unknown token, or null when the command owns every token.</returns>
    internal static string? ValidateArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        ReadOnlySpan<string> tail = args.AsSpan(1);
        return args[0] switch
        {
            "inventory" => UnknownToken(tail, 0, ["--shareable"], ["--out-dir", "-o"]),
            "candidates" => UnknownToken(tail, 0, [], ["--from", "-f", "--device-id"]),
            "probe-read" => UnknownToken(tail, 0, [], ["--from", "-f", "--run", "--out-dir", "-o"]),
            "capture" => UnknownToken(tail, 1, [], ["--recipe", "--out-dir", "-o"]),
            "correlate" => UnknownToken(tail, 1, [], ["--action", "--sources"]),
            "fixture" => UnknownToken(tail, 1, [], ["--from", "-f", "--id", "--out-dir", "-o"]),
            "scaffold" => UnknownToken(tail, 0, [], ["--from", "-f", "--out-dir", "-o", "--usb-instance"]),
            "pack" => UnknownToken(tail, 1, [], ["--out", "-o"]),
            "test" when tail.Length > 0 && tail[0] is "hardware" => null,
            "test" when tail.Length > 0 && tail[0] is "plugin" =>
                UnknownToken(tail, 2, [], ["--from", "-f"]),
            _ => null,
        };
    }

    private static string? UnknownToken(
        ReadOnlySpan<string> args,
        int positionalCount,
        string[] flags,
        string[] valuedOptions)
    {
        int index = Math.Min(positionalCount, args.Length);
        while (index < args.Length)
        {
            string token = args[index];
            if (flags.Contains(token, StringComparer.Ordinal))
            {
                index++;
                continue;
            }

            if (valuedOptions.Contains(token, StringComparer.Ordinal))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    return $"Option '{token}' requires a value.";
                }

                index += 2;
                continue;
            }

            return $"Unknown option or argument '{token}'.";
        }

        return null;
    }

    private static bool Flag(ReadOnlySpan<string> args, string name)
    {
        foreach (string value in args)
        {
            if (value == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string? Option(ReadOnlySpan<string> args, params string[] names)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (names.Contains(args[index], StringComparer.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, OutputJson));

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        return Usage;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        WriteUsage(Console.Error);
        return Usage;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("wsgm-device doctor|inventory|candidates|probe-read|capture|inspect|compare|correlate|fixture|scaffold|glyph|validate|test|pack");
        writer.WriteLine("test: sample | plugin <dir> --from <inventory>");
        writer.WriteLine("scaffold --from <capture> --out-dir <new-dir> [--usb-instance <exact-id>]");
        writer.WriteLine("test hardware <dir> --from <inventory> --state-dir <new-dir> --action capability --capability <id> [--instance <id>] --value <value>");
        writer.WriteLine("test hardware <dir> --from <inventory> --state-dir <new-dir> --action haptic|haptic-sweep|controller [--instance <id>]");
        writer.WriteLine("Only 'test hardware' may access or change hardware, and it requires immediate local confirmation.");
    }

    private static string DescribeHardwareAction(AttendedPluginActionRequest action) => action.Kind switch
    {
        AttendedPluginActionKind.CapabilityValue => action.InstanceId is null
            ? $"capability value {action.CapabilityId}={action.ValueText}"
            : $"capability value {action.CapabilityId}/{action.InstanceId}={action.ValueText}",
        AttendedPluginActionKind.HapticPulse => action.InstanceId is null
            ? "one fixed 250 ms haptic pulse with zero-output cleanup"
            : $"one fixed 250 ms haptic pulse on {action.InstanceId} with zero-output cleanup",
        AttendedPluginActionKind.ControllerManagement =>
            action.InstanceId is null
                ? "one controller-management acquisition with verified topology release"
                : $"one controller-management acquisition for {action.InstanceId} with verified topology release",
        AttendedPluginActionKind.HapticSweep => action.InstanceId is null
            ? "the interactive A/B-stepped haptic calibration sweep with zero-output cleanup"
            : $"the interactive A/B-stepped haptic calibration sweep on {action.InstanceId} with zero-output cleanup",
        _ => action.Kind.ToString(),
    };
}
