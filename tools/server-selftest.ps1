# Runs the KMH server self-test suites OUTSIDE a real RWT server.
# Old loads the shipping launcher; New and RT load the embedded payloads.
# Usage: .\tools\server-selftest.ps1 [-Flavor Old|New|RT] [-RwtServer <matching RWT server build folder>] [-Configuration Debug]
param(
    [ValidateSet("Old", "New", "RT")]
    [string]$Flavor = "RT",
    [string]$RwtServer = "",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [int]$MinSuites = 135,
    [int]$MinChecks = 2527
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

switch ($Flavor) {
    "Old" { $target = Join-Path $repo "Source\bin\$Configuration\net10.0\KMHServerAddon.dll"
            $defaultRwt = 'rwt-26.5.24.1\Source\Server\bin\Debug\net8.0' }
    "New" { $target = Join-Path $repo "Source\bin\payload\KMHAddon.RTShared.dll"
            $defaultRwt = 'rwt-26.6.9.1\Server' }
    "RT"  { $target = Join-Path $repo "Source\bin\payload\KMHAddon.RTServer.dll"
            $defaultRwt = 'rwt-RTServer\Server' }
}

if (-not (Test-Path $target)) {
    Write-Host "  addon assembly not built: $target" -ForegroundColor Yellow
    Write-Host "  build it first: dotnet build Source\KMHServerAddon.csproj -c $Configuration -p:RwtFlavor=$Flavor"
    exit 1
}

if (-not $RwtServer) { $RwtServer = Join-Path (Split-Path -Parent $repo) $defaultRwt }
$marker = if ($Flavor -eq "Old") { "GameServer.dll" } else { "RTShared.dll" }
if (-not (Test-Path (Join-Path $RwtServer $marker))) {
    # Fatal, not a warning: without these the RWT-backed checks throw or vanish and the run still summarises green.
    Write-Host "  RWT server build for flavour ${Flavor} not found ($marker missing in '$RwtServer')." -ForegroundColor Red
    Write-Host "  Pass -RwtServer <folder>. Refusing to run a suite that would silently skip the RWT checks."
    exit 3
}

# Staged, not probed in place: RWT's repacked Newtonsoft.Json fails the strong-name check and takes the suite down.
# Unique per run: a previous run that died holding these files would otherwise block every later one.
$staged = Join-Path ([IO.Path]::GetTempPath()) ("kmh-rwt-server-probe-" + $Flavor + "-" + [Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Force -Path $staged | Out-Null
Get-ChildItem -Path $RwtServer -Filter "*.dll" |
    Where-Object { $_.Name -ne "Newtonsoft.Json.dll" } |
    ForEach-Object { Copy-Item $_.FullName -Destination $staged -Force }

$probe = @($staged)
foreach ($d in @(
    (Join-Path $repo "Source\bin\payload"),
    (Join-Path $repo "Source\bin\$Configuration\net10.0"),
    (Join-Path $repo "Source\KMH.Sdk.Server\bin\$Configuration\net8.0"))) {
    if (Test-Path $d) { $probe += $d }
}

$nugetJson = Join-Path $env:USERPROFILE ".nuget\packages\newtonsoft.json"
$nj = Get-ChildItem -Path $nugetJson -Recurse -Filter "Newtonsoft.Json.dll" -File -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -match '\\lib\\net(standard2\.0|6\.0|8\.0)\\' } |
      Sort-Object FullName -Descending | Select-Object -First 1
if ($nj) { $probe += $nj.Directory.FullName }
else { Write-Host "  warn: no official Newtonsoft.Json.dll found in the NuGet cache" -ForegroundColor Yellow }

$runner = Join-Path $PSScriptRoot "selftest-runner"
# Fails closed if anything under test tries to publish to the real directory - see KmhGlobalSafety.
$env:KMH_SELFTEST = "1"
$output = & dotnet run --project (Join-Path $runner "runner.csproj") -c Release -- $target @probe
$code = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }

# A green summary must not be able to mean "the RWT-backed suites did not run".
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
try { Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue } catch { }

exit $code
