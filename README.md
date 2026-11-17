# Synn Mods

A small Windows app that installs, updates and removes Synn's game mods. Pick a game, check where
it's installed, and press **Install / update all**. Every download is checked against the SHA-256
hash in the mod list before anything is installed.

## For players

1. Download **SynnMods-win-Setup.exe** from the
   [latest release](https://github.com/karlockhart/synn-mods/releases/latest) and run it. It installs
   for your Windows user only and needs no admin rights. It adds Start menu and desktop shortcuts.
2. Pick a game on the left (for example **Core Keeper**).
3. Under **Install to**, check the folder. Synn Mods finds Steam games by itself (in any Steam
   library). If it got it wrong, or the game isn't from Steam, press **Browse…** and pick the game's
   main folder. Picking the `Mods` folder itself works too.
4. Press **Install / update all**, or install mods one at a time. **Remove** deletes a mod.

Close the game first: Windows won't let files be changed while the game is using them.

Synn Mods updates itself. When a new version is out you'll see **Update and restart** at the
bottom left.

### Multiplayer: everyone needs the same mods

For Core Keeper (and most games with mods that add items or creatures), **the host and every
player must have the same mods at the same versions**. If you host a dedicated server, install the
mods there too: choose **Dedicated server** under *Install to*. After an update, everyone should
open Synn Mods and press **Install / update all** before playing together.

### Core Keeper paths

| Install to | Folder | Mods go in |
|---|---|---|
| Game | `…\steamapps\common\Core Keeper` | `CoreKeeper_Data\StreamingAssets\Mods\<ModName>\` |
| Dedicated server | `…\steamapps\common\Core Keeper Dedicated Server` | `CoreKeeperServer_Data\StreamingAssets\Mods\<ModName>\` |

### Troubleshooting

- **"Windows didn't allow changes"**: close the game and any File Explorer windows open on the Mods
  folder. If the game is installed somewhere that needs admin rights, right-click Synn Mods →
  *Run as administrator*.
- **"didn't match its SHA-256 checksum"**: the download was corrupted or changed, so it wasn't
  installed. Try again; if it keeps happening, tell Synn.
- **A mod says "Not managed"**: it was copied in by hand. Synn Mods shows it but leaves it alone.
- Logs and settings are in `%APPDATA%\SynnMods` (`synnmods.log`, `settings.json`, `installed.json`).

## How it works

```
synn-mods/
  corekeeper/          one folder per game (any top-level folder with a game.json)
    game.json          name, multiplayer notice, install targets (Steam app id, Mods path)
    mods.json          the mod manifest: id, name, version, url, sha256, size per mod
    publish.json       publisher-only settings for tools/publish-mods.ps1
  src/SynnMods/        the app (C# / WPF / .NET 10, packaged with Velopack)
  tools/               publish-mods.ps1, sign.ps1, make-icon.ps1
  tests/SmokeTest/     offline installer checks + window screenshot
  build.ps1            builds the (signed) installer
  signing/             Artifact Signing account details (not secret)
```

- **Games**: the app lists the repo's top-level folders through the GitHub API and treats every
  folder that has a `game.json` as a game. Folders like `src` and `tools` have none, so they're
  skipped. Files are read from `raw.githubusercontent.com`, which caches for up to 5 minutes.
- **Mods**: each mod is a zip of its mod folder (so `ModManifest.json` is at the top of the zip),
  attached to a GitHub Release tagged `<game>-<ModId>-v<version>`. `mods.json` lists its URL,
  SHA-256 and size. The app downloads over HTTPS, checks the size and hash, refuses zips with
  unsafe paths, checks that `ModManifest.json`'s `name` matches the mod id, unpacks next to the Mods
  folder and swaps the folder in. A failed install leaves the old version in place.
- **What's installed** is recorded in `%APPDATA%\SynnMods\installed.json` per Mods folder, so the
  app can tell which version you have. Mod folders it didn't install show as *Not managed* or
  *version unknown*.
- **App updates** come from the repo's latest GitHub Release
  (`releases/latest/download/releases.win.json`), which is why mod releases are never marked
  "latest".

## Publishing mods

Needs the GitHub CLI signed in as the repo owner (`gh auth login`). Nothing is uploaded unless you
pass `-Publish`.

1. Build the mods (Unity `BumBumBuilder.BuildAndInstall` / `GhostMommyBuilder.BuildAndInstall`).
   By default they land in the Steam Core Keeper `Mods` folder, which is the script's default source
   (`sourceDir` in [corekeeper/publish.json](corekeeper/publish.json)). Or let the script run Unity:
   add `-Build` (close the Unity editor first).
2. Dry run: package and check the hashes:

   ```powershell
   .\tools\publish-mods.ps1 -Game corekeeper -Version 1.0.0
   ```

   This writes `dist\corekeeper\<Mod>-<version>.zip`. The zips are deterministic, so an unchanged
   mod gives the same hash. The script stops if a mod's `ModManifest.json` `requiredOn` doesn't
   match `mods.json` (Core Keeper mods that add entities or items must be `ClientAndServer` = 3).
3. Optional: try it in the app without uploading:

   ```powershell
   .\tools\publish-mods.ps1 -Game corekeeper -Version 1.0.0 -LocalCatalog $env:TEMP\synn-test
   SynnMods.exe --catalog $env:TEMP\synn-test
   ```

4. Publish and push:

   ```powershell
   .\tools\publish-mods.ps1 -Game corekeeper -Version 1.0.0 -Publish -Push
   ```

   For each changed mod this creates the release (not marked latest), uploads the zip, downloads it
   back to confirm the hash, then updates `corekeeper\mods.json`. `-Push` commits and pushes
   `mods.json`; without it, commit and push it yourself. Players see the update once `mods.json` is
   on `main`.

Use `-Mods GhostMommy` to publish one mod. A mod whose files changed needs a new `-Version`:
releases are never overwritten, so a version always means the same files.

Remember the multiplayer rule above when you publish: tell everyone to update before the next
session.

### Adding a game

Add a top-level folder with a `game.json` and a `mods.json` (copy `corekeeper/` as a template). In
`game.json`, each target has the Steam app id and/or `steamFolder`, the `modsPath` relative to
the game folder, an optional `detectFile` that proves the folder is right, and the `processNames`
that lock the files while running. Push it and it shows up in everyone's app on the next refresh.
`mods.json` entries with an empty `url`/`sha256` show as *Coming soon*.

## Releasing the app

Signing setup and details: [SIGNING.md](SIGNING.md).

- **In GitHub Actions** (recommended, after the one-time setup in SIGNING.md):

  ```powershell
  git tag v0.1.0
  git push origin v0.1.0
  ```

  [release.yml](.github/workflows/release.yml) builds, signs, and publishes the release as
  **latest** with `SynnMods-win-Setup.exe`, the update feed files and `SHA256SUMS.txt`.

- **Locally**:

  ```powershell
  az login
  .\build.ps1 -Version 0.1.0 -Sign
  gh release create v0.1.0 (Get-ChildItem artifacts\releases -File).FullName --latest --title "Synn Mods v0.1.0"
  ```

`.\build.ps1 -Version 0.1.0` without `-Sign` makes an unsigned build for testing. SmartScreen
will show "unknown publisher" for it, so don't share it.

## Developing

- Requires the .NET 10 SDK. `dotnet build src\SynnMods` builds the app; `dotnet tool restore`
  installs the pinned Velopack CLI (`vpk`).
- `SynnMods.exe --catalog <folder>` (or the `SYNNMODS_CATALOG` environment variable) reads games and
  mods from a local checkout instead of GitHub. `file://` URLs in a local `mods.json` work.
- **Testing**: `dotnet run --project tests\SmokeTest -- <out.png> [<folder with built mods>]` runs
  the installer checks (install, update, bad hash, oversized download, `../` zip paths, wrong mod,
  remove, catalog discovery) in a throwaway temp folder and renders the main window to a PNG. It
  never touches the real game folder or `%APPDATA%\SynnMods`.
- `tools\make-icon.ps1` regenerates `assets\synnmods.ico`.
