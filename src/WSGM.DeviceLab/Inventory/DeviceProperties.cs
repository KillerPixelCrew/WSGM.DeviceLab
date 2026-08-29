using System;
using System.Runtime.InteropServices;

namespace WSGM.DeviceLab.Inventory;

/// <summary>
/// Reads PnP device properties through the configuration manager.
/// </summary>
/// <remarks>
/// <c>cfgmgr32</c> rather than WMI, for two reasons. It is the API device properties actually belong
/// to — <c>Win32_PnPEntity</c> exposes them only through a <c>GetDeviceProperties</c> method call,
/// which costs a WMI method invocation per device per property and would make a full sweep of a
/// machine's several hundred device nodes slow enough to notice. And the parent walk below needs
/// <c>CM_Get_Parent</c>, which has no WMI equivalent at all.
/// </remarks>
internal static partial class DeviceProperties
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const int CmLocateDevnodeNormal = 0;

    /// <summary>
    /// Physical location paths of a device. Multi-valued; the first entry is the
    /// <c>PCIROOT(...)</c> form.
    /// </summary>
    private static readonly DevPropKey LocationPaths = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 37);

    /// <summary>
    /// Resolves a device's location path, walking to its parent when the device has none.
    /// </summary>
    /// <param name="instanceId">Device instance identifier.</param>
    /// <param name="maxDepth">How far up the parent chain to look.</param>
    /// <returns>The first location path found, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The walk exists because of a measured fact rather than caution. On the reference unit,
    /// <c>DEVPKEY_Device_LocationPaths</c> is empty on every HID child —
    /// <c>HID\VID_0DB0&amp;PID_1901&amp;IG_00\…</c> and its siblings — and first appears two links up,
    /// on the USB interface. Since a plugin acquires HID interfaces rather than the composite parent,
    /// reading the property off the device in hand returns nothing, and the continuation key the
    /// whole hotplug design depends on would silently be null.
    /// </remarks>
    public static string? ResolveLocationPath(string instanceId, int maxDepth = 8)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CmLocateDevnodeNormal) != CrSuccess)
        {
            return null;
        }

        for (int depth = 0; depth < maxDepth; depth++)
        {
            if (ReadStringList(devInst) is { Length: > 0 } paths)
            {
                return paths[0];
            }

            if (CM_Get_Parent(out uint parent, devInst, 0) != CrSuccess)
            {
                return null;
            }

            devInst = parent;
        }

        return null;
    }

    /// <summary>Resolves the immediate parent PnP instance identifier without opening the device.</summary>
    /// <param name="instanceId">Child PnP instance identifier.</param>
    /// <returns>Parent instance identifier, or <see langword="null"/> when unavailable.</returns>
    public static string? ResolveParentInstanceId(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint child, instanceId, CmLocateDevnodeNormal) != CrSuccess
            || CM_Get_Parent(out uint parent, child, 0) != CrSuccess)
        {
            return null;
        }

        int characters = 0;
        int result = CM_Get_Device_ID_Size(out characters, parent, 0);
        if (result != CrSuccess
            || characters <= 0
            || characters > InventoryLimits.MaximumTextCharacters)
        {
            return null;
        }

        char[] buffer = new char[characters + 1];
        return CM_Get_Device_IDW(parent, buffer, buffer.Length, 0) == CrSuccess
            ? new string(buffer, 0, characters)
            : null;
    }

    /// <summary>
    /// Reduces an interface-level location path to the composite device it belongs to.
    /// </summary>
    /// <param name="locationPath">A path as returned by <see cref="ResolveLocationPath"/>.</param>
    /// <returns>The path with any trailing interface component removed.</returns>
    /// <remarks>
    /// A resolved HID interface yields <c>…#USB(2)#USBMI(0)</c>, where the trailing component names
    /// which interface of the composite device it is. That extra precision is a hazard for the one
    /// job the location path exists to do.
    /// <para>
    /// Continuation has to survive a controller mode switch, and a mode switch changes the interface
    /// layout: the gamepad appears as an XInput interface in one mode and a DirectInput one in the
    /// other. The interface index is therefore not established as stable across the very event this
    /// key must tolerate, while the composite-level prefix was verified byte-identical across a full
    /// switch-and-restore cycle. Key on the prefix; keep the full path as an observation.
    /// </para>
    /// </remarks>
    public static string? ToDeviceLevelPath(string? locationPath)
    {
        if (string.IsNullOrEmpty(locationPath))
        {
            return null;
        }

        int interfaceMarker = locationPath.IndexOf("#USBMI(", StringComparison.OrdinalIgnoreCase);
        return interfaceMarker < 0 ? locationPath : locationPath[..interfaceMarker];
    }

    private static string[]? ReadStringList(uint devInst)
    {
        int size = 0;
        int result = CM_Get_DevNode_PropertyW(devInst, in LocationPaths, out _, null, ref size, 0);

        // An absent property reports success with a zero size rather than an error, so the size is
        // the presence test and a non-buffer-small result means there is nothing to read.
        if (result != CrBufferSmall
            || size <= 0
            || size > InventoryLimits.MaximumTextCharacters * sizeof(char))
        {
            return null;
        }

        byte[] buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, in LocationPaths, out _, buffer, ref size, 0) != CrSuccess)
        {
            return null;
        }

        // REG_MULTI_SZ layout: consecutive null-terminated UTF-16 strings, terminated by an empty one.
        string raw = System.Text.Encoding.Unicode.GetString(buffer, 0, size);
        return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        private readonly Guid _formatId = formatId;
        private readonly uint _propertyId = propertyId;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, int ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, int ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Device_ID_Size(out int pulLen, uint dnDevInst, int ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_IDW(
        uint dnDevInst,
        [Out] char[] buffer,
        int bufferLength,
        int ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        in DevPropKey propertyKey,
        out uint propertyType,
        [Out] byte[]? propertyBuffer,
        ref int propertyBufferSize,
        int ulFlags);
}
