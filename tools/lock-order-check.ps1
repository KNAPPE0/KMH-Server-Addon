# Lock-order guard. A cycle in the "who calls whom while holding a lock" graph deadlocks the whole server, and it
# only shows under load. Sinks (Treasury, ItemLabelCache, ContributionLedger, JsonFileStore) never call upstream.
# Usage: .\tools\lock-order-check.ps1 [-SelfTest] [-List]   (-SelfTest proves the scanner, -List prints every edge)
param([switch]$SelfTest, [switch]$List)

$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "..\Source"

# Callees that take a lock of their own; anything else called under a lock cannot extend the graph.
$LockTakers = @{
    "GuildStore"                 = "Guild"
    "TreasuryStore"              = "Treasury"
    "SiteStore"                  = "Site"
    "MarketplaceStore"           = "Marketplace"
    "AuctionStore"               = "Auction"
    "WantStore"                  = "Want"
    "QuestStore"                 = "Quest"
    "MailStore"                  = "Mail"
    "ChatStore"                  = "Chat"
    "NotificationStore"          = "Notification"
    "ItemLabelCache"             = "ItemLabelCache"
    "KmhGuildContributionLedger" = "ContributionLedger"
    "KmhPayloadEscrow"           = "Treasury"     # delegates straight into TreasuryStore.DepositPayload/DepositItem
}

# Members of those types that take NO lock, each read to confirm: false edges are noise that gets a guard ignored.
$PureMembers = @(
    "ResolveOwnerKeyFor"   # TreasuryStore -> PersonalKeyFor, a string concat
    "PersonalKeyFor"       # string concat
    "StateNote"            # KmhPayloadEscrow -> KmhItemSafety.DescribeStateForLedger, pure
    "IsSplittable"         # KmhPayloadEscrow: reads ScribeXml/Mergeable on the payload, no store access
    "DeliverableUnits"     # KmhPayloadEscrow: counts over the passed list using IsSplittable, no store access
)

# holder -> callee edges that exist today and are known acyclic.
$Baseline = @(
    "SiteStore -> Guild"
    "SiteStore -> ItemLabelCache"
    "GuildStore -> Treasury"
    "GuildStore -> ContributionLedger"
)

function Get-LockedCalls([string]$path) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $out = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^\s*lock\s*\(') { continue }
        $depth = 0; $started = $false; $body = @()
        for ($j = $i; $j -lt $lines.Count; $j++) {
            $l = $lines[$j] -replace '//.*$',''
            $opens = ([regex]::Matches($l, '\{')).Count; $closes = ([regex]::Matches($l, '\}')).Count
            if ($j -gt $i) { $body += $l } elseif ($opens -gt 0) { $body += ($l -replace '^\s*lock\s*\([^)]*\)\s*','') }
            $depth += $opens - $closes
            if ($opens -gt 0) { $started = $true }
            if (-not $started -and $j -eq $i -and $l -match '\)\s*\S') { $body += $l; break }
            if ($started -and $depth -le 0) { break }
        }
        foreach ($b in $body) {
            foreach ($m in [regex]::Matches($b, '\b([A-Z]\w*)\.([A-Za-z]\w*)\s*\(')) {
                $t = $m.Groups[1].Value
                if ($PureMembers -contains $m.Groups[2].Value) { continue }
                if ($LockTakers.ContainsKey($t)) {
                    $out += [pscustomobject]@{ Holder = [System.IO.Path]::GetFileNameWithoutExtension($path)
                                               Callee = $LockTakers[$t]; Line = $i + 1; Member = $m.Groups[2].Value }
                }
            }
        }
    }
    return $out
}

if ($SelfTest) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "lockorder-probe.cs"
    @'
class SiteStore {
    void Braced() {
        lock (_lock)
        {
            GuildStore.WorkerXpMultiplierFor("g");
        }
    }
    void Nested() {
        lock (_lock)
        {
            if (x) { TreasuryStore.GetGuildSilver("g"); }
        }
    }
    void Unlocked() {
        MarketplaceStore.Buy(1);
    }
    void PureHelper() {
        lock (_lock)
        {
            SilverFmt.Format(1);
            TreasuryStore.ResolveOwnerKeyFor("u");
        }
    }
}
'@ | Set-Content $tmp -Encoding ascii
    $hits = Get-LockedCalls $tmp
    $names = (($hits | ForEach-Object { $_.Callee }) | Sort-Object) -join ","
    [System.IO.File]::Delete($tmp)
    Write-Host "  detected: $names"
    $ok = ($names -match 'Guild') -and ($names -match 'Treasury') -and ($names -notmatch 'Marketplace')
    if (-not $ok) { Write-Host "  SELF-TEST FAILED"; exit 1 }
    Write-Host "  self-test OK - finds braced + nested, ignores unlocked calls and pure helpers"
    exit 0
}

# This scanner only sees lock(); each other primitive is allowed by name and printed every run so none goes silent.
$allowedPrims = @{
    'KmhGlobalRegistration.cs' = 'SetupGate: an async gate over the owner-link flow, which awaits HTTP - lock() cannot be held across await. Guards no store, and both holders release in finally.'
    'KmhIdentityChange.cs'     = 'Gate: an async gate over a key rotation, recovery or rebind, which awaits HTTP. Its holders release in finally; the heartbeat path only tries it (Wait(0)) and never blocks. Store locks are taken inside it (the federated links, the key file), never the other way round.'
    'KmhMediaTranscode.cs'     = 'decode slots: a counting semaphore that bounds concurrent image decodes. Guards no store, takes no other lock while held, released in finally.'
    'DiscordLive.cs'           = 'Gate: an async gate so the live-message tick and the activity-log flush take turns on their state file while awaiting Discord. Store locks are only taken inside it (snapshots, the state save), never the other way round, and every holder releases in finally.'
}

# Comments are stripped first: an explanation of why a primitive is NOT used would otherwise fail the scan.
$primPattern = 'Monitor\.(Enter|TryEnter)|SemaphoreSlim|ReaderWriterLock|Mutex\b|SpinLock'
$otherPrims = @()
foreach ($f in (Get-ChildItem -Path $src -Recurse -Filter *.cs -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
    $code = (Get-Content -Raw -LiteralPath $f.FullName) `
                -replace '(?s)/\*.*?\*/', '' -replace '(?m)//.*$', ''
    $n = 0
    foreach ($line in ($code -split "`r?`n")) {
        $n++
        if ($line -match $primPattern) {
            $otherPrims += [pscustomobject]@{ Filename = $f.Name; LineNumber = $n; Line = $line.Trim() }
        }
    }
}

foreach ($name in $allowedPrims.Keys | Sort-Object) {
    if (-not ($otherPrims | Where-Object { $_.Filename -eq $name })) {
        Write-Host "FAIL - $name is allowed a non-lock() primitive but no longer uses one. Drop the allowance." -ForegroundColor Red
        exit 1
    }
    Write-Host "  allowed: $name - $($allowedPrims[$name])"
}

$unexpected = $otherPrims | Where-Object { -not $allowedPrims.ContainsKey($_.Filename) }
if ($unexpected) {
    Write-Host "FAIL - a non-lock() primitive is in use; this scanner cannot see it:"
    $unexpected | ForEach-Object { Write-Host "  $($_.Filename):$($_.LineNumber)" }
    exit 1
}

$edges = @()
Get-ChildItem -Path $src -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'SelfTest' } |
    ForEach-Object { $edges += Get-LockedCalls $_.FullName }

$found = $edges | ForEach-Object { "$($_.Holder) -> $($_.Callee)" } | Sort-Object -Unique |
         Where-Object { $_ -notmatch '^(\w+) -> \1$' }   # a store taking its own lock is not an edge

if ($List) { $edges | Sort-Object Holder, Line | ForEach-Object { "  {0,-24}:{1,-5} -> {2}.{3}" -f $_.Holder, $_.Line, $_.Callee, $_.Member } }

$new = $found | Where-Object { $Baseline -notcontains $_ }
Write-Host ""
Write-Host "=== RESULT ==="
if ($new) {
    Write-Host "  New cross-store call(s) made while holding a lock:"
    $new | ForEach-Object { Write-Host "    $_" }
    Write-Host "  Check this cannot close a cycle, then add it to `$Baseline with a note."
    exit 1
}
$missing = $Baseline | Where-Object { $found -notcontains $_ }
if ($missing) { $missing | ForEach-Object { Write-Host "  (baseline edge no longer present: $_)" } }
Write-Host "  Lock order holds - $($found.Count) edge(s), all baselined, no cycles."
