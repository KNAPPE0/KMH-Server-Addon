# Runs the KMH-Patch client self-test OUTSIDE RimWorld.
#
# Almost every client check is pure (item-key encoding shared with the server, off-map wealth valuation, envelope
# serialisation, display-text neutralisation), so loading the built client assembly with the real RimWorld managed
# assemblies on the probe path runs them for real, without launching the game.
#
# Note: the NuGet Krafs.Rimworld.Ref package will NOT work here - those are metadata-only reference assemblies whose
# method bodies throw. This needs a real RimWorld install.
#
#   .\tools\client-selftest.ps1                     auto-detect RimWorld, run, summarise
#   .\tools\client-selftest.ps1 -List               print every check
#   .\tools\client-selftest.ps1 -RimWorldManaged X  point at a specific Managed folder
param(
    [string]$RimWorldManaged = "",
    [string]$Flavor = "RT",
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

# KMH's extraction deliberately skips Newtonsoft.Json (a second copy with a different identity breaks Harmony's JIT
# hook), so .rwt-runtime never carries it - find one separately or the client assembly won't load.
# Must be the OFFICIAL strong-named build: RWT ships a repacked copy that the loader rejects with
# "A strongly-named assembly is required". Prefer the NuGet cache, then the server's own build output.
$newtonsoft = $null
$nugetJson = Join-Path $env:USERPROFILE ".nuget\packages\newtonsoft.json"
if (Test-Path $nugetJson) {
    $newtonsoft = Get-ChildItem -Path $nugetJson -Recurse -Filter "Newtonsoft.Json.dll" -File -ErrorAction SilentlyContinue |
                  Where-Object { $_.FullName -match '\\lib\\net(standard2\.0|6\.0|47|472)\\' } |
                  Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $newtonsoft) {
    $serverBin = Join-Path $repoRoot "Source\bin\Release\net8.0\Newtonsoft.Json.dll"
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
& dotnet run --project (Join-Path $runner "runner.csproj") -c Release -- $clientDll @probe
$code = $LASTEXITCODE
$env:KMH_LIST = $null
exit $code
