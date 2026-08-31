using System.Text.Json;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Serialization;

namespace WSGM.Device.Tests;

/// <summary>A valid manifest for tests that need a package to exist, not a manifest to be
/// interesting. The SDK's own repository owns the tests that probe manifest validation; these
/// only need something that passes it.</summary>
internal static class PluginManifestFixture
{
    internal static PluginManifest Manifest() => new()
    {
        Id = "wsgm.device.synthetic.dock-x1",
        Name = "Synthetic Dock X1",
        Version = "1.0.0",
        ApiVersion = DeviceApi.Version,
        EntryAssembly = "Synthetic.Dock.dll",
        EntryType = "Synthetic.Dock.Plugin",
    };

    internal static byte[] Serialize(PluginManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, DeviceJsonContext.Default.PluginManifest);
}
