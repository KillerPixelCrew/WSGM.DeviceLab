using WSGM.DeviceLab.Preflight;

namespace WSGM.Tests;

public sealed class DeviceLabOutputPathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"wsgm-output-policy-{Guid.NewGuid():N}");

    public DeviceLabOutputPathPolicyTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingPathIsRejectedAsMalformed(string? path)
    {
        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            path,
            DeviceLabOutputTargetKind.Directory,
            Boundaries());

        Assert.False(decision.IsAllowed);
        Assert.Equal(DeviceLabOutputPathRisk.Malformed, decision.Risk);
    }

    [Fact]
    public void DriveRootAndBroadDirectoriesAreRejected()
    {
        string driveRoot = Path.GetPathRoot(_root)!;
        DeviceLabPathBoundaries boundaries = Boundaries();

        Assert.Equal(
            DeviceLabOutputPathRisk.DriveRoot,
            DeviceLabOutputPathPolicy.Evaluate(
                driveRoot,
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.BroadHomeDirectory,
            DeviceLabOutputPathPolicy.Evaluate(
                boundaries.BroadHomeDirectories[0],
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.RepositoryRoot,
            DeviceLabOutputPathPolicy.Evaluate(
                boundaries.RepositoryRoot,
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);
    }

    [Fact]
    public void LiveDataDirectoryAndEveryChildAreRejectedBeforeCreation()
    {
        DeviceLabPathBoundaries boundaries = Boundaries();

        foreach (string path in new[]
        {
            boundaries.LiveDataDirectory,
            Path.Combine(boundaries.LiveDataDirectory, "capture", "bundle.wsgmcap"),
        })
        {
            DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
                path,
                DeviceLabOutputTargetKind.NewFile,
                boundaries);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeviceLabOutputPathRisk.LiveDataDirectory, decision.Risk);
        }
    }

    [Fact]
    public void ExistingTargetsAreNotOverwrittenOrTreatedAsDirectories()
    {
        string file = Path.Combine(_root, "existing.bin");
        File.WriteAllText(file, "owned");

        Assert.Equal(
            DeviceLabOutputPathRisk.ExistingTarget,
            DeviceLabOutputPathPolicy.Evaluate(
                file,
                DeviceLabOutputTargetKind.NewFile,
                Boundaries()).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.NotDirectory,
            DeviceLabOutputPathPolicy.Evaluate(
                file,
                DeviceLabOutputTargetKind.Directory,
                Boundaries()).Risk);
    }

    [Fact]
    public void DedicatedNewTargetsAreAllowedAndNormalized()
    {
        string requested = Path.Combine(_root, "capture", "..", "capture", "result.wsgmcap");

        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            requested,
            DeviceLabOutputTargetKind.NewFile,
            Boundaries());

        Assert.True(decision.IsAllowed);
        Assert.Equal(DeviceLabOutputPathRisk.None, decision.Risk);
        Assert.Equal(Path.GetFullPath(requested), decision.FullPath);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private DeviceLabPathBoundaries Boundaries() => new()
    {
        LiveDataDirectory = Path.Combine(_root, "live"),
        RepositoryRoot = Path.Combine(_root, "repo"),
        BroadHomeDirectories = [Path.Combine(_root, "home")],
    };
}
