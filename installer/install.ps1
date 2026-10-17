# octoFixes installer (Windows PowerShell 5.1 compatible).
# Finds the Slay the Spire 2 install via Steam (registry -> libraryfolders.vdf -> appmanifest_2868840.acf),
# Replaces the old companion fixes transactionally; backups stay outside mods.
param([string]$GameDir)

$ErrorActionPreference = "Stop"
$AppId = "2868840"
$ModId = "octoFixes"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-GameDir {
    $steamRoots = @()
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam") {
        try {
            $p = Get-ItemProperty $key -ErrorAction Stop
            foreach ($name in "SteamPath", "InstallPath") {
                if ($p.$name -and (Test-Path $p.$name)) { $steamRoots += ($p.$name -replace '/', '\') }
            }
        } catch {}
    }
    foreach ($fallback in "C:\Program Files (x86)\Steam", "C:\Program Files\Steam") {
        if (Test-Path $fallback) { $steamRoots += $fallback }
    }
    $libraries = @()
    foreach ($root in ($steamRoots | Select-Object -Unique)) {
        $libraries += $root
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '^\s*"path"\s+"(.+)"\s*$') { $libraries += ($Matches[1] -replace '\\\\', '\') }
            }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $acf = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path $acf) {
            $installdir = "Slay the Spire 2"
            foreach ($line in Get-Content $acf) {
                if ($line -match '^\s*"installdir"\s+"(.+)"\s*$') { $installdir = $Matches[1] }
            }
            $candidate = Join-Path $lib "steamapps\common\$installdir"
            if (Test-Path (Join-Path $candidate "SlayTheSpire2.exe")) { return $candidate }
        }
        # Manifest missing but folder present (e.g. moved library) ? accept if the exe is there.
        $candidate2 = Join-Path $lib "steamapps\common\Slay the Spire 2"
        if (Test-Path (Join-Path $candidate2 "SlayTheSpire2.exe")) { return $candidate2 }
    }
    return $null
}

Write-Host ""
Write-Host "=== octoFixes - installer ===" -ForegroundColor Cyan
Write-Host ""

function Assert-GameStopped {
    if (Get-Process -Name "SlayTheSpire2" -ErrorAction SilentlyContinue) {
        throw "Close Slay the Spire 2 before installing. No installation files have been changed."
    }
}

function Assert-SafePath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe path outside expected directory: $full"
    }
    # Refuse junctions/symlinks anywhere in an installation path, including ancestors.
    $probe = $full
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            $item = Get-Item -LiteralPath $probe -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing linked installation path: $probe"
            }
        }
        $parent = Split-Path -Parent $probe
        if ($parent -eq $probe) { break }
        $probe = $parent
    }
    return $full
}

function Assert-Package([string]$Folder) {
    $dll = Join-Path $Folder "$ModId.dll"
    $json = Join-Path $Folder "$ModId.json"
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf) -or
        -not (Test-Path -LiteralPath $json -PathType Leaf)) {
        throw "Missing $ModId.dll / $ModId.json in $Folder. Unpack the entire ZIP first."
    }
    $manifest = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
    if ($manifest.id -cne $ModId) { throw "Unexpected mod ID in $json" }
    if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw "Invalid manifest version in $json"
    }
    $versionParts = @(([string]$manifest.version).Split('.'))
    while ($versionParts.Count -lt 4) { $versionParts += '0' }
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($dll)
    if ($assembly.Name -cne $ModId -or $assembly.Version -ne [version]($versionParts -join '.')) {
        throw "DLL identity/version does not match the $ModId manifest."
    }
    return [string]$manifest.version
}

function Assert-SameFiles([string]$Source, [string]$Destination) {
    foreach ($name in @("$ModId.dll", "$ModId.json", "README.md")) {
        $from = Join-Path $Source $name
        if (Test-Path -LiteralPath $from -PathType Leaf) {
            $to = Join-Path $Destination $name
            if (-not (Test-Path -LiteralPath $to -PathType Leaf) -or
                (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash) {
                throw "Installed file verification failed: $name"
            }
        }
    }
}

$moved = @()
$activated = $false
$stage = $null
$dest = $null
$backup = $null
$lock = $null
try {
    $version = Assert-Package $here
    Assert-GameStopped
    if (-not $GameDir) { $GameDir = Find-GameDir }
    while (-not $GameDir -or -not (Test-Path -LiteralPath (Join-Path $GameDir "SlayTheSpire2.exe") -PathType Leaf)) {
        Write-Host "Could not find SlayTheSpire2.exe. Paste the game directory, or press Enter to abort."
        $GameDir = (Read-Host "path").Trim('"', ' ')
        if (-not $GameDir) { throw "Installation cancelled." }
    }
    $GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
    $mods = Assert-SafePath (Join-Path $GameDir 'mods') $GameDir
    $backupRoot = Assert-SafePath (Join-Path $GameDir 'octoFixes-backups') $GameDir
    $dest = Assert-SafePath (Join-Path $mods $ModId) $mods
    $token = [guid]::NewGuid().ToString('N')
    $backup = Assert-SafePath (Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $token)) $backupRoot
    $stage = Assert-SafePath (Join-Path $backup 'staged-octoFixes') $backup
    $targets = @()
    foreach ($id in @($ModId, 'MultiEnchantmentCoopFix', 'HextechFormRuneCoopFix')) {
        $target = Assert-SafePath (Join-Path $mods $id) $mods
        if (Test-Path -LiteralPath $target) {
            if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw "Expected a directory: $target" }
            $targets += [pscustomobject]@{ Original = $target; Backup = (Join-Path $backup $id) }
        }
    }
    # All checks above are read-only. Lock prevents two installers modifying the same game.
    $lockPath = Assert-SafePath (Join-Path $GameDir '.octoFixes-install.lock') $GameDir
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($name in @("$ModId.dll", "$ModId.json", 'README.md')) {
        $from = Join-Path $here $name
        if (Test-Path -LiteralPath $from -PathType Leaf) { Copy-Item -LiteralPath $from -Destination $stage }
    }
    $null = Assert-Package $stage
    Assert-SameFiles $here $stage
    Assert-GameStopped
    New-Item -ItemType Directory -Path $mods -Force | Out-Null
    foreach ($target in $targets) {
        Move-Item -LiteralPath $target.Original -Destination $target.Backup
        $moved += $target
    }
    Move-Item -LiteralPath $stage -Destination $dest
    $activated = $true
    $null = Assert-Package $dest
    Assert-SameFiles $here $dest
    Write-Host "Installed octoFixes v$version -> $dest" -ForegroundColor Green
    if ($moved.Count) { Write-Host "Previous installations saved outside mods: $backup" }
    Write-Host "All players should install the same release before starting the game."
    exit 0
} catch {
    $failure = $_.Exception.Message
    $rollbackErrors = @()
    if ($activated) {
        try {
            # Keep the unsuccessful new install for diagnosis; never recursively delete mods.
            Move-Item -LiteralPath $dest -Destination (Join-Path $backup 'failed-octoFixes')
        } catch { $rollbackErrors += $_.Exception.Message }
    }
    for ($i = $moved.Count - 1; $i -ge 0; $i--) {
        try { Move-Item -LiteralPath $moved[$i].Backup -Destination $moved[$i].Original }
        catch { $rollbackErrors += $_.Exception.Message }
    }
    Write-Host "ERROR: $failure" -ForegroundColor Red
    if ($rollbackErrors.Count) {
        Write-Host "ROLLBACK INCOMPLETE. Keep the game closed. Backups: $backup" -ForegroundColor Red
        foreach ($message in $rollbackErrors) { Write-Host $message -ForegroundColor Red }
    } elseif ($moved.Count) {
        Write-Host "Previous installation restored."
    }
    exit 1
} finally {
    if ($lock) { $lock.Dispose() }
}
