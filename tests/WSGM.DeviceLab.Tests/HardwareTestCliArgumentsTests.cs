using WSGM.DeviceLab.Cli;
using WSGM.DeviceLab.Testing;

namespace WSGM.Device.Tests;

public sealed class HardwareTestCliArgumentsTests
{
    [Fact]
    public void CapabilityAction_ParsesExactlyOneActionAndItsApplicableOptions()
    {
        string[] arguments =
        [
            "plugin",
            "--from", "inventory.json",
            "--state-dir", "state",
            "--action", "capability",
            "--capability", "performance.profile",
            "--instance", "apu",
            "--value", "balanced",
        ];

        bool accepted = HardwareTestCliArguments.TryParse(
            arguments,
            out HardwareTestCliArguments? parsed,
            out string error);

        Assert.True(accepted, error);
        Assert.NotNull(parsed);
        Assert.Equal("plugin", parsed.PackageDirectory);
        Assert.Equal("inventory.json", parsed.InventoryPath);
        Assert.Equal("state", parsed.StateDirectory);
        Assert.Equal(AttendedPluginActionKind.CapabilityValue, parsed.Action.Kind);
        Assert.Equal("performance.profile", parsed.Action.CapabilityId);
        Assert.Equal("apu", parsed.Action.InstanceId);
        Assert.Equal("balanced", parsed.Action.ValueText);
    }

    [Theory]
    [InlineData("haptic", "HapticPulse")]
    [InlineData("controller", "ControllerManagement")]
    public void FixedAction_ParsesWithoutCapabilityOptions(
        string actionName,
        string expectedKind)
    {
        string[] arguments =
        [
            "plugin",
            "-f", "inventory.json",
            "--state-dir", "state",
            "--action", actionName,
        ];

        bool accepted = HardwareTestCliArguments.TryParse(
            arguments,
            out HardwareTestCliArguments? parsed,
            out string error);

        Assert.True(accepted, error);
        Assert.NotNull(parsed);
        Assert.Equal(expectedKind, parsed.Action.Kind.ToString());
    }

    [Theory]
    [MemberData(nameof(DuplicateOptionCases))]
    public void DuplicateOption_IsRejected(string[] arguments)
    {
        Assert.False(HardwareTestCliArguments.TryParse(arguments, out _, out string error));
        Assert.Contains("exactly once", error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnknownOrTrailingArgumentCases))]
    public void UnknownOrTrailingArgument_IsRejected(string[] arguments)
    {
        Assert.False(HardwareTestCliArguments.TryParse(arguments, out _, out string error));
        Assert.Contains("Unknown or trailing", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("--YES")]
    [InlineData("--YeS")]
    public void YesFlag_IsRejectedCaseInsensitively(string flag)
    {
        string[] arguments = ValidFixedAction().Append(flag).ToArray();

        Assert.False(HardwareTestCliArguments.TryParse(arguments, out _, out string error));
        Assert.Contains("never accepts --yes", error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MissingValueCases))]
    public void MissingOptionValue_IsRejected(string[] arguments)
    {
        Assert.False(HardwareTestCliArguments.TryParse(arguments, out _, out string error));
        Assert.Contains("requires", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--capability", "lighting.zone")]
    [InlineData("--instance", "left")]
    [InlineData("--value", "true")]
    public void FixedAction_WithCapabilityOnlyOption_IsRejected(string option, string value)
    {
        string[] arguments = ValidFixedAction().Concat([option, value]).ToArray();

        Assert.False(HardwareTestCliArguments.TryParse(arguments, out _, out string error));
        Assert.Contains("apply only", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingExplicitAction_IsRejected()
    {
        string[] arguments = ["plugin", "--from", "inventory.json", "--state-dir", "state"];

        Assert.False(HardwareTestCliArguments.TryParse(arguments, out _, out string error));
        Assert.Contains("exactly one --action", error, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> DuplicateOptionCases => new()
    {
        {
            [
                "plugin", "--from", "one.json", "-f", "two.json", "--state-dir", "state",
                "--action", "haptic",
            ]
        },
        {
            [
                "plugin", "--from", "inventory.json", "--state-dir", "one", "--state-dir", "two",
                "--action", "haptic",
            ]
        },
        {
            [
                "plugin", "--from", "inventory.json", "--state-dir", "state",
                "--action", "haptic", "--action", "controller",
            ]
        },
        {
            [
                "plugin", "--from", "inventory.json", "--state-dir", "state",
                "--action", "capability", "--capability", "one", "--capability", "two",
                "--value", "true",
            ]
        },
        {
            [
                "plugin", "--from", "inventory.json", "--state-dir", "state",
                "--action", "capability", "--capability", "lighting.zone",
                "--instance", "left", "--instance", "right", "--value", "true",
            ]
        },
        {
            [
                "plugin", "--from", "inventory.json", "--state-dir", "state",
                "--action", "capability", "--capability", "lighting.zone",
                "--value", "true", "--value", "false",
            ]
        },
    };

    public static TheoryData<string[]> UnknownOrTrailingArgumentCases => new()
    {
        { [.. ValidFixedAction(), "--unknown", "value"] },
        { [.. ValidFixedAction(), "trailing-value"] },
    };

    public static TheoryData<string[]> MissingValueCases => new()
    {
        { ["plugin", "--from"] },
        { ["plugin", "--from", "--state-dir", "state", "--action", "haptic"] },
        { ["plugin", "--from", "inventory.json", "--state-dir", "--action", "haptic"] },
        { ["plugin", "--from", "inventory.json", "--state-dir", "state", "--action"] },
        {
            [
                "plugin", "--from", "inventory.json", "--state-dir", "state",
                "--action", "capability", "--capability", "--value", "true",
            ]
        },
        {
            [
                "plugin", "--from", "inventory.json", "--state-dir", "state",
                "--action", "capability", "--capability", "lighting.zone", "--value",
            ]
        },
        {
            [
                "plugin", "--from", "inventory.json", "--state-dir", "state",
                "--action", "capability", "--capability", "lighting.zone",
                "--instance", "--value", "true",
            ]
        },
    };

    private static string[] ValidFixedAction() =>
    [
        "plugin",
        "--from", "inventory.json",
        "--state-dir", "state",
        "--action", "haptic",
    ];
}
