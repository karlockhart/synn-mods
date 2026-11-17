namespace SynnMods;

/// <summary>Per-user choices: which install target and which folder each game uses.</summary>
public sealed class Settings
{
    public string? LastGame { get; set; }
    public Dictionary<string, GameSettings> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public GameSettings For(string gameId)
    {
        if (!Games.TryGetValue(gameId, out var g))
            Games[gameId] = g = new GameSettings();
        return g;
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = Json.Parse<Settings>(File.ReadAllText(FilePath));
                s.Games = new Dictionary<string, GameSettings>(s.Games, StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings; starting fresh", ex);
        }
        return new Settings();
    }

    public void Save()
    {
        try { AppPaths.WriteAllTextAtomic(FilePath, Json.Write(this)); }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
    }
}

public sealed class GameSettings
{
    public string? Target { get; set; }
    /// <summary>Game folder per target id.</summary>
    public Dictionary<string, string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What Synn Mods installed where, so it can tell which version is installed.
/// Keyed by the full Mods folder path, then by mod id.</summary>
public sealed class InstallRecords
{
    public Dictionary<string, Dictionary<string, InstalledMod>> Folders { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(AppPaths.DataDir, "installed.json");

    public static InstallRecords Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var r = Json.Parse<InstallRecords>(File.ReadAllText(FilePath));
                var folders = new Dictionary<string, Dictionary<string, InstalledMod>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in r.Folders)
                    folders[k] = new Dictionary<string, InstalledMod>(v, StringComparer.OrdinalIgnoreCase);
                r.Folders = folders;
                return r;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read install records; starting fresh", ex);
        }
        return new InstallRecords();
    }

    public void Save()
    {
        try { AppPaths.WriteAllTextAtomic(FilePath, Json.Write(this)); }
        catch (Exception ex) { Log.Error("Could not save install records", ex); }
    }

    private static string Key(string modsDir) => Path.GetFullPath(modsDir).TrimEnd('\\', '/');

    public InstalledMod? Get(string modsDir, string modId) =>
        Folders.TryGetValue(Key(modsDir), out var mods) && mods.TryGetValue(modId, out var m) ? m : null;

    public void Set(string modsDir, string modId, InstalledMod mod)
    {
        var key = Key(modsDir);
        if (!Folders.TryGetValue(key, out var mods))
            Folders[key] = mods = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        mods[modId] = mod;
        Save();
    }

    public void Remove(string modsDir, string modId)
    {
        if (Folders.TryGetValue(Key(modsDir), out var mods) && mods.Remove(modId))
            Save();
    }
}

public sealed class InstalledMod
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime InstalledAt { get; set; }
}
