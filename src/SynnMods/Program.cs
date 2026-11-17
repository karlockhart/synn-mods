using Velopack;

namespace SynnMods;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack's install/update/uninstall hooks and exits when it's one of them.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow(ParseCatalogArg(args)));
    }

    /// <summary>--catalog &lt;folder&gt; (or SYNNMODS_CATALOG) reads games and mods from a local checkout.</summary>
    private static string? ParseCatalogArg(string[] args)
    {
        var i = Array.IndexOf(args, "--catalog");
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];
        return Environment.GetEnvironmentVariable("SYNNMODS_CATALOG");
    }
}
