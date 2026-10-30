# Builds every Server Addon release asset: SDK nupkg, media-tools zip, and a framework-dependent plus a self-contained
# zip for each of six RIDs (win-x64/x86/arm64, linux-x64/arm/arm64), then verifies each zip's payload.
# Usage: .\release-all.ps1 [-NoClean]   (-NoClean keeps this version's existing assets)
param(
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"

function Get-ProjectVersion {
    param([string]$ProjectPath)

    $match = Select-String -Path $ProjectPath -Pattern "<Version>([^<]+)</Version>" -List
    if ($match -and $match.Matches.Count -gt 0) {
        return $match.Matches[0].Groups[1].Value
    }

    return "0.0.0"
}

$project = Join-Path $PSScriptRoot "Source\KMHServerAddon.csproj"
$sdkProject = Join-Path $PSScriptRoot "Source\KMH.Sdk.Server\KMH.Sdk.Server.csproj"
$deployScript = Join-Path $PSScriptRoot "deploy.ps1"
$releases = Join-Path $PSScriptRoot "Releases"

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

if (-not (Test-Path $deployScript)) {
    throw "deploy.ps1 not found: $deployScript"
}

if (-not (Test-Path $releases)) {
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
}

$version = Get-ProjectVersion -ProjectPath $project

if (-not $NoClean) {
    Write-Host "[release] Cleaning old v$version generated assets."
    Get-ChildItem $releases -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -like "KMH-ColonyNexus-ServerAddon-v$version-*.zip" -or
            # The pre-rebrand name, so a stale zip under it cannot be picked up by the bundle.
            $_.Name -like "KMHServerAddon-v$version-*.zip" -or
            $_.Name -like "KMH-MediaTools-v$version.zip" -or
            $_.Name -like "KMH.Sdk.Server.$version*.nupkg"
        } |
        Remove-Item -Force
}

# Both layouts for every RID: a host that can't install .NET 10 needs the self-contained zip for its own platform.
$rids = @('win-x64', 'win-x86', 'win-arm64', 'linux-x64', 'linux-arm', 'linux-arm64')
$targets = @()
foreach ($r in $rids) { $targets += @{ Rid = $r; SelfContained = $false } }
foreach ($r in $rids) { $targets += @{ Rid = $r; SelfContained = $true  } }

$i = 0
foreach ($target in $targets) {
    $i++
    $rid = [string]$target.Rid
    $selfContained = [bool]$target.SelfContained
    $label = "$rid$(if ($selfContained) { ' self-contained' } else { '' })"

    Write-Host ""
    Write-Host "==== [$i/$($targets.Count)] $label ====" -ForegroundColor Cyan

    if ($selfContained) {
        & $deployScript -Rid $rid -SelfContained -SkipPack
    }
    else {
        & $deployScript -Rid $rid -SkipPack
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $label with exit code $LASTEXITCODE."
    }
}

if (Test-Path $sdkProject) {
    Write-Host ""
    Write-Host "==== KMH.Sdk.Server NuGet ====" -ForegroundColor Cyan
    & dotnet pack $sdkProject -c Release -o $releases | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "SDK pack failed with exit code $LASTEXITCODE."
    }
}

# A separate download: yt-dlp and ffmpeg carry other licences, and most owners never enable the video server.
$mediaSrc = Join-Path $PSScriptRoot "Templates\media-tools"
if (Test-Path $mediaSrc) {
    Write-Host ""
    Write-Host "==== KMH media tools ====" -ForegroundColor Cyan
    $mediaZip = Join-Path $releases "KMH-MediaTools-v$version.zip"
    if (Test-Path $mediaZip) { Remove-Item $mediaZip -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $w = [System.IO.Compression.ZipFile]::Open($mediaZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in Get-ChildItem $mediaSrc -File) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($w, $f.FullName, "KMH-MediaTools/$($f.Name)")
        }
    }
    finally { $w.Dispose() }

    $need = @('KMH-MediaTools/get-media-tools.ps1', 'KMH-MediaTools/get-media-tools.sh', 'KMH-MediaTools/README.txt')
    $z = [System.IO.Compression.ZipFile]::OpenRead($mediaZip)
    try {
        $have = @($z.Entries | ForEach-Object { $_.FullName })
        $miss = @($need | Where-Object { $have -notcontains $_ })
        if ($miss.Count -gt 0) { throw "Media tools zip is missing: $($miss -join ', ')" }
    }
    finally { $z.Dispose() }
    Write-Host ("[release] Media tools OK - {0} file(s), {1:N1} KB." -f $need.Count, ((Get-Item $mediaZip).Length / 1KB))
}

# Single-target deploy.ps1 runs write into this folder too, so check each zip's payload, not timestamps.
Write-Host ""
Write-Host "==== Package content verification ====" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Builds of one version share an AssemblyVersion; only the build tag (read from source) tells a stale payload apart.
$tagMatch = [regex]::Match([System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Source\KmhVersion.cs')),
                           'BuildTag\s*=\s*"([^"]*)"')
if (-not $tagMatch.Success) { throw "KmhVersion.BuildTag not found - cannot verify which release a package holds." }
$required = @($tagMatch.Groups[1].Value,
              'kmh.hello.incompatible', 'kmh.notify.ack', 'kmh.treasury.open',
              'kmh.config.request', 'kmh.player.ticket')
# Derived from $targets so a new RID cannot be built and then skipped by this check.
$expected = @($targets | ForEach-Object { "$($_.Rid)$(if ($_.SelfContained) { '-selfcontained' })" })
$stale = @()
foreach ($rid in $expected) {
    $zipPath = Join-Path $releases "KMH-ColonyNexus-ServerAddon-v$version-$rid.zip"
    if (-not (Test-Path $zipPath)) { $stale += "$rid (missing)"; continue }

    $found = @{}
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        # Multi-file zips ship the image libraries loose (deploy.ps1 checks single-file); every zip needs the notices.
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
        $mustShip = @("THIRD-PARTY-NOTICES.txt", "LICENSE", "KMH-Data/Icons/LICENSE-Apache-2.0.txt")
        if ($rid -like "*-selfcontained") { $mustShip += @("SixLabors.Fonts.dll", "SixLabors.ImageSharp.Drawing.dll", "SixLabors.ImageSharp.dll",
                                                            "SixLabors.PolygonClipper.dll", "System.IO.Hashing.dll",
                                                            "DOTNET-RUNTIME-LICENSE.TXT", "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT") }
        $absent = @($mustShip | Where-Object { $names -notcontains $_ })
        if ($absent.Count -gt 0) { Write-Host ("  MISSING {0,-24} {1}" -f $rid, ($absent -join ', ')) -ForegroundColor Red; $stale += $rid }
        foreach ($entry in $zip.Entries) {
            # The Linux apphost bundle has NO extension, so an exe/dll filter skips the only file that carries the code.
            if ($entry.Name -match '\.(png|md|txt|json|xml|so|pdb)$') { continue }
            if ($entry.Length -lt 4096) { continue }
            $ms = New-Object System.IO.MemoryStream
            $s = $entry.Open(); $s.CopyTo($ms); $s.Close()
            $bytes = $ms.ToArray(); $ms.Dispose()
            # Literals live in metadata as UTF-16; the payload is embedded, so scan both alignments.
            $text = [System.Text.Encoding]::Unicode.GetString($bytes) +
                    [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) +
                    [System.Text.Encoding]::ASCII.GetString($bytes)
            foreach ($k in $required) { if ($text.Contains($k)) { $found[$k] = $true } }
            if ($found.Count -eq $required.Count) { break }
        }
    }
    finally { $zip.Dispose() }

    $missing = @($required | Where-Object { -not $found.ContainsKey($_) })
    if ($missing.Count -gt 0) {
        Write-Host ("  STALE {0,-26} missing: {1}" -f $rid, ($missing -join ', ')) -ForegroundColor Red
        $stale += $rid
    }
    else { Write-Host ("  ok    {0,-26} carries all {1} marker(s)" -f $rid, $required.Count) -ForegroundColor Green }
}
if ($stale.Count -gt 0) {
    throw "Release packages are not current: $($stale -join ', '). Rebuild the full matrix before publishing."
}
Write-Host "[release] All $($expected.Count) targets verified current from their packaged binaries." -ForegroundColor Green

Write-Host ""
Write-Host "[release] Done. Upload these assets to the GitHub release:" -ForegroundColor Green
Get-ChildItem $releases -File |
    Where-Object {
        $_.Name -like "KMH-ColonyNexus-ServerAddon-v$version-*.zip" -or
        $_.Name -like "KMH.Sdk.Server.$version*.nupkg"
    } |
    Sort-Object Name |
    ForEach-Object {
        "  {0,-56} {1,8:N1} KB" -f $_.Name, ($_.Length / 1KB)
    }

Write-Host ""
Write-Host "[release] Done: v$version."