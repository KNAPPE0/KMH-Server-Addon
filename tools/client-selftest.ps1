# Runs the KMH-Patch client self-test OUTSIDE RimWorld.
# Needs a real RimWorld install: Krafs.Rimworld.Ref reference assemblies have method bodies that throw.
# Usage: .\tools\client-selftest.ps1 [-List] [-Flavor Old|New|RT] [-RimWorldManaged <Managed dir>] [-RwtAssemblies <dir>]
param(
    [string]$RimWorldManaged = "",
    [string]$RwtAssemblies = "",
    [string]$Flavor = "RT",
    [int]$MinSuites = 10,
    [int]$MinChecks = 1170,
    [switch]$List
)

$ErrorActionPreference = "Stop"
$repoRoot   = Split-Path -Parent $PSScriptRoot
$clientRoot = Join-Path (Split-Path -Parent $repoRoot) 'KMH-Patch'

$asmName = switch ($Flavor) {
    "Old" { "KMHPatch.GameClient.dll" }
    "New" { "KMHPatch.RTClient.dll" }
    default { "KMHPatch.RTClientV2.dll" }
}
$clientDll = Join-Path $clientRoot "1.6\KMHLib\$asmName"
if (-not (Test-Path $clientDll)) {
    Write-Host "  client assembly not built: $clientDll" -ForegroundColor Yellow
    Write-Host "  build it first: dotnet build Source\KMHPatch.csproj -p:RwtFlavor=$Flavor -c Release"
    exit 1
}

if (-not $RimWorldManaged) {
    foreach ($c in @(
        "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed",
        "D:\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed",
        "E:\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed",
        "E:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed")) {
        if (Test-Path (Join-Path $c "Assembly-CSharp.dll")) { $RimWorldManaged = $c; break }
    }
}
if (-not $RimWorldManaged -or -not (Test-Path (Join-Path $RimWorldManaged "Assembly-CSharp.dll"))) {
    Write-Host "  no RimWorld install found - pass -RimWorldManaged <path to RimWorldWin64_Data\Managed>" -ForegroundColor Yellow
    exit 1
}

# The client references its own SDK, so that has to resolve or the whole suite throws instead of running.
$rwtProbe = @()
$sdkDir = Join-Path $clientRoot "1.6\Assemblies"
if (Test-Path (Join-Path $sdkDir "KMH.Sdk.Client.dll")) { $rwtProbe += $sdkDir }
else { Write-Host "  warn: KMH.Sdk.Client.dll not found in $sdkDir - build the mod first" -ForegroundColor Yellow }

foreach ($d in (Get-ChildItem -Path $clientRoot, $repoRoot -Recurse -Directory -Filter ".rwt-runtime" -ErrorAction SilentlyContinue)) {
    $rwtProbe += $d.FullName
}

# RWT assemblies must match the flavour: a mismatched generation loads, then throws TypeLoadException on RTShared.
if (-not $RwtAssemblies) {
    $RwtAssemblies = switch ($Flavor) {
        "Old" { Join-Path (Split-Path -Parent $repoRoot) 'rwt-26.5.24.1\1.6\Assemblies' }
        "New" { Join-Path (Split-Path -Parent $repoRoot) 'rwt-26.6.9.1\1.6\Assemblies' }
        default { Join-Path (Split-Path -Parent $repoRoot) 'rwt-RTServer\Client' }
    }
}
$rwtMarker = if ($Flavor -eq "Old") { "Shared.dll" } else { "RTShared.dll" }
if ($RwtAssemblies -and (Test-Path (Join-Path $RwtAssemblies $rwtMarker))) {
    # Staged, not probed in place: RWT's repacked Newtonsoft.Json is rejected by the loader and takes the suite down.
    $staged = Join-Path ([IO.Path]::GetTempPath()) ("kmh-rwt-probe-" + $Flavor)
    New-Item -ItemType Directory -Force -Path $staged | Out-Null
    Get-ChildItem -Path $RwtAssemblies -Filter "*.dll" |
        Where-Object { $_.Name -ne "Newtonsoft.Json.dll" } |
        ForEach-Object { Copy-Item $_.FullName -Destination $staged -Force }
    $rwtProbe += $staged
}
else {
    # Fatal, not a warning: without these the RWT-backed checks fail or vanish and the run still summarises green.
    Write-Host "  RWT assemblies for flavour $Flavor were not found ($rwtMarker missing in '$RwtAssemblies')." -ForegroundColor Red
    Write-Host "  Pass -RwtAssemblies <folder>. Refusing to run a suite that would silently skip the RWT checks."
    exit 3
}

# .rwt-runtime never carries Newtonsoft.Json (a second identity breaks Harmony's JIT hook), and RWT's repacked copy
# is rejected as not strong-named, so find the official build: NuGet cache first, then the server's output.
$newtonsoft = $null
$nugetJson = Join-Path $env:USERPROFILE ".nuget\packages\newtonsoft.json"
if (Test-Path $nugetJson) {
    $newtonsoft = Get-ChildItem -Path $nugetJson -Recurse -Filter "Newtonsoft.Json.dll" -File -ErrorAction SilentlyContinue |
                  Where-Object { $_.FullName -match '\\lib\\net(standard2\.0|6\.0|47|472)\\' } |
                  Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $newtonsoft) {
    $serverBin = Join-Path $repoRoot "Source\bin\Release\net10.0\Newtonsoft.Json.dll"
    if (Test-Path $serverBin) { $newtonsoft = Get-Item $serverBin }
}
if ($newtonsoft) { $rwtProbe += $newtonsoft.Directory.FullName }
else { Write-Host "  warn: no official Newtonsoft.Json.dll found - build the server once so it is on disk" -ForegroundColor Yellow }

$runner = Join-Path $PSScriptRoot "selftest-runner"
if (-not (Test-Path (Join-Path $runner "runner.csproj"))) {
    Write-Host "  runner project missing: $runner" -ForegroundColor Yellow
    exit 1
}

if ($List) { $env:KMH_LIST = "1" }
$probe = @($RimWorldManaged) + $rwtProbe
$output = & dotnet run --project (Join-Path $runner "runner.csproj") -c Release -- $clientDll @probe
$code = $LASTEXITCODE
$env:KMH_LIST = $null
$output | ForEach-Object { Write-Host $_ }
if ($List) { exit $code }

# A missing probe path shrinks the run instead of failing it, so assert suite and check floors with nothing skipped.
$summary = $output | Where-Object { $_ -match '^suites:' } | Select-Object -Last 1
if (-not $summary) { Write-Host "  no suite summary - the run did not complete" -ForegroundColor Red; exit 4 }
if ($summary -notmatch 'suites:\s*(\d+) run,\s*(\d+) skipped\s*\|\s*checks:\s*(\d+) pass,\s*(\d+) fail') {
    Write-Host "  could not read the suite summary: $summary" -ForegroundColor Red; exit 4
}
$suites = [int]$Matches[1]; $skipped = [int]$Matches[2]; $checks = [int]$Matches[3]

if ($suites -lt $MinSuites -or $checks -lt $MinChecks -or $skipped -gt 0) {
    Write-Host ("  gate: expected at least $MinSuites suite(s) and $MinChecks check(s) with none skipped; " +
                "got $suites suite(s), $checks check(s), $skipped skipped.") -ForegroundColor Red
    Write-Host "  That usually means a probe path is missing, not that the code shrank."
    exit 5
}
exit $code
