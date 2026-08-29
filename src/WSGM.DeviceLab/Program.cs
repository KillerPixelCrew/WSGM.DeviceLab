using System;
using WSGM.DeviceLab.Cli;
using WSGM.DeviceLab.Gui;
using WSGM.DeviceLab.Probes;

namespace WSGM.DeviceLab;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return DeviceLabGui.Run([]);
        }

        if (string.Equals(args[0], "gui", StringComparison.Ordinal))
        {
            return DeviceLabGui.Run(args[1..]);
        }

        if (string.Equals(args[0], ReadProbeWorker.Mode, StringComparison.Ordinal))
        {
            return ReadProbeWorker.Run(args[1..]);
        }

        return DeviceLabCli.RunAsync(args).GetAwaiter().GetResult();
    }
}
