# Assembles an acceptance server from the operator's RWT server download plus a KMH build; refuses one that cannot boot.
# Key trap: RWT's net45 Newtonsoft needs System.Security.Permissions.dll on .NET 8, or JSON writes fail unexplained.
# Usage: .\tools\prepare-acceptance-server.ps1 -RwtServer <RWT Server folder> -Destination <folder> [-VerifyOnly]
param(
    [string]$RwtServer = "",
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$KmhPublish = "",
    [switch]$VerifyOnly,
    # Replaces only KMH's files, keeping the RWT half as shipped when there is no pristine copy to re-copy from.
    [switch]$UpdateKmhOnly,
    [switch]$KeepKmhData,
    # Only for a folder that is deliberately keeping the identity it already has, such as an in-place upgrade.
    [switch]$KeepInheritedIdentity
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$problems = New-Object System.Collections.Generic.List[string]
function Bad($m)  { $problems.Add($m); Write-Host "  FAIL: $m" -ForegroundColor Red }
function Good($m) { Write-Host "  ok:   $m" -ForegroundColor Green }
function Note($m) { Write-Host "  note: $m" -ForegroundColor Yellow }

# The TFM is stamped in the assembly's metadata; the net45 build is the one with the transitive dependency.
function Get-AssemblyTfm {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    foreach ($tfm in @('.NETFramework,Version=v4.5', '.NETFramework,Version=v4.6.1', '.NETFramework,Version=v4.7.2',
                       '.NETStandard,Version=v2.0', '.NETCoreApp,Version=v6.0', '.NETCoreApp,Version=v8.0')) {
        if ($text.IndexOf($tfm) -ge 0) { return $tfm }
    }
    return "unknown"
}


$kmhFiles = @("KMHServerAddon.exe", "KMHServerAddon.dll", "KMHServerAddon.deps.json",
              "KMHServerAddon.runtimeconfig.json", "KMH.Sdk.Server.dll")

# RWT's own assemblies, plus the copy of Newtonsoft KMH deliberately binds from RWT's folder rather than shipping.
$rwtOwned = @("RTServer", "RTClient", "RTShared", "RTNetwork", "GameServer", "GameClient", "Shared", "TCPNetwork",
              "MessagePack", "MessagePack.Annotations", "Mono.Nat", "Microsoft.NET.StringTools", "Newtonsoft.Json")

# Harmony, Discord.Net and ImageSharp live only in the KMH publish; a slim RWT download has none of them.
function Copy-KmhDependencies {
    param([string]$From, [string]$To)
    $copied = 0
    foreach ($f in Get-ChildItem $From -File) {
        if ($f.Extension -notin @(".dll", ".exe", ".json")) { continue }
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
        if ($rwtOwned -contains $stem) { continue }
        if ($kmhFiles -contains $f.Name) { continue }
        Copy-Item $f.FullName $To -Force
        $copied++
    }
    if ($copied -gt 0) { Write-Host "[prepare] $copied KMH dependency file(s) copied" }
}

if ($UpdateKmhOnly) {
    if (-not (Test-Path $Destination)) { throw "-UpdateKmhOnly needs an existing folder: $Destination" }
    if (-not $KmhPublish) { $KmhPublish = Join-Path $repoRoot "Source\bin\publish\win-x64-selfcontained" }
    if (-not (Test-Path $KmhPublish)) {
        throw "KMH publish not found: $KmhPublish`n  Build it first: .\deploy.ps1 -Rid win-x64 -SelfContained -SkipPack"
    }

    # A running server holds these open, and a half-copied set boots as a version nobody built.
    Get-Process -Name KMHServerAddon -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like (Join-Path $Destination '*') } |
        ForEach-Object { Write-Host "[prepare] stopping running server (pid $($_.Id))"; Stop-Process -Id $_.Id -Force; Start-Sleep -Seconds 3 }

    foreach ($f in $kmhFiles) {
        $src = Join-Path $KmhPublish $f
        if (Test-Path $src) { Copy-Item $src $Destination -Force } else { Bad "KMH publish is missing $f" }
    }
    Copy-KmhDependencies -From $KmhPublish -To $Destination
    Write-Host "[prepare] KMH files updated in place; KMH-Data and the RWT half untouched."
}

if (-not $VerifyOnly -and -not $UpdateKmhOnly) {
    if (-not $RwtServer) { throw "-RwtServer is required unless -VerifyOnly or -UpdateKmhOnly is used." }
    if (-not (Test-Path $RwtServer)) { throw "RWT server folder not found: $RwtServer" }

    if (-not $KmhPublish) { $KmhPublish = Join-Path $repoRoot "Source\bin\publish\win-x64-selfcontained" }
    if (-not (Test-Path $KmhPublish)) {
        throw "KMH publish not found: $KmhPublish`n  Build it first: .\deploy.ps1 -Rid win-x64 -SelfContained -SkipPack"
    }

    Write-Host "[prepare] RWT server : $RwtServer"
    Write-Host "[prepare] KMH build  : $KmhPublish"
    Write-Host "[prepare] Destination: $Destination"

    if (Test-Path $Destination) {
        # KMH-Data carries the ServerId and the signing key; re-preparing must not silently mint a new identity.
        $data = Join-Path $Destination "KMH-Data"
        $stash = $null
        if ($KeepKmhData -and (Test-Path $data)) {
            $stash = Join-Path ([System.IO.Path]::GetTempPath()) ("kmh-data-" + [guid]::NewGuid().ToString("N"))
            Move-Item $data $stash
            Note "KMH-Data preserved (ServerId and signing key kept)"
        }
        Remove-Item $Destination -Recurse -Force
        New-Item -ItemType Directory -Path $Destination | Out-Null
        if ($stash) { Move-Item $stash (Join-Path $Destination "KMH-Data") }
    } else {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    Copy-Item (Join-Path $RwtServer "*") $Destination -Recurse -Force -Exclude @("KMH-Data", "Logs")

    # Only KMH's own files, so RWT's assemblies stay exactly as they shipped.
    foreach ($f in $kmhFiles) {
        $src = Join-Path $KmhPublish $f
        if (Test-Path $src) { Copy-Item $src $Destination -Force } else { Bad "KMH publish is missing $f" }
    }
    Copy-KmhDependencies -From $KmhPublish -To $Destination
    # Some RWT builds (26.10.4.1) ship no Newtonsoft at all, so KMH's copy goes in; one RWT ships is never overwritten.
    $kmhNewtonsoft = Join-Path $KmhPublish "Newtonsoft.Json.dll"
    if (-not (Test-Path (Join-Path $Destination "Newtonsoft.Json.dll")) -and (Test-Path $kmhNewtonsoft)) {
        Copy-Item $kmhNewtonsoft $Destination
        Note "this RWT ships no Newtonsoft.Json; KMH's own copy was added"
    }
    # Per FILE, not per folder: the addon creates KMH-Data\Icons itself, so a folder check would skip the icons.
    $kmhData = Join-Path $KmhPublish "KMH-Data"
    if (Test-Path $kmhData) {
        $srcIcons = Join-Path $kmhData "Icons"
        $dstIcons = Join-Path $Destination "KMH-Data\Icons"
        if (Test-Path $srcIcons) {
            New-Item -ItemType Directory -Path $dstIcons -Force | Out-Null
            foreach ($icon in Get-ChildItem $srcIcons -File) {
                $dst = Join-Path $dstIcons $icon.Name
                if (-not (Test-Path $dst)) { Copy-Item $icon.FullName $dst -Force }
            }
        }
        foreach ($sub in Get-ChildItem $kmhData -Directory | Where-Object { $_.Name -ne "Icons" }) {
            if (-not (Test-Path (Join-Path $Destination "KMH-Data\$($sub.Name)"))) {
                Copy-Item $sub.FullName (Join-Path $Destination "KMH-Data") -Recurse -Force
            }
        }
    }

    # A folder built on someone else's KMH-Data would heartbeat as that server and log into its Discord bot.
    if ($KeepKmhData -and -not $KeepInheritedIdentity) {
        & (Join-Path $PSScriptRoot "Sanitize-KmhClone.ps1") -Path $Destination -KeepPlayerData
    }
}


Write-Host ""
Write-Host "=== Runtime dependencies ===" -ForegroundColor Cyan

$rwtEntry = @("RTServer.dll", "GameServer.dll") | Where-Object { Test-Path (Join-Path $Destination $_) }
# A self-contained RWT download is one bundled .exe with no loose assembly beside it, and boots perfectly well.
$rwtBundle = @("RTServer.exe", "GameServer.exe") | Where-Object { Test-Path (Join-Path $Destination $_) }
if (-not $rwtEntry -and -not $rwtBundle) { Bad "no RWT server assembly or executable - this is not an RWT server folder" }
elseif (-not $rwtEntry) { Good "RWT server present as a bundled executable ($($rwtBundle -join ', '))" }
else { Good "RWT server assembly present ($($rwtEntry -join ', '))" }

$newtonsoft = Join-Path $Destination "Newtonsoft.Json.dll"
if (-not (Test-Path $newtonsoft)) {
    Bad "Newtonsoft.Json.dll is missing - KMH binds RWT's copy and cannot serialize anything without it"
} else {
    $tfm = Get-AssemblyTfm $newtonsoft
    Good "Newtonsoft.Json.dll present ($tfm)"

    if ($tfm -like '.NETFramework*') {
        $perm = Join-Path $Destination "System.Security.Permissions.dll"
        if (Test-Path $perm) {
            Good "System.Security.Permissions.dll present (required by the $tfm Newtonsoft on .NET 8)"
        } else {
            Bad ("System.Security.Permissions.dll is MISSING. The Newtonsoft.Json in this folder is the $tfm build, " +
                 "which needs it at runtime on .NET 8. Without it every KMH config write fails and RWT's own config " +
                 "loader dies in a SemaphoreFullException. It ships with the official RWT server download - copy it " +
                 "from there. KMH does not redistribute RWT assemblies.")
        }
    }
}

# Read from KMH's own deps.json rather than a hand-written list, so a dependency added later is checked too.
$deps = Join-Path $Destination "KMHServerAddon.deps.json"
if (Test-Path $deps) {
    $missing = @()
    foreach ($m in [regex]::Matches((Get-Content $deps -Raw), '"([A-Za-z0-9_.]+\.dll)"\s*:\s*\{')) {
        $name = $m.Groups[1].Value
        if ($name -eq "KMHServerAddon.dll") { continue }
        if (-not (Test-Path (Join-Path $Destination $name))) { $missing += $name }
    }
    if ($missing.Count -gt 0) {
        Bad ("KMH dependencies missing from this folder: " + (($missing | Select-Object -Unique) -join ', ') +
             ". The server boots and then fails to load its payload. Re-run without -VerifyOnly, or copy them " +
             "from the KMH publish folder.")
    } else { Good "every assembly KMH's deps.json names is present" }
}

Write-Host ""
Write-Host "=== KMH build ===" -ForegroundColor Cyan

$rc = Join-Path $Destination "KMHServerAddon.runtimeconfig.json"
if (-not (Test-Path $rc)) { Bad "KMHServerAddon.runtimeconfig.json is missing" }
else {
    $selfContainedKmh = (Get-Content $rc -Raw) -match 'includedFrameworks'
    # A self-contained RWT leaves hostfxr here; a framework-dependent KMH then dies with "No frameworks were found".
    $appLocalHost = Test-Path (Join-Path $Destination "hostfxr.dll")
    if ($appLocalHost -and -not $selfContainedKmh) {
        Bad ("this folder carries its own hostfxr.dll (RWT published self-contained), so a framework-dependent KMH " +
             "build cannot start here. Rebuild with: .\deploy.ps1 -Rid win-x64 -SelfContained")
    } elseif ($selfContainedKmh) {
        Good "KMH is self-contained, which matches a folder that carries its own host"
    } else {
        Good "KMH is framework-dependent and this folder has no app-local host"
    }
}

$addon = Join-Path $Destination "KMHServerAddon.dll"
if (Test-Path $addon) {
    # FileVersion is the same on every build of a version; only the build tag tells a fresh copy from a stale one.
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($addon)
    $landedTag = if ($info.ProductVersion -match 'rel-\d+') { $Matches[0] } else { "" }

    $sourceTag = ""
    $versionFile = Join-Path $repoRoot "Source\KmhVersion.cs"
    if (Test-Path $versionFile) {
        $m = Select-String -Path $versionFile -Pattern 'BuildTag\s*=\s*"(rel-\d+)"' -List
        if ($m) { $sourceTag = $m.Matches[0].Groups[1].Value }
    }

    if (-not $landedTag) {
        Bad "KMHServerAddon.dll carries no build tag - this is not a KMH release build"
    } elseif ($sourceTag -and $landedTag -ne $sourceTag) {
        Bad ("KMHServerAddon.dll is $landedTag but this repo is $sourceTag - the publish folder is stale. " +
             "Rebuild it: .\deploy.ps1 -Rid win-x64 -SelfContained -SkipPack")
    } else {
        Good "KMHServerAddon.dll $($info.FileVersion) ($landedTag)"
    }
} else { Bad "KMHServerAddon.dll is missing" }

foreach ($cfg in @("Configs\ServerConfig.json")) {
    if (Test-Path (Join-Path $Destination $cfg)) { Good "$cfg present" } else { Bad "$cfg is missing" }
}

Write-Host ""
if ($problems.Count -gt 0) {
    Write-Host "  NOT READY - $($problems.Count) problem(s) above. This folder would fail at boot in a way that does" -ForegroundColor Red
    Write-Host "  not name the real cause, which is exactly what this check exists to prevent." -ForegroundColor Red
    exit 1
}
Write-Host "  Acceptance server ready: $Destination" -ForegroundColor Green
exit 0
