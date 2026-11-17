using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace SynnMods;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly CatalogClient _catalog;
    private readonly Settings _settings = Settings.Load();
    private readonly InstallRecords _records = InstallRecords.Load();
    private readonly ModInstaller _installer;
    private readonly SelfUpdater _updater = new();
    private readonly CancellationTokenSource _closing = new();
    private bool _loadingGame;
    /// <summary>Mod folders found by the last refresh, with the dependencies their manifests declare.</summary>
    private List<ModInstaller.LocalMod> _installed = new();

    public MainWindow(string? localCatalog)
    {
        InitializeComponent();
        _catalog = new CatalogClient(localCatalog);
        _installer = new ModInstaller(_records);
        _vm.CatalogSource = $"Mods from {_catalog.SourceDescription}";
        DataContext = _vm;
        _vm.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedGame)) await OnGameSelectedAsync();
        };
        Loaded += async (_, _) =>
        {
            await LoadGamesAsync();
            await CheckForAppUpdateAsync();
        };
        Closed += (_, _) => _closing.Cancel();
    }

    // ---------- Loading ----------

    private async Task LoadGamesAsync()
    {
        _vm.LoadError = null;
        await RunBusyAsync("Loading games…", async () =>
        {
            try
            {
                var previous = _vm.SelectedGame?.Info.Id ?? _settings.LastGame;
                var (games, fromCache) = await _catalog.GetGamesAsync(_closing.Token);
                _vm.Games.Clear();
                foreach (var g in games) _vm.Games.Add(new GameViewModel(g));
                if (_vm.Games.Count == 0) _vm.LoadError = "No games found in the catalog.";
                _vm.SelectedGame = _vm.Games.FirstOrDefault(g => g.Info.Id == previous) ?? _vm.Games.FirstOrDefault();
                _vm.Status = fromCache
                    ? "Offline: showing the games from last time. Installing needs an internet connection."
                    : $"Found {games.Count} game{(games.Count == 1 ? "" : "s")}.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Loading games failed", ex);
                _vm.LoadError = $"Couldn't load the game list: {ex.Message}";
                _vm.Status = "Couldn't reach the mod catalog. Check your connection and press Refresh.";
            }
        }, indeterminate: true);
    }

    private async Task OnGameSelectedAsync()
    {
        var game = _vm.SelectedGame;
        if (game == null) return;
        _settings.LastGame = game.Info.Id;
        _settings.Save();

        _loadingGame = true;
        try
        {
            var gs = _settings.For(game.Info.Id);
            foreach (var t in game.Targets)
            {
                if (gs.Paths.TryGetValue(t.Target.Id, out var saved)) t.Path = saved;
            }
            game.SelectedTarget = game.Targets.FirstOrDefault(t => t.Target.Id == gs.Target) ?? game.Targets[0];
            if (string.IsNullOrWhiteSpace(game.SelectedTarget.Path))
                AutoDetect(game, game.SelectedTarget, quiet: true);
        }
        finally
        {
            _loadingGame = false;
        }
        await LoadModsAsync(game);
    }

    private async Task LoadModsAsync(GameViewModel game)
    {
        game.ModsError = null;
        if (game.Catalog == null)
        {
            game.ModsLoading = true;
            try
            {
                game.Catalog = await _catalog.GetModsAsync(game.Info.Id, _closing.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"Loading mods for {game.Info.Id} failed", ex);
                game.ModsError = $"Couldn't load the mod list: {ex.Message}";
            }
            finally
            {
                game.ModsLoading = false;
            }
        }
        RefreshModStates(game);
    }

    /// <summary>Compares the catalog with what's in the selected Mods folder.</summary>
    private void RefreshModStates(GameViewModel game)
    {
        var target = game.SelectedTarget;
        var check = GameFolder.Check(target!.Target, target.Path);
        game.PathOk = check.Ok;
        game.PathMessage = check.Message;

        var installed = new List<ModInstaller.LocalMod>();
        string? modsDir = null;
        if (check.Ok)
        {
            modsDir = GameFolder.ModsDir(target.Target, target.Path);
            try
            {
                ModInstaller.CleanUpLeftovers(modsDir);
                installed = _installer.ListInstalled(modsDir);
            }
            catch (Exception ex)
            {
                Log.Error($"Reading {modsDir}", ex);
                game.PathMessage = $"Couldn't read the Mods folder: {ex.Message}";
            }
        }

        game.Mods.Clear();
        foreach (var entry in game.Catalog?.Mods ?? new List<ModEntry>())
        {
            var local = installed.FirstOrDefault(m => string.Equals(m.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            var vm = new ModViewModel(entry, entry.Id) { IsOnDisk = local != null };
            if (local == null)
            {
                if (modsDir != null) _records.Remove(modsDir, entry.Id); // deleted by hand
                vm.State = entry.IsPublished ? ModState.NotInstalled : ModState.NotPublished;
            }
            else if (local.Record == null)
            {
                vm.State = entry.IsPublished ? ModState.Unknown : ModState.NotPublished;
            }
            else
            {
                vm.InstalledVersion = local.Record.Version;
                var same = local.Record.Version == entry.Version &&
                           (!entry.IsPublished || string.Equals(local.Record.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase));
                vm.State = !entry.IsPublished ? ModState.NotPublished : same ? ModState.UpToDate : ModState.UpdateAvailable;
            }
            game.Mods.Add(vm);
        }

        // Mods in the folder that aren't in the catalog: listed so the user can see everything installed.
        foreach (var local in installed.Where(l => game.Mods.All(m => !string.Equals(m.Id, l.Id, StringComparison.OrdinalIgnoreCase))))
            game.Mods.Add(new ModViewModel(null, local.Id) { State = ModState.OtherMod, IsOnDisk = true });

        _installed = installed;
        UpdateDependencyStates(game);
    }

    /// <summary>Fills in each mod's "Needs …" line and flags installed mods whose dependencies are
    /// missing or out of date (they won't load in-game).</summary>
    private static void UpdateDependencyStates(GameViewModel game)
    {
        foreach (var vm in game.Mods)
        {
            var deps = vm.Entry?.Dependencies ?? new List<string>();
            if (deps.Count == 0) continue;

            var names = new List<string>();
            var problems = new List<string>();
            foreach (var depId in deps)
            {
                var dep = FindMod(game, depId);
                var name = dep?.Name ?? depId;
                names.Add(name);
                if (dep == null || NeedsInstall(dep))
                {
                    problems.Add(dep?.State switch
                    {
                        ModState.UpdateAvailable => $"{name} needs an update",
                        ModState.Unknown => $"{name} is an unknown version",
                        _ => $"{name} is missing",
                    });
                }
            }
            vm.NeedsText = vm.IsOnDisk || problems.Count == 0
                ? $"Needs {string.Join(", ", names)}"
                : $"Needs {string.Join(", ", names)} (installed with it)";
            vm.MissingText = problems.Count > 0 ? string.Join(", ", problems) : null;
        }
    }

    private static ModViewModel? FindMod(GameViewModel game, string id) =>
        game.Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the mod isn't installed, or is installed at a different or unknown version.</summary>
    private static bool NeedsInstall(ModViewModel mod) => mod.Entry == null
        ? !mod.IsOnDisk
        : mod.State is ModState.NotInstalled or ModState.UpdateAvailable or ModState.Unknown
          || (mod.State == ModState.NotPublished && !mod.IsOnDisk);

    // ---------- Install location ----------

    private void Target_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingGame || _vm.SelectedGame is not { } game || game.SelectedTarget is not { } target) return;
        _settings.For(game.Info.Id).Target = target.Target.Id;
        _settings.Save();
        if (string.IsNullOrWhiteSpace(target.Path)) AutoDetect(game, target, quiet: true);
        RefreshModStates(game);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGame is not { } game || game.SelectedTarget is not { } target) return;
        var dlg = new OpenFolderDialog
        {
            Title = $"Choose the {game.Name} folder ({target.Label})",
            InitialDirectory = Directory.Exists(target.Path) ? target.Path : SteamLocator.FindSteamRoot() ?? "",
        };
        if (dlg.ShowDialog(this) == true) SetPath(game, target, dlg.FolderName);
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGame is { } game && game.SelectedTarget is { } target) AutoDetect(game, target, quiet: false);
    }

    private void AutoDetect(GameViewModel game, TargetViewModel target, bool quiet)
    {
        var found = SteamLocator.FindGame(target.Target);
        if (found != null)
        {
            SetPath(game, target, found);
            _vm.Status = $"Found {target.Label} in Steam: {found}";
        }
        else if (!quiet)
        {
            MessageBox.Show(this,
                $"Couldn't find {game.Name} ({target.Label}) in your Steam libraries.\n\nUse Browse… to pick the folder yourself.",
                "Synn Mods", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SetPath(GameViewModel game, TargetViewModel target, string picked)
    {
        var path = string.IsNullOrWhiteSpace(picked) ? "" : GameFolder.Normalize(target.Target, picked);
        target.Path = path;
        _settings.For(game.Info.Id).Paths[target.Target.Id] = path;
        _settings.Save();
        if (!_loadingGame) RefreshModStates(game);
    }

    private void PathBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyTypedPath();

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyTypedPath();
    }

    private void ApplyTypedPath()
    {
        if (_vm.SelectedGame is not { } game || game.SelectedTarget is not { } target) return;
        if (PathBox.Text.Trim() == target.Path) return;
        try { SetPath(game, target, PathBox.Text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            game.PathOk = false;
            game.PathMessage = "That isn't a valid folder path.";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGame is not { } game || game.SelectedTarget is not { } target || !game.PathOk) return;
        var dir = GameFolder.ModsDir(target.Target, target.Path);
        if (!Directory.Exists(dir)) dir = target.Path;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ---------- Install / remove ----------

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModViewModel { Entry: { } entry } vm || _vm.SelectedGame is not { } game)
            return;
        // Up to date but a dependency is missing: install just what it needs.
        if (vm.HasDependencyProblem && vm.State == ModState.UpToDate)
        {
            var needed = entry.Dependencies
                .Select(id => FindMod(game, id))
                .Where(d => d?.Entry != null && NeedsInstall(d))
                .Select(d => d!.Entry!)
                .ToList();
            if (needed.Count > 0)
            {
                await InstallAsync(needed);
                return;
            }
        }
        await InstallAsync(new[] { entry });
    }

    private async void InstallAll_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGame is not { } game) return;
        var todo = game.Mods
            .Where(m => m.Entry != null && m.CanInstall && m.State is ModState.NotInstalled or ModState.UpdateAvailable or ModState.Unknown)
            .Select(m => m.Entry!)
            .ToList();
        if (todo.Count == 0)
        {
            _vm.Status = "Everything is already up to date.";
            return;
        }
        await InstallAsync(todo);
    }

    private async Task InstallAsync(IReadOnlyList<ModEntry> requested)
    {
        if (!TryGetModsDir(out var game, out var target, out var modsDir)) return;

        // Add missing or outdated dependencies, each before the mods that need it.
        List<ModEntry> mods;
        try
        {
            var catalog = (game.Catalog?.Mods ?? new List<ModEntry>())
                .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            mods = DependencyPlanner.Plan(requested, catalog,
                id => FindMod(game, id) is not { } m || NeedsInstall(m));
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Synn Mods", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfirmGameNotRunning(target)) return;

        var done = 0;
        await RunBusyAsync($"Installing…", async () =>
        {
            var progress = new Progress<(string Text, double? Fraction)>(p =>
            {
                _vm.Status = mods.Count > 1 ? $"({done + 1}/{mods.Count}) {p.Text}" : p.Text;
                _vm.ProgressIndeterminate = p.Fraction == null;
                if (p.Fraction is double f) _vm.Progress = f;
            });
            try
            {
                foreach (var mod in mods)
                {
                    await _installer.InstallAsync(modsDir, mod, progress, _closing.Token);
                    done++;
                }
                _vm.Status = mods.Count == 1
                    ? $"Installed {mods[0].Name} {mods[0].Version}."
                    : $"Installed {string.Join(", ", mods.Select(m => $"{m.Name} {m.Version}"))}.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Install failed", ex);
                _vm.Status = "Install failed.";
                MessageBox.Show(this, Explain(ex, modsDir), "Synn Mods", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RefreshModStates(game);
            }
        }, indeterminate: false);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModViewModel mod) return;
        if (!TryGetModsDir(out var game, out var target, out var modsDir)) return;
        var dependents = DependencyPlanner.Dependents(_installed, mod.Id)
            .Select(d => FindMod(game, d.Id)?.Name ?? d.Id)
            .ToList();
        var warning = dependents.Count == 0
            ? ""
            : $"{string.Join(", ", dependents)} {(dependents.Count == 1 ? "needs" : "need")} {mod.Name} " +
              $"and won't load without it. Remove {(dependents.Count == 1 ? "it" : "them")} too, or keep {mod.Name}.\n\n";
        if (MessageBox.Show(this,
                $"{warning}Remove {mod.Name} from {target.Label}?\n\nThis deletes {Path.Combine(modsDir, mod.Id)}.\n" +
                "Worlds that used this mod's items may lose them.",
                "Synn Mods", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        if (!ConfirmGameNotRunning(target)) return;

        await RunBusyAsync($"Removing {mod.Name}…", () =>
        {
            try
            {
                _installer.Remove(modsDir, mod.Id);
                _vm.Status = $"Removed {mod.Name}.";
            }
            catch (Exception ex)
            {
                Log.Error("Remove failed", ex);
                _vm.Status = "Remove failed.";
                MessageBox.Show(this, Explain(ex, modsDir), "Synn Mods", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RefreshModStates(game);
            }
            return Task.CompletedTask;
        }, indeterminate: true);
    }

    private bool TryGetModsDir(out GameViewModel game, out InstallTarget target, out string modsDir)
    {
        game = _vm.SelectedGame!;
        target = game?.SelectedTarget?.Target!;
        modsDir = "";
        if (game?.SelectedTarget == null) return false;
        var check = GameFolder.Check(target, game.SelectedTarget.Path);
        if (!check.Ok)
        {
            MessageBox.Show(this, $"{check.Message}\n\nSet the install folder first (Browse… or Find in Steam).",
                "Synn Mods", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        modsDir = GameFolder.ModsDir(target, game.SelectedTarget.Path);
        return true;
    }

    private bool ConfirmGameNotRunning(InstallTarget target)
    {
        while (true)
        {
            var running = ModInstaller.RunningGameProcesses(target);
            if (running.Count == 0) return true;
            var answer = MessageBox.Show(this,
                $"{string.Join(", ", running)} is running. Close it, then press OK.\n\n" +
                "Mod files can't be changed while the game or server is using them.",
                "Synn Mods", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return false;
        }
    }

    private static string Explain(Exception ex, string modsDir) => ex switch
    {
        UnauthorizedAccessException =>
            $"Windows didn't allow changes in:\n{modsDir}\n\nClose the game, then try again. " +
            "If the game is installed somewhere that needs admin rights, right-click Synn Mods and choose \"Run as administrator\".",
        IOException io => $"A file couldn't be changed: {io.Message}\n\nClose the game (and any open File Explorer windows on the Mods folder), then try again.",
        HttpRequestException http => $"Download failed: {http.Message}\n\nCheck your internet connection and try again.",
        _ => ex.Message,
    };

    // ---------- App ----------

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        foreach (var g in _vm.Games) g.Catalog = null;
        await LoadGamesAsync();
    }

    private async Task CheckForAppUpdateAsync()
    {
        var version = await _updater.CheckAsync();
        if (version != null) _vm.UpdateText = $"Synn Mods {version} is available.";
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Downloading the Synn Mods update…", async () =>
        {
            try
            {
                await _updater.ApplyAndRestartAsync(p => Dispatcher.Invoke(() => _vm.Progress = p / 100.0));
            }
            catch (Exception ex)
            {
                Log.Error("Self-update failed", ex);
                MessageBox.Show(this, $"The update failed: {ex.Message}", "Synn Mods", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }, indeterminate: false);
    }

    private async Task RunBusyAsync(string status, Func<Task> work, bool indeterminate)
    {
        _vm.IsBusy = true;
        _vm.Status = status;
        _vm.Progress = 0;
        _vm.ProgressIndeterminate = indeterminate;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // window closing
        }
        finally
        {
            _vm.IsBusy = false;
            _vm.ProgressIndeterminate = false;
        }
    }
}
