<#
.SYNOPSIS
  Builds the Synn Mods installer (Velopack): artifacts\releases\SynnMods-win-Setup.exe plus the
  update feed files. With -Sign, every exe is Authenticode-signed with Azure Artifact Signing.

.DESCRIPTION
  1. dotnet publish: one self-contained SynnMods.exe (no .NET install needed on friends' PCs).
  2. vpk pack: Setup.exe (per-user install, no admin, Start menu + desktop shortcuts), a portable
     zip, and the update package + releases.win.json that installed copies update from.
  3. -Sign: Velopack calls tools\sign.ps1 for SynnMods.exe, its Update.exe and Setup.exe. That
     uses your `az login` session and signing\metadata.json (see SIGNING.md). No secrets here.

  Publishing a release is a separate, deliberate step: push a v<version> tag and GitHub Actions
  builds, signs and uploads it (see README.md), or upload artifacts\releases yourself with
  `gh release create`.

.EXAMPLE
  .\build.ps1 -Version 0.1.0          # unsigned, for testing
.EXAMPLE
  .\build.ps1 -Version 0.1.0 -Sign    # signed, after az login
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [switch] $Sign
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got $Version)." }
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root 'artifacts\publish'
$releases = Join-Path $root 'artifacts\releases'
foreach ($d in $publish, $releases) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }

Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    dotnet publish src\SynnMods\SynnMods.csproj -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $pack = @(
        'vpk', 'pack',
        '--packId', 'SynnMods',
        '--packVersion', $Version,
        '--packDir', $publish,
        '--mainExe', 'SynnMods.exe',
        '--packTitle', 'Synn Mods',
        '--packAuthors', 'Synn',
        '--icon', (Join-Path $root 'assets\synnmods.ico'),
        '--outputDir', $releases
    )
    if ($Sign) {
        # Fail early with a clear message rather than halfway through packing.
        if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
            throw 'The Azure CLI is needed to sign: winget install -e --id Microsoft.AzureCLI, then az login'
        }
        az account show --only-show-errors 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Not signed in to Azure. Run: az login' }
        $signScript = Join-Path $root 'tools\sign.ps1'
        # One file per call, one at a time (the first call downloads Microsoft's signing client).
        $pack += @('--signTemplate', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$signScript`" `"{{file}}`"",
                   '--signParallel', '1')
    }
    dotnet @pack
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }
} finally {
    Pop-Location
}

$setup = Join-Path $releases 'SynnMods-win-Setup.exe'
if (-not (Test-Path $setup)) { throw "Expected $setup was not created." }

if ($Sign) {
    # Velopack signs copies, so check the installer and the exe inside the update package.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $check = Join-Path ([IO.Path]::GetTempPath()) "synnmods-sigcheck-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $check | Out-Null
    try {
        $nupkg = Get-ChildItem $releases -Filter '*-full.nupkg' | Select-Object -First 1
        $zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
        try {
            $entry = $zip.Entries | Where-Object { $_.FullName -match '(^|/)SynnMods\.exe$' } | Select-Object -First 1
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $check 'SynnMods.exe'))
        } finally { $zip.Dispose() }
        foreach ($f in $setup, (Join-Path $check 'SynnMods.exe')) {
            $sig = Get-AuthenticodeSignature $f
            if ($sig.Status -ne 'Valid') { throw "$f is not validly signed ($($sig.Status))." }
            Write-Host "Signed: $(Split-Path -Leaf $f) by $($sig.SignerCertificate.Subject)"
        }
    } finally { Remove-Item -Recurse -Force $check }
} else {
    Write-Warning 'Unsigned build: Windows SmartScreen will warn "unknown publisher". Use -Sign for anything you share.'
}

Get-ChildItem $releases | Format-Table Name, @{ n = 'MB'; e = { '{0:0.0}' -f ($_.Length / 1MB) } } -AutoSize | Out-String | Write-Host
Write-Host "Installer: $setup"
