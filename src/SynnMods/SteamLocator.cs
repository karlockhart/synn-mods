using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SynnMods;

/// <summary>Finds games installed through Steam, in any Steam library folder.</summary>
public static class SteamLocator
{
    public static string? FindSteamRoot()
    {
        foreach (var (hive, key, value) in new[]
                 {
                     // HKLM first: HKCU's SteamPath is often all lower-case.
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                 })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue(value) is string p && Directory.Exists(p))
                    return Path.GetFullPath(p.Replace('/', '\\'));
            }
            catch (Exception ex)
            {
                Log.Error($"Reading {key}", ex);
            }
        }

        foreach (var guess in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                 })
        {
            if (Directory.Exists(guess)) return guess;
        }
        return null;
    }

    /// <summary>All Steam library folders (each has a steamapps folder).</summary>
    public static List<string> FindLibraries()
    {
        var libs = new List<string>();
        var root = FindSteamRoot();
        if (root == null) return libs;
        libs.Add(root);

        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(p) && !libs.Any(l => string.Equals(Path.GetFullPath(l), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)))
                        libs.Add(p);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading libraryfolders.vdf", ex);
        }
        return libs;
    }

    /// <summary>Finds the game folder for an install target, or null.</summary>
    public static string? FindGame(InstallTarget target)
    {
        foreach (var lib in FindLibraries())
        {
            var steamapps = Path.Combine(lib, "steamapps");
            string? folder = null;

            if (target.SteamAppId is int appId)
            {
                var acf = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
                try
                {
                    if (File.Exists(acf))
                    {
                        var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (m.Success) folder = Path.Combine(steamapps, "common", m.Groups[1].Value);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Reading {acf}", ex);
                }
            }

            if (folder == null && !string.IsNullOrWhiteSpace(target.SteamFolder))
                folder = Path.Combine(steamapps, "common", target.SteamFolder);

            if (folder != null && GameFolder.Check(target, folder).Ok)
                return folder;
        }
        return null;
    }
}

/// <summary>Works out and checks the game folder a user picked.</summary>
public static class GameFolder
{
    public sealed record Result(bool Ok, string Message);

    /// <summary>
    /// Accepts the game folder or any folder on the way to the Mods folder (e.g. if the user picks
    /// ...\CoreKeeper_Data\StreamingAssets\Mods), and returns the game folder.
    /// </summary>
    public static string Normalize(InstallTarget target, string picked)
    {
        var path = Path.GetFullPath(picked.Trim().Trim('"')).TrimEnd('\\', '/');
        var segs = target.ModsPathSegments;
        for (var k = segs.Length; k >= 1; k--)
        {
            var tail = path.Split('\\', '/');
            if (tail.Length <= k) continue;
            var matches = true;
            for (var i = 0; i < k; i++)
            {
                if (!string.Equals(tail[tail.Length - k + i], segs[i], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
                return string.Join('\\', tail.Take(tail.Length - k));
        }
        return path;
    }

    public static string ModsDir(InstallTarget target, string gameDir) =>
        Path.Combine(new[] { gameDir }.Concat(target.ModsPathSegments).ToArray());

    public static Result Check(InstallTarget target, string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
            return new(false, "Choose the folder where the game is installed.");
        if (!Directory.Exists(gameDir))
            return new(false, "That folder doesn't exist.");
        if (!string.IsNullOrWhiteSpace(target.DetectFile) && !File.Exists(Path.Combine(gameDir, target.DetectFile)))
            return new(false, $"{target.DetectFile} isn't in that folder. Pick the folder that contains it.");
        var dataDir = Path.Combine(gameDir, target.ModsPathSegments[0]);
        if (!Directory.Exists(dataDir))
            return new(false, $"{target.ModsPathSegments[0]} isn't in that folder. Pick the game's main folder.");
        return new(true, $"Mods go in {ModsDir(target, gameDir)}");
    }
}
