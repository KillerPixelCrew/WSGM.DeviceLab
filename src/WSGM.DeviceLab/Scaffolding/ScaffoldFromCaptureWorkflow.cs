using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Packaging;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Scaffolding;

/// <summary>Exact identity copied into a new minimal plugin starter.</summary>
internal sealed record PluginScaffoldIdentity
{
    /// <summary>Required SMBIOS system manufacturer.</summary>
    public required string SystemManufacturer { get; init; }

    /// <summary>Required SMBIOS baseboard product.</summary>
    public required string BaseboardProduct { get; init; }

    /// <summary>Required SMBIOS system SKU.</summary>
    public required string SystemSku { get; init; }

    /// <summary>Required exact BIOS version.</summary>
    public required string BiosVersion { get; init; }

    /// <summary>Required USB vendor identifier.</summary>
    public required string UsbVendorId { get; init; }

    /// <summary>Required USB product identifier.</summary>
    public required string UsbProductId { get; init; }

    /// <summary>Required USB device release.</summary>
    public required string UsbDeviceRelease { get; init; }
}

/// <summary>Files written by token replacement from the checked-in minimal plugin template.</summary>
internal sealed record PluginScaffoldResult
{
    /// <summary>New absolute output directory.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Stable starter package ID.</summary>
    public required string PackageId { get; init; }

    /// <summary>Root namespace and assembly name.</summary>
    public required string RootNamespace { get; init; }

    /// <summary>Exact copied device identity.</summary>
    public required PluginScaffoldIdentity Identity { get; init; }

    /// <summary>Relative files written from the checked-in templates.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>Copies the checked-in minimal plugin template and replaces exact identity tokens.</summary>
internal static partial class ScaffoldFromCaptureWorkflow
{
    private const string ResourcePrefix = "WSGM.DeviceLab.Templates.MinimalPlugin.";

    private static readonly IReadOnlyList<TemplateFile> Templates =
    [
        new("plugin.wsgm.json.template", "plugin.wsgm.json"),
        new("Plugin.csproj.template", "{rootNamespace}.csproj"),
        new("DevicePlugin.cs.template", "DevicePlugin.cs"),
        new("README.md.template", "README.md"),
        new("LICENSE.txt.template", "LICENSE.txt"),
    ];

    /// <summary>Writes a hardware-empty starter from one validated current capture.</summary>
    /// <param name="capturePath">Sanitized source capture.</param>
    /// <param name="outputDirectory">New explicit output directory.</param>
    /// <param name="boundaries">Filesystem safety boundaries.</param>
    /// <param name="cancellationToken">Cancels validation or publication.</param>
    /// <returns>The copied template files and exact identity.</returns>
    public static PluginScaffoldResult Run(
        string capturePath,
        string outputDirectory,
        DeviceLabPathBoundaries boundaries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(boundaries);
        cancellationToken.ThrowIfCancellationRequested();

        using FileStream capture = new(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        CaptureBundleReadResult read = CaptureBundleReader.Read(capture, cancellationToken);
        if (!read.Succeeded || read.Bundle is null)
        {
            throw new InvalidDataException($"Source capture was rejected: {read.Failure} ({read.Detail}).");
        }

        PluginScaffoldIdentity identity = SelectExactIdentity(read.Bundle);
        string slug = Slug(identity.BaseboardProduct);
        string rootNamespace = $"WSGM.Device.Scaffold.{Identifier(slug)}";
        string packageId = $"wsgm.device.scaffold.{slug}";
        string deviceDefinitionId = $"scaffold.{slug}";
        string displayName = $"{identity.SystemManufacturer} {identity.BaseboardProduct} Device Plugin";
        Dictionary<string, string> tokens = Tokens(
            boundaries,
            rootNamespace,
            packageId,
            deviceDefinitionId,
            displayName,
            identity);

        List<(string Path, string Content)> rendered = [];
        foreach (TemplateFile template in Templates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = template.OutputPath.Replace("{rootNamespace}", rootNamespace, StringComparison.Ordinal);
            string content = ReplaceTokens(ReadTemplate(template.ResourceName), tokens);
            rendered.Add((path, Normalize(content)));
        }

        PluginManifestReadResult manifest = PluginManifestReader.Read(
            Encoding.UTF8.GetBytes(rendered.Single(file => file.Path == "plugin.wsgm.json").Content));
        if (!manifest.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", manifest.Errors.Select(error => error.Message)));
        }

        DeviceLabOutputPathDecision output = DeviceLabOutputPathPolicy.Evaluate(
            outputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!output.IsAllowed || output.FullPath is null)
        {
            throw new IOException(output.Reason ?? "Scaffold output path was rejected.");
        }

        if (Directory.Exists(output.FullPath) || File.Exists(output.FullPath))
        {
            throw new IOException("Scaffold output must be a new directory.");
        }

        string parent = Path.GetDirectoryName(output.FullPath)
            ?? throw new IOException("Scaffold output has no parent directory.");
        string temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(output.FullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(parent);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(temporary);
            foreach ((string relative, string content) in rendered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(temporary, relative));
                if (!path.StartsWith(
                    temporary + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("A template output escaped the scaffold directory.");
                }

                using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(Encoding.UTF8.GetBytes(content));
                file.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(output.FullPath) || File.Exists(output.FullPath))
            {
                throw new IOException("Scaffold output was created before publication.");
            }
            Directory.Move(temporary, output.FullPath);
        }
        catch
        {
            TryDeleteTemporaryDirectory(temporary);
            throw;
        }

        return new PluginScaffoldResult
        {
            OutputDirectory = output.FullPath,
            PackageId = packageId,
            RootNamespace = rootNamespace,
            Identity = identity,
            Files = [.. rendered.Select(file => file.Path).Order(StringComparer.Ordinal)],
        };
    }

    private static PluginScaffoldIdentity SelectExactIdentity(SanitizedCaptureBundle bundle)
    {
        UsbInterfaceInventory endpoint = bundle.Inventory.UsbInterfaces
            .Where(candidate => candidate.Present
                && candidate.VendorId is { Length: 4 }
                && candidate.ProductId is { Length: 4 }
                && candidate.DeviceRelease is { Length: 4 })
            .OrderBy(candidate => candidate.VendorId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ProductId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.DeviceRelease, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Capture has no present exact USB VID/PID/release endpoint.");

        return new PluginScaffoldIdentity
        {
            SystemManufacturer = bundle.Inventory.Firmware.SystemManufacturer
                ?? throw new InvalidDataException("Capture has no exact SMBIOS system manufacturer."),
            BaseboardProduct = bundle.Inventory.Firmware.BaseboardProduct
                ?? throw new InvalidDataException("Capture has no exact baseboard product."),
            SystemSku = bundle.Inventory.Firmware.SystemSku
                ?? throw new InvalidDataException("Capture has no exact SMBIOS system SKU."),
            BiosVersion = bundle.Inventory.Firmware.BiosVersion
                ?? throw new InvalidDataException("Capture has no exact BIOS version."),
            UsbVendorId = endpoint.VendorId!,
            UsbProductId = endpoint.ProductId!,
            UsbDeviceRelease = endpoint.DeviceRelease!,
        };
    }

    private static Dictionary<string, string> Tokens(
        DeviceLabPathBoundaries boundaries,
        string rootNamespace,
        string packageId,
        string deviceDefinitionId,
        string displayName,
        PluginScaffoldIdentity identity)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ROOT_NAMESPACE"] = rootNamespace,
            ["PACKAGE_ID_JSON"] = Json(identity: packageId),
            ["PACKAGE_ID_CS"] = CSharp(packageId),
            ["DISPLAY_NAME_JSON"] = Json(identity: displayName),
            ["DISPLAY_NAME_MD"] = Markdown(displayName),
            ["DEVICE_ID_CS"] = CSharp(deviceDefinitionId),
            ["API_VERSION"] = DeviceApi.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SDK_REFERENCE_XML"] = SdkReferenceXml(boundaries),
            ["MANUFACTURER_CS"] = CSharp(identity.SystemManufacturer),
            ["MANUFACTURER_MD"] = Markdown(identity.SystemManufacturer),
            ["BOARD_CS"] = CSharp(identity.BaseboardProduct),
            ["BOARD_MD"] = Markdown(identity.BaseboardProduct),
            ["SYSTEM_SKU_CS"] = CSharp(identity.SystemSku),
            ["SYSTEM_SKU_MD"] = Markdown(identity.SystemSku),
            ["BIOS_CS"] = CSharp(identity.BiosVersion),
            ["BIOS_MD"] = Markdown(identity.BiosVersion),
            ["USB_VENDOR_CS"] = CSharp(identity.UsbVendorId),
            ["USB_PRODUCT_CS"] = CSharp(identity.UsbProductId),
            ["USB_RELEASE_CS"] = CSharp(identity.UsbDeviceRelease),
            ["USB_MD"] = Markdown($"{identity.UsbVendorId}:{identity.UsbProductId} release {identity.UsbDeviceRelease}"),
        };
    }

    /// <summary>Returns a buildable reference to the exact SDK used by this Device Lab process.</summary>
    /// <remarks>
    /// A checkout gets a project reference for normal source development. The installed tool is not
    /// inside a checkout, so its scaffold instead records the absolute path of the exact SDK assembly
    /// shipped beside it. An unresolved MSBuild property is never emitted.
    /// </remarks>
    internal static string SdkReferenceXml(DeviceLabPathBoundaries boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (boundaries.RepositoryRoot is { Length: > 0 } root)
        {
            string candidate = Path.GetFullPath(
                Path.Combine(root, "src", "WSGM.Device.Sdk", "WSGM.Device.Sdk.csproj"));
            if (File.Exists(candidate))
            {
                return $"<ProjectReference Include=\"{Xml(candidate)}\" />";
            }
        }

        string sdkAssembly = typeof(DeviceApi).Assembly.Location;
        if (string.IsNullOrWhiteSpace(sdkAssembly)
            || !File.Exists(sdkAssembly)
            || !string.Equals(
                Path.GetFileName(sdkAssembly),
                "WSGM.Device.Sdk.dll",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The exact WSGM.Device.Sdk assembly is unavailable. Run Device Lab from a complete "
                + "installation or a WSGM source checkout.");
        }

        string resolved = Path.GetFullPath(sdkAssembly);
        return "<Reference Include=\"WSGM.Device.Sdk\">\n"
            + $"      <HintPath>{Xml(resolved)}</HintPath>\n"
            + "      <Private>false</Private>\n"
            + "    </Reference>";
    }

    private static string ReadTemplate(string name)
    {
        Assembly assembly = typeof(ScaffoldFromCaptureWorkflow).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidDataException($"Checked-in plugin template '{name}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReplaceTokens(string template, IReadOnlyDictionary<string, string> tokens)
    {
        string rendered = template;
        foreach ((string key, string value) in tokens)
        {
            rendered = rendered.Replace($"{{{{{key}}}}}", value, StringComparison.Ordinal);
        }

        if (rendered.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A checked-in plugin template contains an unresolved token.");
        }

        return rendered;
    }

    private static string Slug(string value)
    {
        string slug = NonIdentifier().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "unknown-device" : slug;
    }

    private static string Identifier(string slug)
    {
        StringBuilder builder = new();
        foreach (string segment in slug.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(segment[0])).Append(segment.AsSpan(1));
        }

        return builder.Length == 0 ? "UnknownDevice" : builder.ToString();
    }

    private static string CSharp(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Json(string identity) => JsonEncodedText.Encode(identity).ToString();

    private static string Xml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Markdown(string value) => value
        .Replace('`', '\'')
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Normalize(string content) => content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .TrimEnd() + "\n";

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Preserve the original scaffold failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original scaffold failure.
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonIdentifier();

    private sealed record TemplateFile(string ResourceName, string OutputPath);
}
