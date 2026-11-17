<#
.SYNOPSIS
  Packages a game's mods as zips and (with -Publish) uploads them as GitHub Releases and updates
  <game>\mods.json with each mod's version, download URL, SHA-256 and size.

.DESCRIPTION
  Without -Publish this only builds the zips in dist\<game>\ and prints what it would do, so you
  can check them first. Nothing leaves your PC.

  With -Publish, for each changed mod it:
    1. creates the GitHub release <game>-<ModId>-v<version> (marked "not latest", so it never
       hides the Synn Mods app release) and attaches the zip, using your logged-in `gh` CLI;
    2. writes the new version, URL, SHA-256 and size into <game>\mods.json.
  Then commit and push mods.json (or pass -Push). Friends see the update once mods.json is on
  GitHub's main branch (raw.githubusercontent.com may take up to 5 minutes).

  Releases are immutable here: if a mod changed but its version didn't, the script stops and asks
  you to pass a new -Version, so nobody's installed copy silently differs from the manifest.

  The source folder (sourceDir in <game>\publish.json, or -SourceDir) must contain one folder per
  mod, each with ModManifest.json, e.g. the game's StreamingAssets\Mods folder after a build.
  -Build first runs each mod's Unity builder (BuildAndInstall -modsPath) into dist\<game>\build.

.EXAMPLE
  .\tools\publish-mods.ps1 -Game corekeeper -Version 1.0.0
  Builds dist\corekeeper\BumBum-1.0.0.zip and GhostMommy-1.0.0.zip and shows the hashes.

.EXAMPLE
  .\tools\publish-mods.ps1 -Game corekeeper -Mods GhostMommy -Version 1.0.1 -Publish -Push

.EXAMPLE
  .\tools\publish-mods.ps1 -Game corekeeper -Version 1.0.0 -LocalCatalog $env:TEMP\synn-test
  Writes a test catalog that points at the local zips; run the app with --catalog $env:TEMP\synn-test.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Game,
    [string[]] $Mods,                 # default: every mod in <game>\mods.json
    [string] $Version,                # default: keep each mod's current version in mods.json
    [string] $SourceDir,              # default: sourceDir in <game>\publish.json
    [switch] $Build,                  # run the Unity builders first
    [switch] $Publish,                # create GitHub releases and update mods.json
    [switch] $Push,                   # with -Publish: commit and push mods.json
    [string] $LocalCatalog,           # write a test catalog with file:// URLs here
    [string] $Repo = 'karlockhart/synn-mods'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$gameDir = Join-Path $root $Game
$modsJsonPath = Join-Path $gameDir 'mods.json'
if (-not (Test-Path (Join-Path $gameDir 'game.json'))) { throw "No $Game\game.json. Is '$Game' a game folder?" }
$catalog = Get-Content -Raw $modsJsonPath | ConvertFrom-Json
$publishCfgPath = Join-Path $gameDir 'publish.json'
$publishCfg = if (Test-Path $publishCfgPath) { Get-Content -Raw $publishCfgPath | ConvertFrom-Json } else { $null }

if ($Push -and -not $Publish) { throw '-Push only makes sense with -Publish.' }
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "Version must look like 1.2.3 (got $Version)." }

$selected = @($catalog.mods | Where-Object { -not $Mods -or $Mods -contains $_.id })
if ($Mods) {
    foreach ($m in $Mods) { if (-not ($catalog.mods | Where-Object { $_.id -eq $m })) { throw "$m isn't in $Game\mods.json. Add an entry for it first." } }
}
if ($selected.Count -eq 0) { throw 'No mods selected.' }

$dist = Join-Path $root "dist\$Game"
New-Item -ItemType Directory -Force $dist | Out-Null

# ---------- optional Unity build ----------
if ($Build) {
    if (-not $publishCfg -or -not $publishCfg.unity) { throw "No unity section in $publishCfgPath." }
    $unity = $publishCfg.unity
    $buildDir = Join-Path $dist 'build'
    if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
    New-Item -ItemType Directory -Force $buildDir | Out-Null
    foreach ($mod in $selected) {
        $method = $unity.builders.($mod.id)
        if (-not $method) { throw "No Unity builder for $($mod.id) in $publishCfgPath." }
        $log = Join-Path $dist "build-$($mod.id).log"
        Write-Host "Building $($mod.id) with Unity ($method)... (log: $log)"
        $p = Start-Process -FilePath $unity.editor -Wait -PassThru -NoNewWindow -ArgumentList @(
            '-batchmode', '-quit', '-projectPath', "`"$($unity.project)`"", '-executeMethod', $method,
            '-modsPath', "`"$buildDir`"", '-logFile', "`"$log`"")
        if ($p.ExitCode -ne 0) { throw "Unity build of $($mod.id) failed (exit $($p.ExitCode)). See $log. Close the Unity editor if the project is open." }
    }
    $SourceDir = $buildDir
}
if (-not $SourceDir) {
    if (-not $publishCfg -or -not $publishCfg.sourceDir) { throw "Pass -SourceDir or set sourceDir in $publishCfgPath." }
    $SourceDir = $publishCfg.sourceDir
}
if (-not (Test-Path $SourceDir)) { throw "Source folder not found: $SourceDir" }

# ---------- helpers ----------
$requiredOnValues = @{ 'None' = 0; 'Client' = 1; 'Server' = 2; 'ClientAndServer' = 3 }

function New-ModZip([string] $folder, [string] $zipPath) {
    # Deterministic zip: sorted entries, fixed timestamps, forward slashes. Same input, same hash.
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    $base = (Resolve-Path $folder).Path.TrimEnd('\') + '\'
    $files = Get-ChildItem -LiteralPath $folder -Recurse -File | Sort-Object { $_.FullName.Substring($base.Length).Replace('\', '/') } -CaseSensitive
    $fixedTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $fs = [IO.File]::Open($zipPath, 'CreateNew')
    $zip = New-Object IO.Compression.ZipArchive $fs, ([IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $files) {
            $name = $f.FullName.Substring($base.Length).Replace('\', '/')
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTime
            $out = $entry.Open()
            try { $in = [IO.File]::OpenRead($f.FullName); try { $in.CopyTo($out) } finally { $in.Dispose() } }
            finally { $out.Dispose() }
        }
    } finally {
        $zip.Dispose(); $fs.Dispose()
    }
}

function ConvertTo-JsonString([string] $s) {
    if ($null -eq $s) { return '""' }
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($c in $s.ToCharArray()) {
        switch ($c) {
            '"' { [void]$sb.Append('\"') } '\' { [void]$sb.Append('\\') }
            "`n" { [void]$sb.Append('\n') } "`r" { [void]$sb.Append('\r') } "`t" { [void]$sb.Append('\t') }
            default { if ([int]$c -lt 32) { [void]$sb.AppendFormat('\u{0:x4}', [int]$c) } else { [void]$sb.Append($c) } }
        }
    }
    [void]$sb.Append('"')
    $sb.ToString()
}

function Write-ModsJson($mods, [string] $path) {
    # Hand-written so the file stays tidy (Windows PowerShell's ConvertTo-Json indents oddly).
    $items = foreach ($m in $mods) {
        $props = @(
            "      `"id`": $(ConvertTo-JsonString $m.id)",
            "      `"name`": $(ConvertTo-JsonString $m.name)",
            "      `"description`": $(ConvertTo-JsonString $m.description)",
            "      `"version`": $(ConvertTo-JsonString $m.version)",
            "      `"url`": $(ConvertTo-JsonString $m.url)",
            "      `"sha256`": $(ConvertTo-JsonString $m.sha256)",
            "      `"size`": $([long]$m.size)",
            "      `"requiredOn`": $(ConvertTo-JsonString $m.requiredOn)"
        )
        "    {`n" + ($props -join ",`n") + "`n    }"
    }
    $text = "{`n  `"schema`": 1,`n  `"mods`": [`n" + ($items -join ",`n") + "`n  ]`n}`n"
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
}

# ---------- package ----------
$results = @()
foreach ($mod in $selected) {
    $id = $mod.id
    $folder = Join-Path $SourceDir $id
    $manifestPath = Join-Path $folder 'ModManifest.json'
    if (-not (Test-Path $manifestPath)) { throw "$manifestPath not found. Build $id first (or pass -Build)." }
    $manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
    if ($manifest.name -cne $id) { throw "$manifestPath says name '$($manifest.name)', expected '$id'." }

    $wantRequiredOn = $requiredOnValues[[string]$mod.requiredOn]
    if ($null -ne $wantRequiredOn -and [int]$manifest.requiredOn -ne $wantRequiredOn) {
        throw "$id's ModManifest.json has requiredOn=$($manifest.requiredOn), but mods.json says $($mod.requiredOn) ($wantRequiredOn). Rebuild the mod with the right setting or fix mods.json."
    }

    $ver = if ($Version) { $Version } else { [string]$mod.version }
    if (-not $ver) { throw "No version for $id. Pass -Version." }
    $zipName = "$id-$ver.zip"
    $zipPath = Join-Path $dist $zipName
    New-ModZip $folder $zipPath
    $hash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
    $size = (Get-Item $zipPath).Length
    $tag = "$Game-$id-v$ver"
    $url = "https://github.com/$Repo/releases/download/$tag/$zipName"

    $status =
        if ($mod.sha256 -eq $hash -and $mod.version -eq $ver) { 'unchanged' }
        elseif ($mod.sha256 -and $mod.version -eq $ver) { 'changed-same-version' }
        else { 'new' }
    $results += [pscustomobject]@{ Mod = $mod; Id = $id; Version = $ver; Zip = $zipPath; ZipName = $zipName
                                   Sha256 = $hash; Size = $size; Tag = $tag; Url = $url; Status = $status }
}

$results | Format-Table Id, Version, Status, Size, Sha256 -AutoSize | Out-String | Write-Host

$bad = @($results | Where-Object Status -eq 'changed-same-version')
if ($bad.Count -gt 0 -and ($Publish)) {
    throw "These mods changed but kept the same version: $(($bad | ForEach-Object Id) -join ', '). Pass a new -Version."
}

# ---------- local test catalog ----------
if ($LocalCatalog) {
    $testGame = Join-Path $LocalCatalog $Game
    New-Item -ItemType Directory -Force $testGame | Out-Null
    Copy-Item (Join-Path $gameDir 'game.json') $testGame -Force
    $testMods = foreach ($m in $catalog.mods) {
        $copy = $m.PSObject.Copy()
        $r = $results | Where-Object Id -eq $m.id
        if ($r) {
            $copy.version = $r.Version; $copy.sha256 = $r.Sha256; $copy.size = $r.Size
            $copy.url = ([Uri](Resolve-Path $r.Zip).Path).AbsoluteUri
        }
        $copy
    }
    Write-ModsJson $testMods (Join-Path $testGame 'mods.json')
    Write-Host "Wrote a test catalog to $LocalCatalog. Try it: SynnMods.exe --catalog `"$LocalCatalog`""
}

if (-not $Publish) {
    Write-Host "Packaged into $dist. Nothing was uploaded. Re-run with -Publish to release the changed mods."
    return
}

# ---------- publish ----------
# Runs a native command and returns its exit code. Windows PowerShell 5.1 turns anything a native
# command writes to stderr (progress, "release not found") into a terminating error under
# ErrorActionPreference=Stop, so success is judged by the exit code alone.
function Invoke-Native([scriptblock] $Command, [switch] $Quiet) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { & $Command *> $null } else { & $Command 2>&1 | ForEach-Object { Write-Host $_ } }
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $saved }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is not installed: winget install -e --id GitHub.cli' }
if ((Invoke-Native { gh auth status } -Quiet) -ne 0) { throw 'Not signed in to GitHub. Run: gh auth login' }

$toRelease = @($results | Where-Object Status -eq 'new')
if ($toRelease.Count -eq 0) { Write-Host 'Nothing changed; nothing to publish.'; return }

foreach ($r in $toRelease) {
    if ((Invoke-Native { gh release view $r.Tag -R $Repo --json tagName } -Quiet) -eq 0) { throw "Release $($r.Tag) already exists. Releases aren't overwritten; pass a new -Version." }
}

$gameName = (Get-Content -Raw (Join-Path $gameDir 'game.json') | ConvertFrom-Json).name
foreach ($r in $toRelease) {
    Write-Host "Creating release $($r.Tag)..."
    $notes = "$gameName mod **$($r.Mod.name)** $($r.Version).`n`nInstall it with Synn Mods. SHA-256: ``$($r.Sha256)``"
    $title = "$gameName - $($r.Mod.name) $($r.Version)"
    if ((Invoke-Native { gh release create $r.Tag $r.Zip -R $Repo --title $title --notes $notes --latest=false }) -ne 0) { throw "gh release create $($r.Tag) failed." }

    # Check what GitHub serves matches what we hashed.
    $check = Join-Path $dist "verify-$($r.ZipName)"
    if ((Invoke-Native { gh release download $r.Tag -R $Repo -p $r.ZipName -O $check --clobber }) -ne 0) { throw "Couldn't download $($r.ZipName) back from the release to check it." }
    $remote = (Get-FileHash -Algorithm SHA256 $check).Hash.ToLowerInvariant()
    Remove-Item $check
    if ($remote -ne $r.Sha256) { throw "The uploaded $($r.ZipName) hashes to $remote, not $($r.Sha256). mods.json was not changed." }

    $r.Mod.version = $r.Version; $r.Mod.url = $r.Url; $r.Mod.sha256 = $r.Sha256; $r.Mod.size = $r.Size
}

Write-ModsJson $catalog.mods $modsJsonPath
Write-Host "Updated $modsJsonPath."

if ($Push) {
    Push-Location $root
    try {
        $message = "$($gameName): publish $(($toRelease | ForEach-Object { "$($_.Id) $($_.Version)" }) -join ', ')"
        if ((Invoke-Native { git add -- "$Game/mods.json" }) -ne 0) { throw 'git add failed.' }
        if ((Invoke-Native { git commit -m $message }) -ne 0) { throw 'git commit failed.' }
        if ((Invoke-Native { git push }) -ne 0) { throw 'git push failed.' }
    } finally { Pop-Location }
    Write-Host 'Pushed. Friends will see the update in Synn Mods within about 5 minutes.'
} else {
    Write-Host "Now commit and push $Game\mods.json so friends see the update."
}
