using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SynnMods;

/// <summary>Downloads, checks and installs mod packages, and removes installed mods.</summary>
public sealed class ModInstaller
{
    private const string TempPrefix = ".synnmods-";
    private const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024;

    private readonly InstallRecords _records;

    public ModInstaller(InstallRecords records) => _records = records;

    public sealed record LocalMod(string Id, string Folder, string? ManifestName, InstalledMod? Record);

    /// <summary>Every mod folder in <paramref name="modsDir"/> (folders with a ModManifest.json).</summary>
    public List<LocalMod> ListInstalled(string modsDir)
    {
        var result = new List<LocalMod>();
        if (!Directory.Exists(modsDir)) return result;
        foreach (var dir in Directory.EnumerateDirectories(modsDir))
        {
            var id = Path.GetFileName(dir);
            if (id.StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var manifest = Path.Combine(dir, "ModManifest.json");
            if (!File.Exists(manifest)) continue;
            result.Add(new LocalMod(id, dir, ReadManifestName(manifest), _records.Get(modsDir, id)));
        }
        return result;
    }

    /// <summary>Deletes folders left behind by an install that was interrupted.</summary>
    public static void CleanUpLeftovers(string modsDir)
    {
        if (!Directory.Exists(modsDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(modsDir, TempPrefix + "*"))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { Log.Error($"Could not delete leftover {dir}", ex); }
        }
    }

    /// <summary>Names of running processes that would lock the mod files, if any.</summary>
    public static List<string> RunningGameProcesses(InstallTarget target)
    {
        var running = new List<string>();
        foreach (var name in target.ProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) running.Add(name);
            foreach (var p in procs) p.Dispose();
        }
        return running;
    }

    public async Task InstallAsync(string modsDir, ModEntry mod, IProgress<(string Text, double? Fraction)> progress,
        CancellationToken ct)
    {
        if (!mod.IsPublished)
            throw new InvalidOperationException($"{mod.Name} hasn't been published yet.");
        if (!Names.IsSafeFolderName(mod.Id))
            throw new InvalidDataException($"Bad mod id \"{mod.Id}\".");

        Directory.CreateDirectory(modsDir);
        var download = Path.Combine(Path.GetTempPath(), $"synnmods-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(modsDir, $"{TempPrefix}new-{mod.Id}-{Guid.NewGuid():N}");
        try
        {
            var hash = await DownloadAsync(mod, download, progress, ct);
            if (!string.Equals(hash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"{mod.Id} {mod.Version}: SHA-256 {hash} doesn't match the manifest's {mod.Sha256}");
                throw new InvalidDataException(
                    $"The {mod.Name} download didn't match its SHA-256 checksum, so it wasn't installed. " +
                    "It may have been corrupted or changed. Try again; if it keeps failing, tell whoever published it.");
            }

            progress.Report(($"Unpacking {mod.Name}…", null));
            await Task.Run(() => Extract(download, staging, mod), ct);

            progress.Report(($"Installing {mod.Name}…", null));
            ReplaceFolder(staging, Path.Combine(modsDir, mod.Id));

            _records.Set(modsDir, mod.Id, new InstalledMod
            {
                Version = mod.Version,
                Sha256 = mod.Sha256.ToLowerInvariant(),
                InstalledAt = DateTime.UtcNow,
            });
            Log.Info($"Installed {mod.Id} {mod.Version} into {modsDir}");
        }
        finally
        {
            TryDelete(download);
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); }
                catch (Exception ex) { Log.Error($"Could not delete {staging}", ex); }
            }
        }
    }

    public void Remove(string modsDir, string modId)
    {
        if (!Names.IsSafeFolderName(modId))
            throw new InvalidDataException($"Bad mod id \"{modId}\".");
        var folder = Path.Combine(modsDir, modId);
        if (Directory.Exists(folder))
        {
            // Rename first so a half-deleted folder never looks like a working mod.
            var trash = Path.Combine(modsDir, $"{TempPrefix}old-{modId}-{Guid.NewGuid():N}");
            Directory.Move(folder, trash);
            Directory.Delete(trash, recursive: true);
        }
        _records.Remove(modsDir, modId);
        Log.Info($"Removed {modId} from {modsDir}");
    }

    private static async Task<string> DownloadAsync(ModEntry mod, string dest,
        IProgress<(string Text, double? Fraction)> progress, CancellationToken ct)
    {
        progress.Report(($"Downloading {mod.Name} {mod.Version}…", mod.Size > 0 ? 0 : null));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = File.Create(dest);
        await using var input = await OpenAsync(mod.Url, ct);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (mod.Size > 0 && total > mod.Size)
                throw new InvalidDataException($"The {mod.Name} download is bigger than the manifest says. It wasn't installed.");
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            if (mod.Size > 0)
                progress.Report(($"Downloading {mod.Name} {mod.Version}… {total / 1048576.0:0.0} of {mod.Size / 1048576.0:0.0} MB",
                    (double)total / mod.Size));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<Stream> OpenAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.IsFile) return File.OpenRead(uri.LocalPath); // local catalog testing
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"Mods are only downloaded over HTTPS ({url}).");
        var resp = await CatalogClient.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            throw new HttpRequestException($"Download failed: {(int)resp.StatusCode} {resp.ReasonPhrase} ({url})");
        }
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>Unpacks the zip into <paramref name="staging"/>, refusing anything that would land outside it.</summary>
    private static void Extract(string zipPath, string staging, ModEntry mod)
    {
        Directory.CreateDirectory(staging);
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        long unpacked = 0;

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The {mod.Name} package has an unsafe path: {entry.FullName}");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                unpacked += entry.Length;
                if (unpacked > MaxUnpackedBytes)
                    throw new InvalidDataException($"The {mod.Name} package is too large to unpack.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: false);
            }
        }

        var manifest = Path.Combine(staging, "ModManifest.json");
        if (!File.Exists(manifest))
            throw new InvalidDataException($"The {mod.Name} package has no ModManifest.json at its top level.");
        var name = ReadManifestName(manifest);
        if (!string.Equals(name, mod.Id, StringComparison.Ordinal))
            throw new InvalidDataException($"The {mod.Name} package's ModManifest.json is for \"{name}\", not \"{mod.Id}\".");
    }

    /// <summary>Swaps the new folder in, keeping the old one until the new one is in place.</summary>
    private static void ReplaceFolder(string staging, string final)
    {
        string? backup = null;
        if (Directory.Exists(final))
        {
            backup = Path.Combine(Path.GetDirectoryName(final)!, $"{TempPrefix}old-{Path.GetFileName(final)}-{Guid.NewGuid():N}");
            Directory.Move(final, backup);
        }
        try
        {
            Directory.Move(staging, final);
        }
        catch
        {
            if (backup != null && !Directory.Exists(final))
                Directory.Move(backup, final);
            throw;
        }
        if (backup != null)
        {
            try { Directory.Delete(backup, true); }
            catch (Exception ex) { Log.Error($"Could not delete {backup}; it will be cleaned up next time", ex); }
        }
    }

    private static string? ReadManifestName(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {manifestPath}", ex);
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) { Log.Error($"Could not delete {file}", ex); }
    }
}
