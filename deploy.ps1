# KMH Server Addon - release packaging
#
# Produces a SINGLE-FILE KMHServerAddon.exe that contains only KMH code + our
# own deps (Harmony, Discord.Net, the SDK). It does NOT bundle RimWorld
# Together - redistributing RWT would break its license. At runtime the exe
# loads RWT's DLLs from the folder it runs in (the admin's RWT server folder).
#
# The exe is framework-dependent: the server needs the .NET 8 runtime
# installed. A self-contained single-file isn't possible for a Harmony addon -
# Harmony's runtime patching can't locate clrjit inside a self-contained bundle.
#
# Usage:  .\deploy.ps1            # win-x64
#         .\deploy.ps1 -Rid linux-x64
param(
    [string]$Rid    = "win-x64",
    [string]$Deploy = (Join-Path $PSScriptRoot "Deploy")
)
$ErrorActionPreference = "Stop"

$Proj = Join-Path $PSScriptRoot "Source\KMHServerAddon.csproj"
$ext  = if ($Rid -like "win*") { ".exe" } else { "" }

# --- read version from csproj for the zip name ---
$ver = "0.0.0"
$m = Select-String -Path $Proj -Pattern "<Version>([^<]+)</Version>" -List
if ($m -and $m.Matches.Count -gt 0) { $ver = $m.Matches[0].Groups[1].Value }

# --- publish the single-file, KMH-only exe ---
Write-Host "[deploy] Publishing single-file KMHServerAddon ($Rid, framework-dependent, no RWT bundled)"
$pub = Join-Path $PSScriptRoot "Source\bin\publish\$Rid"
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
& dotnet publish "$Proj" -c Release -r $Rid --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none `
    -o $pub | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $pub "KMHServerAddon$ext"
if (-not (Test-Path $exe)) { throw "Published exe not found at $exe" }

# Safety net: make sure we did NOT accidentally bundle any RWT assembly.
$leaked = Get-ChildItem $pub -File | Where-Object { $_.Name -in @("GameServer.dll","Shared.dll","TCPNetwork.dll","MessagePack.dll","Mono.Nat.dll") }
if ($leaked) { throw "RWT assemblies leaked into the publish: $($leaked.Name -join ', ') - check Private=False on the RWT references." }

# --- assemble the release folder: the exe + setup + KMH-Data templates (incl. icons) ---
if (Test-Path $Deploy) { Remove-Item -Recurse -Force "$Deploy\*" } else { New-Item -ItemType Directory -Path $Deploy -Force | Out-Null }
Copy-Item $exe $Deploy -Force

# Stage the KMH-Data starter folder. The source is tracked lowercase (Templates/kmh-data)
# so git stays clean on case-insensitive Windows; the release always ships it PascalCase
# (KMH-Data) to match what the addon reads at runtime (KmhDataPaths), so a case-sensitive
# host (Linux) finds it too. Copy contents into an explicit KMH-Data folder rather than
# copying the folder by name, so the case is fixed regardless of the source's tracked case.
$DataDst = Join-Path $Deploy "KMH-Data"
New-Item -ItemType Directory -Path $DataDst -Force | Out-Null
$TemplateData = Join-Path $PSScriptRoot "Templates\kmh-data"
if (Test-Path $TemplateData) {
    Write-Host "[deploy] Staging KMH-Data/ templates"
    Copy-Item (Join-Path $TemplateData "*") $DataDst -Recurse -Force
}

# Bundled Discord embed icons go inside KMH-Data/Icons - the exact folder the addon
# reads at runtime (KmhDataPaths.IconsDir). It's auto-created on first boot, but
# pre-filling it here means embeds have art out of the box and owners see the folder
# they're meant to customize. Sourced from the committed Source/Assets/Icons set.
$IconsSrc = Join-Path $PSScriptRoot "Source\Assets\Icons"
if (Test-Path $IconsSrc) {
    $IconsDst = Join-Path $DataDst "Icons"
    New-Item -ItemType Directory -Path $IconsDst -Force | Out-Null
    Copy-Item (Join-Path $IconsSrc "*") $IconsDst -Recurse -Force
    Write-Host "[deploy] Staging KMH-Data/Icons (bundled embed icons)"
}

$Setup = Join-Path $PSScriptRoot "SETUP.txt"
if (Test-Path $Setup) { Copy-Item $Setup $Deploy -Force }

# --- pack the SDK (NuGet + standalone bundle) for extension authors ---
$SdkProj = Join-Path $PSScriptRoot "Source\KMH.Sdk.Server\KMH.Sdk.Server.csproj"
$Releases = Join-Path $PSScriptRoot "Releases"
if (-not (Test-Path $Releases)) { New-Item -ItemType Directory -Path $Releases -Force | Out-Null }
if (Test-Path $SdkProj) {
    Write-Host "[deploy] Packing KMH.Sdk.Server NuGet package"
    & dotnet pack "$SdkProj" -c Release -o $Releases | Out-Host
}

# --- zip the release ---
$Zip = Join-Path $Releases "KMHServerAddon-v$ver-$Rid.zip"
if (Test-Path $Zip) { Remove-Item $Zip -Force }
Compress-Archive -Path (Join-Path $Deploy "*") -DestinationPath $Zip -Force
$mb = [Math]::Round(((Get-Item $Zip).Length / 1MB), 2)
$exeMb = [Math]::Round(((Get-Item $exe).Length / 1MB), 2)

Write-Host ""
Write-Host "[deploy] Done."
Write-Host "[deploy] Single exe:   $Deploy\KMHServerAddon$ext  (${exeMb} MB, KMH only - no RWT)"
Write-Host "[deploy] Release zip:  $Zip  (${mb} MB)"
Write-Host "[deploy] Install: drop KMHServerAddon$ext next to the official GameServer.exe and"
Write-Host "[deploy]          run KMHServerAddon$ext (not GameServer.exe). Needs .NET 8."
