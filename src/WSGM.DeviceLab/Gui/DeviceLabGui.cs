using Avalonia;

namespace WSGM.DeviceLab.Gui;

internal static class DeviceLabGui
{
    internal static int Run(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    private static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
