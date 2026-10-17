#requires -Version 7.0
<##
Build and publish the current, committed main revision. Never replaces a tag or asset.
-DryRun performs all read-only preflight checks and the local build/tests, without
creating tags/releases. Requires an authenticated gh CLI and an already pushed main.
##>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$NotesFile,
    [string]$GameDir = 'F:\SteamLibrary\steamapps\common\Slay the Spire 2',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$repo = 'olegsubdiv/octoFixes'
$root = $PSScriptRoot

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    $output = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed (exit $LASTEXITCODE): $($Arguments -join ' ')"
    }
    return ($output -join "`n")
}

function Assert-CleanTree {
    $status = Invoke-Checked git @('status', '--porcelain=v1', '--untracked-files=all')
    if ($status) {
        throw "Commit or remove the following changes before publishing (ignored build artifacts are allowed):`n$status"
    }
}

function Get-Release {
    # Unlike `release view`, a missing release is distinguishable from an API error.
    $json = Invoke-Checked gh @('api', '--paginate', '--slurp', "repos/$repo/releases")
    $pages = ConvertFrom-Json -InputObject $json -NoEnumerate
    foreach ($page in $pages) {
        foreach ($item in $page) {
            if ($item.tag_name -eq $tag) { return $item }
        }
    }
    return $null
}

Push-Location $root
try {
    foreach ($command in 'git', 'gh', 'dotnet') {
        Get-Command $command -ErrorAction Stop | Out-Null
    }
    $notes = (Resolve-Path -LiteralPath $NotesFile).Path
    if (-not (Test-Path -LiteralPath $notes -PathType Leaf) -or
        [string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $notes -Raw))) {
        throw 'NotesFile must be a non-empty release notes text file.'
    }
    $branch = Invoke-Checked git @('branch', '--show-current')
    if ($branch -ne 'main') { throw "Publish from main, not '$branch'." }
    Assert-CleanTree
    $origin = Invoke-Checked git @('remote', 'get-url', 'origin')
    if ($origin -notmatch '^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)olegsubdiv/octoFixes(?:\.git)?/?$') {
        throw "Unexpected origin: $origin. Expected github.com/$repo."
    }
    $head = Invoke-Checked git @('rev-parse', 'HEAD')
    $remoteMain = Invoke-Checked git @('ls-remote', '--exit-code', 'origin', 'refs/heads/main')
    if (($remoteMain -split '\s+')[0] -ne $head) {
        throw 'Push the committed main branch to origin before publishing; local HEAD must equal remote main.'
    }
    Invoke-Checked gh @('auth', 'status', '--hostname', 'github.com') | Out-Host
    $manifestPath = Join-Path $root 'src/octoFixes.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $version = [string]$manifest.version
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Manifest version must be X.Y.Z: '$version'."
    }
    $tag = "v$version"
    $release = Get-Release
    if ($release -and -not $release.draft) {
        throw "Release $tag is already published. Increment the version; published releases are immutable."
    }
    $localTags = Invoke-Checked git @('tag', '--list', $tag)
    if ($localTags) {
        $localCommit = Invoke-Checked git @('rev-parse', "$tag^{commit}")
        if ($localCommit -ne $head) { throw "Local tag $tag points to a different commit." }
    }
    $remoteTags = Invoke-Checked git @('ls-remote', 'origin', "refs/tags/$tag", "refs/tags/$tag^{}")
    if ($remoteTags) {
        $lines = @($remoteTags -split "`n")
        $peeled = @($lines | Where-Object { $_ -match '\^\{\}$' })
        $tagCommit = (($lines[0] -split '\s+')[0])
        if ($peeled.Count) { $tagCommit = ($peeled[0] -split '\s+')[0] }
        if ($tagCommit -ne $head) { throw "Remote tag $tag points to a different commit; it will not be changed." }
    }

    & (Join-Path $root 'build.ps1') -Zip -GameDir $GameDir
    if (-not $?) { throw 'Build/test/package script failed.' }
    Assert-CleanTree
    if ((Invoke-Checked git @('rev-parse', 'HEAD')) -ne $head) {
        throw 'HEAD changed during the build. Restart publishing.'
    }
    $dll = Join-Path $root 'src/bin/Release/octoFixes.dll'
    $builtManifestPath = Join-Path $root 'src/bin/Release/octoFixes.json'
    $builtManifest = Get-Content -LiteralPath $builtManifestPath -Raw | ConvertFrom-Json
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($dll).Version
    $expectedVersion = [version]"$version.0"
    if ($assemblyVersion -ne $expectedVersion -or $builtManifest.version -ne $version) {
        throw "Version mismatch: source=$version, built manifest=$($builtManifest.version), assembly=$assemblyVersion."
    }
    $zip = Join-Path $root 'dist/octoFixes.zip'
    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "Missing package: $zip" }
    # Verify the ZIP actually contains the tested DLL and the current manifest.
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($file in @(@{ Name = 'octoFixes.dll'; Path = $dll },
                            @{ Name = 'octoFixes.json'; Path = $builtManifestPath })) {
            $entries = @($archive.Entries | Where-Object { $_.Name -ceq $file.Name })
            if ($entries.Count -ne 1) { throw "Package must contain exactly one $($file.Name)." }
            $stream = $entries[0].Open()
            try { $packedHash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
            finally { $stream.Dispose() }
            if ($packedHash -ne (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash) {
                throw "Packaged $($file.Name) differs from the tested build."
            }
        }
    }
    finally { $archive.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksum = "$zip.sha256"
    Set-Content -LiteralPath $checksum -Value "$zipHash  octoFixes.zip" -Encoding ascii
    Write-Host "Verified $tag at $head; SHA256 $zipHash"
    if ($DryRun) {
        Write-Host 'Dry run complete. No tags, uploads or releases were created.'
        return
    }

    if (-not $remoteTags) {
        if (-not $localTags) {
            Invoke-Checked git @('tag', '-a', $tag, '-m', "octoFixes $version", $head) | Out-Host
        }
        Invoke-Checked git @('push', 'origin', "refs/tags/$tag") | Out-Host
    }
    # Recheck after building; another publisher may have finished meanwhile.
    $release = Get-Release
    if ($release -and -not $release.draft) { throw "Release $tag has already been published." }
    if (-not $release) {
        Invoke-Checked gh @('release', 'create', $tag, '--repo', $repo, '--verify-tag', '--draft',
            '--title', "octoFixes $version", '--notes-file', $notes) | Out-Host
        $release = Get-Release
        if (-not $release -or -not $release.draft) { throw 'Expected draft release after creation.' }
    }
    # Retrying a partial upload is safe: compare existing bytes, upload only missing assets.
    foreach ($assetPath in @($zip, $checksum)) {
        $assetName = Split-Path -Leaf $assetPath
        $existing = @($release.assets | Where-Object { $_.name -ceq $assetName })
        if ($existing.Count -gt 1) { throw "Duplicate draft asset: $assetName" }
        if ($existing.Count -eq 1) {
            $tempDir = Join-Path ([IO.Path]::GetTempPath()) ("octoFixes-release-" + [guid]::NewGuid())
            New-Item -ItemType Directory -Path $tempDir | Out-Null
            try {
                Invoke-Checked gh @('release', 'download', $tag, '--repo', $repo, '--pattern', $assetName,
                    '--dir', $tempDir) | Out-Host
                $downloadedHash = (Get-FileHash -LiteralPath (Join-Path $tempDir $assetName) -Algorithm SHA256).Hash
                if ($downloadedHash -ne (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash) {
                    throw "Draft asset $assetName differs from this build. No asset was replaced. Inspect the draft before retrying."
                }
            }
            finally {
                # Only the known files created in this unique temporary directory are removed.
                $download = Join-Path $tempDir $assetName
                if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
                Remove-Item -LiteralPath $tempDir
            }
        }
        else {
            Invoke-Checked gh @('release', 'upload', $tag, $assetPath, '--repo', $repo) | Out-Host
        }
    }
    $release = Get-Release
    if (-not $release -or -not $release.draft) { throw 'Release is no longer a draft.' }
    if (@($release.assets).Count -ne 2) { throw 'Draft must contain exactly the ZIP and its checksum.' }
    Invoke-Checked gh @('release', 'edit', $tag, '--repo', $repo, '--notes-file', $notes,
        '--draft=false', '--latest') | Out-Host
    Write-Host "Published: https://github.com/$repo/releases/tag/$tag"
    Write-Host "Permanent download: https://github.com/$repo/releases/latest/download/octoFixes.zip"
}
finally { Pop-Location }
