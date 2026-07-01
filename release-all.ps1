# KMH Server Addon - build the official GitHub release assets.
#
#   KMH.Sdk.Server.<version>.nupkg
#   KMHServerAddon-v<version>-win-x64.zip
#   KMHServerAddon-v<version>-win-x86.zip
#   KMHServerAddon-v<version>-win-arm64.zip
#   KMHServerAddon-v<version>-linux-x64.zip
#   KMHServerAddon-v<version>-linux-arm.zip
#   KMHServerAddon-v<version>-linux-arm64.zip
#   KMHServerAddon-v<version>-linux-x64-selfcontained.zip
#
# Usage:
#   .\release-all.ps1
#   .\release-all.ps1 -NoClean
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
            $_.Name -like "KMHServerAddon-v$version-*.zip" -or
            $_.Name -like "KMH.Sdk.Server.$version*.nupkg"
        } |
        Remove-Item -Force
}

$targets = @(
    @{ Rid = "win-x64";   SelfContained = $false },
    @{ Rid = "win-x86";   SelfContained = $false },
    @{ Rid = "win-arm64"; SelfContained = $false },
    @{ Rid = "linux-x64"; SelfContained = $false },
    @{ Rid = "linux-arm"; SelfContained = $false },
    @{ Rid = "linux-arm64"; SelfContained = $false },
    @{ Rid = "linux-x64"; SelfContained = $true  }
)

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

Write-Host ""
Write-Host "[release] Done. Upload these assets to the GitHub release:" -ForegroundColor Green
Get-ChildItem $releases -File |
    Where-Object {
        $_.Name -like "KMHServerAddon-v$version-*.zip" -or
        $_.Name -like "KMH.Sdk.Server.$version*.nupkg"
    } |
    Sort-Object Name |
    ForEach-Object {
        "  {0,-56} {1,8:N1} KB" -f $_.Name, ($_.Length / 1KB)
    }

Write-Host ""
Write-Host "[release] Do not be dumb duh $version."