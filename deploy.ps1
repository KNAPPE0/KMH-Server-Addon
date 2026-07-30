# KMH Server Addon - build one release asset.
#
# Usage:
#   .\deploy.ps1
#   .\deploy.ps1 -Rid linux-x64
#   .\deploy.ps1 -Rid linux-x64 -SelfContained
#   .\deploy.ps1 -Rid win-x64 -SkipPack
param(
    [string]$Rid = "win-x64",
    [switch]$SelfContained,
    [switch]$SkipPack,
    [string]$Deploy = (Join-Path $PSScriptRoot "Deploy")
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

# Package guard: RWT and its native deps are referenced Private=False, so they must never appear in our output.
function Assert-NoUnexpectedBinaries {
    param([string]$Path)

    $blocked = @(
        "GameServer", "GameClient", "RTServer", "RTClient",
        "Shared", "RTShared", "TCPNetwork", "RTNetwork",
        "MessagePack", "Mono.Nat"
    )

    $found = Get-ChildItem $Path -Recurse -File -Include *.dll,*.exe -ErrorAction SilentlyContinue |
        Where-Object { $blocked -contains [System.IO.Path]::GetFileNameWithoutExtension($_.Name) }

    if ($found) {
        throw "Unexpected binary in publish: $(($found | ForEach-Object Name) -join ', ')."
    }

    Write-Host "[deploy] Package guard OK - no unexpected binaries."
}

# A missing payload starts fine and fails only on that server generation, so check the shipped exe itself
# rather than trusting the build inputs.
function Assert-PayloadsEmbedded {
    param([string]$ExePath)

    $bytes = [System.IO.File]::ReadAllBytes($ExePath)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)

    foreach ($name in @("KMHAddon.RTShared.dll", "KMHAddon.RTServer.dll")) {
        if ($text.IndexOf($name) -lt 0) {
            throw "Published executable does not embed $name - that RWT generation would be unsupported."
        }
    }

    Write-Host "[deploy] Payload embedding OK - both new-generation payloads are inside the executable."
}

# Self-contained runs with no RWT beside it at publish time, so it must carry Newtonsoft.Json itself.
function Assert-NewtonsoftPresent {
    param([string]$Path)

    if (-not (Test-Path (Join-Path $Path "Newtonsoft.Json.dll"))) {
        throw "Self-contained package is missing Newtonsoft.Json.dll."
    }

    Write-Host "[deploy] Dependency OK - Newtonsoft.Json.dll included."
}

$project = Join-Path $PSScriptRoot "Source\KMHServerAddon.csproj"
$sdkProject = Join-Path $PSScriptRoot "Source\KMH.Sdk.Server\KMH.Sdk.Server.csproj"
$releases = Join-Path $PSScriptRoot "Releases"

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

if (-not (Test-Path $releases)) {
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
}

$version = Get-ProjectVersion -ProjectPath $project
$ext = if ($Rid.StartsWith("win")) { ".exe" } else { "" }
$publishName = if ($SelfContained) { "$Rid-selfcontained" } else { $Rid }
$publish = Join-Path $PSScriptRoot "Source\bin\publish\$publishName"

# One payload per new-generation RWT build; a stale one silently ships old code to that generation.
# Rebuild first, then verify each version.
$payloads = @(
    @{ Flavor = "New"; Dll = "KMHAddon.RTShared.dll"; For = "26.6.x (GameServer.dll)" },
    @{ Flavor = "RT";  Dll = "KMHAddon.RTServer.dll"; For = "renamed build (RTServer.dll)" }
)

foreach ($p in $payloads) {
    Write-Host "[deploy] Building the $($p.Flavor) payload for $($p.For) so the launcher embeds a fresh one."
    & dotnet build $project -c Release -p:RwtFlavor=$($p.Flavor) -v q | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Payload build (RwtFlavor=$($p.Flavor)) failed with exit code $LASTEXITCODE."
    }

    $payloadDll = Join-Path $PSScriptRoot "Source\bin\payload\$($p.Dll)"
    if (-not (Test-Path $payloadDll)) {
        throw "Payload missing after build: $payloadDll"
    }

    $payloadVersion = [System.Reflection.AssemblyName]::GetAssemblyName($payloadDll).Version.ToString(3)
    if ($payloadVersion -ne $version) {
        throw "Payload version $payloadVersion does not match project version $version - stale payload would ship."
    }

    Write-Host "[deploy] Payload OK - $([System.IO.Path]::GetFileNameWithoutExtension($p.Dll)) v$payloadVersion matches project v$version."
}

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

if ($SelfContained) {
    Write-Host "[deploy] Publishing KMHServerAddon v$version for $Rid, self-contained folder, no RWT bundled."

    & dotnet publish $project `
        -c Release `
        -r $Rid `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:CopyLocalLockFileAssemblies=true `
        -p:DebugType=none `
        -o $publish | Out-Host
}
else {
    Write-Host "[deploy] Publishing KMHServerAddon v$version for $Rid, framework-dependent single-file, no RWT bundled."

    & dotnet publish $project `
        -c Release `
        -r $Rid `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:CopyLocalLockFileAssemblies=true `
        -p:DebugType=none `
        -p:AllowedReferenceRelatedFileExtensions=none `
        -o $publish | Out-Host
}

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed for $Rid$(if ($SelfContained) { ' self-contained' } else { '' }) with exit code $LASTEXITCODE."
}

$exe = Join-Path $publish "KMHServerAddon$ext"
if (-not (Test-Path $exe)) {
    throw "Published executable not found: $exe"
}

Assert-NoUnexpectedBinaries -Path $publish
Assert-PayloadsEmbedded -ExePath $(if ($SelfContained) { Join-Path $publish "KMHServerAddon.dll" } else { $exe })

if ($SelfContained) {
    Assert-NewtonsoftPresent -Path $publish
}

if (Test-Path $Deploy) {
    Remove-Item (Join-Path $Deploy "*") -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $Deploy -Force | Out-Null
}

if ($SelfContained) {
    Copy-Item (Join-Path $publish "*") $Deploy -Recurse -Force
}
else {
    Copy-Item $exe $Deploy -Force
}

$template = Join-Path $PSScriptRoot "Templates\kmh-data"
if (Test-Path $template) {
    $dataOut = Join-Path $Deploy "KMH-Data"
    if (Test-Path $dataOut) {
        Remove-Item $dataOut -Recurse -Force
    }
    Copy-Item $template $dataOut -Recurse -Force
}

# Discord embed icons ship in every zip at KMH-Data/Icons (the one folder the addon reads). Staged from the
# committed source set and guarded so they can never silently drop out of a release again.
$iconsSrc = Join-Path $PSScriptRoot "Source\Assets\Icons"
$iconsOut = Join-Path $Deploy "KMH-Data\Icons"
New-Item -ItemType Directory -Path $iconsOut -Force | Out-Null
Copy-Item (Join-Path $iconsSrc "*") $iconsOut -Recurse -Force
$iconCount = (Get-ChildItem $iconsOut -Filter *.png -ErrorAction SilentlyContinue | Measure-Object).Count
if ($iconCount -lt 1) {
    throw "No icons staged into KMH-Data/Icons - Source\Assets\Icons is missing or empty."
}
Write-Host "[deploy] Icons OK - $iconCount icon(s) staged into KMH-Data/Icons."

$setup = Join-Path $PSScriptRoot "SETUP.txt"
if (Test-Path $setup) {
    Copy-Item $setup $Deploy -Force
}

$readme = Join-Path $PSScriptRoot "README.md"
if (Test-Path $readme) {
    Copy-Item $readme $Deploy -Force
}

if (-not $SkipPack -and (Test-Path $sdkProject)) {
    Write-Host "[deploy] Packing KMH.Sdk.Server NuGet package."
    & dotnet pack $sdkProject -c Release -o $releases | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "SDK pack failed with exit code $LASTEXITCODE."
    }
}

$assetName = "KMHServerAddon-v$version-$Rid$(if ($SelfContained) { '-selfcontained' } else { '' }).zip"
$zip = Join-Path $releases $assetName
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

Compress-Archive -Path (Join-Path $Deploy "*") -DestinationPath $zip -Force

$zipMb = [Math]::Round((Get-Item $zip).Length / 1MB, 2)
$exeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 2)

Write-Host ""
Write-Host "[deploy] Done." -ForegroundColor Green
Write-Host "[deploy] Release asset: $zip ($zipMb MB)"
Write-Host "[deploy] App:           $exe ($exeMb MB)"

$rwtExe = "GameServer$ext / RTServer$ext"

if ($SelfContained) {
    Write-Host "[deploy] Install: extract the full zip beside the official RWT server ($rwtExe) and run KMHServerAddon$ext."
}
else {
    Write-Host "[deploy] Install: place KMHServerAddon$ext beside the official RWT server ($rwtExe) and run KMHServerAddon$ext. Requires .NET 8."
}