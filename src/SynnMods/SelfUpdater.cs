using Velopack;
using Velopack.Sources;

namespace SynnMods;

/// <summary>
/// Updates Synn Mods itself from the latest GitHub release. Manager releases are tagged v1.2.3
/// and marked "latest"; mod releases are published with --latest=false so they never shadow them.
/// </summary>
public sealed class SelfUpdater
{
    public const string FeedUrl = $"https://github.com/{CatalogClient.Owner}/{CatalogClient.Repo}/releases/latest/download/";

    private readonly UpdateManager? _mgr;
    private UpdateInfo? _pending;

    public SelfUpdater()
    {
        try
        {
            _mgr = new UpdateManager(new SimpleWebSource(FeedUrl));
        }
        catch (Exception ex)
        {
            // Not started through Velopack (e.g. a test host): no self-update, everything else works.
            Log.Error("Self-update unavailable", ex);
        }
    }

    /// <summary>Returns the new version if one is available, or null.</summary>
    public async Task<string?> CheckAsync()
    {
        if (_mgr is not { IsInstalled: true }) return null; // dev build, not an install
        try
        {
            _pending = await _mgr.CheckForUpdatesAsync();
            return _pending?.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            return null;
        }
    }

    public async Task ApplyAndRestartAsync(Action<int> progress)
    {
        if (_mgr == null || _pending == null) return;
        await _mgr.DownloadUpdatesAsync(_pending, progress);
        _mgr.ApplyUpdatesAndRestart(_pending);
    }
}
