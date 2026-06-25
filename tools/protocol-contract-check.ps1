<#
.SYNOPSIS
  KMH client/server protocol contract check. Catches the silent drift that produces blank Server Standings,
  zero-value rows, and ignored responses BEFORE live testing.

.DESCRIPTION
  Compares KMH-Server-Addon (server) against KMH-Patch (client) on three axes:
    1. Kind parity   - both KmhProtocol.cs define the same kmh.* wire kinds with the same string values.
    2. Handler cover - every "client -> server" kind has a server RegisterHandler; every "server -> client"
                       kind has a client RegisterHandler. (Direction comes from the server KmhProtocol.cs.)
    3. DTO parity    - DTO files at the same Features/.../Dto/*.cs path expose the same [JsonProperty] names.

  Read-only. Exits 1 on any hard mismatch (CI-friendly), 0 when the contract holds.

.PARAMETER ServerRoot
  KMH-Server-Addon repo root. Defaults to the parent of this script's tools/ folder.

.PARAMETER ClientRoot
  KMH-Patch repo root. Defaults to a sibling KMH-Patch next to the server repo.
#>
param(
    [string]$ServerRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ClientRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'KMH-Patch')
)

$ErrorActionPreference = 'Stop'
$fail = $false
function Section($t) { Write-Host ''; Write-Host "=== $t ===" -ForegroundColor Cyan }
function Bad($t)     { Write-Host "  FAIL: $t" -ForegroundColor Red;    $script:fail = $true }
function Warn($t)    { Write-Host "  warn: $t" -ForegroundColor Yellow }
function Good($t)    { Write-Host "  ok:   $t" -ForegroundColor Green }

$serverSrc = Join-Path $ServerRoot 'Source'
$clientSrc = Join-Path $ClientRoot 'Source'
foreach ($p in @($serverSrc, $clientSrc)) {
    if (-not (Test-Path $p)) { Write-Host "Source not found: $p" -ForegroundColor Red; exit 2 }
}

# --- parse a KmhProtocol.cs into @{ ConstName = @{ Value=...; Dir=... } } (kmh.* kinds only) ---
function Parse-Kinds($protoPath) {
    $map = @{}
    if (-not (Test-Path $protoPath)) { return $map }
    foreach ($line in Get-Content $protoPath) {
        $m = [regex]::Match($line, 'public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"\s*;(.*)$')
        if (-not $m.Success) { continue }
        $val = $m.Groups[2].Value
        if ($val -notmatch '^kmh\.') { continue }   # skip BuildVersion / usernames
        $dir = 'none'
        $cmt = $m.Groups[3].Value
        if ($cmt -match 'client\s*->\s*server') { $dir = 'c2s' }
        elseif ($cmt -match 'server\s*->\s*client') { $dir = 's2c' }
        $map[$m.Groups[1].Value] = @{ Value = $val; Dir = $dir }
    }
    return $map
}

$serverProto = Join-Path $serverSrc 'SubProtocol\KmhProtocol.cs'
$clientProto = Join-Path $clientSrc 'SubProtocol\KmhProtocol.cs'
$srvKinds = Parse-Kinds $serverProto
$cliKinds = Parse-Kinds $clientProto

# --- collect handler registrations + kind references on each side ---
function Grep-Kinds($root, $pattern) {
    $set = New-Object System.Collections.Generic.HashSet[string]
    Get-ChildItem -Path $root -Recurse -Filter *.cs | ForEach-Object {
        foreach ($mm in [regex]::Matches((Get-Content $_.FullName -Raw), $pattern)) {
            [void]$set.Add($mm.Groups[1].Value)
        }
    }
    return $set
}
$srvHandlers = Grep-Kinds $serverSrc 'KmhRouter\.RegisterHandler\(\s*KmhProtocol\.Kind\.(\w+)'
$cliHandlers = Grep-Kinds $clientSrc 'KmhDispatcher\.RegisterHandler\(\s*KmhProtocol\.Kind\.(\w+)'
$srvRefs     = Grep-Kinds $serverSrc 'KmhProtocol\.Kind\.(\w+)'
$cliRefs     = Grep-Kinds $clientSrc 'KmhProtocol\.Kind\.(\w+)'

# --- 1. Kind parity ---
Section '1. Kind parity (constant name + string value)'
$allNames = ($srvKinds.Keys + $cliKinds.Keys) | Sort-Object -Unique
foreach ($n in $allNames) {
    $inS = $srvKinds.ContainsKey($n); $inC = $cliKinds.ContainsKey($n)
    if ($inS -and -not $inC) { Bad "$n exists on server but NOT client" }
    elseif ($inC -and -not $inS) { Bad "$n exists on client but NOT server" }
    elseif ($srvKinds[$n].Value -ne $cliKinds[$n].Value) {
        Bad "$n value mismatch: server '$($srvKinds[$n].Value)' vs client '$($cliKinds[$n].Value)'"
    }
}
if (-not $fail) { Good "$($allNames.Count) kinds match on both sides" }

# --- 2. Handler coverage (direction-aware, server protocol is the source of truth) ---
Section '2. Handler coverage'
foreach ($n in ($srvKinds.Keys | Sort-Object)) {
    $dir = $srvKinds[$n].Dir
    if ($dir -eq 'c2s') {
        if (-not $srvHandlers.Contains($n)) { Bad "$n (client->server) has NO server handler - requests are silently ignored" }
    }
    elseif ($dir -eq 's2c') {
        if (-not $cliHandlers.Contains($n)) { Bad "$n (server->client) has NO client handler - responses are silently dropped (blank UI)" }
    }
    else {
        if (-not ($srvHandlers.Contains($n) -or $cliHandlers.Contains($n))) { Warn "$n (no direction) is handled on neither side" }
    }
    # Dead-constant hint: defined but referenced nowhere on its own side.
    if (-not $srvRefs.Contains($n)) { Warn "$n is defined but never referenced in the server" }
    if ($cliKinds.ContainsKey($n) -and -not $cliRefs.Contains($n)) { Warn "$n is defined but never referenced in the client" }
}
if (-not $fail) { Good 'every directional kind has a handler on the receiving side' }

# --- 3. DTO JsonProperty parity ---
Section '3. DTO [JsonProperty] parity'
function Get-JsonProps($file) {
    $set = New-Object System.Collections.Generic.HashSet[string]
    foreach ($mm in [regex]::Matches((Get-Content $file -Raw), '\[JsonProperty\(\s*"([^"]+)"')) { [void]$set.Add($mm.Groups[1].Value) }
    return $set
}
# Known server->client folder renames (same DTO, different feature-folder name on each side).
$dtoAlias = @{
    'Features\ItemLabels\Dto\ItemLabelsPush.cs'        = 'Features\Catalog\Dto\ItemLabelsPush.cs'
    'Features\Notifications\Dto\NotificationSnapshot.cs' = 'Features\OfflineMail\Dto\NotificationSnapshot.cs'
}
$dtoFail = $false
Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs | Where-Object { $_.FullName -match '\\Dto\\' } | ForEach-Object {
    $rel = $_.FullName.Substring($serverSrc.Length).TrimStart('\','/')
    $relC = if ($dtoAlias.ContainsKey($rel)) { $dtoAlias[$rel] } else { $rel }
    $clientFile = Join-Path $clientSrc $relC
    if (-not (Test-Path $clientFile)) { Warn "server DTO has no client counterpart: $rel"; return }
    $sp = Get-JsonProps $_.FullName
    $cp = Get-JsonProps $clientFile
    $onlyS = @($sp | Where-Object { -not $cp.Contains($_) })
    $onlyC = @($cp | Where-Object { -not $sp.Contains($_) })
    if ($onlyS.Count -or $onlyC.Count) {
        $dtoFail = $true
        Bad "$rel JsonProperty mismatch"
        if ($onlyS.Count) { Write-Host "        server-only: $($onlyS -join ', ')" -ForegroundColor Red }
        if ($onlyC.Count) { Write-Host "        client-only: $($onlyC -join ', ')" -ForegroundColor Red }
    }
}
if (-not $dtoFail) { Good 'all paired DTO files expose identical JsonProperty names' }

Section 'RESULT'
if ($fail) { Write-Host '  CONTRACT BROKEN - fix the FAILs above before shipping.' -ForegroundColor Red; exit 1 }
Write-Host '  Contract holds.' -ForegroundColor Green
exit 0
