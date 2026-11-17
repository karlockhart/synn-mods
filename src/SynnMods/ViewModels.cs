using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SynnMods;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MainViewModel : Observable
{
    private GameViewModel? _selectedGame;
    private string _status = "";
    private bool _busy;
    private double _progress;
    private bool _progressIndeterminate;
    private string? _updateText;
    private string? _loadError;

    public ObservableCollection<GameViewModel> Games { get; } = new();
    public string AppVersionText { get; } = $"v{AppPaths.AppVersion}";
    public string CatalogSource { get; set; } = "";

    public GameViewModel? SelectedGame { get => _selectedGame; set => Set(ref _selectedGame, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_busy;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; set => Set(ref _progressIndeterminate, value); }
    public string? UpdateText { get => _updateText; set { if (Set(ref _updateText, value)) Raise(nameof(HasUpdate)); } }
    public bool HasUpdate => !string.IsNullOrEmpty(_updateText);
    public string? LoadError { get => _loadError; set { if (Set(ref _loadError, value)) Raise(nameof(HasLoadError)); } }
    public bool HasLoadError => !string.IsNullOrEmpty(_loadError);
}

public sealed class GameViewModel : Observable
{
    private TargetViewModel? _selectedTarget;
    private string _pathMessage = "";
    private bool _pathOk;
    private string? _modsError;
    private bool _modsLoading;

    public GameViewModel(GameInfo info)
    {
        Info = info;
        foreach (var t in info.Targets) Targets.Add(new TargetViewModel(t, this));
    }

    public GameInfo Info { get; }
    public string Name => Info.Name;
    public string Description => Info.Description;
    public string Notice => Info.Notice;
    public bool HasNotice => !string.IsNullOrWhiteSpace(Info.Notice);
    public string Initial => string.IsNullOrEmpty(Info.Name) ? "?" : Info.Name[..1].ToUpperInvariant();
    public ModCatalog? Catalog { get; set; }

    public ObservableCollection<TargetViewModel> Targets { get; } = new();
    public bool HasMultipleTargets => Targets.Count > 1;
    public ObservableCollection<ModViewModel> Mods { get; } = new();

    public TargetViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!Set(ref _selectedTarget, value)) return;
            foreach (var t in Targets) t.NotifySelected();
        }
    }

    public string PathMessage { get => _pathMessage; set => Set(ref _pathMessage, value); }
    public bool PathOk { get => _pathOk; set { if (Set(ref _pathOk, value)) Raise(nameof(PathBad)); } }
    public bool PathBad => !_pathOk;
    public string? ModsError { get => _modsError; set { if (Set(ref _modsError, value)) Raise(nameof(HasModsError)); } }
    public bool HasModsError => !string.IsNullOrEmpty(_modsError);
    public bool ModsLoading { get => _modsLoading; set => Set(ref _modsLoading, value); }
}

public sealed class TargetViewModel : Observable
{
    private readonly GameViewModel _game;
    private string _path = "";

    public TargetViewModel(InstallTarget target, GameViewModel game)
    {
        Target = target;
        _game = game;
    }

    public InstallTarget Target { get; }
    public string Label => Target.Label;
    public string Description => Target.Description;
    public string Path { get => _path; set => Set(ref _path, value); }

    public bool IsSelected
    {
        get => _game.SelectedTarget == this;
        set { if (value) _game.SelectedTarget = this; }
    }

    internal void NotifySelected() => Raise(nameof(IsSelected));
}

public enum ModState { NotInstalled, UpToDate, UpdateAvailable, Unknown, NotPublished, OtherMod }

public sealed class ModViewModel : Observable
{
    private ModState _state;
    private string? _installedVersion;

    public ModViewModel(ModEntry? entry, string id)
    {
        Entry = entry;
        Id = id;
    }

    public ModEntry? Entry { get; }
    public string Id { get; }
    public string Name => Entry?.Name ?? Id;
    public string Description => Entry?.Description is { Length: > 0 } d
        ? d
        : Entry == null ? "Installed by hand, not from this list. Synn Mods leaves it alone." : "";
    public string AvailableVersion => Entry?.Version is { Length: > 0 } v ? v : "–";
    public string RequiredOnText => Entry?.RequiredOn switch
    {
        null => "",
        "Client" => "Players only",
        "Server" => "Host/server only",
        _ => "Host + every player",
    };

    public string? InstalledVersion
    {
        get => _installedVersion;
        set { if (Set(ref _installedVersion, value)) Raise(nameof(InstalledVersionText)); }
    }

    public string InstalledVersionText => State switch
    {
        ModState.NotInstalled or ModState.NotPublished when _installedVersion == null => "–",
        ModState.Unknown or ModState.OtherMod => "unknown",
        _ => _installedVersion ?? "–",
    };

    public ModState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(StatusKind));
            Raise(nameof(ActionText));
            Raise(nameof(CanInstall));
            Raise(nameof(CanRemove));
            Raise(nameof(InstalledVersionText));
        }
    }

    public string StatusText => State switch
    {
        ModState.NotInstalled => "Not installed",
        ModState.UpToDate => "Up to date",
        ModState.UpdateAvailable => "Update available",
        ModState.Unknown => "Installed (version unknown)",
        ModState.NotPublished => "Coming soon",
        ModState.OtherMod => "Not managed",
        _ => "",
    };

    /// <summary>Used by the UI to pick the status colour: good, warn, muted.</summary>
    public string StatusKind => State switch
    {
        ModState.UpToDate => "good",
        ModState.UpdateAvailable or ModState.Unknown => "warn",
        _ => "muted",
    };

    public string ActionText => State switch
    {
        ModState.NotInstalled => "Install",
        ModState.UpdateAvailable => $"Update to {AvailableVersion}",
        ModState.Unknown => $"Reinstall {AvailableVersion}",
        ModState.UpToDate => "Reinstall",
        _ => "Install",
    };

    public bool CanInstall => Entry?.IsPublished == true && State != ModState.OtherMod;
    public bool CanRemove => Entry != null && State is ModState.UpToDate or ModState.UpdateAvailable or ModState.Unknown
                             || (Entry != null && State == ModState.NotPublished && _installedVersion != null);
}
