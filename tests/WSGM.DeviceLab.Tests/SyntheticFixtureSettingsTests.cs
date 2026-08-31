using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.DeviceLab.Testing;

namespace WSGM.Device.Tests;

public sealed class SyntheticFixtureSettingsTests
{
    [Fact]
    public void SettingsManifest_IsValid()
    {
        Assert.True(
            SyntheticDockPlugin.SettingsManifest.TryValidate(out string? error),
            error);
    }

    [Fact]
    public void SettingsManifest_CoversEveryValueKindTheSdkAllows()
    {
        var kinds = SyntheticDockPlugin.SettingsManifest.Settings
            .Select(setting => setting.ValueKind)
            .ToHashSet();

        // Curve is excluded by design: a curve is authored as a named profile, not toggled as a
        // setting, and the SDK refuses one here.
        Assert.Equal(
            [
                CapabilityValueKind.Boolean,
                CapabilityValueKind.Integer,
                CapabilityValueKind.Choice,
                CapabilityValueKind.Color,
                CapabilityValueKind.Text,
            ],
            kinds.OrderBy(kind => (int)kind));
    }

    [Fact]
    public void SettingsManifest_KeepsOneSettingInAnUndeclaredSection()
    {
        PluginSettingDescriptor orphan = Assert.Single(
            SyntheticDockPlugin.SettingsManifest.Settings,
            setting => setting.SettingId == SyntheticDockPlugin.OrphanSettingId);

        Assert.DoesNotContain(
            SyntheticDockPlugin.SettingsManifest.Sections,
            section => section.SectionId == orphan.SectionId);
    }

    [Fact]
    public void SettingsManifest_EveryDefaultSatisfiesItsOwnDeclaration()
    {
        foreach (PluginSettingDescriptor setting in SyntheticDockPlugin.SettingsManifest.Settings)
        {
            Assert.True(
                setting.TryValidateValue(setting.Default, out string? error),
                $"{setting.SettingId}: {error}");
        }
    }
}
