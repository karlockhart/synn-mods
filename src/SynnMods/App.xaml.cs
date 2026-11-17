using System.Windows;
using System.Windows.Threading;

namespace SynnMods;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"Synn Mods {AppPaths.AppVersion} starting");
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in {AppPaths.LogFile}",
            "Synn Mods", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
