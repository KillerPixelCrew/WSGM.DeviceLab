using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;

namespace WSGM.DeviceLab.Inventory;

/// <summary>
/// Reads the machine's identity, endpoints, and provider surface.
/// </summary>
/// <remarks>
/// Enumeration only. Nothing here opens a device for writing, invokes a vendor method, or transmits
/// on any bus — a method name is recorded because a catalog predicate may gate on its presence, and
/// recording a name is not calling it.
/// <para>
/// Every read is individually guarded. A machine that denies one WMI class must still produce a
/// complete inventory of everything else: this runs on unknown hardware by definition, and an
/// inventory that aborts on the first refusal tells a developer nothing about the other twenty
/// things that worked.
/// </para>
/// </remarks>
internal static partial class WindowsInventoryCollector
{
    /// <summary>Schema version emitted by this collector.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Collects a full read-only inventory of the current machine.
    /// </summary>
    /// <param name="capturedAt">Timestamp to stamp on the inventory.</param>
    /// <param name="wmiClassesToProbe">
    /// Namespace and class pairs whose presence should be recorded. Presence only; never invoked.
    /// </param>
    /// <param name="cancellationToken">Cancels between bounded inventory sections.</param>
    /// <returns>The inventory, with unreadable sections left null or marked.</returns>
    public static MachineInventory Collect(
        DateTimeOffset capturedAt,
        IReadOnlyList<(string Namespace, string ClassName)>? wmiClassesToProbe = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<InventoryCollectionIssue> collectionIssues = [];
        MachineInventory collected = new()
        {
            SchemaVersion = CurrentSchemaVersion,
            Firmware = CollectSection(CollectFirmware, cancellationToken),
            Processor = CollectSection(CollectProcessor, cancellationToken),
            GraphicsAdapters = CollectSection(
                () => CollectGraphicsAdapters(collectionIssues),
                cancellationToken),
            UsbInterfaces = CollectUsbInterfaces(cancellationToken, collectionIssues),
            WmiClasses = CollectWmiClasses(wmiClassesToProbe ?? [], cancellationToken),
            SerialEndpoints = CollectSection(CollectSerialEndpoints, cancellationToken),
            Sensors = CollectSection(CollectSensors, cancellationToken),
            InputBackends = CollectSection(CollectInputBackends, cancellationToken),
            Processes = CollectSection(CollectRelevantProcesses, cancellationToken),
            Services = CollectSection(CollectRelevantServices, cancellationToken),
            ScheduledTasks = CollectSection(CollectRelevantScheduledTasks, cancellationToken),
            CollectionIssues = collectionIssues,
            CapturedAt = capturedAt,
        };
        collected = collected with
        {
            NativeBinaries = CollectSection(
                () => CollectNativeBinaries(collected.Processes, collected.Services),
                cancellationToken),
            Providers = CollectSection(
                () => CollectRelevantProviders(collected.Processes),
                cancellationToken),
            TopologyGenerations = collected.UsbInterfaces.Select(endpoint =>
                new TopologyGenerationInventory
                {
                    Generation = 1,
                    Change = TopologyChangeKind.Baseline,
                    InstanceId = endpoint.InstanceId,
                    AssociationId = endpoint.DeviceLevelLocationPath,
                    Present = endpoint.Present,
                }).ToArray(),
        };
        cancellationToken.ThrowIfCancellationRequested();
        collected = collected with
        {
            ResourceConflicts = CollectSection(
                () => DeriveResourceConflicts(
                    collected.Processes,
                    collected.Services,
                    collected.NativeBinaries),
                cancellationToken),
        };
        cancellationToken.ThrowIfCancellationRequested();
        return MachineInventoryNormalizer.Normalize(collected);
    }

    private static T CollectSection<T>(Func<T> collect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        T result = collect();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static FirmwareInventory CollectFirmware()
    {
        using ManagementObject? system = QuerySingle("root\\CIMV2", "SELECT * FROM Win32_ComputerSystem");
        using ManagementObject? board = QuerySingle("root\\CIMV2", "SELECT * FROM Win32_BaseBoard");
        using ManagementObject? bios = QuerySingle("root\\CIMV2", "SELECT * FROM Win32_BIOS");

        string? ecVersion = null;
        if (bios is not null)
        {
            // Recorded exactly as reported, including 255/255 - the SMBIOS "unknown" encoding. A
            // matcher has to be able to tell "firmware says it does not know" from "nobody looked",
            // and the usable EC version comes from the vendor provider instead.
            string? major = Text(bios, "EmbeddedControllerMajorVersion");
            string? minor = Text(bios, "EmbeddedControllerMinorVersion");
            if (major is not null || minor is not null)
            {
                ecVersion = $"{major ?? "?"}.{minor ?? "?"}";
            }
        }

        return new FirmwareInventory
        {
            SystemManufacturer = Text(system, "Manufacturer"),
            SystemProduct = Text(system, "Model"),
            SystemSku = Text(system, "SystemSKUNumber"),
            SystemFamily = Text(system, "SystemFamily"),
            BaseboardProduct = Text(board, "Product"),
            BaseboardVersion = Text(board, "Version"),
            BiosVersion = Text(bios, "SMBIOSBIOSVersion"),
            EmbeddedControllerVersion = ecVersion,
        };
    }

    private static ProcessorInventory? CollectProcessor()
    {
        using ManagementObject? cpu = QuerySingle("root\\CIMV2", "SELECT * FROM Win32_Processor");
        if (cpu is null)
        {
            return null;
        }

        // Win32_Processor exposes Description as "Family N Model N Stepping N" rather than the three
        // as separate usable numbers, so the identity used for matching is parsed from it.
        string? description = Text(cpu, "Description");
        Match match = description is null ? Match.Empty : CpuDescription().Match(description);

        return new ProcessorInventory
        {
            Name = Text(cpu, "Name"),
            Family = match.Success ? int.Parse(match.Groups["family"].Value, CultureInfo.InvariantCulture) : null,
            Model = match.Success ? int.Parse(match.Groups["model"].Value, CultureInfo.InvariantCulture) : null,
            Stepping = match.Success ? int.Parse(match.Groups["stepping"].Value, CultureInfo.InvariantCulture) : null,
            Cores = int.TryParse(Text(cpu, "NumberOfCores"), out int cores) ? cores : null,
        };
    }

    private static IReadOnlyList<UsbInterfaceInventory> CollectUsbInterfaces(
        CancellationToken cancellationToken,
        ICollection<InventoryCollectionIssue> collectionIssues)
    {
        List<UsbInterfaceInventory> interfaces = [];

        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\CIMV2",
                "SELECT DeviceID, PNPClass, Status, HardwareID FROM Win32_PnPEntity "
                    + "WHERE DeviceID LIKE 'USB%' OR DeviceID LIKE 'HID%'");

            foreach (ManagementBaseObject entity in searcher.Get())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (entity)
                {
                    string? instanceId = Text(entity, "DeviceID");
                    if (instanceId is null)
                    {
                        continue;
                    }

                    Match ids = UsbIdentifiers().Match(instanceId);
                    string? hardwareIds = TextList(entity, "HardwareID");
                    Match release = hardwareIds is null ? Match.Empty : UsbRelease().Match(hardwareIds);
                    string? locationPath = DeviceProperties.ResolveLocationPath(instanceId);

                    interfaces.Add(new UsbInterfaceInventory
                    {
                        InstanceId = instanceId,
                        DeviceClass = Text(entity, "PNPClass"),
                        VendorId = ids.Success ? ids.Groups["vid"].Value.ToUpperInvariant() : null,
                        ProductId = ids.Success ? ids.Groups["pid"].Value.ToUpperInvariant() : null,
                        DeviceRelease = release.Success ? release.Groups["rev"].Value.ToUpperInvariant() : null,
                        InterfaceNumber = ParseInterfaceNumber(instanceId),
                        LocationPath = locationPath,
                        DeviceLevelLocationPath = DeviceProperties.ToDeviceLevelPath(locationPath),
                        Present = string.Equals(Text(entity, "Status"), "OK", StringComparison.Ordinal),
                    });
                }
            }
        }
        catch (ManagementException exception)
        {
            collectionIssues.Add(new InventoryCollectionIssue
            {
                Lane = "usb",
                Error = exception.ErrorCode.ToString(),
            });
        }
        catch (UnauthorizedAccessException)
        {
            collectionIssues.Add(new InventoryCollectionIssue
            {
                Lane = "usb",
                Error = "AccessDenied",
            });
        }

        interfaces.Sort((a, b) => string.CompareOrdinal(a.InstanceId, b.InstanceId));
        return interfaces;
    }

    private static IReadOnlyList<WmiClassInventory> CollectWmiClasses(
        IReadOnlyList<(string Namespace, string ClassName)> classes,
        CancellationToken cancellationToken)
    {
        List<WmiClassInventory> results = [];

        foreach ((string ns, string className) in classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ProbeClass(ns, className));
        }

        return results;
    }

    private static WmiClassInventory ProbeClass(string ns, string className)
    {
        try
        {
            using ManagementClass definition = new(
                new ManagementScope(ns), new ManagementPath(className), null);
            definition.Get();

            List<string> methods = [];
            foreach (MethodData method in definition.Methods)
            {
                methods.Add(method.Name);
            }

            methods.Sort(StringComparer.Ordinal);

            int? instanceCount = null;
            try
            {
                using ManagementObjectSearcher searcher = new(ns, $"SELECT * FROM {className}");
                int count = 0;
                foreach (ManagementBaseObject instance in searcher.Get())
                {
                    instance.Dispose();
                    count++;
                }

                instanceCount = count;
            }
            catch (ManagementException enumerationFailure)
                when (enumerationFailure.ErrorCode == ManagementStatus.AccessDenied)
            {
                // The class exists and its shape is readable, but instances are not. Distinct from
                // absent, and the distinction decides whether a capability is unsupported or merely
                // needs elevation.
                return new WmiClassInventory
                {
                    Namespace = ns,
                    ClassName = className,
                    Access = WmiAccess.AccessDenied,
                    MethodNames = methods,
                };
            }

            return new WmiClassInventory
            {
                Namespace = ns,
                ClassName = className,
                Access = WmiAccess.Available,
                InstanceCount = instanceCount,
                MethodNames = methods,
            };
        }
        catch (ManagementException ex)
        {
            return new WmiClassInventory
            {
                Namespace = ns,
                ClassName = className,
                Access = ex.ErrorCode switch
                {
                    ManagementStatus.AccessDenied => WmiAccess.AccessDenied,
                    ManagementStatus.InvalidNamespace => WmiAccess.NamespaceUnavailable,
                    _ => WmiAccess.NotFound,
                },
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new WmiClassInventory
            {
                Namespace = ns,
                ClassName = className,
                Access = WmiAccess.AccessDenied,
            };
        }
    }

    private static ManagementObject? QuerySingle(string ns, string query)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(ns, query);
            foreach (ManagementBaseObject item in searcher.Get())
            {
                return (ManagementObject)item;
            }
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? Text(ManagementBaseObject? source, string property)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            object? value = source[property];
            string? text = value?.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            return text.Length <= InventoryLimits.MaximumTextCharacters
                ? text
                : text[..InventoryLimits.MaximumTextCharacters];
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static string? TextList(ManagementBaseObject source, string property)
    {
        try
        {
            if (source[property] is not string[] values)
            {
                return Text(source, property);
            }

            string joined = string.Join(";", values.Take(64));
            return joined.Length <= InventoryLimits.MaximumTextCharacters
                ? joined
                : joined[..InventoryLimits.MaximumTextCharacters];
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static int? ParseInterfaceNumber(string instanceId)
    {
        Match match = InterfaceNumber().Match(instanceId);
        return match.Success
            ? int.Parse(match.Groups["mi"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : null;
    }

    [GeneratedRegex(@"VID_(?<vid>[0-9A-Fa-f]{4})&PID_(?<pid>[0-9A-Fa-f]{4})")]
    private static partial Regex UsbIdentifiers();

    [GeneratedRegex(@"&MI_(?<mi>[0-9A-Fa-f]{2})")]
    private static partial Regex InterfaceNumber();

    [GeneratedRegex(@"&REV_(?<rev>[0-9A-Fa-f]{4})")]
    private static partial Regex UsbRelease();

    [GeneratedRegex(@"Family\s+(?<family>\d+)\s+Model\s+(?<model>\d+)\s+Stepping\s+(?<stepping>\d+)")]
    private static partial Regex CpuDescription();
}
