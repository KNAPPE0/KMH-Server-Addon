# Drives the real config migration over real schema-3 KMH-Data trees, one process per case.
# One process each: the migration uses process-wide state, and "the next boot rolls it back" needs a real next process.
# Usage: .\tools\migration-harness.ps1 [-Flavor Old|New|RT] [-RwtServer <RWT server build folder>] [-Configuration Debug]
param(
    [ValidateSet("Old", "New", "RT")]
    [string]$Flavor = "RT",
    [string]$RwtServer = "",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
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
    Write-Host "  RWT server build for flavour ${Flavor} not found ($marker missing in '$RwtServer')." -ForegroundColor Red
    exit 3
}

# Same staging as the self-test: RWT ships a repacked Newtonsoft.Json the loader rejects by identity.
$staged = Join-Path ([IO.Path]::GetTempPath()) ("kmh-rwt-migrate-probe-" + $Flavor)
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

$harness = Join-Path $PSScriptRoot "migration-harness"
$env:KMH_SELFTEST = "1"

# Built once: `dotnet run` per case would rebuild and dominate the run.
dotnet build (Join-Path $harness "harness.csproj") -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "  harness build failed" -ForegroundColor Red; exit 4 }
$exe = Join-Path $harness "bin\Release\net10.0\migrate.dll"

$cases = @("happy", "backup-fail", "verify-fail", "journal-fail",
           "config-write-fail", "config-read-fail", "schema-fail", "crash-journal", "schema5-minutes")

$pass = 0; $fail = 0; $bad = @()
foreach ($case in $cases) {
    $out = & dotnet $exe $target $case @probe
    $out | ForEach-Object { Write-Host $_ }
    $summary = $out | Where-Object { $_ -match '^checks:' } | Select-Object -Last 1
    if (-not $summary -or $summary -notmatch 'checks:\s*(\d+) pass,\s*(\d+) fail') {
        Write-Host "  case '$case' did not report a summary" -ForegroundColor Red
        $bad += $case; continue
    }
    $pass += [int]$Matches[1]; $fail += [int]$Matches[2]
    if ([int]$Matches[2] -gt 0) { $bad += $case }
    Write-Host ""
}

Write-Host "=== migration harness ===" -ForegroundColor Cyan
Write-Host "cases: $($cases.Count) run   |   checks: $pass pass, $fail fail"

# A case that silently stopped asserting would otherwise read as green.
if ($pass -lt 56) {
    Write-Host "  gate: expected at least 56 checks; got $pass. A case stopped asserting." -ForegroundColor Red
    exit 5
}
if ($bad.Count -gt 0) {
    Write-Host "  FAILED: $($bad -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "  RESULT: ALL PASS" -ForegroundColor Green
exit 0
