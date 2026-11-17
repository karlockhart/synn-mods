using System.Reflection;

namespace SynnMods;

public static class AppPaths
{
    /// <summary>%APPDATA%\SynnMods: settings, install records, cached catalog and the log.
    /// Kept out of the Velopack install folder so it survives reinstalls.</summary>
    public static string DataDir { get; } = CreateDir(
        Environment.GetEnvironmentVariable("SYNNMODS_DATA_DIR") is { Length: > 0 } custom
            ? custom // tests
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SynnMods"));

    public static string CacheDir { get; } = CreateDir(Path.Combine(DataDir, "cache"));

    public static string LogFile => Path.Combine(DataDir, "synnmods.log");

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.0.0";

    private static string CreateDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file by writing a temp file next to it and then swapping it in.</summary>
    public static void WriteAllTextAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
