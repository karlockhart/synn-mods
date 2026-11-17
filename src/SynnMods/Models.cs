using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SynnMods;

/// <summary>A game's <c>game.json</c>: what the game is and where its mods go.</summary>
public sealed class GameInfo
{
    public int Schema { get; set; } = 1;
    /// <summary>Folder name in the repo, e.g. "corekeeper". Filled in from the folder, not trusted from the file.</summary>
    [JsonIgnore] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Shown above the mod list, e.g. "the host and every player need the same mods".</summary>
    public string Notice { get; set; } = "";
    public List<InstallTarget> Targets { get; set; } = new();
}

/// <summary>One place a game's mods can be installed (e.g. the game itself, or its dedicated server).</summary>
public sealed class InstallTarget
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Steam app id, used to find the install in any Steam library.</summary>
    public int? SteamAppId { get; set; }
    /// <summary>Folder name under steamapps\common, used if the app manifest isn't found.</summary>
    public string? SteamFolder { get; set; }
    /// <summary>Mods folder relative to the game folder, with forward slashes, e.g. "CoreKeeper_Data/StreamingAssets/Mods".</summary>
    public string ModsPath { get; set; } = "";
    /// <summary>A file that proves a folder is this game, e.g. "CoreKeeper.exe". Optional.</summary>
    public string? DetectFile { get; set; }
    /// <summary>Process names (no .exe) that lock the mod files while running.</summary>
    public List<string> ProcessNames { get; set; } = new();

    [JsonIgnore] public string[] ModsPathSegments =>
        ModsPath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>A game's <c>mods.json</c>: the mods anyone can install, with where to download each one.</summary>
public sealed class ModCatalog
{
    public int Schema { get; set; } = 1;
    public List<ModEntry> Mods { get; set; } = new();
}

public sealed class ModEntry
{
    /// <summary>The mod's folder name inside the Mods folder. Must match ModManifest.json "name".</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    /// <summary>"ClientAndServer" (everyone needs it), "Client" or "Server".</summary>
    public string RequiredOn { get; set; } = "ClientAndServer";

    [JsonIgnore] public bool IsPublished =>
        !string.IsNullOrWhiteSpace(Url) && Regex.IsMatch(Sha256 ?? "", "^[0-9a-fA-F]{64}$");
}

public static class Names
{
    private static readonly Regex Safe = new("^[A-Za-z0-9][A-Za-z0-9_.\\- ]{0,63}$");

    /// <summary>True if <paramref name="name"/> is safe to use as a single folder name.</summary>
    public static bool IsSafeFolderName(string? name) =>
        name != null && Safe.IsMatch(name) && !name.Contains("..") && !name.EndsWith('.') && !name.EndsWith(' ');
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Parse<T>(string text) =>
        JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("Empty JSON.");

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
