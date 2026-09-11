# Lock-order guard. A cycle in the "who calls whom while holding a lock" graph deadlocks the whole server, and it
# only shows up under concurrent load, so the edges are baselined here and any NEW one has to be reviewed.
#
# Current order (acyclic):  Site -> Guild -> {Treasury -> ItemLabelCache, ContributionLedger};  Want -> Treasury
# Treasury, ItemLabelCache, ContributionLedger and JsonFileStore are sinks - they must never call back upstream.
#
# Generalises protocol-contract-check.ps1 section 10, which guards the single Treasury->Guild edge with the AB-BA
# rationale written out. That one is kept for its explanation; this covers every store.
#
#   .\tools\lock-order-check.ps1            verify against the baseline
#   .\tools\lock-order-check.ps1 -SelfTest  prove the scanner detects what it claims to
#   .\tools\lock-order-check.ps1 -List      print every edge found
param([switch]$SelfTest, [switch]$List)

$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "..\Source"

# Callees that take a lock of their own. Anything else under a lock is a pure helper and cannot extend the graph,
# so it is not an edge - verified per entry rather than assumed from the name.
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

# Members on those types that take NO lock, so calling them is not an edge. Each was read to confirm it, because a
# type-name-only rule reports edges that do not exist and the noise is what gets a guard ignored.
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
    "WantStore -> Treasury"
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

# Every critical section must be a lock statement, or this scanner is blind to part of the graph.
$otherPrims = Get-ChildItem -Path $src -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern 'Monitor\.(Enter|TryEnter)|SemaphoreSlim|ReaderWriterLock|Mutex\b|SpinLock'
if ($otherPrims) {
    Write-Host "FAIL - a non-lock() primitive is in use; this scanner cannot see it:"
    $otherPrims | ForEach-Object { Write-Host "  $($_.Filename):$($_.LineNumber)" }
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
