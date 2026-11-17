using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SynnMods;

/// <summary>
/// Reads the game list and each game's mod manifest from the public GitHub repo
/// (every top-level folder with a game.json is a game), or from a local checkout for testing
/// (<c>--catalog C:\path\to\synn-mods</c> or the SYNNMODS_CATALOG environment variable).
/// </summary>
public sealed class CatalogClient
{
    public const string Owner = "karlockhart";
    public const string Repo = "synn-mods";
    public const string Branch = "main";

    public static readonly HttpClient Http = CreateHttp();

    private readonly string? _localRoot;

    public CatalogClient(string? localRoot)
    {
        _localRoot = string.IsNullOrWhiteSpace(localRoot) ? null : Path.GetFullPath(localRoot);
    }

    public string SourceDescription => _localRoot ?? $"github.com/{Owner}/{Repo}";
    public bool IsLocal => _localRoot != null;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SynnMods", AppPaths.AppVersion));
        return http;
    }

    /// <summary>Returns the games, plus whether the list came from the offline cache.</summary>
    public async Task<(List<GameInfo> Games, bool FromCache)> GetGamesAsync(CancellationToken ct)
    {
        try
        {
            var ids = await ListGameFoldersAsync(ct);
            var loaded = await Task.WhenAll(ids.Select(id => TryLoadGameAsync(id, ct)));
            var games = loaded.Where(g => g != null).Select(g => g!).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (!IsLocal)
                SaveCache("games.json", Json.Write(games.Select(g => new CachedGame(g.Id, g)).ToList()));
            return (games, false);
        }
        catch (Exception ex) when (!IsLocal && ex is not OperationCanceledException)
        {
            Log.Error("Could not load the game list; trying the cache", ex);
            var cached = LoadCache("games.json");
            if (cached == null) throw;
            var games = Json.Parse<List<CachedGame>>(cached).Select(c => { c.Game.Id = c.Id; return c.Game; }).ToList();
            return (games, true);
        }
    }

    public async Task<ModCatalog> GetModsAsync(string gameId, CancellationToken ct)
    {
        var text = await ReadRepoFileAsync($"{gameId}/mods.json", ct)
                   ?? throw new FileNotFoundException($"{gameId}/mods.json is missing from the catalog.");
        var catalog = Json.Parse<ModCatalog>(text);
        if (catalog.Schema > 1)
            throw new InvalidDataException("This mod list needs a newer Synn Mods. Update Synn Mods and try again.");
        foreach (var m in catalog.Mods)
        {
            if (!Names.IsSafeFolderName(m.Id))
                throw new InvalidDataException($"The mod list has a bad mod id: \"{m.Id}\".");
            if (string.IsNullOrWhiteSpace(m.Name)) m.Name = m.Id;
        }
        return catalog;
    }

    private async Task<List<string>> ListGameFoldersAsync(CancellationToken ct)
    {
        if (IsLocal)
        {
            return Directory.EnumerateDirectories(_localRoot!)
                .Select(Path.GetFileName)
                .Where(n => Names.IsSafeFolderName(n) && File.Exists(Path.Combine(_localRoot!, n!, "game.json")))
                .Select(n => n!)
                .ToList();
        }

        // The contents API is rate limited (60/hour per IP without a token), so it's called once per refresh.
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repo}/contents/?ref={Branch}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub's rate limit was reached. Try again in a few minutes.");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "dir")
            .Select(e => e.GetProperty("name").GetString() ?? "")
            .Where(n => Names.IsSafeFolderName(n) && !n.StartsWith('.'))
            .ToList();
    }

    private async Task<GameInfo?> TryLoadGameAsync(string id, CancellationToken ct)
    {
        try
        {
            var text = await ReadRepoFileAsync($"{id}/game.json", ct);
            if (text == null) return null; // a folder that isn't a game (e.g. src, tools)
            var game = Json.Parse<GameInfo>(text);
            game.Id = id;
            if (string.IsNullOrWhiteSpace(game.Name)) game.Name = id;
            game.Targets = game.Targets
                .Where(t => Names.IsSafeFolderName(t.Id) && t.ModsPathSegments.Length > 0 &&
                            t.ModsPathSegments.All(s => Names.IsSafeFolderName(s)))
                .ToList();
            return game.Targets.Count > 0 ? game : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Skipping game folder {id}", ex);
            return null;
        }
    }

    /// <summary>Returns a repo file's text, or null if it doesn't exist.</summary>
    private async Task<string?> ReadRepoFileAsync(string relPath, CancellationToken ct)
    {
        if (IsLocal)
        {
            var file = Path.Combine(_localRoot!, relPath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? await File.ReadAllTextAsync(file, ct) : null;
        }

        // raw.githubusercontent.com isn't rate limited. It caches for up to 5 minutes.
        var url = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/{relPath}";
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static void SaveCache(string name, string text)
    {
        try { AppPaths.WriteAllTextAtomic(Path.Combine(AppPaths.CacheDir, name), text); }
        catch (Exception ex) { Log.Error("Could not write cache", ex); }
    }

    private static string? LoadCache(string name)
    {
        var file = Path.Combine(AppPaths.CacheDir, name);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    private sealed record CachedGame(string Id, GameInfo Game);
}
