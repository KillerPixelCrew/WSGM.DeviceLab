using System.IO.Compression;
using WSGM.Device.Sdk.Packaging;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;

namespace WSGM.Device.Tests;

public sealed class DeviceLabPackagingTests
{
    [Fact]
    public void Pack_ValidMinimalPackage_ProducesDeterministicArchiveContainingOnlyPackageFiles()
    {
        using TemporaryDirectory temporary = new();
        string source = CreatePackage(temporary);
        DeviceLabPathBoundaries boundaries = Boundaries(temporary);
        string first = temporary.GetPath("first.wsgmpkg");
        string second = temporary.GetPath("second.wsgmpkg");

        PluginPackageValidationReport validated = PluginPackageWorkflow.ValidateOffline(source);
        PluginPackageValidationReport firstReport = PluginPackageWorkflow.Pack(
            source,
            first,
            boundaries);
        PluginPackageValidationReport secondReport = PluginPackageWorkflow.Pack(
            source,
            second,
            boundaries);

        Assert.True(validated.Valid, Describe(validated));
        Assert.True(firstReport.Valid, Describe(firstReport));
        Assert.True(secondReport.Valid, Describe(secondReport));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

        using ZipArchive archive = ZipFile.OpenRead(first);
        string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("plugin.wsgm.json", entries);
        Assert.Contains("Synthetic.Dock.dll", entries);
        Assert.Equal(2, entries.Length);
    }

    [Fact]
    public void ValidateOffline_PrivilegedProvisioningArtifact_IsRejected()
    {
        using TemporaryDirectory temporary = new();
        string source = CreatePackage(temporary);
        File.WriteAllText(Path.Combine(source, "install-driver.ps1"), "exit 0");

        PluginPackageValidationReport report = PluginPackageWorkflow.ValidateOffline(source);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "forbidden-provisioning-artifact");
    }

    [Fact]
    public void ValidateOffline_OversizedManifest_IsRejectedBeforeReadingThePayload()
    {
        using TemporaryDirectory temporary = new();
        string source = temporary.GetPath("package");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(
            Path.Combine(source, "plugin.wsgm.json"),
            new byte[ManifestLimits.MaxDocumentBytes + 1]);

        PluginPackageValidationReport report = PluginPackageWorkflow.ValidateOffline(source);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "document-too-large");
    }

    [Fact]
    public void ValidateOffline_NonX64EntryAssembly_IsRejectedBeforePacking()
    {
        using TemporaryDirectory temporary = new();
        string source = CreatePackage(temporary);
        WriteManagedPe(Path.Combine(source, "Synthetic.Dock.dll"), machine: 0x014c);

        PluginPackageValidationReport report = PluginPackageWorkflow.ValidateOffline(source);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "architecture-unsupported");
    }

    [Fact]
    public void ValidateOffline_TruncatedAmd64HeaderIsRejectedAsMalformed()
    {
        using TemporaryDirectory temporary = new();
        string source = CreatePackage(temporary);
        WriteTruncatedPeHeader(Path.Combine(source, "Synthetic.Dock.dll"), machine: 0x8664);

        PluginPackageValidationReport report = PluginPackageWorkflow.ValidateOffline(source);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "architecture-unsupported");
    }

    [Fact]
    public void BoundedEntryCapture_StopsAfterOneOverflowObservationBeforeSorting()
    {
        int observed = 0;
        IEnumerable<string> Entries()
        {
            while (true)
            {
                observed++;
                yield return $"entry-{observed}";
            }
        }

        IReadOnlyList<string> accepted = DeviceLabPackageSnapshot.TakeBoundedEntries(
            Entries(),
            remaining: 4,
            CancellationToken.None,
            out bool exceeded);

        Assert.True(exceeded);
        Assert.Equal(4, accepted.Count);
        Assert.Equal(5, observed);
    }

    [Fact]
    public void Pack_PinsValidatedSourceBytesAgainstReplacementUntilArchivePublication()
    {
        using TemporaryDirectory temporary = new();
        string source = CreatePackage(temporary);
        string entryAssembly = Path.Combine(source, "Synthetic.Dock.dll");
        string output = temporary.GetPath("pinned.wsgmpkg");
        bool replacementBlocked = false;

        PluginPackageValidationReport report = PluginPackageWorkflow.Pack(
            source,
            output,
            Boundaries(temporary),
            CancellationToken.None,
            sourceValidated: () =>
            {
                _ = Assert.Throws<IOException>(() => File.WriteAllBytes(entryAssembly, [1, 2, 3]));
                replacementBlocked = true;
            });

        Assert.True(report.Valid, Describe(report));
        Assert.True(replacementBlocked);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Pack_CancellationAfterValidationPublishesNoArchive()
    {
        using TemporaryDirectory temporary = new();
        string source = CreatePackage(temporary);
        string output = temporary.GetPath("cancelled.wsgmpkg");
        using CancellationTokenSource cancellation = new();

        _ = Assert.Throws<OperationCanceledException>(() => PluginPackageWorkflow.Pack(
            source,
            output,
            Boundaries(temporary),
            cancellation.Token,
            cancellation.Cancel));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(temporary.Root, "cancelled.wsgmpkg.*.tmp"));
    }

    [Fact]
    public void PackageBudget_RejectsFileCountSingleFileAndAggregateOverflow()
    {
        Assert.True(PluginPackageWorkflow.PackageEntryBudgetExceeded(
            PluginPackageWorkflow.MaximumPackageEntries));
        Assert.False(PluginPackageWorkflow.PackageEntryBudgetExceeded(
            PluginPackageWorkflow.MaximumPackageEntries - 1));
        Assert.Equal(
            "package-too-many-files",
            PluginPackageWorkflow.PackageBudgetViolation(
                PluginPackageWorkflow.MaximumPackageFiles,
                acceptedBytes: 0,
                nextFileBytes: 0));
        Assert.Equal(
            "file-too-large",
            PluginPackageWorkflow.PackageBudgetViolation(
                acceptedFileCount: 0,
                acceptedBytes: 0,
                nextFileBytes: PluginPackageWorkflow.MaximumPackageFileBytes + 1));
        Assert.Equal(
            "package-too-large",
            PluginPackageWorkflow.PackageBudgetViolation(
                acceptedFileCount: 1,
                acceptedBytes: PluginPackageWorkflow.MaximumPackageBytes,
                nextFileBytes: 1));
    }

    private static string CreatePackage(TemporaryDirectory temporary)
    {
        string source = temporary.GetPath("package");
        Directory.CreateDirectory(source);
        WriteManagedPe(Path.Combine(source, "Synthetic.Dock.dll"));
        File.WriteAllBytes(
            Path.Combine(source, "plugin.wsgm.json"),
            PluginManifestFixture.Serialize(PluginManifestFixture.Manifest()));
        return source;
    }

    private static void WriteManagedPe(string path, ushort? machine = null)
    {
        byte[] bytes = File.ReadAllBytes(typeof(DeviceLabPackagingTests).Assembly.Location);
        int peOffset = BitConverter.ToInt32(bytes, 60);
        if (machine is { } patchedMachine)
        {
            BitConverter.GetBytes(patchedMachine).CopyTo(bytes, peOffset + 4);
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteTruncatedPeHeader(string path, ushort machine)
    {
        byte[] bytes = new byte[70];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 60);
        bytes[64] = (byte)'P';
        bytes[65] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 68);
        File.WriteAllBytes(path, bytes);
    }

    private static DeviceLabPathBoundaries Boundaries(TemporaryDirectory temporary) => new()
    {
        LiveDataDirectory = temporary.GetPath("never-live-data"),
        BroadHomeDirectories = [],
    };

    private static string Describe(PluginPackageValidationReport report) =>
        string.Join("; ", report.Issues.Select(issue => $"{issue.Path}: {issue.Message}"));
}
