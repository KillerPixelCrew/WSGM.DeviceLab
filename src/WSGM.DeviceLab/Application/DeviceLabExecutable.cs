using System;
using System.IO;

namespace WSGM.DeviceLab.Application;

internal static class DeviceLabExecutable
{
    internal static string CurrentPath
    {
        get
        {
            string appHost = Path.Combine(AppContext.BaseDirectory, "wsgm-device.exe");
            if (File.Exists(appHost))
            {
                return Path.GetFullPath(appHost);
            }

            if (Environment.ProcessPath is { Length: > 0 } processPath && File.Exists(processPath))
            {
                return Path.GetFullPath(processPath);
            }

            throw new FileNotFoundException("The Device Lab executable path is unavailable.");
        }
    }
}
