param(
    [switch]$Zip,
    [switch]$Install,
    [string]$GameDir = $env:STS2_GAME_DIR,
    [string]$HextechDll,
    [string]$Net9RuntimeDir
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $GameDir) { $GameDir = 'F:\SteamLibrary\steamapps\common\Slay the Spire 2' }
$gameData = Join-Path $GameDir 'data_sts2_windows_x86_64'
foreach ($name in @('sts2.dll','0Harmony.dll','GodotSharp.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $gameData $name))) { throw "Missing $name. Pass -GameDir or set STS2_GAME_DIR." }
}
if (-not $Net9RuntimeDir) {
    $runtime = & dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0) { throw 'dotnet runtime lookup failed' }
    $candidates = foreach ($line in $runtime) {
        if ($line -match '^Microsoft.NETCore.App (9\.\d+\.\d+) \[(.+)\]$') {
            [pscustomobject]@{ Version = [version]$Matches[1]; Path = Join-Path $Matches[2] $Matches[1] }
        }
    }
    $Net9RuntimeDir = ($candidates | Sort-Object Version -Descending | Select-Object -First 1).Path
}
if (-not $Net9RuntimeDir -or -not (Test-Path -LiteralPath (Join-Path $Net9RuntimeDir 'System.Runtime.dll'))) {
    throw '.NET 9 runtime required. Install the runtime or pass -Net9RuntimeDir.'
}
if (-not $HextechDll) {
    $steamApps = Split-Path -Parent (Split-Path -Parent $GameDir)
    $HextechDll = Join-Path $steamApps 'workshop\content\2868840\3747501308\lib\0.111.0\HextechRunes.dll'
}
if (-not (Test-Path -LiteralPath $HextechDll)) { throw 'HextechRunes test DLL missing. Pass -HextechDll (game-compatible variant).' }
$manifest = Get-Content (Join-Path $root 'src/octoFixes.json') -Raw | ConvertFrom-Json
[xml]$project = Get-Content (Join-Path $root 'src/octoFixes.csproj') -Raw
if ($manifest.version -ne [string]$project.Project.PropertyGroup.Version) { throw 'Manifest/project version mismatch' }
$projects = @('src/octoFixes.csproj','tests/MultiEnchantmentCoopFix.Tests.csproj','tests/Hextech/HextechFormRuneCoopFix.Tests.csproj')
foreach ($projectPath in $projects) {
    Write-Host "Build: $projectPath"
    & dotnet build (Join-Path $root $projectPath) -c Release -nologo -v q "-p:Sts2Dir=$gameData" "-p:Net9RuntimeDir=$Net9RuntimeDir"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $projectPath" }
}
foreach ($test in @(
    @{ Dir = 'tests/bin/Release'; Name = 'MultiEnchantmentCoopFix.Tests' },
    @{ Dir = 'tests/Hextech/bin/Release'; Name = 'HextechFormRuneCoopFix.Tests' }
)) {
    $testDir = Join-Path $root $test.Dir
    $deps = Join-Path $testDir ($test.Name + '.deps.json')
    if (Test-Path -LiteralPath $deps) { Remove-Item -LiteralPath $deps -Force }
    '{ "runtimeOptions": { "tfm": "net9.0", "rollForward": "Major", "framework": { "name": "Microsoft.NETCore.App", "version": "9.0.0" } } }' |
        Set-Content -LiteralPath (Join-Path $testDir ($test.Name + '.runtimeconfig.json')) -Encoding ascii
    foreach ($name in @('sts2.dll','0Harmony.dll','GodotSharp.dll')) { Copy-Item -LiteralPath (Join-Path $gameData $name) -Destination $testDir -Force }
    Write-Host "Test: $($test.Name)"
    & dotnet (Join-Path $testDir ($test.Name + '.dll')) $HextechDll $gameData
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($test.Name)" }
}
$out = Join-Path $root 'src/bin/Release'
$dllVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $out 'octoFixes.dll')).Version
if ($dllVersion -ne [version]($manifest.version + '.0')) { throw 'Built assembly version mismatch' }
if ($Zip -or $Install) {
    $dist = Join-Path $root 'dist'
    $stage = Join-Path $dist ('package-' + [guid]::NewGuid().ToString('N'))
    $package = Join-Path $stage 'octoFixes'
    New-Item -ItemType Directory -Force $package | Out-Null
    Copy-Item -LiteralPath (Join-Path $out 'octoFixes.dll'),(Join-Path $out 'octoFixes.json') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'installer/install.bat'),(Join-Path $root 'installer/install.ps1'),(Join-Path $root 'README.md') -Destination $package
    $zipPath = Join-Path $dist 'octoFixes.zip'
    # Keep the old archive until the new archive has been produced successfully.
    $newZip = Join-Path $stage 'octoFixes.zip'
    Compress-Archive -LiteralPath $package -DestinationPath $newZip
    Copy-Item -LiteralPath $newZip -Destination $zipPath -Force
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  octoFixes.zip" | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ascii
    Write-Host "Package: $zipPath (v$($manifest.version))"
    if ($Install) { & (Join-Path $package 'install.ps1') -GameDir $GameDir; if ($LASTEXITCODE -ne 0) { throw 'Installation failed' } }
}
