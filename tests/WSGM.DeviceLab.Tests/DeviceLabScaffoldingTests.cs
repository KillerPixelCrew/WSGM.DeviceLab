using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using WSGM.Device.Sdk;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Scaffolding;

namespace WSGM.Device.Tests;

public sealed class DeviceLabScaffoldingTests
{
    [Fact]
    public void SdkReference_OutsideCheckout_UsesTheExactShippedAssemblyWithoutAnUndefinedProperty()
    {
        DeviceLabPathBoundaries boundaries = new()
        {
            LiveDataDirectory = Path.Combine(Path.GetTempPath(), "never-live-wsgm"),
            BroadHomeDirectories = [],
        };

        string reference = ScaffoldFromCaptureWorkflow.SdkReferenceXml(boundaries);
        XElement element = XElement.Parse(reference);

        Assert.Equal("Reference", element.Name.LocalName);
        Assert.Equal("WSGM.Device.Sdk", (string?)element.Attribute("Include"));
        Assert.Equal("false", (string?)element.Element("Private"));
        string hintPath = Assert.IsType<string>((string?)element.Element("HintPath"));
        Assert.Equal(Path.GetFullPath(typeof(DeviceApi).Assembly.Location), hintPath);
        Assert.True(File.Exists(hintPath));
        Assert.DoesNotContain("$(WsgmRepositoryRoot)", reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_OutsideCheckout_BuildsAgainstTheExactShippedSdkAssembly()
    {
        using TemporaryDirectory temporary = new();
        string capturePath = temporary.GetPath("source.wsgmcap");
        using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, Capture());
        }
        DeviceLabPathBoundaries boundaries = new()
        {
            LiveDataDirectory = temporary.GetPath("never-live-wsgm"),
            BroadHomeDirectories = [],
        };

        PluginScaffoldResult result = ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            temporary.GetPath("scaffold"),
            boundaries);

        string projectPath = Directory.EnumerateFiles(result.OutputDirectory, "*.csproj").Single();
        XDocument project = XDocument.Load(projectPath);
        XElement reference = Assert.Single(project.Descendants("Reference"));
        string hintPath = Assert.IsType<string>((string?)reference.Element("HintPath"));
        Assert.Equal(Path.GetFullPath(typeof(DeviceApi).Assembly.Location), hintPath);
        Assert.True(File.Exists(hintPath));
        Assert.Equal("x64", Assert.Single(project.Descendants("PlatformTarget")).Value);
        Assert.DoesNotContain("$(WsgmRepositoryRoot)", File.ReadAllText(projectPath), StringComparison.Ordinal);
        Assert.Contains(
            project.Descendants("None"),
            item => string.Equals((string?)item.Attribute("Update"), "LICENSE.txt", StringComparison.Ordinal)
                && string.Equals((string?)item.Attribute("CopyToOutputDirectory"), "PreserveNewest", StringComparison.Ordinal)
                && string.Equals((string?)item.Attribute("CopyToPublishDirectory"), "PreserveNewest", StringComparison.Ordinal));
        Assert.Contains("LICENSE.txt", result.Files);
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "LICENSE.txt")));
        // A scaffolded plugin links the MIT SDK, never WSGM, so its author picks its licence. The
        // starter ships MIT with a placeholder rather than stamping the plugin with WSGM's GPL-3
        // and this project's copyright holder, which claimed something untrue about their work.
        string scaffoldedLicense =
            File.ReadAllText(Path.Combine(result.OutputDirectory, "LICENSE.txt")).TrimStart();
        Assert.StartsWith("MIT License", scaffoldedLicense, StringComparison.Ordinal);
        Assert.Contains("<your name here>", scaffoldedLicense, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GNU GENERAL PUBLIC LICENSE", scaffoldedLicense, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SPDX-License-Identifier",
            File.ReadAllText(Path.Combine(result.OutputDirectory, "DevicePlugin.cs")),
            StringComparison.Ordinal);

        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = result.OutputDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_CLI_HOME"] = temporary.GetPath("dotnet-home");
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        // Without this the SDK's first run in a fresh CLI home appends
        // "<home>\.dotnet\tools" to the USER's persisted PATH — not just this child process's.
        // DOTNET_SKIP_FIRST_TIME_EXPERIENCE stopped suppressing that in .NET 6, so every run of
        // this test left one more dead temp path behind: 55 of them had accumulated on the
        // development machine, taking PATH past 6.8 KB and breaking VsDevCmd.bat, which is what
        // both build.ps1 and eng\verify.ps1 use to export-check the Steam Input gate.
        startInfo.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        startInfo.Environment["NUGET_PACKAGES"] = temporary.GetPath("nuget-packages");
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add("win-x64");
        startInfo.ArgumentList.Add("--no-self-contained");
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("--property:RestoreIgnoreFailedSources=true");
        startInfo.ArgumentList.Add("--property:NuGetAudit=false");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The .NET SDK process did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw new TimeoutException("The generated plugin project did not build within one minute.");
        }

        string diagnostic = (await output) + Environment.NewLine + (await error);
        if (process.ExitCode != 0)
        {
            Assert.Fail(diagnostic);
        }
        string buildOutput = Path.Combine(
            result.OutputDirectory,
            "bin",
            "Release",
            "net10.0-windows",
            "win-x64");
        Assert.True(File.Exists(Path.Combine(buildOutput, $"{result.RootNamespace}.dll")), diagnostic);
        Assert.True(File.Exists(Path.Combine(buildOutput, "LICENSE.txt")), diagnostic);
        Assert.False(File.Exists(Path.Combine(buildOutput, "WSGM.Device.Sdk.dll")));
        PluginPackageValidationReport validation = PluginPackageWorkflow.ValidateOffline(buildOutput);
        Assert.True(
            validation.Valid,
            string.Join("; ", validation.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
    }

    [Fact]
    public void Scaffold_PreCancelledRequestPublishesNoPartialDirectory()
    {
        using TemporaryDirectory temporary = new();
        string capturePath = temporary.GetPath("source.wsgmcap");
        using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, Capture());
        }
        string output = temporary.GetPath("cancelled-scaffold");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = Assert.Throws<OperationCanceledException>(() => ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            output,
            new DeviceLabPathBoundaries
            {
                LiveDataDirectory = temporary.GetPath("never-live-wsgm"),
                BroadHomeDirectories = [],
            },
            cancellation.Token));

        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Root, ".cancelled-scaffold.*.tmp"));
    }

    [Fact]
    public void Scaffold_MultipleExactUsbEndpointsRequireAnExplicitInstance()
    {
        using TemporaryDirectory temporary = new();
        SanitizedCaptureBundle original = Capture();
        SanitizedCaptureBundle multiple = original with
        {
            Inventory = original.Inventory with
            {
                UsbInterfaces =
                [
                    original.Inventory.UsbInterfaces[0] with { InstanceId = "usb-left" },
                    original.Inventory.UsbInterfaces[0] with { InstanceId = "usb-right" },
                ],
            },
        };
        string capturePath = temporary.GetPath("multiple.wsgmcap");
        using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, multiple);
        }
        DeviceLabPathBoundaries boundaries = new()
        {
            LiveDataDirectory = temporary.GetPath("never-live-wsgm"),
            BroadHomeDirectories = [],
        };

        InvalidDataException ambiguous = Assert.Throws<InvalidDataException>(() =>
            ScaffoldFromCaptureWorkflow.Run(
                capturePath,
                temporary.GetPath("ambiguous"),
                boundaries));
        PluginScaffoldResult selected = ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            temporary.GetPath("selected"),
            boundaries,
            usbInstanceId: "usb-right");

        Assert.Contains("Select one exact instance ID", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal("CAFE", selected.Identity.UsbVendorId);
        Assert.True(Directory.Exists(selected.OutputDirectory));
    }

    [Fact]
    public void MinimalTemplate_DemonstratesPartialStateCanonicalIoCancellationDiagnosticsAndRestore()
    {
        Assembly assembly = typeof(ScaffoldFromCaptureWorkflow).Assembly;
        using Stream stream = Assert.IsAssignableFrom<Stream>(assembly.GetManifestResourceStream(
            "WSGM.DeviceLab.Templates.MinimalPlugin.DevicePlugin.cs.template"));
        using StreamReader reader = new(stream);
        string template = reader.ReadToEnd();

        Assert.Contains("Available = false", template, StringComparison.Ordinal);
        Assert.Contains("PluginOperationalState.Degraded", template, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", template, StringComparison.Ordinal);
        Assert.Contains("PublishControllerSampleAsync", template, StringComparison.Ordinal);
        Assert.Contains("ApplyHapticOutputAsync", template, StringComparison.Ordinal);
        Assert.Contains("GetDiagnosticsAsync", template, StringComparison.Ordinal);
        Assert.Contains("_exampleValue = _capturedExampleValue", template, StringComparison.Ordinal);
    }

    private static SanitizedCaptureBundle Capture()
    {
        DateTimeOffset timestamp = DateTimeOffset.UnixEpoch;
        return new SanitizedCaptureBundle
        {
            Manifest = new ShareableCaptureManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                BundleId = "scaffold-test",
                ToolVersion = "test-1",
                StartedAt = timestamp,
                CompletedAt = timestamp,
                QpcFrequency = 1,
            },
            Recipe = new ObserveOnlyRecipe
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                RecipeId = "scaffold-test",
                DisplayName = "Scaffold test",
            },
            Inventory = new MachineInventory
            {
                SchemaVersion = 1,
                Firmware = new FirmwareInventory
                {
                    SystemManufacturer = "Contoso Devices",
                    BaseboardProduct = "BOARD-X1",
                    SystemSku = "BOARD-X1-SKU",
                    BiosVersion = "1.0.0",
                },
                UsbInterfaces =
                [
                    new UsbInterfaceInventory
                    {
                        InstanceId = "redacted-instance",
                        VendorId = "CAFE",
                        ProductId = "BEEF",
                        DeviceRelease = "0100",
                        Present = true,
                    },
                ],
                CapturedAt = timestamp,
            },
            Redaction = new CaptureRedactionManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                DefaultRedactionApplied = true,
            },
        };
    }
}
