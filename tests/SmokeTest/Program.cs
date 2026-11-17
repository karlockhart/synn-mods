using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SynnMods;

/// <summary>
/// Offline smoke test: builds a throwaway catalog, game folder and data folder under %TEMP%, then
/// checks install / update / bad-hash / unsafe-zip / remove, and renders the main window to a PNG.
/// Never touches the real game folder or %APPDATA%\SynnMods.
/// Usage: dotnet run --project tests/SmokeTest -- [out.png] [folder with built mods]
/// </summary>
public static class SmokeTest
{
    private static int _failures;

    [STAThread]
    public static int Main(string[] args)
    {
        var work = Path.Combine(Path.GetTempPath(), "synnmods-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        // Must be set before anything touches AppPaths.
        Environment.SetEnvironmentVariable("SYNNMODS_DATA_DIR", Path.Combine(work, "data"));
        var png = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(work, "window.png");
        var source = args.Length > 1 ? args[1]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Core Keeper\CoreKeeper_Data\StreamingAssets\Mods";
        try
        {
            Run(work, png, source);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL (crash): {ex}");
            _failures++;
        }
        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        try { Directory.Delete(work, true); } catch { /* best effort */ }
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string work, string png, string source)
    {
        var repo = FindRepoRoot();
        var catalogDir = Path.Combine(work, "catalog");
        var gameCat = Path.Combine(catalogDir, "corekeeper");
        Directory.CreateDirectory(gameCat);
        Directory.CreateDirectory(Path.Combine(catalogDir, "notagame"));
        File.Copy(Path.Combine(repo, "corekeeper", "game.json"), Path.Combine(gameCat, "game.json"));

        // Packages built from read-only copies of the real mods.
        var pkgs = Path.Combine(work, "pkgs");
        Directory.CreateDirectory(pkgs);
        var bumZip = Zip(Path.Combine(source, "BumBum"), Path.Combine(pkgs, "BumBum.zip"));
        var ghostZip = Zip(Path.Combine(source, "GhostMommy"), Path.Combine(pkgs, "GhostMommy.zip"));
        var evilZip = Path.Combine(pkgs, "evil.zip");
        using (var z = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("ModManifest.json").Open())) w.Write("{\"name\":\"BumBum\"}");
            using (var w = new StreamWriter(z.CreateEntry("../../evil.txt").Open())) w.Write("pwned");
        }

        ModEntry Entry(string id, string zip, string version, string? sha = null, long? size = null) => new()
        {
            Id = id, Name = id, Version = version, Url = new Uri(zip).AbsoluteUri,
            Sha256 = sha ?? Sha(zip), Size = size ?? new FileInfo(zip).Length,
        };

        // ---- game folder detection ----
        var game = Json.Parse<GameInfo>(File.ReadAllText(Path.Combine(gameCat, "game.json")));
        var client = game.Targets.Single(t => t.Id == "client");
        var root = Path.Combine(work, "Core Keeper");
        Directory.CreateDirectory(Path.Combine(root, "CoreKeeper_Data", "StreamingAssets"));
        Check("Check: folder without CoreKeeper.exe is rejected", !GameFolder.Check(client, root).Ok);
        File.WriteAllText(Path.Combine(root, "CoreKeeper.exe"), "");
        Check("Check: game folder is accepted", GameFolder.Check(client, root).Ok);
        Check("Normalize: game folder", GameFolder.Normalize(client, root) == root);
        Check("Normalize: Mods folder", GameFolder.Normalize(client, Path.Combine(root, "CoreKeeper_Data", "StreamingAssets", "Mods")) == root);
        Check("Normalize: data folder", GameFolder.Normalize(client, Path.Combine(root, "CoreKeeper_Data")) == root);
        var modsDir = GameFolder.ModsDir(client, root);
        // Read-only: just reports what Steam detection finds on this PC.
        foreach (var t in game.Targets)
            Console.WriteLine($"info: Steam detection for {t.Label}: {SteamLocator.FindGame(t) ?? "(not installed)"}");

        // ---- installer ----
        var records = InstallRecords.Load();
        var installer = new ModInstaller(records);
        var progress = new Progress<(string, double?)>(_ => { });
        var ct = CancellationToken.None;

        installer.InstallAsync(modsDir, Entry("BumBum", bumZip, "0.9.0"), progress, ct).GetAwaiter().GetResult();
        Check("Install: ModManifest.json in place", File.Exists(Path.Combine(modsDir, "BumBum", "ModManifest.json")));
        Check("Install: version recorded", records.Get(modsDir, "BumBum")?.Version == "0.9.0");

        installer.InstallAsync(modsDir, Entry("BumBum", bumZip, "1.0.0"), progress, ct).GetAwaiter().GetResult();
        Check("Update: version recorded", records.Get(modsDir, "BumBum")?.Version == "1.0.0");
        Check("Update: no leftover temp folders", !Directory.EnumerateDirectories(modsDir, ".synnmods-*").Any());

        Throws<InvalidDataException>("Bad SHA-256 is refused",
            () => installer.InstallAsync(modsDir, Entry("BumBum", bumZip, "2.0.0", sha: new string('0', 64)), progress, ct));
        Check("Bad SHA-256: old version kept", records.Get(modsDir, "BumBum")?.Version == "1.0.0"
                                               && File.Exists(Path.Combine(modsDir, "BumBum", "ModManifest.json")));
        Throws<InvalidDataException>("Oversized download is refused",
            () => installer.InstallAsync(modsDir, Entry("BumBum", bumZip, "2.0.0", size: 10), progress, ct));
        Throws<InvalidDataException>("Zip with ../ paths is refused",
            () => installer.InstallAsync(modsDir, Entry("BumBum", evilZip, "2.0.0"), progress, ct));
        Check("Zip with ../ paths: nothing written outside", !File.Exists(Path.Combine(modsDir, "..", "evil.txt"))
                                                             && !File.Exists(Path.Combine(modsDir, "evil.txt")));
        Throws<InvalidDataException>("Package for a different mod is refused",
            () => installer.InstallAsync(modsDir, Entry("BumBum", ghostZip, "2.0.0"), progress, ct));
        Check("Failed installs: no leftover temp folders", !Directory.EnumerateDirectories(modsDir, ".synnmods-*").Any());

        installer.InstallAsync(modsDir, Entry("GhostMommy", ghostZip, "1.0.0"), progress, ct).GetAwaiter().GetResult();
        Check("List: both mods found", installer.ListInstalled(modsDir).Count == 2);
        installer.Remove(modsDir, "GhostMommy");
        Check("Remove: folder gone", !Directory.Exists(Path.Combine(modsDir, "GhostMommy")));
        Check("Remove: record gone", records.Get(modsDir, "GhostMommy") == null);
        Throws<InvalidDataException>("Remove refuses unsafe ids", () => { installer.Remove(modsDir, ".."); return Task.CompletedTask; });

        // A mod someone copied in by hand
        Directory.CreateDirectory(Path.Combine(modsDir, "HandMod"));
        File.WriteAllText(Path.Combine(modsDir, "HandMod", "ModManifest.json"), "{\"name\":\"HandMod\"}");

        // ---- catalog ----
        var catalog = new ModCatalog
        {
            Mods =
            {
                Entry("BumBum", bumZip, "1.0.1"),
                Entry("GhostMommy", ghostZip, "1.0.0"),
                new ModEntry { Id = "Upcoming", Name = "Upcoming", Description = "Not published yet.", Version = "0.1.0" },
            },
        };
        catalog.Mods[0].Name = "Bum Bum";
        catalog.Mods[0].Description = "A fishing gnome pet who lives in a Ship in a Bottle.";
        catalog.Mods[1].Name = "Ghost Mommy";
        catalog.Mods[1].Description = "A vampiric ghost pet who heals nearby friends.";
        File.WriteAllText(Path.Combine(gameCat, "mods.json"), Json.Write(catalog));

        var client2 = new CatalogClient(catalogDir);
        var (games, _) = client2.GetGamesAsync(ct).GetAwaiter().GetResult();
        Check("Catalog: only folders with game.json are games", games.Count == 1 && games[0].Id == "corekeeper");
        var mods = client2.GetModsAsync("corekeeper", ct).GetAwaiter().GetResult();
        Check("Catalog: mods.json parsed", mods.Mods.Count == 3 && mods.Mods[0].IsPublished && !mods.Mods[2].IsPublished);

        // ---- window screenshot ----
        var settings = new Settings { LastGame = "corekeeper" };
        settings.For("corekeeper").Target = "client";
        settings.For("corekeeper").Paths["client"] = root;
        settings.Save();
        RenderWindow(catalogDir, png);
        Console.WriteLine($"Window rendered to {png}");
    }

    private static void RenderWindow(string catalogDir, string png)
    {
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow(catalogDir)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var content = (FrameworkElement)window.Content;
            var bmp = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(content);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            using (var fs = File.Create(png)) enc.Save(fs);
            var vm = (MainViewModel)window.DataContext;
            var states = vm.SelectedGame?.Mods.Select(m => $"{m.Id}={m.State}").ToList() ?? new List<string>();
            Console.WriteLine("Mod states: " + string.Join(", ", states));
            Check("Window: states", string.Join(",", states) ==
                "BumBum=UpdateAvailable,GhostMommy=NotInstalled,Upcoming=NotPublished,HandMod=OtherMod");
            window.Close();
        };
        window.Loaded += (_, _) => timer.Start();
        app.Run(window);
    }

    private static string Zip(string folder, string zip)
    {
        ZipFile.CreateFromDirectory(folder, zip);
        return zip;
    }

    private static string Sha(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "corekeeper"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("Couldn't find the repo root.");
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "pass" : "FAIL")}: {name}");
        if (!ok) _failures++;
    }

    private static void Throws<T>(string name, Func<Task> action) where T : Exception
    {
        try
        {
            action().GetAwaiter().GetResult();
            Check(name + " (no exception)", false);
        }
        catch (T)
        {
            Check(name, true);
        }
        catch (Exception ex)
        {
            Check($"{name} (threw {ex.GetType().Name}: {ex.Message})", false);
        }
    }
}
