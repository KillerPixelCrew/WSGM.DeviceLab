using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WSGM.DeviceLab.Inventory;

internal static partial class WindowsInventoryCollector
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotConnected = 1167;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RimTypeHid = 2;

    private static readonly string[] RelevantNameFragments =
    [
        "wsgm",
        "msi",
        "center",
        "handheld",
        "hidhide",
        "hidmaestro",
        "steam",
        "rtss",
        "rivatuner",
        "xinput",
        "gamepad",
    ];

    private static IReadOnlyList<GraphicsAdapterInventory> CollectGraphicsAdapters(
        ICollection<InventoryCollectionIssue> collectionIssues)
    {
        List<GraphicsAdapterInventory> adapters = [];
        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\CIMV2",
                "SELECT PNPDeviceID, Name, DriverVersion FROM Win32_VideoController");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? instanceId = Text(item, "PNPDeviceID");
                    if (instanceId is null)
                    {
                        continue;
                    }

                    Match identifiers = PciIdentifiers().Match(instanceId);
                    adapters.Add(new GraphicsAdapterInventory
                    {
                        InstanceId = instanceId,
                        Name = Text(item, "Name"),
                        VendorId = identifiers.Success ? identifiers.Groups["ven"].Value.ToUpperInvariant() : null,
                        DeviceId = identifiers.Success ? identifiers.Groups["dev"].Value.ToUpperInvariant() : null,
                        DriverVersion = Text(item, "DriverVersion"),
                    });
                    if (adapters.Count >= InventoryLimits.MaximumEndpointsPerLane)
                    {
                        break;
                    }
                }
            }
        }
        catch (ManagementException exception)
        {
            collectionIssues.Add(new InventoryCollectionIssue
            {
                Lane = "graphics",
                Error = exception.ErrorCode.ToString(),
            });
        }
        catch (UnauthorizedAccessException)
        {
            collectionIssues.Add(new InventoryCollectionIssue
            {
                Lane = "graphics",
                Error = "AccessDenied",
            });
        }

        return [.. adapters.OrderBy(adapter => adapter.InstanceId, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<SerialEndpointInventory> CollectSerialEndpoints()
    {
        List<SerialEndpointInventory> endpoints = [];
        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\CIMV2",
                "SELECT DeviceID, PNPDeviceID, Name, Description, BaudRate, ByteSize, Parity, StopBits "
                    + "FROM Win32_SerialPort");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string instanceId = Text(item, "PNPDeviceID") ?? Text(item, "DeviceID") ?? "unknown-serial";
                    List<SerialFramingCandidate> candidates = [];
                    uint? baud = UInt32(item, "BaudRate");
                    byte? dataBits = Byte(item, "ByteSize");
                    byte? parity = Byte(item, "Parity");
                    byte? stopBits = Byte(item, "StopBits");
                    if (baud is not null || dataBits is not null || parity is not null || stopBits is not null)
                    {
                        candidates.Add(new SerialFramingCandidate
                        {
                            BaudRate = baud,
                            DataBits = dataBits,
                            Parity = parity,
                            StopBits = stopBits,
                            Source = "Win32_SerialPort-current-driver-state",
                        });
                    }

                    endpoints.Add(new SerialEndpointInventory
                    {
                        InstanceId = instanceId,
                        PortName = Text(item, "DeviceID"),
                        Name = Text(item, "Name"),
                        Manufacturer = Text(item, "Description"),
                        LocationPath = DeviceProperties.ResolveLocationPath(instanceId),
                        AssociationId = DeviceProperties.ResolveParentInstanceId(instanceId),
                        Present = true,
                        Access = InventoryAccess.Available,
                        FramingCandidates = candidates,
                    });
                    if (endpoints.Count >= InventoryLimits.MaximumEndpointsPerLane)
                    {
                        break;
                    }
                }
            }
        }
        catch (ManagementException exception)
        {
            if (exception.ErrorCode == ManagementStatus.AccessDenied)
            {
                endpoints.Add(new SerialEndpointInventory
                {
                    InstanceId = "serial-inventory",
                    Access = InventoryAccess.AccessDenied,
                });
            }
        }

        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\CIMV2",
                "SELECT DeviceID, Name, Manufacturer, Status FROM Win32_PnPEntity "
                    + "WHERE PNPClass = 'Ports'");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? instanceId = Text(item, "DeviceID");
                    if (instanceId is null)
                    {
                        continue;
                    }

                    string? name = Text(item, "Name");
                    bool present = string.Equals(Text(item, "Status"), "OK", StringComparison.OrdinalIgnoreCase);
                    int existingIndex = endpoints.FindIndex(endpoint => string.Equals(
                        endpoint.InstanceId,
                        instanceId,
                        StringComparison.OrdinalIgnoreCase));
                    if (existingIndex >= 0)
                    {
                        SerialEndpointInventory existing = endpoints[existingIndex];
                        endpoints[existingIndex] = existing with
                        {
                            Name = existing.Name ?? name,
                            Manufacturer = Text(item, "Manufacturer") ?? existing.Manufacturer,
                            Present = present,
                            Access = present ? existing.Access : InventoryAccess.Disconnected,
                        };
                    }
                    else
                    {
                        endpoints.Add(new SerialEndpointInventory
                        {
                            InstanceId = instanceId,
                            PortName = SerialPortName().Match(name ?? string.Empty) is { Success: true } match
                                ? match.Groups["port"].Value.ToUpperInvariant()
                                : null,
                            Name = name,
                            Manufacturer = Text(item, "Manufacturer"),
                            LocationPath = DeviceProperties.ResolveLocationPath(instanceId),
                            AssociationId = DeviceProperties.ResolveParentInstanceId(instanceId),
                            Present = present,
                            Access = present ? InventoryAccess.Available : InventoryAccess.Disconnected,
                        });
                    }

                    if (endpoints.Count >= InventoryLimits.MaximumEndpointsPerLane)
                    {
                        break;
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // Win32_SerialPort observations remain valid when the broader PnP lane is unavailable.
        }

        return [.. endpoints.OrderBy(endpoint => endpoint.InstanceId, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<SensorEndpointInventory> CollectSensors()
    {
        List<SensorEndpointInventory> sensors = [];
        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\CIMV2",
                "SELECT DeviceID, Name, PNPClass, Status FROM Win32_PnPEntity "
                    + "WHERE PNPClass = 'Sensor' OR PNPClass = 'HIDClass'");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? instanceId = Text(item, "DeviceID");
                    string? name = Text(item, "Name");
                    string? deviceClass = Text(item, "PNPClass");
                    bool namedSensor = name?.Contains("sensor", StringComparison.OrdinalIgnoreCase) is true
                        || name?.Contains("accelerometer", StringComparison.OrdinalIgnoreCase) is true
                        || name?.Contains("gyroscope", StringComparison.OrdinalIgnoreCase) is true
                        || name?.Contains("inclinometer", StringComparison.OrdinalIgnoreCase) is true;
                    if (instanceId is null || (!string.Equals(deviceClass, "Sensor", StringComparison.OrdinalIgnoreCase)
                        && !namedSensor))
                    {
                        continue;
                    }

                    bool controllerSensor = string.Equals(
                        deviceClass,
                        "HIDClass",
                        StringComparison.OrdinalIgnoreCase);
                    string? association = DeviceProperties.ResolveParentInstanceId(instanceId);

                    sensors.Add(new SensorEndpointInventory
                    {
                        InstanceId = instanceId,
                        Name = name,
                        Kind = deviceClass,
                        AssociationId = association,
                        Api = controllerSensor ? SensorApiKind.Controller : SensorApiKind.Pnp,
                        AssociationBasis = controllerSensor
                            ? "HID-controller-parent"
                            : "PnP-parent",
                        DeviceLevelLocationPath = DeviceProperties.ToDeviceLevelPath(
                            DeviceProperties.ResolveLocationPath(instanceId)),
                        Access = string.Equals(Text(item, "Status"), "OK", StringComparison.OrdinalIgnoreCase)
                            ? InventoryAccess.Available
                            : InventoryAccess.Disconnected,
                    });
                    if (sensors.Count >= InventoryLimits.MaximumEndpointsPerLane)
                    {
                        break;
                    }
                }
            }
        }
        catch (ManagementException exception)
        {
            if (exception.ErrorCode == ManagementStatus.AccessDenied)
            {
                sensors.Add(new SensorEndpointInventory
                {
                    InstanceId = "sensor-inventory",
                    Api = SensorApiKind.Pnp,
                    Access = InventoryAccess.AccessDenied,
                });
            }
        }

        sensors.AddRange(CollectWinRtSensors());
        return [.. sensors.OrderBy(sensor => sensor.Api)
            .ThenBy(sensor => sensor.InstanceId, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<SensorEndpointInventory> CollectWinRtSensors()
    {
        (string TypeName, string Kind, string Unit)[] definitions =
        [
            ("Windows.Devices.Sensors.Accelerometer", "accelerometer", "g"),
            ("Windows.Devices.Sensors.Gyrometer", "gyrometer", "degrees-per-second"),
            ("Windows.Devices.Sensors.Inclinometer", "inclinometer", "degrees"),
            ("Windows.Devices.Sensors.Compass", "compass", "degrees"),
            ("Windows.Devices.Sensors.OrientationSensor", "orientation", "quaternion"),
        ];
        List<SensorEndpointInventory> sensors = [];
        foreach ((string typeName, string kind, string unit) in definitions)
        {
            string endpointId = $"winrt:{kind}";
            try
            {
                // The type list and parameterless GetDefault member are closed here. Inventory never
                // reads a sensor sample, sets ReportInterval, or subscribes to ReadingChanged.
                Type? sensorType = Type.GetType(
                    $"{typeName}, Windows, ContentType=WindowsRuntime",
                    throwOnError: false);
                MethodInfo? getDefault = sensorType?.GetMethod(
                    "GetDefault",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);
                if (getDefault is null)
                {
                    sensors.Add(new SensorEndpointInventory
                    {
                        InstanceId = endpointId,
                        Kind = kind,
                        Api = SensorApiKind.WinRt,
                        Unit = unit,
                        Access = InventoryAccess.Unsupported,
                    });
                    continue;
                }

                object? sensor = getDefault.Invoke(null, null);
                if (sensor is null)
                {
                    sensors.Add(new SensorEndpointInventory
                    {
                        InstanceId = endpointId,
                        Kind = kind,
                        Api = SensorApiKind.WinRt,
                        Unit = unit,
                        Access = InventoryAccess.Disconnected,
                    });
                    continue;
                }

                string? deviceId = sensorType!.GetProperty("DeviceId")?.GetValue(sensor)?.ToString();
                object? intervalValue = sensorType.GetProperty("MinimumReportInterval")?.GetValue(sensor);
                uint? interval = intervalValue is null
                    ? null
                    : Convert.ToUInt32(intervalValue, CultureInfo.InvariantCulture);
                sensors.Add(new SensorEndpointInventory
                {
                    InstanceId = deviceId ?? endpointId,
                    Kind = kind,
                    AssociationId = deviceId,
                    Api = SensorApiKind.WinRt,
                    AssociationBasis = deviceId is null ? null : "WinRT-DeviceId",
                    MinimumReportIntervalMilliseconds = interval,
                    SupportedReportIntervalsMilliseconds = interval is { } value ? [value] : [],
                    Unit = unit,
                    Access = InventoryAccess.Available,
                });
            }
            catch (TargetInvocationException exception)
                when (exception.InnerException is UnauthorizedAccessException)
            {
                sensors.Add(new SensorEndpointInventory
                {
                    InstanceId = endpointId,
                    Kind = kind,
                    Api = SensorApiKind.WinRt,
                    Unit = unit,
                    Access = InventoryAccess.AccessDenied,
                });
            }
            catch (UnauthorizedAccessException)
            {
                sensors.Add(new SensorEndpointInventory
                {
                    InstanceId = endpointId,
                    Kind = kind,
                    Api = SensorApiKind.WinRt,
                    Unit = unit,
                    Access = InventoryAccess.AccessDenied,
                });
            }
            catch (TargetInvocationException)
            {
                sensors.Add(new SensorEndpointInventory
                {
                    InstanceId = endpointId,
                    Kind = kind,
                    Api = SensorApiKind.WinRt,
                    Unit = unit,
                    Access = InventoryAccess.Unsupported,
                });
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException
                or NotSupportedException
                or TypeLoadException
                or AmbiguousMatchException
                or InvalidCastException
                or FormatException
                or OverflowException)
            {
                sensors.Add(new SensorEndpointInventory
                {
                    InstanceId = endpointId,
                    Kind = kind,
                    Api = SensorApiKind.WinRt,
                    Unit = unit,
                    Access = InventoryAccess.Unsupported,
                });
            }
        }

        return sensors;
    }

    private static IReadOnlyList<InputBackendInventory> CollectInputBackends()
    {
        return
        [
            CollectXInput(),
            CollectDirectInputView(),
            CollectSdlView(),
            CollectRawInput(),
            CollectRawHidView(),
        ];
    }

    private static InputBackendInventory CollectXInput()
    {
        List<InputEndpointInventory> endpoints = [];
        try
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                uint result = XInputGetCapabilities(slot, 0, out XInputCapabilities capabilities);
                if (result == ErrorSuccess)
                {
                    endpoints.Add(new InputEndpointInventory
                    {
                        EndpointId = $"xinput:{slot}",
                        Name = "XInput controller",
                        DeviceType = $"{capabilities.Type:x2}:{capabilities.SubType:x2}",
                        Connected = true,
                    });
                }
                else if (result != ErrorDeviceNotConnected)
                {
                    return new InputBackendInventory
                    {
                        Backend = InputBackendKind.XInput,
                        Access = InventoryAccess.AccessDenied,
                        View = InputBackendViewKind.LiveApi,
                        RuntimeAvailable = true,
                        Endpoints = endpoints,
                        Limitation = $"XInputGetCapabilities returned {result}.",
                    };
                }
            }
        }
        catch (DllNotFoundException)
        {
            return UnavailableBackend(InputBackendKind.XInput, "The system XInput runtime was unavailable.");
        }
        catch (EntryPointNotFoundException)
        {
            return UnavailableBackend(InputBackendKind.XInput, "The system XInput entry point was unavailable.");
        }

        return new InputBackendInventory
        {
            Backend = InputBackendKind.XInput,
            Access = InventoryAccess.Available,
            View = InputBackendViewKind.LiveApi,
            RuntimeAvailable = true,
            Endpoints = endpoints,
            Limitation = "XInput exposes slots, not stable physical device identities.",
        };
    }

    private static InputBackendInventory CollectDirectInputView() => CollectPnpInputView(
        InputBackendKind.DirectInput,
        static (name, service) => ContainsAny(name, "joystick", "game controller", "gamepad")
            || ContainsAny(service, "gameinput", "xusb"),
        "This passive compatibility view does not instantiate DirectInput or acquire a device.");

    private static InputBackendInventory CollectRawHidView() => CollectPnpInputView(
        InputBackendKind.RawHid,
        static (_, service) => string.Equals(service, "HidUsb", StringComparison.OrdinalIgnoreCase),
        "PnP HID presence does not prove a report descriptor or exclusive-open policy.");

    private static InputBackendInventory CollectPnpInputView(
        InputBackendKind backend,
        Func<string?, string?, bool> predicate,
        string limitation)
    {
        List<InputEndpointInventory> endpoints = [];
        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\CIMV2",
                "SELECT DeviceID, Name, Service, Status FROM Win32_PnPEntity WHERE PNPClass = 'HIDClass'");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? instanceId = Text(item, "DeviceID");
                    string? name = Text(item, "Name");
                    string? service = Text(item, "Service");
                    if (instanceId is null || !predicate(name, service))
                    {
                        continue;
                    }

                    endpoints.Add(new InputEndpointInventory
                    {
                        EndpointId = instanceId,
                        InstanceId = instanceId,
                        Name = name,
                        DeviceType = service,
                        Access = string.Equals(Text(item, "Status"), "OK", StringComparison.OrdinalIgnoreCase)
                            ? InventoryAccess.Available
                            : InventoryAccess.Disconnected,
                        VendorId = UsbIdentifiers().Match(instanceId) is { Success: true } ids
                            ? ids.Groups["vid"].Value.ToUpperInvariant()
                            : null,
                        ProductId = UsbIdentifiers().Match(instanceId) is { Success: true } productIds
                            ? productIds.Groups["pid"].Value.ToUpperInvariant()
                            : null,
                        AssociationId = DeviceProperties.ToDeviceLevelPath(
                            DeviceProperties.ResolveLocationPath(instanceId)),
                        DescriptorAccess = InventoryAccess.Unsupported,
                        Connected = string.Equals(Text(item, "Status"), "OK", StringComparison.OrdinalIgnoreCase),
                    });
                    if (endpoints.Count >= InventoryLimits.MaximumEndpointsPerLane)
                    {
                        break;
                    }
                }
            }
        }
        catch (ManagementException exception)
        {
            return new InputBackendInventory
            {
                Backend = backend,
                Access = exception.ErrorCode == ManagementStatus.AccessDenied
                    ? InventoryAccess.AccessDenied
                    : InventoryAccess.Unsupported,
                View = InputBackendViewKind.PassiveCompatibility,
                RuntimeAvailable = BackendRuntimeAvailable(backend),
                Limitation = limitation,
            };
        }

        return new InputBackendInventory
        {
            Backend = backend,
            Access = InventoryAccess.Available,
            View = InputBackendViewKind.PassiveCompatibility,
            RuntimeAvailable = BackendRuntimeAvailable(backend),
            Endpoints = [.. endpoints.OrderBy(endpoint => endpoint.EndpointId, StringComparer.Ordinal)],
            Limitation = limitation,
        };
    }

    private static InputBackendInventory CollectSdlView()
    {
        string appLocal = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
        string? path = File.Exists(appLocal) ? appLocal : null;
        return new InputBackendInventory
        {
            Backend = InputBackendKind.Sdl,
            Access = InventoryAccess.Unsupported,
            View = InputBackendViewKind.RuntimeOnly,
            RuntimeAvailable = path is not null,
            Limitation = path is null
                ? "SDL3.dll is not installed beside Device Lab; no runtime was loaded."
                : "SDL is present but Device Lab does not initialize its gamepad subsystem during inventory.",
        };
    }

    private static unsafe InputBackendInventory CollectRawInput()
    {
        uint count = 0;
        uint structureSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, structureSize) == uint.MaxValue)
        {
            return UnavailableBackend(InputBackendKind.RawInput, "Raw Input device count was unavailable.");
        }
        if (count > InventoryLimits.MaximumEndpointsPerLane)
        {
            return new InputBackendInventory
            {
                Backend = InputBackendKind.RawInput,
                Access = InventoryAccess.Malformed,
                View = InputBackendViewKind.LiveApi,
                RuntimeAvailable = true,
                Limitation = "Raw Input reported more devices than the bounded inventory accepts.",
            };
        }

        RawInputDeviceList[] devices = new RawInputDeviceList[count];
        uint enumerationResult = 0;
        if (count != 0)
        {
            fixed (RawInputDeviceList* devicesPointer = devices)
            {
                enumerationResult = GetRawInputDeviceList(devicesPointer, ref count, structureSize);
            }
        }
        if (enumerationResult == uint.MaxValue)
        {
            return UnavailableBackend(InputBackendKind.RawInput, "Raw Input device enumeration failed.");
        }

        List<InputEndpointInventory> endpoints = [];
        for (int index = 0; index < count; index++)
        {
            uint characters = 0;
            _ = GetRawInputDeviceInfo(devices[index].Device, RidiDeviceName, null, ref characters);
            if (characters > InventoryLimits.MaximumTextCharacters)
            {
                endpoints.Add(new InputEndpointInventory
                {
                    EndpointId = $"rawinput:{index}",
                    DeviceType = "malformed-name",
                    Access = InventoryAccess.Malformed,
                    DescriptorAccess = InventoryAccess.Malformed,
                    Connected = true,
                });
                continue;
            }
            char[] name = new char[Math.Max(characters, 1)];
            uint nameResult;
            fixed (char* namePointer = name)
            {
                nameResult = GetRawInputDeviceInfo(
                    devices[index].Device,
                    RidiDeviceName,
                    namePointer,
                    ref characters);
            }

            int terminator = Array.IndexOf(name, '\0');
            int nameLength = terminator >= 0 ? terminator : Math.Min((int)characters, name.Length);
            string? deviceName = nameResult == uint.MaxValue ? null : new string(name, 0, nameLength);
            string? instanceId = CanonicalRawInputInstance(deviceName);
            endpoints.Add(new InputEndpointInventory
            {
                EndpointId = string.Empty,
                InstanceId = instanceId,
                Name = deviceName,
                Access = nameResult == uint.MaxValue
                    ? InventoryAccess.AccessDenied
                    : InventoryAccess.Available,
                DescriptorAccess = InventoryAccess.Unsupported,
                DeviceType = devices[index].Type switch
                {
                    RimTypeMouse => "mouse",
                    RimTypeKeyboard => "keyboard",
                    RimTypeHid => "hid",
                    _ => $"type-{devices[index].Type}",
                },
                VendorId = instanceId is null || UsbIdentifiers().Match(instanceId) is not { Success: true } ids
                    ? null
                    : ids.Groups["vid"].Value.ToUpperInvariant(),
                ProductId = instanceId is null || UsbIdentifiers().Match(instanceId) is not { Success: true } products
                    ? null
                    : products.Groups["pid"].Value.ToUpperInvariant(),
                AssociationId = instanceId is null
                    ? null
                    : DeviceProperties.ToDeviceLevelPath(
                        DeviceProperties.ResolveLocationPath(instanceId)),
                Connected = true,
            });
        }

        InputEndpointInventory[] ordered = endpoints
            .OrderBy(endpoint => endpoint.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.DeviceType, StringComparer.Ordinal)
            .Select((endpoint, index) => endpoint with { EndpointId = $"rawinput:{index}" })
            .ToArray();

        return new InputBackendInventory
        {
            Backend = InputBackendKind.RawInput,
            Access = InventoryAccess.Available,
            View = InputBackendViewKind.LiveApi,
            RuntimeAvailable = true,
            Endpoints = ordered,
            Limitation = "Raw Input names are session observations and do not prove exclusive ownership.",
        };
    }

    private static InputBackendInventory UnavailableBackend(InputBackendKind backend, string limitation) => new()
    {
        Backend = backend,
        Access = InventoryAccess.Unsupported,
        View = InputBackendViewKind.LiveApi,
        Limitation = limitation,
    };

    private static bool BackendRuntimeAvailable(InputBackendKind backend) => backend switch
    {
        InputBackendKind.RawHid => true,
        InputBackendKind.DirectInput => File.Exists(Path.Combine(Environment.SystemDirectory, "dinput8.dll")),
        _ => false,
    };

    private static string? CanonicalRawInputInstance(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        string value = deviceName.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? deviceName[4..]
            : deviceName;
        int classGuid = value.LastIndexOf("#{", StringComparison.Ordinal);
        if (classGuid >= 0)
        {
            value = value[..classGuid];
        }

        return value.Replace('#', '\\');
    }

    private static IReadOnlyList<ProcessInventory> CollectRelevantProcesses()
    {
        Dictionary<int, string?> commandLines = QueryProcessCommandLines();
        List<ProcessInventory> observations = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (!IsRelevant(name))
                {
                    continue;
                }

                string? path = null;
                List<string> modules = [];
                InventoryAccess access = InventoryAccess.Available;
                try
                {
                    path = process.MainModule?.FileName;
                    foreach (ProcessModule module in process.Modules)
                    {
                        if (IsRelevant(module.ModuleName))
                        {
                            modules.Add(module.FileName);
                            if (modules.Count >= InventoryLimits.MaximumEndpointsPerLane)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Access is represented by missing optional fields; process presence remains useful.
                    access = InventoryAccess.AccessDenied;
                }

                observations.Add(new ProcessInventory
                {
                    ProcessId = process.Id,
                    Name = name,
                    Access = access,
                    Path = path,
                    CommandLine = commandLines.GetValueOrDefault(process.Id),
                    LoadedModulePaths = [.. modules.Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(module => module, StringComparer.OrdinalIgnoreCase)],
                });
                if (observations.Count >= InventoryLimits.MaximumSystemEntriesPerLane)
                {
                    break;
                }
            }
        }

        return [.. observations.OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.ProcessId)];
    }

    private static Dictionary<int, string?> QueryProcessCommandLines()
    {
        Dictionary<int, string?> lines = [];
        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\CIMV2",
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    if (IsRelevant(Text(item, "Name"))
                        && int.TryParse(Text(item, "ProcessId"), CultureInfo.InvariantCulture, out int processId))
                    {
                        lines[processId] = Text(item, "CommandLine");
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // Process enumeration does not depend on command-line access.
        }

        return lines;
    }

    private static IReadOnlyList<ServiceInventory> CollectRelevantServices()
    {
        List<ServiceInventory> services = [];
        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\CIMV2",
                "SELECT Name, DisplayName, State, PathName, ProcessId FROM Win32_Service");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? name = Text(item, "Name");
                    string? displayName = Text(item, "DisplayName");
                    string? path = Text(item, "PathName");
                    if (name is null || !(IsRelevant(name) || IsRelevant(displayName) || IsRelevant(path)))
                    {
                        continue;
                    }

                    services.Add(new ServiceInventory
                    {
                        Name = name,
                        Access = InventoryAccess.Available,
                        DisplayName = displayName,
                        State = Text(item, "State"),
                        PathName = path,
                        ProcessId = int.TryParse(Text(item, "ProcessId"), CultureInfo.InvariantCulture, out int id)
                            && id != 0
                                ? id
                                : null,
                    });
                    if (services.Count >= InventoryLimits.MaximumSystemEntriesPerLane)
                    {
                        break;
                    }
                }
            }
        }
        catch (ManagementException exception)
        {
            if (exception.ErrorCode == ManagementStatus.AccessDenied)
            {
                services.Add(new ServiceInventory
                {
                    Name = "service-inventory",
                    Access = InventoryAccess.AccessDenied,
                });
            }
        }

        return [.. services.OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<ScheduledTaskInventory> CollectRelevantScheduledTasks()
    {
        List<ScheduledTaskInventory> tasks = [];
        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\Microsoft\\Windows\\TaskScheduler",
                "SELECT TaskName, TaskPath, State, Enabled FROM MSFT_ScheduledTask");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? name = Text(item, "TaskName");
                    string? path = Text(item, "TaskPath");
                    if (!(IsRelevant(name) || IsRelevant(path)))
                    {
                        continue;
                    }

                    tasks.Add(new ScheduledTaskInventory
                    {
                        Path = $"{path}{name}",
                        Access = InventoryAccess.Available,
                        State = Text(item, "State"),
                        Enabled = bool.TryParse(Text(item, "Enabled"), out bool enabled) ? enabled : null,
                    });
                    if (tasks.Count >= InventoryLimits.MaximumSystemEntriesPerLane)
                    {
                        break;
                    }
                }
            }
        }
        catch (ManagementException exception)
        {
            tasks.Add(new ScheduledTaskInventory
            {
                Path = "task-inventory",
                Access = exception.ErrorCode == ManagementStatus.AccessDenied
                    ? InventoryAccess.AccessDenied
                    : InventoryAccess.Unsupported,
            });
        }

        return [.. tasks.OrderBy(task => task.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<NativeBinaryInventory> CollectNativeBinaries(
        IReadOnlyList<ProcessInventory> processes,
        IReadOnlyList<ServiceInventory> services)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string systemDirectory = Environment.SystemDirectory;
        foreach (string name in new[] { "xinput1_4.dll", "hid.dll", "setupapi.dll", "cfgmgr32.dll" })
        {
            paths.Add(Path.Combine(systemDirectory, name));
        }

        foreach (ProcessInventory process in processes)
        {
            if (process.Path is not null)
            {
                paths.Add(process.Path);
            }

            foreach (string module in process.LoadedModulePaths)
            {
                if (IsRelevant(Path.GetFileName(module)))
                {
                    paths.Add(module);
                }
            }
        }

        foreach (ServiceInventory service in services)
        {
            string? executable = ExtractExecutablePath(service.PathName);
            if (executable is not null)
            {
                paths.Add(executable);
            }
        }

        List<NativeBinaryInventory> binaries = [];
        foreach (string path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            NativeBinaryInventory binary = NativePeInspector.Inspect(path);
            binaries.Add(binary);
            if (binaries.Count >= InventoryLimits.MaximumSystemEntriesPerLane)
            {
                break;
            }
        }

        return binaries;
    }

    private static IReadOnlyList<ProviderInventory> CollectRelevantProviders(
        IReadOnlyList<ProcessInventory> processes)
    {
        List<ProviderInventory> providers = [];
        try
        {
            using ManagementObjectSearcher searcher = CreateSearcher(
                "root\\CIMV2",
                "SELECT Namespace, Provider, HostProcessIdentifier FROM MSFT_Providers");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? name = Text(item, "Provider");
                    string? context = Text(item, "Namespace");
                    if (name is null || !(IsRelevant(name) || IsRelevant(context)))
                    {
                        continue;
                    }

                    providers.Add(new ProviderInventory
                    {
                        Kind = "WMI-loaded-provider",
                        Name = name,
                        Context = context,
                        HostProcessId = int.TryParse(
                            Text(item, "HostProcessIdentifier"),
                            CultureInfo.InvariantCulture,
                            out int processId)
                                ? processId
                                : null,
                        Loaded = true,
                        Access = InventoryAccess.Available,
                    });
                    if (providers.Count >= InventoryLimits.MaximumSystemEntriesPerLane)
                    {
                        break;
                    }
                }
            }
        }
        catch (ManagementException exception)
        {
            if (exception.ErrorCode == ManagementStatus.AccessDenied)
            {
                providers.Add(new ProviderInventory
                {
                    Kind = "WMI-loaded-provider-lane",
                    Name = "provider-inventory",
                    Loaded = false,
                    Access = InventoryAccess.AccessDenied,
                });
            }
        }

        foreach (ProcessInventory process in processes)
        {
            foreach (string module in process.LoadedModulePaths.Where(module =>
                IsRelevant(Path.GetFileName(module))))
            {
                providers.Add(new ProviderInventory
                {
                    Kind = "process-loaded-module",
                    Name = Path.GetFileName(module),
                    Context = process.Name,
                    HostProcessId = process.ProcessId,
                    ModulePath = module,
                    Loaded = true,
                    Access = process.Access,
                });
                if (providers.Count >= InventoryLimits.MaximumSystemEntriesPerLane)
                {
                    break;
                }
            }
            if (providers.Count >= InventoryLimits.MaximumSystemEntriesPerLane)
            {
                break;
            }
        }

        return providers
            .GroupBy(provider =>
                $"{provider.Kind}\0{provider.Name}\0{provider.Context}\0{provider.HostProcessId}\0{provider.ModulePath}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(provider => provider.Kind, StringComparer.Ordinal)
            .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.Context, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ResourceConflictInventory> DeriveResourceConflicts(
        IReadOnlyList<ProcessInventory> processes,
        IReadOnlyList<ServiceInventory> services,
        IReadOnlyList<NativeBinaryInventory> nativeBinaries)
    {
        List<ResourceConflictInventory> conflicts = [];
        foreach (string owner in processes.Select(process => process.Name)
            .Concat(services.Select(service => service.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? resource = owner.Contains("hidhide", StringComparison.OrdinalIgnoreCase)
                || owner.Contains("hidmaestro", StringComparison.OrdinalIgnoreCase)
                ? "controller-routing"
                : owner.Contains("msi", StringComparison.OrdinalIgnoreCase)
                    || owner.Contains("center", StringComparison.OrdinalIgnoreCase)
                    || owner.Contains("handheld", StringComparison.OrdinalIgnoreCase)
                    ? "vendor-control"
                    : null;
            if (resource is not null)
            {
                conflicts.Add(new ResourceConflictInventory
                {
                    ResourceId = resource,
                    Owner = owner,
                    Signal = ConflictSignalKind.PresenceOnly,
                });
            }
        }

        foreach (NativeBinaryInventory binary in nativeBinaries.Where(binary =>
            binary.Access is InventoryAccess.ExclusiveAccessDenied))
        {
            conflicts.Add(new ResourceConflictInventory
            {
                ResourceId = $"native-file:{binary.Name}",
                Owner = "unidentified-holder",
                Signal = ConflictSignalKind.ExclusiveAccessDenied,
            });
        }

        return [.. conflicts.OrderBy(conflict => conflict.ResourceId, StringComparer.Ordinal)
            .ThenBy(conflict => conflict.Owner, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsRelevant(string? value) => value is not null
        && RelevantNameFragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string? value, params string[] fragments) => value is not null
        && fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static uint? UInt32(ManagementBaseObject source, string property) =>
        uint.TryParse(Text(source, property), CultureInfo.InvariantCulture, out uint value) ? value : null;

    private static byte? Byte(ManagementBaseObject source, string property) =>
        byte.TryParse(Text(source, property), CultureInfo.InvariantCulture, out byte value) ? value : null;

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string trimmed = Environment.ExpandEnvironmentVariables(command.Trim());
        if (trimmed[0] == '"')
        {
            int close = trimmed.IndexOf('"', 1);
            return close > 1 ? trimmed[1..close] : null;
        }

        int exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? trimmed[..(exe + 4)] : null;
    }

    [GeneratedRegex(@"VEN_(?<ven>[0-9A-Fa-f]{4})&DEV_(?<dev>[0-9A-Fa-f]{4})")]
    private static partial Regex PciIdentifiers();

    [GeneratedRegex(@"\((?<port>COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex SerialPortName();

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilities
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputVibration Vibration;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public nint Device;
        public uint Type;
    }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetCapabilities(
        uint userIndex,
        uint flags,
        out XInputCapabilities capabilities);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceList")]
    private static unsafe partial uint GetRawInputDeviceList(
        RawInputDeviceList* rawInputDeviceList,
        ref uint numberOfDevices,
        uint size);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static unsafe partial uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        char* data,
        ref uint size);
}
