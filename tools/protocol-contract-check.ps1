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
    'Features\ItemLabels\Dto\ItemLabelsPush.cs'          = 'Features\Catalog\Dto\ItemLabelsPush.cs'
    'Features\Notifications\Dto\NotificationSnapshot.cs' = 'Features\OfflineNotices\Dto\NotificationSnapshot.cs'
}
$dtoFail = $false

# A stale alias is worse than no alias: the pair silently drops to a "no client counterpart" WARN and stops being
# compared at all. That is exactly what a folder rename did here once (OfflineMail -> OfflineNotices), so an alias
# whose two ends no longer both exist is a hard FAIL, not a warning.
foreach ($aliasKey in $dtoAlias.Keys) {
    $sPath = Join-Path $serverSrc $aliasKey
    $cPath = Join-Path $clientSrc $dtoAlias[$aliasKey]
    if (-not (Test-Path $sPath)) { Bad "DTO alias is stale - server file is gone: $aliasKey"; $dtoFail = $true }
    if (-not (Test-Path $cPath)) { Bad "DTO alias is stale - client file is gone: $($dtoAlias[$aliasKey]) (aliased from $aliasKey)"; $dtoFail = $true }
}

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

# --- 4. Anonymous payload drift (heuristic, warn-only) ---
# Most server->client sends are anonymous objects (new { foo_bar = ... }) with no DTO file, so section 3 can't see
# them. A typo there fails SILENTLY: the client just reads the default forever. Cross-check the member names the
# server can emit against the names the client consumes (JsonProperty on a DTO, or an env.Get*("name") accessor).
# Heuristic - hence warnings, not failures.
Section '4. Anonymous payload field drift (heuristic)'

function Get-Names([string]$root, [string]$pattern, [int]$group = 1) {
    $set = New-Object System.Collections.Generic.HashSet[string]
    Get-ChildItem -Path $root -Recurse -Filter *.cs | ForEach-Object {
        foreach ($mm in [regex]::Matches((Get-Content $_.FullName -Raw), $pattern)) { [void]$set.Add($mm.Groups[$group].Value) }
    }
    return ,$set   # comma stops PowerShell unrolling the set into an array
}

# Any `name =` the server writes, which covers anonymous payload members in every layout (multi-line or
# new { a = 1, b = 2 }). Deliberately permissive: over-collecting here only suppresses false warnings.
$srvEmits = Get-Names $serverSrc '\[JsonProperty\(\s*"([^"]+)"'
foreach ($n in (Get-Names $serverSrc '([a-z][a-z0-9_]*)\s*=[^=]')) { [void]$srvEmits.Add($n) }

# What the client CONSUMES from server payloads: DTO properties + envelope accessors. Client anonymous members are
# deliberately excluded - those are client->server sends, a different direction.
$cliReads = Get-Names $clientSrc '\[JsonProperty\(\s*"([^"]+)"'
foreach ($n in (Get-Names $clientSrc 'Get(?:Int|String|Bool|IntArray)\(\s*"([^"]+)"')) { [void]$cliReads.Add($n) }

# Only wire-shaped names (contain an underscore) are checked; single words are too noisy to distinguish from locals.
$missing = @($cliReads | Where-Object { $_ -match '_' -and -not $srvEmits.Contains($_) }) | Sort-Object
if ($missing.Count) {
    Warn "client expects field(s) the server never emits (verify by hand): $($missing -join ', ')"
} else {
    Good 'every underscore-shaped field the client reads is emitted somewhere on the server'
}

# --- 5. Shared rule parity (logic hand-mirrored across the two repos) ---
# A few rules exist as duplicate implementations on both sides because the repos share no code. Nothing else
# catches drift in them, and drift is silent: a changed DM id format means the two sides compute DIFFERENT channel
# ids and direct messages simply stop matching; a changed escrow-state set means the save-reset purge and the
# wealth ledger disagree about what is sheltered.
Section '5. Shared rule parity (cross-repo duplicated logic)'

function Get-Consts([string]$file) {
    $map = @{}
    if (-not (Test-Path $file)) { return $map }
    foreach ($mm in [regex]::Matches((Get-Content $file -Raw), 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')) {
        $map[$mm.Groups[1].Value] = $mm.Groups[2].Value
    }
    return $map
}

$srvChan = Get-Consts (Join-Path $serverSrc 'Features\Chat\ChatChannels.cs')
$cliChan = Get-Consts (Join-Path $clientSrc 'Features\Chat\ChatChannels.cs')
$chanBad = $false
foreach ($k in @('Server','GuildPrefix','DmPrefix')) {
    if ($srvChan[$k] -ne $cliChan[$k]) {
        Bad "ChatChannels.$k differs: server='$($srvChan[$k])' client='$($cliChan[$k])' - DM/guild channel ids would not match"
        $chanBad = $true
    }
}
# The DM id must stay case-insensitive AND direction-independent on both sides, or one pair yields two channels.
foreach ($side in @(@{n='server';f=(Join-Path $serverSrc 'Features\Chat\ChatChannels.cs')}, @{n='client';f=(Join-Path $clientSrc 'Features\Chat\ChatChannels.cs')})) {
    $txt = if (Test-Path $side.f) { Get-Content $side.f -Raw } else { '' }
    if ($txt -notmatch 'ToLowerInvariant' -or $txt -notmatch 'CompareOrdinal') {
        Bad "ChatChannels ($($side.n)) lost its lowercase+ordinal DM canonicalisation"
        $chanBad = $true
    }
}
if (-not $chanBad) { Good 'ChatChannels prefixes + DM canonicalisation identical on both sides' }

function Get-EscrowStates([string]$file, [string]$anchor) {
    if (-not (Test-Path $file)) { return @() }
    $txt = Get-Content $file -Raw
    $m = [regex]::Match($txt, [regex]::Escape($anchor) + '(?s).{0,400}?;')
    if (-not $m.Success) { return @() }
    return @([regex]::Matches($m.Value, 'State[A-Za-z]+') | ForEach-Object { $_.Value } | Sort-Object -Unique)
}

# Token-presence was too weak: both sides can mention ToLowerInvariant and CompareOrdinal and still compute
# different ids. These four PRODUCE the shared identifiers - an item key that differs breaks vault lookups, a DM id
# that differs stops direct messages matching - so their bodies are compared outright, comments and whitespace
# normalised away. Deliberately NOT applied to predicates like TryParseDm: those two agree behaviourally while
# differing in expression (`!IsDm(channel)` vs the inlined check), and a guard that fails on that would just teach
# people to ignore it.
function Get-MethodBody([string]$path, [string]$sig) {
    if (-not (Test-Path $path)) { return $null }
    $lines = [System.IO.File]::ReadAllLines($path)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch [regex]::Escape($sig)) { continue }
        $depth = 0; $started = $false; $buf = @()
        for ($j = $i; $j -lt $lines.Count; $j++) {
            $l = $lines[$j] -replace '//.*$', ''
            $buf += $l
            $depth += ([regex]::Matches($l, '\{')).Count - ([regex]::Matches($l, '\}')).Count
            if ($l -match '\{') { $started = $true }
            if (-not $started -and $l -match ';') { break }
            if ($started -and $depth -le 0) { break }
        }
        return (($buf -join ' ') -replace '\s+', ' ').Trim()
    }
    return $null
}

$mirrored = @(
    @{ A = (Join-Path $serverSrc 'Util\ItemKey.cs');            B = (Join-Path $clientSrc 'UI\ItemKeys.cs')
       Sig = 'public static string Compose(string defName, string stuffDefName, int qualityIndex)'
       Why = 'item keys would stop matching vault entries' }
    @{ A = (Join-Path $serverSrc 'Util\ItemKey.cs');            B = (Join-Path $clientSrc 'UI\ItemKeys.cs')
       Sig = 'public static void Split(string key, out string defName, out string stuffDefName, out int qualityIndex)'
       Why = 'item keys would decode differently on each side' }
    @{ A = (Join-Path $serverSrc 'Util\ItemKey.cs');            B = (Join-Path $clientSrc 'UI\ItemKeys.cs')
       Sig = 'public static bool Matches(string key, string targetDefName, string requiredStuff, int requiredQualityIndex)'
       Why = 'the board would show a "you have N" the server''s withdraw then refuses' }
    @{ A = (Join-Path $serverSrc 'Features\Chat\ChatChannels.cs'); B = (Join-Path $clientSrc 'Features\Chat\ChatChannels.cs')
       Sig = 'public static string Dm(string a, string b)'
       Why = 'the two sides would key different DM channels' }
    @{ A = (Join-Path $serverSrc 'Features\Chat\ChatChannels.cs'); B = (Join-Path $clientSrc 'Features\Chat\ChatChannels.cs')
       Sig = 'public static bool CanDm(string username)'
       Why = 'one side would offer a DM the other refuses' }
    @{ A = (Join-Path $serverSrc 'Features\Roadworks\RoadTiers.cs'); B = (Join-Path $clientSrc 'Features\Roadworks\RoadKeys.cs')
       Sig = 'public static string For(int layerA, int tileA, int layerB, int tileB)'
       Why = 'the client would stop recognising the roads it applied, and could delete another mod''s instead' }
    @{ A = (Join-Path $serverSrc 'Features\Roadworks\RoadTiers.cs'); B = (Join-Path $clientSrc 'Features\Roadworks\RoadKeys.cs')
       Sig = 'public static List<string> NewSegmentKeys(List<string> routeKeys, HashSet<string> existing)'
       Why = 'a player would be quoted one price for a road and charged another' }
    @{ A = (Join-Path $serverSrc 'Features\Roadworks\RoadTiers.cs'); B = (Join-Path $clientSrc 'Features\Roadworks\KmhRoadDefs.cs')
       Sig = 'public static bool TierRankAllowed(int tierRank, int siteTier)'
       Why = 'the client would offer a road tier the server then refuses' }
)
$mirrorBad = $false
foreach ($m in $mirrored) {
    $ba = Get-MethodBody $m.A $m.Sig
    $bb = Get-MethodBody $m.B $m.Sig
    $short = ($m.Sig -replace '^public static \S+ ', '') -replace '\(.*', ''
    if ($null -eq $ba -or $null -eq $bb) { Warn "could not locate $short on one side - verify that mirror by hand"; continue }
    if ($ba -cne $bb) { Bad "$short differs between repos - $($m.Why)"; $mirrorBad = $true }
}
if (-not $mirrorBad) { Good "$($mirrored.Count) id/key-producing mirror(s) byte-identical across repos" }

$srvStates = Get-EscrowStates (Join-Path $serverSrc 'Features\Quests\QuestStore.cs') 'static bool HoldsEscrow'
$cliStates = Get-EscrowStates (Join-Path $clientSrc 'Features\Wealth\Sources\QuestWealthSource.cs') 'static bool HoldsEscrow'
if ($srvStates.Count -eq 0 -or $cliStates.Count -eq 0) {
    Warn 'could not locate HoldsEscrow on one side - verify the quest escrow-state rule by hand'
} elseif (($srvStates -join ',') -ne ($cliStates -join ',')) {
    Bad "quest HoldsEscrow states differ: server=[$($srvStates -join ' ')] client=[$($cliStates -join ' ')]"
} else {
    Good "quest escrow-state rule identical on both sides ($($srvStates.Count) states)"
}

# --- 6. Notifications vs Player Mail naming separation ---
# Two DIFFERENT systems: the offline notice queue (Features/Notifications) and Player Mail (Features/Mail), which
# carries escrowed value. A server class literally named KmhMail lived in the Notifications namespace, and the
# client kept its notification handler in a namespace called OfflineMail. That one naming choice produced FOUR
# surfaces reporting the notice queue under a "mail" label - the README, the player snapshot record, kmh
# audit-player, and the audit's own summary line - each found and fixed separately. Guard the name, not the sites.
Section '6. Notifications / Player Mail naming separation'

$nameBad = $false

# Only DECLARED names count. Matching anywhere on the line flagged method bodies that merely CALL the other system
# (e.g. Dialog_KMHCompose's `=> Notifications.KmhNotifications.Rejected(msg)`), which is legitimate.
$reNamespace = '^\s*namespace\s+(?<n>[\w\.]+)'
$reType      = '^\s*(?:(?:internal|public|private|protected|static|sealed|abstract|partial|readonly|new)\s+)*(?:class|struct|interface|enum|record)\s+(?<n>\w+)'

function Get-DeclaredNames([string]$dir) {
    $out = @()
    foreach ($f in (Get-ChildItem $dir -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue)) {
        $n = 0
        foreach ($line in (Get-Content $f.FullName)) {
            $n++
            if ($line -match '^\s*//') { continue }
            foreach ($re in @($reNamespace, $reType)) {
                $m = [regex]::Match($line, $re)
                if ($m.Success) { $out += [pscustomobject]@{ File = $f.Name; Line = $n; Name = $m.Groups['n'].Value; Text = $line.Trim() } }
            }
        }
    }
    return ,$out
}

# Locate the notice system by where its types actually live, not by a fixed folder - the folder has already been
# renamed once and a hard-coded path would silently degrade to "skipped" instead of failing.
foreach ($pair in @(
    @{ Repo = 'server'; Src = $serverSrc },
    @{ Repo = 'client'; Src = $clientSrc })) {
    $anchors = Get-ChildItem $pair.Src -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
               Select-String -Pattern '^\s*(internal|public)\s+(static\s+)?(sealed\s+)?class\s+Notification\w*' |
               ForEach-Object { $_.Path } | Sort-Object -Unique
    if (-not $anchors) {
        Warn "$($pair.Repo): found no Notification* class - verify the notice queue by hand"
        continue
    }
    foreach ($d in ($anchors | ForEach-Object { Split-Path $_ -Parent } | Sort-Object -Unique)) {
        foreach ($decl in (Get-DeclaredNames $d)) {
            if ($decl.Name -cmatch 'Mail') {
                Bad "$($pair.Repo): '$($decl.File):$($decl.Line)' declares '$($decl.Name)' inside the notice system - $($decl.Text)"
                $nameBad = $true
            }
        }
    }
}

# And the reverse: Player Mail must not present itself as the notice queue.
foreach ($pair in @(
    @{ Repo = 'server'; Dir = (Join-Path $serverSrc 'Features\Mail') },
    @{ Repo = 'client'; Dir = (Join-Path $clientSrc 'Features\Mail') })) {
    if (-not (Test-Path $pair.Dir)) { continue }
    foreach ($decl in (Get-DeclaredNames $pair.Dir)) {
        if ($decl.Name -cmatch 'Notification' -or $decl.Name -cmatch 'Notice') {
            Bad "$($pair.Repo): '$($decl.File):$($decl.Line)' declares '$($decl.Name)' inside Player Mail - $($decl.Text)"
            $nameBad = $true
        }
    }
}
if (-not $nameBad) { Good 'notice queue and Player Mail keep separate names on both sides' }

# --- 7. Client cache clear-on-server-switch coverage ---
# Every client cache holds ONE server's data. KmhClientCaches.ClearAll runs on disconnect so the next server never
# briefly shows the previous server's numbers. That list is hand-written and the client has no headless test
# runner, so this is the only place the omission can be caught - SeasonArchiveCache had been missing since it was
# added, leaving the old server's leaderboards and all-time records on screen after a switch.
# Discriminator: a cache that raises `event Action Updated` holds server-pushed state. Infrastructure files
# (KmhCacheEvents, CacheAdapters, Caches, KmhClientCaches) do not raise it and are skipped automatically.
Section '7. Client cache clear-on-server-switch coverage'

# Reset by a DIFFERENT path on purpose - each needs a reason, and the reason is checked below.
$cacheExempt = @{
    'EnforcementCache' = 'reset via Apply(false,...) in Patch_DisconnectionManager so the lock falls back to the applied-profile file list instead of unlocking'
}

$clearAllFile = Join-Path $clientSrc 'SubProtocol\KmhClientCaches.cs'
if (-not (Test-Path $clearAllFile)) {
    Warn 'KmhClientCaches.cs not found - verify cache clearing by hand'
} else {
    $clearAllText = Get-Content $clearAllFile -Raw
    $cacheBad = $false
    $cacheFiles = Get-ChildItem $clientSrc -Recurse -File -Filter *.cs |
                  Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and (Select-String -Path $_.FullName -Pattern 'event\s+Action\s+Updated' -Quiet) }
    foreach ($cf in $cacheFiles) {
        $name = $cf.BaseName
        if ($clearAllText -match [regex]::Escape("$name.Clear()")) { continue }
        if ($cacheExempt.ContainsKey($name)) {
            # An exemption must still be true: the named cache has to be reset SOMEWHERE on disconnect.
            $dc = Join-Path $clientSrc 'Patches\Patch_DisconnectionManager_KmhDisconnect.cs'
            if (-not ((Test-Path $dc) -and (Select-String -Path $dc -Pattern "$name\." -Quiet))) {
                Bad "$name is exempt from ClearAll but nothing resets it on disconnect either - $($cacheExempt[$name])"
                $cacheBad = $true
            }
            continue
        }
        Bad "$name raises Updated (holds server data) but is NOT cleared on server switch - the previous server's data stays on screen"
        $cacheBad = $true
    }
    # A stale exemption is drift too.
    foreach ($k in $cacheExempt.Keys) {
        if (-not ($cacheFiles | Where-Object { $_.BaseName -eq $k })) {
            Bad "cache exemption '$k' names a cache that no longer exists"
            $cacheBad = $true
        }
    }
    if (-not $cacheBad) { Good "all $($cacheFiles.Count) server-data caches are cleared on server switch (or exempt with a verified reason)" }
}

# --- 8. Client SDK event-bus coverage ---
# KmhClientEventBus forwards cache updates to extensions. A cache that is never forwarded is invisible to every
# client extension - which is how Chat and Mail, the two newest player-facing systems, shipped unobservable.
# Same hand-maintained-list shape as sections 6 and 7.
Section '8. Client SDK event-bus coverage'

# Not forwarded on purpose. Each needs a reason; a stale entry is drift and fails below.
$busExempt = @{
    'ColonistProfileCache' = 'per-colonist detail fetched on demand, not ambient player state (PlayerStatsCacheUpdated covers the roster signal)'
    'ColonistRosterCache'  = 'same family as ColonistProfileCache; roster changes surface via PlayerStatsCacheUpdated'
    'SiteCatalogCache'     = 'UI picker catalog of offerable defs - server config, not player state'
    'EnforcementCache'     = 'config-enforcement posture, not player data; extensions read EnforcementCache directly'
}

$busFile = Join-Path $clientSrc 'Extensibility\KmhClientEventBus.cs'
if (-not (Test-Path $busFile)) {
    Warn 'KmhClientEventBus.cs not found - verify SDK event coverage by hand'
} else {
    $busText = Get-Content $busFile -Raw
    $busBad = $false
    $busCaches = Get-ChildItem $clientSrc -Recurse -File -Filter *.cs |
                 Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and (Select-String -Path $_.FullName -Pattern 'event\s+Action\s+Updated' -Quiet) }
    foreach ($bc in $busCaches) {
        $name = $bc.BaseName
        if ($busText -match [regex]::Escape("$name.Updated")) { continue }
        if ($busExempt.ContainsKey($name)) { continue }
        Bad "$name raises Updated but is NOT forwarded to the client SDK bus - extensions cannot observe it"
        $busBad = $true
    }
    foreach ($k in $busExempt.Keys) {
        if (-not ($busCaches | Where-Object { $_.BaseName -eq $k })) {
            Bad "SDK bus exemption '$k' names a cache that no longer exists"
            $busBad = $true
        }
    }
    if (-not $busBad) { Good "$($busCaches.Count) caches: all forwarded to extensions or exempt with a reason" }
}

# --- 9. Feature-key vocabulary (fail-open guard) ---
# FeaturesConfig.IsEnabled ends in `default: return true`, so an unrecognised key does not error - it reports the
# feature ENABLED. Any typo therefore fails OPEN: the owner switches a feature off in Features.json and it keeps
# serving, with nothing logged. Three places use these keys and all three must speak the same vocabulary:
# IsEnabled's switch (the authority), FeatureForKind (gates every inbound request), and the client dashboard rows
# (which grey out as "disabled by server").
Section '9. Feature-key vocabulary (gates fail open on a typo)'

$featCfg = Join-Path $serverSrc 'Features\FeaturesConfig.cs'
$dashFile = Join-Path $clientSrc 'UI\KMHDashboard.cs'
if (-not (Test-Path $featCfg)) {
    Warn 'FeaturesConfig.cs not found - verify feature keys by hand'
} else {
    $featText = Get-Content $featCfg -Raw
    $known = @([regex]::Matches($featText, 'case\s+"([a-z_]+)"\s*:') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $used  = @([regex]::Matches($featText, 'return\s+"([a-z_]+)"\s*;')  | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $keyBad = $false
    if ($known.Count -eq 0) {
        Warn 'could not read IsEnabled cases - verify feature keys by hand'
    } else {
        foreach ($k in $used) {
            if ($known -notcontains $k) { Bad "FeatureForKind returns '$k' but IsEnabled has no such case - that gate FAILS OPEN (feature can never be disabled)"; $keyBad = $true }
        }
        if (Test-Path $dashFile) {
            $dashKeys = @([regex]::Matches((Get-Content $dashFile -Raw), 'new Row\("[^"]*",\s*"([a-z_]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
            foreach ($k in $dashKeys) {
                if ($known -notcontains $k) { Bad "client dashboard row uses feature key '$k' with no IsEnabled case - the row can never show 'disabled by server'"; $keyBad = $true }
            }
        } else { Warn 'KMHDashboard.cs not found - client feature keys unchecked' }
        if (-not $keyBad) { Good "feature keys agree across IsEnabled ($($known.Count)), FeatureForKind and the client dashboard" }
    }
}

# --- 10. Treasury/Guild lock order ---
# GuildStore.ComputeLeaderboard reads TreasuryStore under GuildStore._lock, so the nesting is Guild -> Treasury.
# TreasuryStore must therefore never call GuildStore while holding its own lock (donation finalisation is
# deliberately dispatched after the lock closes). The reverse order would be an AB-BA deadlock: a server hang
# under concurrent load, with nothing logged.
Section '10. Treasury/Guild lock order'

$tsFile = Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs'
if (-not (Test-Path $tsFile)) {
    Warn 'TreasuryStore.cs not found - verify lock order by hand'
} else {
    # `lock (_lock)` and its `{` are on separate lines, so the block is only open once depth exceeds the depth
    # recorded at the lock statement - without that latch the scan closes the block instantly and never sees inside.
    $depth = 0; $lockDepth = $null; $entered = $false; $lockBad = $false; $n = 0; $scanned = 0
    foreach ($line in (Get-Content $tsFile)) {
        $n++
        if ($null -eq $lockDepth -and $line -match '^\s*lock \(_lock\)') { $lockDepth = $depth; $entered = $false }
        if ($entered) {
            $scanned++
            if ($line -match 'GuildStore\.' -and $line -notmatch '^\s*//') {
                Bad "TreasuryStore.cs:$n calls GuildStore while holding _lock - AB-BA deadlock against GuildStore.ComputeLeaderboard: $($line.Trim())"
                $lockBad = $true
            }
        }
        $depth += ([regex]::Matches($line, '\{').Count - [regex]::Matches($line, '\}').Count)
        if ($null -ne $lockDepth) {
            if (-not $entered) { if ($depth -gt $lockDepth) { $entered = $true } }
            elseif ($depth -le $lockDepth) { $lockDepth = $null; $entered = $false }
        }
    }
    if ($scanned -eq 0) { Bad 'lock-order scan saw no lines inside any _lock block - the scan itself is broken'; $lockBad = $true }
    if (-not $lockBad) { Good "TreasuryStore never calls GuildStore under its lock ($scanned lines inside locks scanned)" }
}

# --- 11. Site guild-derived values are live, not frozen ---
# MaxWorkers and OwnerGuild are written once at creation from the owner's guild. Reading either back means a perk
# bought later never applies, one gained by briefly joining a guild outlives leaving it, and access/quota stay
# anchored to a guild the owner has left. Decisions must go through MaxWorkersLive / OwnerCurrentGuild, so
# SiteStore must never read the stored fields.
Section '11. Site guild-derived values are live'

$siteFile = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
if (-not (Test-Path $siteFile)) {
    Warn 'SiteStore.cs not found - verify the derived site values by hand'
} else {
    $siteBad = $false
    foreach ($helper in @('MaxWorkersLive', 'OwnerCurrentGuild')) {
        if (-not (Select-String -Path $siteFile -Pattern $helper -Quiet)) { Bad "SiteStore.$helper is gone - that site value is no longer derived"; $siteBad = $true }
    }
    # Only READS are the defect. Assigning the field from its own live helper is the fix, not a violation, so a line
    # whose right-hand side is that helper is allowed - "c.MaxWorkers = s.MaxWorkers" still fails, as it should.
    foreach ($pair in @(@{ F = '\.MaxWorkers\b'; U = 'MaxWorkersLive' }, @{ F = 's\.OwnerGuild\b'; U = 'OwnerCurrentGuild' })) {
        foreach ($h in (Select-String -Path $siteFile -Pattern $pair.F | Where-Object { $_.Line -notmatch '^\s*//' })) {
            $rhs = ($h.Line -split '=', 2)
            if ($rhs.Count -eq 2 -and $rhs[1] -match [regex]::Escape($pair.U)) { continue }
            Bad "SiteStore.cs:$($h.LineNumber) reads a frozen site field - use $($pair.U): $($h.Line.Trim())"
            $siteBad = $true
        }
    }
    if (-not $siteBad) { Good 'SiteStore derives worker cap + owner guild live (no reads of the stored fields)' }
}

# --- 12. Wealth-source coverage (a value surface nobody registered is a free raid shelter) ---
# KmhWealthLedger.EnsureBuiltins is a hand-written list, and the same shape of omission has already shipped twice
# (see KmhEscrowPurge's note on quest bounty and mail attachments surviving a wipe). Every IKmhWealthSource on disk
# must be registered, or wealth parked in that system stops counting toward raid/threat scaling.
Section '12. Wealth-source coverage (off-map value cannot dodge raids)'

$wealthDir    = Join-Path $clientSrc 'Features\Wealth'
$ledgerFile   = Join-Path $wealthDir 'KmhWealthLedger.cs'
if (-not (Test-Path $ledgerFile)) {
    Warn 'KmhWealthLedger.cs not found - verify wealth-source coverage by hand'
} else {
    $ledgerText = [System.IO.File]::ReadAllText($ledgerFile)
    $declared = @()
    Get-ChildItem -Path $wealthDir -Recurse -Filter *.cs -File | ForEach-Object {
        foreach ($m in [regex]::Matches([System.IO.File]::ReadAllText($_.FullName), 'class\s+(\w+)\s*:\s*IKmhWealthSource')) {
            $declared += $m.Groups[1].Value
        }
    }
    $wealthBad = $false
    foreach ($src in ($declared | Sort-Object -Unique)) {
        if ($ledgerText -notmatch "Register\(new\s+(?:Sources\.)?$src\s*\(\)") {
            Bad "$src implements IKmhWealthSource but EnsureBuiltins never registers it - value held there dodges raid scaling"
            $wealthBad = $true
        }
    }
    if ($declared.Count -eq 0) { Warn 'no IKmhWealthSource implementations found - check the scan pattern' }
    elseif (-not $wealthBad) { Good "$($declared.Count) wealth source(s) declared, all registered" }
}

# --- 13. Value/quantity arithmetic must saturate, not wrap ---
# Silver balances and item counts are int, the amounts added to them are client-claimed, and every per-action cap
# treats 0 as "unlimited" - so a raw `+=` can wrap a fortune or a stockpile NEGATIVE. A negative count is worse than
# a stuck one: it silently reverses every rule that reads it, including the off-map wealth figure that drives raid
# scaling. All of these go through KmhSafe.AddSaturating; this fails if a raw one comes back.
Section '13. Value/quantity arithmetic saturates'

$rawAdds = Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'SelfTest' } |
    Select-String -Pattern 'SilverBalance\s*\+=|RemainingQty\s*\+=|StackCount\s*\+=|(?:Items|EscrowedOutItems)\[[^\]]+\]\s*=\s*\w+\s*\+\s*\w' |
    Where-Object { $_.Line -notmatch '^\s*//' }
if ($rawAdds) {
    foreach ($h in $rawAdds) { Bad "$($h.Filename):$($h.LineNumber) adds to a value/count directly - use KmhSafe.AddSaturating: $($h.Line.Trim())" }
} else {
    Good 'no raw += on a silver balance or item count (all saturate)'
}

# --- 14. Every self-test suite is wired into a KMH command ---
# The offline runner finds *SelfTest types by REFLECTION, but the in-game commands run a hand-written list. So a new
# suite passes in the build loop while being silently absent from the command an owner actually runs - which is how
# four of them accumulated unnoticed. Reflection was masking the gap rather than covering it.
# Both surfaces live in KmhSmokeTest.cs: Run (live health, `kmh smoketest`) and RunRegression (`kmh selftest`).
Section '14. Self-test suites are wired into kmh smoketest / kmh selftest'

$smokeFile = Join-Path $serverSrc 'Maintenance\KmhSmokeTest.cs'
if (-not (Test-Path $smokeFile)) {
    Warn 'KmhSmokeTest.cs not found - verify suite wiring by hand'
} else {
    $smokeText = [System.IO.File]::ReadAllText($smokeFile)
    $suites = Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object { [regex]::Matches([System.IO.File]::ReadAllText($_.FullName), 'class\s+(\w*SelfTest)\b') } |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $unwired = @()
    foreach ($s in $suites) {
        # `X.Run(` not `X.Run()` - the discovery suite takes (log, out summary) and a stricter match misses it.
        if ($smokeText -notmatch ([regex]::Escape($s) + '\.Run\(')) { $unwired += $s }
    }
    foreach ($s in $unwired) { Bad "$s is never run by kmh smoketest or kmh selftest - it passes offline and is absent in-game" }
    if ($suites.Count -eq 0) { Warn 'no SelfTest suites found - check the scan pattern' }
    elseif ($unwired.Count -eq 0) { Good "$($suites.Count) self-test suite(s) all wired into kmh smoketest / kmh selftest" }
}

# --- 15. No source file contains a raw NUL byte ---
# ripgrep treats a file with a NUL as BINARY and silently skips it, so the file becomes invisible to every
# grep-based sweep - including the ones used to audit this codebase. Three files here had one: two deliberate
# sentinels/attack-strings and one typo, and all three were unsearchable for the whole review without saying so.
# The fix is never to remove the NUL from the STRING - it is to write it as the `\0` escape, which compiles to the
# identical value and leaves the source plain text. So this fails on the byte, not on the intent.
Section '15. Source files are searchable (no raw NUL bytes)'
$nulFiles = @()
foreach ($root in @($serverSrc, $clientSrc)) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
            if ([Array]::IndexOf($bytes, [byte]0) -ge 0) { $nulFiles += $_.FullName }
        }
}
foreach ($f in $nulFiles) {
    Bad "$f contains a raw NUL byte - grep skips it as binary. Write the character as the \0 escape instead."
}
if ($nulFiles.Count -eq 0) { Good 'no raw NUL bytes - every source file is searchable' }

# --- 15a. No stray control characters in source ---
# A raw control byte is invisible in an editor and silently changes what the code means. A shell heredoc turned a
# regex \b into a literal backspace here, which made the pattern unmatchable while looking perfectly correct.
# SelfTest files are exempt for non-NUL bytes: some deliberately embed control characters as sanitiser fixtures.
Section '15a. Source carries no stray control characters'
$ctrl15a = @(0x00, 0x07, 0x08, 0x0B, 0x0C, 0x1A, 0x1B)
$hits15a = @()
foreach ($root15a in @((Join-Path $ServerRoot 'Source'), (Join-Path $ClientRoot 'Source'))) {
    if (-not (Test-Path -LiteralPath $root15a)) { continue }
    foreach ($f15a in Get-ChildItem -LiteralPath $root15a -Filter *.cs -Recurse -File) {
        $bytes15a = [System.IO.File]::ReadAllBytes($f15a.FullName)
        $isTest15a = $f15a.Name -like '*SelfTest.cs'
        foreach ($b15a in $ctrl15a) {
            if ($bytes15a -notcontains $b15a) { continue }
            if ($isTest15a -and $b15a -ne 0x00) { continue }
            $hits15a += ("{0}: 0x{1:X2}" -f $f15a.Name, $b15a)
        }
    }
}
if ($hits15a.Count -gt 0) { Bad ("stray control character(s) in source: " + ($hits15a -join ', ')); }
else { Good 'no stray control characters - nothing in source means something other than it looks' }

# --- 16. The "can this stack be split" rule exists exactly once ---
# A payload with a saved blob is atomic; a fungible one divides. Copies of that test agreed with each other on the
# PREDICATE and diverged on the CONSEQUENCE - one caller sized a fill by raw units while the delivery loop refused
# to split, so a buyer was charged for goods that went straight back to the seller. Splitting decisions must route
# through KmhPayloadEscrow.IsSplittable (usually via KmhItemService.PlanTake), never re-derive it inline.
Section '16. Stack-splitting rule is not re-implemented'
$splitRe = 'IsNullOrEmpty\(\s*\w+\.ScribeXml\s*\)\s*\|\|\s*\w+\.Mergeable'
$dupes = @()
foreach ($root in @($serverSrc, $clientSrc)) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'KmhPayloadEscrow.cs' } |
        ForEach-Object {
            $lineNo = 0
            foreach ($line in [System.IO.File]::ReadAllLines($_.FullName)) {
                $lineNo++
                if ($line -match $splitRe) { $dupes += "$($_.Name):$lineNo" }
            }
        }
}
foreach ($d in $dupes) {
    Bad "$d re-implements the stack-splitting rule - call KmhPayloadEscrow.IsSplittable instead (a divergent copy already charged a buyer for undelivered units once)"
}
if ($dupes.Count -eq 0) { Good 'stack-splitting rule lives only in KmhPayloadEscrow.IsSplittable' }

# --- 17. Reading must not create ---
# GetSnapshotFor returned a COPY but obtained it with GetOrCreateLocked, so merely LOOKING UP a name wrote a
# permanent empty vault to Treasury.json. An admin typo did it; the smoke test did it on every run (24 were found
# in a real server's file). A row must be created by the action that means the owner exists - a deposit - not by
# someone reading. Methods named like readers therefore may not call a get-or-create helper.
Section '17. Read-shaped methods do not create rows'
$readish = '^(Get|Has|Find|Build|Count|Total|Peek|List|Snapshot|Describe|Recent|View|Read)'
$creators = 'GetOrCreate\w*|EnsureRow\w*|GetOrAdd\w*'
$badReads = @()
foreach ($root in @($serverSrc)) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'SelfTest' } |
        ForEach-Object {
            $lines = [System.IO.File]::ReadAllLines($_.FullName); $m = ''
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^\s*(public|private|internal)\s+static\s+[^=]*?\s+(\w+)\s*\(') { $m = $Matches[2] }
                # The helper's own definition is not a call site.
                if ($m -match $creators) { continue }
                if ($m -match $readish -and $lines[$i] -match ("(" + $creators + ")\s*\(")) {
                    $badReads += "$($_.Name):$($i+1) $m"
                }
            }
        }
}
foreach ($b in $badReads) {
    Bad "$b is read-shaped but calls a get-or-create helper - reading must not write a row (this leaked 24 empty vaults into a live Treasury.json)"
}
if ($badReads.Count -eq 0) { Good 'no read-shaped method creates a persisted row' }

# --- 18. Detecting escrow and burning it stay in step ---
# KmhEscrowPurge answers two questions about the same set of stores: does this player hold escrow outside their
# treasury (HasAny, which decides whether a reset runs at all), and burn it (BurnAll). Quest bounty and mail
# attachments each shipped on one side only and survived a wipe. A store named on one side and not the other is
# either a reset that skips real value or a reset that fires for nothing.
Section '18. Escrow detection and escrow burning name the same stores'
$purgeFile = Join-Path $serverSrc 'Features\Economy\KmhEscrowPurge.cs'
if (-not (Test-Path -LiteralPath $purgeFile)) { Bad 'KmhEscrowPurge.cs not found - the shared escrow list is gone' }
else {
    $src = [System.IO.File]::ReadAllText($purgeFile)
    # Strip comments: a store named only in prose is not wired.
    $code = [regex]::Replace($src, '//[^\r\n]*', '')
    $stores = { param($body) ,@([regex]::Matches($body, '(\w+Store)\.') |
                                ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) }
    $hasBody  = ([regex]::Matches($code, 'public\s+static\s+bool\s+Has\w*\s*\([^)]*\)\s*(=>[^;]*;|\{(?:[^{}]|\{[^{}]*\})*\})') |
                 ForEach-Object { $_.Value }) -join "`n"
    $burnBody = ([regex]::Matches($code, 'public\s+static\s+[^\r\n]*BurnAll\s*\([^)]*\)\s*(=>[^;]*;|\{(?:[^{}]|\{[^{}]*\})*\})') |
                 ForEach-Object { $_.Value }) -join "`n"
    if (-not $hasBody -or -not $burnBody) { Bad 'could not read the Has*/BurnAll bodies in KmhEscrowPurge - check 18 cannot verify anything' }
    else {
        $hasStores  = & $stores $hasBody
        $burnStores = & $stores $burnBody
        $onlyHas  = @($hasStores  | Where-Object { $burnStores -notcontains $_ })
        $onlyBurn = @($burnStores | Where-Object { $hasStores  -notcontains $_ })
        foreach ($s in $onlyHas)  { Bad "$s is detected as holding escrow but BurnAll never clears it - a reset would report it and leave it" }
        foreach ($s in $onlyBurn) { Bad "$s is burned by BurnAll but no Has* reports it - a player holding only that escrow skips the reset entirely" }
        if ($hasStores.Count -eq 0) { Bad 'KmhEscrowPurge names no stores at all - the shared escrow list is empty' }
        elseif ($onlyHas.Count -eq 0 -and $onlyBurn.Count -eq 0) {
            Good ("escrow detection and burning agree on $($hasStores.Count) store(s): " + ($hasStores -join ', '))
        }
    }
}

# --- 19. Capability tokens agree across repos ---
# The client gates features on Has("token"). A token spelled differently on one side does not error - it just reads
# as "the server cannot do this", so the feature silently never appears. Same failure mode as a feature-key typo
# (section 9), and equally invisible without a check.
Section '19. Capability tokens agree across repos'
$capSrv = Join-Path $serverSrc 'SubProtocol\KmhCapabilities.cs'
$capCli = Join-Path $clientSrc 'SubProtocol\KmhCapabilities.cs'
if (-not (Test-Path -LiteralPath $capSrv) -or -not (Test-Path -LiteralPath $capCli)) {
    Warn 'KmhCapabilities.cs missing on one side - verify capability tokens by hand'
} else {
    function Get-CapTokens([string]$path) {
        $map = @{}
        foreach ($m in [regex]::Matches([System.IO.File]::ReadAllText($path), 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')) {
            $map[$m.Groups[1].Value] = $m.Groups[2].Value
        }
        return $map
    }
    $sTok = Get-CapTokens $capSrv
    $cTok = Get-CapTokens $capCli
    $capBad = $false
    # The server is the authority: every token it advertises must be nameable by the client.
    foreach ($k in $sTok.Keys) {
        if (-not $cTok.ContainsKey($k)) { Bad "capability $k exists on the server but not on the client - the client can never gate on it"; $capBad = $true }
        elseif ($cTok[$k] -cne $sTok[$k]) { Bad "capability $k is '$($sTok[$k])' on the server and '$($cTok[$k])' on the client - Has() would silently read false"; $capBad = $true }
    }
    foreach ($k in $cTok.Keys) {
        if (-not $sTok.ContainsKey($k)) { Bad "capability $k is gated on by the client but no server advertises it - that feature is permanently off"; $capBad = $true }
    }
    # A token in the manifest list is what actually reaches the wire; a declared-but-unlisted one never arrives.
    $manifest = [System.IO.File]::ReadAllText($capSrv)
    $listBlock = [regex]::Match($manifest, '_all\s*=\s*new\s+List<string>\s*\{(.*?)\}', 'Singleline')
    if (-not $listBlock.Success) { Bad 'could not read the capability manifest list - check 19 cannot verify what is advertised'; $capBad = $true }
    else {
        foreach ($k in $sTok.Keys) {
            if ($listBlock.Groups[1].Value -notmatch ('\b' + [regex]::Escape($k) + '\b')) {
                Bad "capability $k is declared but missing from the manifest list - it is never advertised, so clients gate it off"; $capBad = $true
            }
        }
    }
    # A gate applied at each call site is a gate someone forgets on the next one. The roadworks sends funnel through
    # one helper, so this file may contain exactly one Send - a second means an ungated path to a server that has no
    # roadworks handler at all.
    $rwHandler = Join-Path $clientSrc 'Features\Roadworks\RoadworksHandler.cs'
    if (Test-Path -LiteralPath $rwHandler) {
        $sends = ([regex]::Matches([System.IO.File]::ReadAllText($rwHandler), 'KmhDispatcher\.Send\s*\(')).Count
        if ($sends -ne 1) {
            Bad "RoadworksHandler makes $sends direct Send call(s) - all of them must funnel through the capability-gated helper (expected exactly 1)"
            $capBad = $true
        }
    }
    if (-not $capBad) { Good "$($sTok.Count) capability token(s) agree across repos and are all advertised" }
}

# --- 20. Shared DTO vocabulary agrees across repos ---
# Section 3 pairs DTO files by their JsonProperty names, which says nothing about the string CONSTANTS inside them -
# the access modes, reward destinations, archetypes and states both sides compare against. A drifted constant does
# not error; the comparison just never matches, so the UI silently omits a site or shows the wrong state.
Section '20. Shared DTO vocabulary agrees across repos'
$vocabBad = $false
$vocabPairs = 0
foreach ($sf in (Get-ChildItem (Join-Path $serverSrc 'Features') -Recurse -File -Filter *.cs |
                 Where-Object { $_.FullName -match '\\Dto\\' -and $_.FullName -notmatch '\\(bin|obj)\\' })) {
    $rel = $sf.FullName.Substring($serverSrc.Length).TrimStart('\')
    $cf  = Join-Path $clientSrc $rel
    if (-not (Test-Path -LiteralPath $cf)) { continue }
    function Get-Consts([string]$p) {
        $m = @{}
        foreach ($x in [regex]::Matches([System.IO.File]::ReadAllText($p), 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')) { $m[$x.Groups[1].Value] = $x.Groups[2].Value }
        return $m
    }
    $sc = Get-Consts $sf.FullName
    $cc = Get-Consts $cf
    if ($sc.Count -eq 0 -and $cc.Count -eq 0) { continue }
    $vocabPairs++
    foreach ($k in $sc.Keys) {
        if (-not $cc.ContainsKey($k)) { Bad "$rel : $k exists on the server but not on the client - the client cannot name that value"; $vocabBad = $true }
        elseif ($cc[$k] -cne $sc[$k]) { Bad "$rel : $k is '$($sc[$k])' on the server and '$($cc[$k])' on the client - comparisons would silently never match"; $vocabBad = $true }
    }
    foreach ($k in $cc.Keys) {
        if (-not $sc.ContainsKey($k)) { Bad "$rel : $k exists on the client but not on the server - it can never be sent"; $vocabBad = $true }
    }
}
if (-not $vocabBad) { Good "$vocabPairs paired DTO file(s) share identical string constants" }

# --- 21. Every Discord subcommand has a handler ---
# A subcommand declared in the builder but missing from the dispatch switch still APPEARS in Discord and still
# autocompletes - it just answers "Unknown command". Same silent-hole shape as sections 2 and 14.
Section '21. Discord subcommands are all dispatched'
$slashFile = Join-Path $serverSrc 'Features\Discord\Commands\DiscordSlashCommands.cs'
if (-not (Test-Path -LiteralPath $slashFile)) { Warn 'DiscordSlashCommands.cs not found - verify command dispatch by hand' }
else {
    $slashText = [System.IO.File]::ReadAllText($slashFile)
    $subs  = @([regex]::Matches($slashText, 'Sub\(\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $cases = @([regex]::Matches($slashText, 'case\s+"([^"]+)"\s*:')  | ForEach-Object { $_.Groups[1].Value })
    $undispatched = @()
    foreach ($s in $subs) {
        $hit = $false
        foreach ($c in $cases) { if ($c -ceq $s -or $c.EndsWith('.' + $s)) { $hit = $true; break } }
        if (-not $hit) { $undispatched += $s }
    }
    foreach ($u in $undispatched) { Bad "Discord subcommand '$u' is declared but never dispatched - it would answer 'Unknown command'" }
    if ($subs.Count -eq 0) { Warn 'no Discord subcommands found - check 21 verified nothing' }
    elseif ($undispatched.Count -eq 0) { Good "$($subs.Count) Discord subcommand(s) all reach a handler" }
}

# --- 22. Site ownership goes through one authority ---
# A bare OwnerUsername comparison is the bug this whole phase exists to prevent. On a system or neutral Outpost that
# field is EMPTY, so `s.OwnerUsername == caller` hands control to any caller with an empty username - the same shape
# that already hid every guild-only site from the admin view once. SiteOwnership is the only place allowed to
# compare it; everywhere else asks IsOwnedBy / CanManage / PayoutAccount / OwningGuildLive.
Section '22. Site ownership goes through one authority'
# Comparisons on a DIFFERENT type's OwnerUsername. Each needs a reason, and a stale entry fails below.
$ownExempt = @{
    'RoadworksStore.cs'        = 'RoadProject.OwnerUsername is the player who started a road project, not a Site controller'
}
$ownBad = @()
$ownScanned = 0
Get-ChildItem $serverSrc -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'SiteOwnership.cs' } |
    ForEach-Object {
        $lines = [System.IO.File]::ReadAllLines($_.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -notmatch 'OwnerUsername') { continue }
            # A comparison, not a read or an assignment.
            if ($line -notmatch '(string\.Equals\s*\([^)]*OwnerUsername)|(OwnerUsername\s*(==|!=))|((==|!=)\s*\w+\.OwnerUsername)') { continue }
            $ownScanned++
            if ($ownExempt.ContainsKey($_.Name)) { continue }
            $ownBad += "$($_.Name):$($i+1)"
        }
    }
foreach ($b in $ownBad) {
    Bad "$b compares OwnerUsername directly - use SiteOwnership (an empty owner on a system Outpost would match an empty caller)"
}
# An exemption that no longer has a comparison in it is drift.
foreach ($k in $ownExempt.Keys) {
    $f = Get-ChildItem $serverSrc -Recurse -File -Filter $k | Select-Object -First 1
    if (-not $f) { Bad "ownership exemption '$k' names a file that no longer exists"; continue }
    if (-not (Select-String -Path $f.FullName -Pattern 'OwnerUsername\s*(==|!=)|string\.Equals\s*\([^)]*OwnerUsername' -Quiet)) {
        Bad "ownership exemption '$k' is stale - it no longer compares OwnerUsername at all"
    }
}
if ($ownBad.Count -eq 0) { Good "$ownScanned OwnerUsername comparison(s) scanned, all inside SiteOwnership or exempt with a reason" }

# --- 23. Background work runs on one scheduler ---
# ExpirySweeper and WorldEngine each carried an identical loop scaffold - cancellation token, initial delay,
# try/catch, Task.Delay - and the Frontier director would have been a third copy. Three loops means three places to
# fix a scheduling bug and three threads doing the same waiting. Jobs register with KmhScheduler instead.
Section '23. Background work runs on one scheduler'
# Not scheduled work. Each needs a reason, and a stale entry fails below.
$loopExempt = @{
    'KmhChatDiscordBridge.cs' = 'demand-driven drain with Discord rate-limit pacing - it exits when the queue empties and must stay single-flight for relay ordering, so it is not a periodic job'
}
$loopBad = @()
Get-ChildItem $serverSrc -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'KmhScheduler.cs' -and -not $loopExempt.ContainsKey($_.Name) } |
    ForEach-Object {
        $text = [System.IO.File]::ReadAllText($_.FullName)
        # A periodic loop: a delay inside a while. Awaiting once (a timeout, a handshake) is not a loop.
        if ($text -match 'while\s*\([^)]*\)\s*\{(?:[^{}]|\{[^{}]*\})*Task\.Delay') { $loopBad += $_.Name }
    }
foreach ($b in $loopBad) {
    Bad "$b runs its own periodic loop - register the job with KmhScheduler instead (three copies of one scaffold is how a scheduling bug gets fixed twice and missed once)"
}
# An exemption for a file that no longer has such a loop is drift.
foreach ($k in $loopExempt.Keys) {
    $f = Get-ChildItem $serverSrc -Recurse -File -Filter $k | Select-Object -First 1
    if (-not $f) { Bad "scheduler exemption '$k' names a file that no longer exists"; continue }
    $t = [System.IO.File]::ReadAllText($f.FullName)
    if ($t -notmatch 'while\s*\([^)]*\)\s*\{(?:[^{}]|\{[^{}]*\})*Task\.Delay') {
        Bad "scheduler exemption '$k' is stale - it no longer runs its own loop at all"
    }
}
if ($loopBad.Count -eq 0) { Good "no periodic loop outside KmhScheduler ($($loopExempt.Count) exempt with a reason)" }

# --- 24. Completed operations reach the consequence dispatcher ---
# A Frontier Operation is a server quest whose completion changes the world. FinishCompletion is the one path both
# contribute and deliver take; if the dispatch call is dropped, operations still complete and pay out while the
# world silently never changes - the failure looks like nothing at all.
Section '24. Completed operations reach the consequence dispatcher'
$engine = Join-Path $serverSrc 'Features\World\WorldEngine.cs'
$dispatcher = Join-Path $serverSrc 'Features\Frontier\KmhOperationConsequence.cs'
if (-not (Test-Path -LiteralPath $engine) -or -not (Test-Path -LiteralPath $dispatcher)) {
    Warn 'world engine or consequence dispatcher not found - verify operation dispatch by hand'
} else {
    $engineText = [System.IO.File]::ReadAllText($engine)
    if ($engineText -notmatch 'KmhOperationConsequence\.Apply') {
        Bad 'the quest completion path never calls KmhOperationConsequence.Apply - operations would pay out and change nothing'
    }
    # The dispatcher must refuse a consequence it cannot apply rather than ignoring it.
    $dispText = [System.IO.File]::ReadAllText($dispatcher)
    if ($dispText -notmatch 'IsDispatchable') {
        Bad 'the consequence dispatcher does not check IsDispatchable - an unimplemented consequence would resolve into silence'
    }
    if (-not $fail) { Good 'completed operations dispatch, and undispatchable consequences are refused' }
}

# --- 25. Client->server payload keys are read (the mirror of section 4) ---
# Section 4 only checks the server->client direction, and says so. This is the other half: the client builds request
# payloads as anonymous objects and dictionaries, so a key the server never reads is not a compile error - it is a
# silently ignored field on paths that move value (a deposit's txn_id, a withdraw's fingerprint).
Section '25. Client request keys are read by the server'

# Wire-shaped keys the client SENDS: dictionary entries { "a_b", x } and anonymous members a_b = x.
$cliSends = Get-Names $clientSrc '\{\s*"([a-z][a-z0-9]*(?:_[a-z0-9]+)+)"\s*,'
foreach ($n in (Get-Names $clientSrc '(?<![\w."])([a-z][a-z0-9]*(?:_[a-z0-9]+)+)\s*=[^=]')) { [void]$cliSends.Add($n) }

# What the server CONSUMES: DTO properties it deserializes into, plus envelope accessors.
$srvReads = Get-Names $serverSrc '\[JsonProperty\(\s*"([^"]+)"'
# Every accessor KmhEnvelope exposes. Missing one here reads as "the server never looks at this key", which is a
# false alarm on a path that works - GetIntArray was absent on the first run and flagged the Roadworks route.
foreach ($n in (Get-Names $serverSrc 'Get(?:Int|Long|String|Bool|IntArray|IntMap|StringList)\(\s*"([^"]+)"')) { [void]$srvReads.Add($n) }

# Names the client also declares as its own DTO properties are server->client reads, not sends - section 3 pairs those.
$cliDtoProps = Get-Names $clientSrc '\[JsonProperty\(\s*"([^"]+)"'

$unread = @($cliSends | Where-Object { -not $srvReads.Contains($_) -and -not $cliDtoProps.Contains($_) }) | Sort-Object
if ($unread.Count) {
    Warn "client sends key(s) the server never reads (verify by hand): $($unread -join ', ')"
} else {
    Good "every wire-shaped key the client sends is read somewhere on the server ($($cliSends.Count) checked)"
}

# --- 26. The director's state never leaves its lock uncopied ---
# _state carries four mutable collections plus a placement token whose proposals arrive on the network thread. Every
# reader runs outside the lock - the tick's NextStep enumerates the resolution journal, the save serializes the whole
# record - so handing out the live object is an InvalidOperationException waiting for a busy server. This shipped
# four times in one file, which is why it is a check rather than a comment.
Section '26. Director state is copied out of its lock'

$directorFile = Join-Path $serverSrc 'Features\Frontier\KmhWorldDirector.cs'
if (-not (Test-Path -LiteralPath $directorFile)) {
    Warn 'KmhWorldDirector.cs not found - verify the director state handoff by hand'
} else {
    $dirBad = $false
    $lineNo = 0
    foreach ($line in [System.IO.File]::ReadAllLines($directorFile)) {
        $lineNo++
        if ($line -match '^\s*//') { continue }
        # Any assignment whose right-hand side is the bare live state, rather than a copy of it.
        if ($line -match '=\s*_state\s*;') {
            Bad "KmhWorldDirector.cs:${lineNo} hands out the live director state - use _state.CopyForRead(): $($line.Trim())"
            $dirBad = $true
        }
    }
    if (-not (Select-String -Path $directorFile -Pattern 'CopyForRead' -Quiet)) {
        Bad 'KmhWorldDirector no longer copies its state for readers'
        $dirBad = $true
    }
    # The cooldown map is persisted and only ever added to. A pure prune helper is easy to test and just as easy to
    # leave unwired - the map then grows for the life of the server with every test still green.
    $dirText = [System.IO.File]::ReadAllText($directorFile)
    $pruneCalls = ([regex]::Matches($dirText, 'PruneExpiredCooldowns\s*\(')).Count
    if ($pruneCalls -lt 3) {   # the definition, the place that adds one, and the load path
        Bad "KmhWorldDirector calls PruneExpiredCooldowns $pruneCalls time(s) - the persisted cooldown map is no longer pruned where it grows"
        $dirBad = $true
    }
    if (-not $dirBad) { Good 'director state is copied for readers, and its cooldown map is pruned where it grows' }
}

# --- 27. The claim window has a way out ---
# 'claimable' is the only outpost state with a deadline, and being claimed is the only transition anyone wrote a
# caller for. If nothing expires it, an unclaimed location sits claimable-but-refused forever while still holding a
# tile and a slot against the director's cap - and after MaxOutposts of those the director never acts again. The
# failure is silent and permanent, which is why the sweep is checked rather than trusted.
Section '27. Unclaimed outposts leave the claim window'

$siteStoreFile = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
$dirFile27     = Join-Path $serverSrc 'Features\Frontier\KmhWorldDirector.cs'
if (-not (Test-Path -LiteralPath $siteStoreFile) -or -not (Test-Path -LiteralPath $dirFile27)) {
    Warn 'SiteStore or KmhWorldDirector not found - verify the claim-window expiry by hand'
} else {
    $expBad = $false
    if (-not (Select-String -Path $siteStoreFile -Pattern 'ExpireClaimWindows' -Quiet)) {
        Bad 'SiteStore has no ExpireClaimWindows - an unclaimed outpost would hold its slot forever'
        $expBad = $true
    }
    $dirText27 = [System.IO.File]::ReadAllText($dirFile27)
    if ($dirText27 -notmatch 'ExpireClaimWindows') {
        Bad 'the director never expires claim windows - its outpost cap fills with unclaimable locations'
        $expBad = $true
    }
    # Freeing the slot is the half that actually unblocks the director; expiring without it changes nothing.
    elseif ($dirText27 -notmatch 'ExpireClaimWindows[\s\S]{0,300}?NoteOutpost\s*\(\s*\w+\s*,\s*false') {
        Bad 'expired outposts are not released with NoteOutpost(tile, false) - the cap stays full'
        $expBad = $true
    }
    # An expiry advances the outpost with no operation behind it. Stamping the origin unconditionally would overwrite
    # it with 0 and lose which operation created the place - the heritage the capture UI reads.
    if ([System.IO.File]::ReadAllText($siteStoreFile) -notmatch 'if\s*\(\s*operationId\s*>\s*0\s*\)\s*s\.OriginOperationId\s*=') {
        Bad 'TryAdvanceOutpost stamps OriginOperationId unconditionally - a claim-window expiry would erase the origin'
        $expBad = $true
    }
    if (-not $expBad) { Good 'an unclaimed outpost goes dormant, releases its slot, and keeps its origin' }
}

# --- 28. Roadworks construction has a producer ---
# AdvanceProject is the only code that lays road, and it shipped with NO caller: projects took full escrow and never
# built anything while every Roadworks test stayed green. The rule being tested is not the rule being reached, so the
# producer is checked structurally - the site work cycle must feed it, outside the site lock.
Section '28. Roadworks work reaches AdvanceProject'

$siteStore28 = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
$roadStore28 = Join-Path $serverSrc 'Features\Roadworks\RoadworksStore.cs'
if (-not (Test-Path -LiteralPath $siteStore28) -or -not (Test-Path -LiteralPath $roadStore28)) {
    Warn 'SiteStore or RoadworksStore not found - verify Roadworks progression by hand'
} else {
    $rwBad = $false
    $siteText28 = [System.IO.File]::ReadAllText($siteStore28)
    if ($siteText28 -notmatch 'AdvanceProject\s*\(') {
        Bad 'the site work cycle never calls AdvanceProject - Roadworks projects would take escrow and never build'
        $rwBad = $true
    }
    if ($siteText28 -notmatch 'RoadworkPerCycle\s*\(') {
        Bad 'nothing computes Roadworks construction work - the archetype would produce goods only'
        $rwBad = $true
    }
    # Labour, not yield: SiteStore.OutputFactor carries the tier multiplier and the production-building bonus.
    # Scoped to the helper's own body - the goods path in RunRewardCycle uses OutputFactor(s) quite legitimately,
    # and a window that spilled into it made this check fire on correct code.
    $bodyMatch = [regex]::Match($siteText28, 'internal static double RoadworkPerCycle[\s\S]*?\r?\n        \}')
    if (-not $bodyMatch.Success) {
        Bad 'RoadworkPerCycle not found in the expected shape - verify the Roadworks labour model by hand'
        $rwBad = $true
    }
    elseif ($bodyMatch.Value -match '(?<!SiteStability\.)OutputFactor\s*\(\s*s\s*\)') {
        Bad 'RoadworkPerCycle uses SiteStore.OutputFactor - yield multipliers must not accelerate road construction'
        $rwBad = $true
    }
    if (-not $rwBad) { Good 'site labour reaches AdvanceProject, and road speed is not driven by yield multipliers' }
}

# --- 29. Standings statistics have authoritative writers ---
# SitesBuilt/WorkerXp/SitesOwned all shipped as permanent zeros: declared, displayed in Standings and Discord, fed
# into the economy score, and written by nothing. Two are live-derived and one is a lifetime counter, so each needs a
# different proof - a counter needs its single production caller, a derivation needs to key off the right identity.
Section '29. Standings statistics are actually written'

$statsFile = Join-Path $serverSrc 'Features\PlayerStats\PlayerStatsStore.cs'
$siteFile29 = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
if (-not (Test-Path -LiteralPath $statsFile) -or -not (Test-Path -LiteralPath $siteFile29)) {
    Warn 'PlayerStatsStore or SiteStore not found - verify the Standings writers by hand'
} else {
    $statsText = [System.IO.File]::ReadAllText($statsFile)
    $siteText29 = [System.IO.File]::ReadAllText($siteFile29)
    $stBad = $false

    if ($statsText -notmatch 'BumpSitesBuilt') {
        Bad 'PlayerStatsStore has no BumpSitesBuilt - SitesBuilt would be a permanent zero again'; $stBad = $true
    }
    # The counter is worthless without its production caller, and a helper with tests but no caller is this
    # codebase's most repeated defect.
    elseif ($siteText29 -notmatch 'BumpSitesBuilt\s*\(') {
        Bad 'nothing calls BumpSitesBuilt when a site is built - the lifetime counter never increments'; $stBad = $true
    }
    foreach ($d in @('SitesOwnedByPlayer', 'WorkerXpByWorker')) {
        if ($statsText -notmatch "$d\s*\(") { Bad "PlayerStatsStore no longer derives $d"; $stBad = $true }
    }
    # WorkerXp belongs to the worker. Keying it off the site's payout account would credit the owner instead.
    $xpBody = [regex]::Match($statsText, 'private static Dictionary<string, long> WorkerXpByWorker[\s\S]*?\r?\n        \}')
    if ($xpBody.Success -and $xpBody.Value -match 'PayoutAccount') {
        Bad 'WorkerXpByWorker keys off the site payout account - a site owner would absorb their workers'' XP'
        $stBad = $true
    }
    if (-not $stBad) { Good 'SitesBuilt increments where sites are built; SitesOwned and WorkerXp derive from the right identity' }
}

# --- 30. Guild item contributions are recorded like silver ---
# RecordItem shipped with no caller: silver donations built a contribution history and item donations built nothing,
# so the two were never comparable in 'kmh contributions', player snapshots or Discord. The recording must also stay
# scoped to guild vaults - an ordinary personal deposit is not a contribution to anyone.
Section '30. Guild item contributions are recorded'

$treas30 = Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs'
if (-not (Test-Path -LiteralPath $treas30)) {
    Warn 'TreasuryStore not found - verify guild item contributions by hand'
} else {
    $t30 = [System.IO.File]::ReadAllText($treas30)
    $giBad = $false
    $depositBody = [regex]::Match($t30, 'public static bool DepositItem[\s\S]*?\r?\n        \}')
    if (-not $depositBody.Success) {
        Bad 'DepositItem not found in the expected shape - verify guild item contributions by hand'; $giBad = $true
    }
    else {
        if ($depositBody.Value -notmatch 'RecordItem\s*\(') {
            Bad 'an item deposit into a guild vault never reaches RecordItem - item contribution history stays empty'
            $giBad = $true
        }
        if ($depositBody.Value -notmatch 'AddItemsContributed\s*\(') {
            Bad 'ItemsContributed is never incremented - the member summary silver keeps would stay zero for items'
            $giBad = $true
        }
        # Scoped to guild vaults only, and recorded after the deposit has been persisted.
        if ($depositBody.Value -notmatch '_personal:[\s\S]{0,400}?RecordItem') {
            Bad 'guild item contribution recording is not scoped to guild vaults - a personal deposit would count'
            $giBad = $true
        }
    }
    if (-not $giBad) { Good 'a guild item deposit records a contribution, and a personal deposit does not' }
}

# --- 31. Frontier captures are recorded when one commits ---
# A capture is history: ownership can be lost afterwards, so it cannot be reconstructed from current state, and
# KmhHistory is pruned so it cannot back an all-time record either. The counter must therefore increment inside the
# committed claim path - and only there, or a refused claim would award one.
Section '31. Frontier captures are recorded on commit'

$siteFile31 = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
$statsFile31 = Join-Path $serverSrc 'Features\PlayerStats\PlayerStatsStore.cs'
if (-not (Test-Path -LiteralPath $siteFile31) -or -not (Test-Path -LiteralPath $statsFile31)) {
    Warn 'SiteStore or PlayerStatsStore not found - verify Frontier capture records by hand'
} else {
    $fcBad = $false
    if ([System.IO.File]::ReadAllText($statsFile31) -notmatch 'BumpFrontierCaptures') {
        Bad 'PlayerStatsStore has no BumpFrontierCaptures - captures would leave no historical record'; $fcBad = $true
    }
    $claimBody = [regex]::Match([System.IO.File]::ReadAllText($siteFile31),
                                'public static \(bool ok, string reason\) ClaimOutpost[\s\S]*?\r?\n        \}')
    if (-not $claimBody.Success) {
        Bad 'ClaimOutpost not found in the expected shape - verify the capture record by hand'; $fcBad = $true
    }
    elseif ($claimBody.Value -notmatch 'BumpFrontierCaptures\s*\(') {
        Bad 'a committed capture never records a Frontier capture - the statistic would stay a permanent zero'
        $fcBad = $true
    }
    # It must sit after the transfer is persisted, not before the refusal checks - a refused claim awards nothing.
    elseif ($claimBody.Value.IndexOf('BumpFrontierCaptures') -lt $claimBody.Value.IndexOf('SaveToDisk')) {
        Bad 'the capture is recorded before the transfer is persisted - a refused claim could still award one'
        $fcBad = $true
    }
    if (-not $fcBad) { Good 'a committed capture records a Frontier capture, and only after it persists' }
}

# --- 32. A seller-less auction never burns the winner's silver ---
# Posting refuses an empty seller, so a row without one is malformed rather than ownerless. DepositSilver returns false
# for an empty owner, so settling such a row as a sale would hand the item to the winner and discard their escrowed bid
# with no record. MarketplaceStore already refuses this case in Buy/Cancel/Expire/AdminCancel; auctions must too, and
# any silver that cannot be credited has to land in recovery rather than nowhere.
Section '32. A seller-less auction never burns the winner''s silver'

$aucFile32 = Join-Path $serverSrc 'Features\Auctions\AuctionStore.cs'
if (-not (Test-Path -LiteralPath $aucFile32)) {
    Warn 'AuctionStore not found - verify the seller-less auction guard by hand'
} else {
    $aucText32 = [System.IO.File]::ReadAllText($aucFile32)
    $auBad = $false

    $settleBody32 = [regex]::Match($aucText32, 'public static SettleOutcome SettleNow[\s\S]*?\r?\n        \}')
    if (-not $settleBody32.Success) {
        Bad 'SettleNow not found in the expected shape - verify the seller-less guard by hand'; $auBad = $true
    }
    elseif ($settleBody32.Value -notmatch 'IsNullOrEmpty\(\s*a\.SellerUsername\s*\)') {
        Bad 'SettleNow does not refuse a seller-less row - the winner''s escrowed bid would be discarded'; $auBad = $true
    }

    $bidBody32 = [regex]::Match($aucText32, 'public static BidResult Bid[\s\S]*?\r?\n        \}')
    if (-not $bidBody32.Success) {
        Bad 'Bid not found in the expected shape - verify the seller-less guard by hand'; $auBad = $true
    }
    elseif ($bidBody32.Value -notmatch 'IsNullOrEmpty\(\s*pa\.SellerUsername\s*\)') {
        Bad 'Bid accepts a bid on a seller-less row - escrow would strand on a malformed auction'; $auBad = $true
    }

    # Uncreditable silver must be held, not dropped - and via the one shared primitive, so every feature behaves alike.
    $depBody32 = [regex]::Match($aucText32, 'private static bool DepositLong[\s\S]*?\r?\n        \}')
    if (-not $depBody32.Success) {
        Bad 'DepositLong does not return its result - a failed credit would be silently discarded again'; $auBad = $true
    }
    elseif ($depBody32.Value -notmatch 'DeliverSilver\s*\(') {
        Bad 'auction payouts bypass the shared credit-or-hold, so a failed credit can drop silver'; $auBad = $true
    }

    $escrowFile32 = Join-Path $serverSrc 'Items\KmhPayloadEscrow.cs'
    if (-not (Test-Path -LiteralPath $escrowFile32)) {
        Warn 'KmhPayloadEscrow not found - verify the shared silver delivery by hand'
    } else {
        $deliver32 = [regex]::Match([System.IO.File]::ReadAllText($escrowFile32),
                                    'public static bool DeliverSilver[\s\S]*?\r?\n        \}')
        if (-not $deliver32.Success) {
            Bad 'DeliverSilver is missing - silver has no credit-or-hold sibling to Deliver/DeliverCompact'; $auBad = $true
        }
        elseif ($deliver32.Value -notmatch 'HoldSilver\s*\(') {
            Bad 'DeliverSilver drops silver it could not credit instead of holding it in recovery'; $auBad = $true
        }
    }

    if (-not $auBad) { Good 'a seller-less auction refunds the bidder, and uncreditable silver is held in recovery' }
}

# --- 33. Reloaded vaults and sealed corrupt files ---
# Both fixes are invisible until something has already gone wrong, so no ordinary test would notice them being
# dropped: a vault read back from JSON must match item keys the way every live path does, and a corrupt file we
# could not move aside must survive the next save - without freezing the economy for longer than the file exists.
Section '33. Reloaded vaults and sealed corrupt files'

$treasFile33 = Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs'
$jsonFile33  = Join-Path $serverSrc 'Persistence\JsonFileStore.cs'
if (-not (Test-Path -LiteralPath $treasFile33) -or -not (Test-Path -LiteralPath $jsonFile33)) {
    Warn 'TreasuryStore or JsonFileStore not found - verify the reload/seal invariants by hand'
} else {
    $t33 = [System.IO.File]::ReadAllText($treasFile33)
    $j33 = [System.IO.File]::ReadAllText($jsonFile33)
    $bad33 = $false

    $load33 = [regex]::Match($t33, 'public static void LoadFromDisk[\s\S]*?\r?\n        \}')
    if (-not $load33.Success) {
        Bad 'TreasuryStore.LoadFromDisk not found in the expected shape - verify the item-key comparer by hand'; $bad33 = $true
    }
    elseif ($load33.Value -notmatch 'NormalizeItemKeys') {
        Bad 'a reloaded vault keeps the ordinal item map JSON produced - live lookups and the snapshot copy disagree'; $bad33 = $true
    }

    $create33 = [regex]::Match($t33, 'private static TreasurySnapshot GetOrCreateLocked[\s\S]*?\r?\n        \}')
    if ($create33.Success -and $create33.Value -notmatch 'NormalizeItemKeys') {
        Bad 'a newly created vault gets the default comparer - its items stop matching case-insensitively'; $bad33 = $true
    }

    # Reconcile only acts on Pending, and commit/revert remove the row - anything else resting there is unresolvable
    # and invisible (the file is not corrupt, so the integrity check passes it). Load has to say so.
    if ($load33.Success -and $load33.Value -notmatch 'WarnOnUnreconcilableDeposits') {
        Bad 'a pending deposit in an unreconcilable state loads silently - its value would sit held forever'; $bad33 = $true
    }

    $save33 = [regex]::Match($j33, 'public static bool Save<T>\(string path, T value, long sequence\)[\s\S]*?\r?\n        \}')
    if (-not $save33.Success) {
        Bad 'JsonFileStore.Save not found in the expected shape - verify the corrupt-file seal by hand'; $bad33 = $true
    }
    else {
        if ($save33.Value -notmatch 'Unquarantined') {
            Bad 'Save ignores the unquarantined set - a corrupt file that could not be moved aside gets overwritten'; $bad33 = $true
        }
        # The seal has to be able to end. RetryUnwritableStores lifts the economy freeze only if a later save can pass.
        elseif ($save33.Value -notmatch 'TryRemove') {
            Bad 'the seal never clears - the persistence freeze would outlive the corrupt file it protects'; $bad33 = $true
        }
    }

    # SiteStore keeps the same guarantee independently of Newtonsoft. Its DTO initializers currently carry the
    # comparer, so a reload arrives correct - but nothing would notice if that stopped being true, and a site whose
    # workers stop matching reads as having none and silently pauses.
    $siteFile33 = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
    if (Test-Path -LiteralPath $siteFile33) {
        $s33 = [System.IO.File]::ReadAllText($siteFile33)
        $sload33 = [regex]::Match($s33, 'public static void LoadFromDisk\(\)[\s\S]*?\r?\n        \}')
        if (-not $sload33.Success) {
            Warn 'SiteStore.LoadFromDisk not found in the expected shape - verify the reloaded comparers by hand'
        }
        elseif ($sload33.Value -notmatch 'NormalizeKeys') {
            Bad 'a reloaded site keeps the ordinal comparer JSON produced - its workers stop matching and it pauses'; $bad33 = $true
        }
    }

    if (-not $bad33) { Good 'a reloaded vault and a reloaded site normalize their keys, and a sealed corrupt file blocks saves only while it exists' }
}

# --- 34. One economy score ---
# The score weighs live-joined fields (WorkerXp comes from the sites, SitesBuilt is bumped elsewhere), so caching it
# on the row silently dropped both terms while the tests still passed. The client showed the right number only because
# it kept its own copy of the formula - two implementations of one rule, already disagreeing.
Section '34. One economy score'

$statsFile34 = Join-Path $serverSrc 'Features\PlayerStats\PlayerStatsStore.cs'
$standFile34 = Join-Path $clientSrc 'Features\Standings\StandingsData.cs'
$dlgFile34   = Join-Path $clientSrc 'Features\Standings\Dialog_KMHStandings.cs'
if (-not (Test-Path -LiteralPath $statsFile34) -or -not (Test-Path -LiteralPath $standFile34)) {
    Warn 'PlayerStatsStore or StandingsData not found - verify the economy score by hand'
} else {
    $s34 = [System.IO.File]::ReadAllText($statsFile34)
    $c34 = [System.IO.File]::ReadAllText($standFile34)
    $d34 = if (Test-Path -LiteralPath $dlgFile34) { [System.IO.File]::ReadAllText($dlgFile34) } else { '' }
    $bad34 = $false

    $snap34 = [regex]::Match($s34, 'public static PlayerStatsSnapshot BuildSnapshot\(\)[\s\S]*?\r?\n        \}')
    if (-not $snap34.Success) {
        Bad 'BuildSnapshot not found in the expected shape - verify the score derivation by hand'; $bad34 = $true
    }
    else {
        if ($snap34.Value -notmatch 'EconomyScoreOf') {
            Bad 'BuildSnapshot ships a score it did not derive - the WorkerXp join lands after the copy, so the term is lost'; $bad34 = $true
        }
        # Order matters more than presence: derived before the joins is the same stale number with extra steps.
        elseif ($snap34.Value.IndexOf('EconomyScoreOf') -lt $snap34.Value.IndexOf('workerXp.TryGetValue')) {
            Bad 'the score is derived before the live joins - site work would not be counted'; $bad34 = $true
        }
    }

    # A second copy of the weighting is how the two surfaces drifted the first time.
    if ($c34 -match 'SilverDonated[\s\S]{0,200}SitesBuilt' -or $d34 -match 'SilverDonated[\s\S]{0,200}SitesBuilt') {
        Bad 'the client recomputes the economy score - the server owns the weighting and ranks the same number on Discord'; $bad34 = $true
    }

    if (-not $bad34) { Good 'the economy score is derived once, on the server, after the live joins' }
}

# --- 35. Mail escrow always comes back ---
# Mail attachments leave the sender's treasury and live outside every vault until released. Reading a message does not
# claim its attachment, so the inbox-cap eviction can hit a row still holding escrow - Delete and PruneOld refund first,
# and that one dropped the row. Silver also has to travel the recovery-safe primitive: a discarded DepositSilver result
# burns it, which is why the item and gear paths already use the wrappers.
Section '35. Mail escrow always comes back'

$mailFile35 = Join-Path $serverSrc 'Features\Mail\MailStore.cs'
if (-not (Test-Path -LiteralPath $mailFile35)) {
    Warn 'MailStore not found - verify the mail escrow release by hand'
} else {
    $m35 = [System.IO.File]::ReadAllText($mailFile35)
    $bad35 = $false

    $room35 = [regex]::Match($m35, 'private static bool MakeRoomLocked\([\s\S]*?\r?\n        \}')
    if (-not $room35.Success) {
        Bad 'MakeRoomLocked not found in the expected shape - verify the eviction refund by hand'; $bad35 = $true
    }
    elseif ($room35.Value -notmatch 'TryResolveAttachment') {
        Bad 'an evicted inbox message is dropped without releasing its escrow - the sender''s attachment is burned'; $bad35 = $true
    }

    # Every silver release in this file goes through the shared credit-or-hold primitive.
    if ($m35 -match 'TreasuryStore\.DepositSilver') {
        Bad 'mail credits silver with a raw DepositSilver - a refused deposit is discarded instead of held'; $bad35 = $true
    }
    if ($m35 -notmatch 'KmhPayloadEscrow\.DeliverSilver') {
        Bad 'mail never uses DeliverSilver - attachment silver has no recovery path'; $bad35 = $true
    }

    if (-not $bad35) { Good 'an evicted or released mail attachment is refunded, and its silver can never be dropped' }
}

# --- 36. A server-initiated credit is never dropped ---
# Deposit* refuses an empty owner, an empty item and a non-positive amount, returning false without moving anything. A
# caller that ignores that answer burns the value: it left escrow (or a guild vault) and now exists nowhere. The whole
# class is avoided by shape - if the call STARTS a statement its result is discarded, so payouts, rewards and refunds
# go through KmhPayloadEscrow (DeliverSilver / DeliverCompact / Deliver), which parks whatever it cannot credit in the
# recovery queue. Player-initiated deposits are different and stay direct: there the refusal reaches the caller.
Section '36. A server-initiated credit is never dropped'

$dropped36 = New-Object System.Collections.Generic.List[string]
foreach ($f36 in Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs -File) {
    $n = 0
    foreach ($line in [System.IO.File]::ReadAllLines($f36.FullName)) {
        $n++
        # A discarded result can only happen when the call opens the statement; anything consuming it (if/return/
        # assignment/negation) puts other tokens first.
        if ($line -match '^\s*((Features\.)?Treasury\.)?TreasuryStore\.Deposit(Silver|Item|Payload)\(') {
            $dropped36.Add("$($f36.FullName.Substring($serverSrc.Length + 1)):$n")
        }
    }
}
if ($dropped36.Count -gt 0) {
    Bad "value credited with the result discarded (use KmhPayloadEscrow): $($dropped36 -join ', ')"
} else {
    Good 'every server-initiated silver/item/payload credit either checks its result or goes through KmhPayloadEscrow'
}

# --- 37. Every way an operation ends gives its slots back ---
# A Frontier operation holds an operation slot and an outpost slot. Only COMPLETION runs through the consequence
# dispatcher, so the other two endings - the quest expiring unfinished, and a consequence that could not be applied -
# used to keep both forever. Once MaxOutposts or MaxActiveOperations fills with those, the director stops acting for
# good; section 27 covers only the last exit (a claimable outpost nobody took).
Section '37. Every way an operation ends gives its slots back'

$conseqFile37 = Join-Path $serverSrc 'Features\Frontier\KmhOperationConsequence.cs'
$worldFile37  = Join-Path $serverSrc 'Features\World\WorldEngine.cs'
if (-not (Test-Path -LiteralPath $conseqFile37) -or -not (Test-Path -LiteralPath $worldFile37)) {
    Warn 'KmhOperationConsequence or WorldEngine not found - verify the operation slot release by hand'
} else {
    $c37 = [System.IO.File]::ReadAllText($conseqFile37)
    $w37 = [System.IO.File]::ReadAllText($worldFile37)
    $bad37 = $false

    if ($c37 -notmatch 'public static void Abandon\(') {
        Bad 'no Abandon path - an operation that ends without completing never gives its slots back'; $bad37 = $true
    }
    # The expiring-quest sweep is the only place that learns a global quest ended unfinished.
    $expiry37 = [regex]::Match($w37, 'CollectEndedQuests\([\s\S]{0,1200}?\r?\n            \}')
    if (-not $expiry37.Success) {
        Warn 'the expired-quest sweep was not found in the expected shape - verify the release by hand'
    }
    elseif ($expiry37.Value -notmatch 'Abandon\(') {
        Bad 'an expired global quest never tells Frontier - its operation slot and its outpost are held forever'; $bad37 = $true
    }

    # A consequence that could not be applied is never retried, so it has to release there too.
    $apply37 = [regex]::Match($c37, 'public static void Apply\([\s\S]*?\r?\n        \}')
    if ($apply37.Success -and $apply37.Value -notmatch 'ReleaseLocation') {
        Bad 'a consequence that did not apply leaves its location derelict, holding a director slot nothing can free'; $bad37 = $true
    }

    # The fourth way a location can fail to progress: its operation is never funded at all. An outpost with no
    # operation behind it has no quest to finish and none to expire, so neither section 27 nor the endings above can
    # ever reach it - it just holds a slot. Funding is therefore decided BEFORE anything is created.
    $dirFile37 = Join-Path $serverSrc 'Features\Frontier\KmhWorldDirector.cs'
    if (Test-Path -LiteralPath $dirFile37) {
        $d37 = [System.IO.File]::ReadAllText($dirFile37)
        $est37 = [regex]::Match($d37, 'internal static bool TryEstablishAt\([\s\S]*?\r?\n        \}')
        if (-not $est37.Success) {
            Warn 'TryEstablishAt not found in the expected shape - verify the funding order by hand'
        }
        else {
            $affordAt   = $est37.Value.IndexOf('Affordable(')
            $establishAt = $est37.Value.IndexOf('EstablishOutpost(')
            if ($affordAt -lt 0 -or $establishAt -lt 0 -or $affordAt -gt $establishAt) {
                Bad 'the outpost is created before its operation is known to be affordable - an unfunded location holds a slot for good'; $bad37 = $true
            }
            # And the narrow race where the pool drains between that check and the debit.
            elseif ($est37.Value -notmatch 'OutpostDormant') {
                Bad 'a reclaim that could not be funded leaves its location live - nothing will ever advance it'; $bad37 = $true
            }
        }
    }

    if (-not $bad37) { Good 'an operation that never starts, expires, or fails to apply all release their outpost and operation slots' }
}

# --- 38. Client previews match the rules the server enforces ---
# Some dialogs restate a server rule locally to show the player a number before they commit: free building slots,
# storage capacity, what a perk level buys. The server decides either way, so a drift is not exploitable - it just
# makes the UI quietly lie about what an action will do, which is the kind of thing nobody reports as a bug.
Section '38. Client previews match the rules the server enforces'

$srvBld38 = Join-Path $serverSrc 'Features\Sites\SiteBuildings.cs'
$cliBld38 = Join-Path $clientSrc 'Features\Sites\Dialog_KMHSiteBuildings.cs'
if (-not (Test-Path -LiteralPath $srvBld38) -or -not (Test-Path -LiteralPath $cliBld38)) {
    Warn 'SiteBuildings or the client buildings dialog not found - verify the slot/storage mirror by hand'
} else {
    $s38 = [System.IO.File]::ReadAllText($srvBld38)
    $c38 = [System.IO.File]::ReadAllText($cliBld38)
    $bad38 = $false

    # Two acceptable shapes, and the better one is preferred: the client either reads the figures the server SENDS
    # (no second copy of the rule to drift), or restates the same literals. Only a third shape - its own numbers -
    # is a failure.
    $perStorage = [regex]::Match($s38, 'UnitsPerStorage\s*=\s*(\d+)')
    $maxStorage = [regex]::Match($s38, 'MaxStorageUnits\s*=\s*(\d+)')
    $cliStorage = [regex]::Match($c38, 'private static int StorageCapacity\([\s\S]*?;')
    if (-not $perStorage.Success -or -not $maxStorage.Success -or -not $cliStorage.Success) {
        Warn 'storage constants not found in the expected shape - verify the mirror by hand'
    }
    elseif ($cliStorage.Value -match 'StoragePerBuilding' -and $cliStorage.Value -match 'MaxStorageUnits') {
        # Derived from the snapshot. Check the SERVER fills those fields from its own constants, or the client is
        # faithfully rendering a number nothing ever set.
        $srvSnap38 = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
        $snapTxt38 = if (Test-Path -LiteralPath $srvSnap38) { [System.IO.File]::ReadAllText($srvSnap38) } else { '' }
        if ($snapTxt38 -notmatch 'StoragePerBuilding\s*=\s*SiteBuildings\.UnitsPerStorage' -or
            $snapTxt38 -notmatch 'MaxStorageUnits\s*=\s*SiteBuildings\.MaxStorageUnits') {
            Bad 'the client reads the storage figures from the snapshot, but the server does not fill them from SiteBuildings'; $bad38 = $true
        }
        else { Good 'storage capacity is read from the server, not restated' }
    }
    else {
        if ($cliStorage.Value -notmatch "\b$($perStorage.Groups[1].Value)\b") {
            Bad "the client previews a different storage-per-building figure than the server's $($perStorage.Groups[1].Value)"; $bad38 = $true
        }
        if ($cliStorage.Value -notmatch "\b$($maxStorage.Groups[1].Value)\b") {
            Bad "the client previews a different storage ceiling than the server's $($maxStorage.Groups[1].Value)"; $bad38 = $true
        }
    }

    # Slot budget: every value the server can return has to appear in the client's mirror. Deliberately a drift
    # tripwire rather than a proof - it catches a changed number, not a rearranged boundary.
    $srvSlotBody = [regex]::Match($s38, 'public static int SlotsForTier\(int tier\)[\s\S]*?\r?\n        \}').Value
    $cliSlotLine = [regex]::Match($c38, 'private static int SlotsForTier\(int tier\).*').Value
    $srvNums = @([regex]::Matches($srvSlotBody, 'return (\d+)') | ForEach-Object { $_.Groups[1].Value })
    if ($srvNums.Count -lt 3 -or [string]::IsNullOrEmpty($cliSlotLine)) {
        Warn 'SlotsForTier not found in the expected shape - verify the slot budget by hand'
    }
    else {
        $cliNums = @([regex]::Matches($cliSlotLine, '\d+') | ForEach-Object { $_.Value })
        foreach ($n in $srvNums) {
            if ($cliNums -notcontains $n) {
                Bad "the client's slot budget does not carry the server's tier value $n (server: $($srvNums -join ','))"; $bad38 = $true; break
            }
        }
    }

    # The guild hall lists what each worker-XP perk level buys, restating the server's own ladder.
    $srvPerk38 = Join-Path $serverSrc 'Features\Guilds\Dto\GuildSnapshot.cs'
    $cliHall38 = Join-Path $clientSrc 'Features\Guilds\Dialog_KMHGuildHall.cs'
    if ((Test-Path -LiteralPath $srvPerk38) -and (Test-Path -LiteralPath $cliHall38)) {
        $p38 = [regex]::Match([System.IO.File]::ReadAllText($srvPerk38),
                              'public double WorkerXpMultiplier[\s\S]*?\r?\n        \}')
        if (-not $p38.Success) {
            Warn 'WorkerXpMultiplier not found in the expected shape - verify the perk ladder by hand'
        }
        else {
            $h38 = [System.IO.File]::ReadAllText($cliHall38)
            foreach ($m in [regex]::Matches($p38.Value, 'return ([0-9.]+);')) {
                $v = $m.Groups[1].Value
                # The client renders them as display strings ("1.25"), so compare on the number itself.
                if ($h38 -notmatch [regex]::Escape($v)) {
                    Bad "the guild hall does not show the server's worker-XP perk multiplier $v"; $bad38 = $true; break
                }
            }
        }
    }

    if (-not $bad38) { Good 'the buildings picker and the perk ladder show the same numbers the server enforces' }
}

# --- 39a. Every event the bus raises is subscribable ---
# KmhEventBus can declare and raise an event that IKmhEvents never exposes. Extensions only ever hold the INTERFACE,
# so such an event fires into nothing and no build or test notices. ChatMessagePosted and MailSent were both in that
# state - raised on every chat line and every mail, reachable by nobody.
Section '39a. Every event the bus raises is subscribable through the SDK interface'
$busSrc39a  = Get-Content -LiteralPath (Join-Path $ServerRoot 'Source\Extensibility\KmhEventBus.cs') -Raw -ErrorAction SilentlyContinue
$ifaceSrc39a = Get-Content -LiteralPath (Join-Path $ServerRoot 'Source\KMH.Sdk.Server\Apis\IKmhEvents.cs') -Raw -ErrorAction SilentlyContinue
if (-not $busSrc39a -or -not $ifaceSrc39a) {
    Warn 'event bus or IKmhEvents not found - SDK event coverage not checked'
} else {
    $busEvents39a = [regex]::Matches($busSrc39a, 'public\s+event\s+Action<\s*(\w+)\s*>\s+(\w+)') |
                    ForEach-Object { $_.Groups[2].Value } | Sort-Object -Unique
    $ifaceEvents39a = [regex]::Matches($ifaceSrc39a, 'event\s+Action<\s*(\w+)\s*>\s+(\w+)') |
                      ForEach-Object { $_.Groups[2].Value } | Sort-Object -Unique
    $missing39a = @($busEvents39a | Where-Object { $ifaceEvents39a -notcontains $_ })
    if ($missing39a.Count -gt 0) {
        Bad ("raised by KmhEventBus but not on IKmhEvents, so no extension can subscribe: " + ($missing39a -join ', '))
    } else {
        Good "all $($busEvents39a.Count) bus event(s) are exposed on IKmhEvents"
    }
    $orphan39a = @($ifaceEvents39a | Where-Object { $busEvents39a -notcontains $_ })
    if ($orphan39a.Count -gt 0) { Bad ("on IKmhEvents but never raised: " + ($orphan39a -join ', ')) }
    else { Good 'every interface event is actually raised somewhere' }
}

# --- 39. SDK records are fully populated ---
# A field added to an SDK record but not to the adapter that builds it leaves extensions reading a silent zero, which
# is how SitesOwned/OutpostsHeld/FrontierCaptures shipped empty. Assignments are unioned across construction sites:
# the defect worth catching is a field nobody sets anywhere, not a deliberately partial build.
Section '39. SDK records are fully populated'

$recFile39 = Join-Path $clientSrc 'KMH.Sdk.Client\Records\Records.cs'
$adpFile39 = Join-Path $clientSrc 'Extensibility\CacheAdapters.cs'
if (-not (Test-Path -LiteralPath $recFile39) -or -not (Test-Path -LiteralPath $adpFile39)) {
    Warn 'SDK records or CacheAdapters not found - verify record population by hand'
} else {
    $r39 = [System.IO.File]::ReadAllText($recFile39)
    $a39 = [System.IO.File]::ReadAllText($adpFile39)
    $missing39 = New-Object System.Collections.Generic.List[string]
    $checked39 = 0

    foreach ($cls in [regex]::Matches($r39, 'public sealed class (\w+Record)\s*\r?\n\s*\{([\s\S]*?)\r?\n    \}')) {
        $name  = $cls.Groups[1].Value
        $props = @([regex]::Matches($cls.Groups[2].Value, '\s(\w+)\s*\{ get; init; \}') | ForEach-Object { $_.Groups[1].Value })
        if ($props.Count -eq 0) { continue }

        $sites = [regex]::Matches($a39, "new $name\s*\r?\n?\s*\{([\s\S]*?)\}")
        if ($sites.Count -eq 0) { continue }   # built elsewhere or not surfaced to extensions
        $checked39++

        $assigned = @()
        foreach ($s in $sites) {
            $assigned += @([regex]::Matches($s.Groups[1].Value, '(\w+)\s*=') | ForEach-Object { $_.Groups[1].Value })
        }
        foreach ($p in $props) { if ($assigned -notcontains $p) { $missing39.Add("$name.$p") } }
    }

    if ($missing39.Count -gt 0) {
        Bad "SDK record field(s) never populated - extensions read zero: $($missing39 -join ', ')"
    } else {
        Good "$checked39 SDK record(s) built for extensions carry every field they declare"
    }
}

# --- 40. Inbound envelopes reach handlers on the main thread ---
# The SDK tells extension authors their handlers run on the main thread. Both transports must therefore marshal:
# dispatching inline from a network thread lets a handler touch Verse while the main thread draws, which corrupts
# rather than throws.
Section '40. Inbound envelopes reach handlers on the main thread'

$unmarshalled40 = New-Object System.Collections.Generic.List[string]
foreach ($f40 in Get-ChildItem -Path $clientSrc -Recurse -Filter *.cs -File) {
    $n = 0
    foreach ($line in [System.IO.File]::ReadAllLines($f40.FullName)) {
        $n++
        if ($line -match 'KmhDispatcher\.Receive\(' -and $line -notmatch 'KmhMainThread\.Post') {
            $unmarshalled40.Add("$($f40.FullName.Substring($clientSrc.Length + 1)):$n")
        }
    }
}
if ($unmarshalled40.Count -gt 0) {
    Bad "envelope dispatched without marshalling to the main thread: $($unmarshalled40 -join ', ')"
} else {
    Good 'every inbound envelope is dispatched through the main-thread pump'
}

# --- 41. A want's material/quality requirements reach the withdraw that enforces them ---
# Material and quality are checked on BOTH fulfil paths, so neither forces the payload route any more. That makes the
# wiring the only thing keeping them real: if Fulfill passes 0 / "" to the compact withdraw again, the buyer's stated
# requirements are silently ignored and they pay for goods they excluded. The pure matcher passing is not evidence -
# ItemKey.Matches would still be correct while nobody handed it the want's values.
Section "41. A want's requirements reach the compact withdraw"

$wantStore41 = Join-Path $serverSrc 'Features\WantBoard\WantStore.cs'
$treasury41  = Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs'
if (-not (Test-Path -LiteralPath $wantStore41) -or -not (Test-Path -LiteralPath $treasury41)) {
    Warn 'WantStore or TreasuryStore not found - verify want requirement wiring by hand'
} else {
    $bad41 = $false
    $wantText41 = [System.IO.File]::ReadAllText($wantStore41)
    $treaText41 = [System.IO.File]::ReadAllText($treasury41)

    # The compact call must carry the want's own values, not literals.
    $call41 = [regex]::Match($wantText41, 'TryWithdrawMatching\(\s*seller,[\s\S]{0,240}?\)\)')
    if (-not $call41.Success) {
        Bad 'WantStore no longer calls TryWithdrawMatching in the expected shape - verify by hand'
        $bad41 = $true
    } else {
        if ($call41.Value -notmatch '\bminQ\b')     { Bad "the want's quality floor is not passed to the compact withdraw - it would be ignored";  $bad41 = $true }
        if ($call41.Value -notmatch '\breqStuff\b') { Bad "the want's material requirement is not passed to the compact withdraw - it would be ignored"; $bad41 = $true }
    }

    # And the withdraw must route its decision through the one shared matcher.
    if ($treaText41 -notmatch 'ItemKey\.Matches\(') {
        Bad 'TryWithdrawMatching no longer uses ItemKey.Matches - the compact matcher and its self-test would drift'
        $bad41 = $true
    }

    # Only taint/damage/gear-state may force the payload path; re-adding the constraints hides compact stock.
    $needs41 = [regex]::Match($wantText41, 'internal static bool NeedsPayloadFulfil[\s\S]*?;')
    if ($needs41.Success -and ($needs41.Value -match 'minQuality' -or $needs41.Value -match 'requiredStuff')) {
        Bad 'NeedsPayloadFulfil forces the payload path for material/quality again - a seller''s compact stock would read as "you have none"'
        $bad41 = $true
    }

    if (-not $bad41) { Good "a want's material and quality reach the compact withdraw, which decides via ItemKey.Matches" }
}

# --- 42. Chat history actually reaches disk and comes back ---
# Chat was memory-only until 1.3.0, so a restart threw away real player conversation. The self-test round-trips the
# in-memory seams, which proves the SHAPE survives but not that anything is wired to a file: the store still has to be
# force-flushed, loaded at boot, and it must not be the season reset's job to write the empty state.
Section '42. Chat history is persisted and restored'

$chatStore42 = Join-Path $serverSrc 'Features\Chat\ChatStore.cs'
$flush42     = Join-Path $serverSrc 'Maintenance\KmhDataFlush.cs'
$main42      = Join-Path $serverSrc 'Main.cs'
if (-not (Test-Path -LiteralPath $chatStore42) -or -not (Test-Path -LiteralPath $flush42) -or -not (Test-Path -LiteralPath $main42)) {
    Warn 'ChatStore, KmhDataFlush or Main.cs not found - verify chat persistence by hand'
} else {
    $bad42 = $false
    $chatText42  = [System.IO.File]::ReadAllText($chatStore42)
    $flushText42 = [System.IO.File]::ReadAllText($flush42)
    $mainText42  = [System.IO.File]::ReadAllText($main42)

    if ($chatText42 -notmatch 'public static void SaveToDisk') {
        Bad 'ChatStore has no SaveToDisk - chat history would die with the process'; $bad42 = $true
    }
    if ($chatText42 -notmatch 'public static void LoadFromDisk') {
        Bad 'ChatStore has no LoadFromDisk - saved chat would never be read back'; $bad42 = $true
    }
    # Without this the store is skipped by `kmh save`, by the expiry sweeper's periodic flush, and by process exit.
    if ($flushText42 -notmatch 'ChatStore\.SaveToDisk') {
        Bad 'ChatStore is missing from KmhDataFlush - `kmh save`, autosave and shutdown would all skip chat'; $bad42 = $true
    }
    if ($mainText42 -notmatch 'ChatStore\.LoadFromDisk') {
        Bad 'Main does not load ChatStore at boot - the file would be written and never read'; $bad42 = $true
    }
    # Retention must be applied on the way in as well: a server down over a weekend would otherwise resurrect a ring
    # that is older than the owner's retention window.
    $apply42 = [regex]::Match($chatText42, 'internal static void ApplyLoaded[\s\S]*?\n        \}')
    if ($apply42.Success -and $apply42.Value -notmatch 'TrimRing\(') {
        Bad 'ApplyLoaded does not TrimRing - loading would ignore MaxRecentPerChannel/RetentionHours'; $bad42 = $true
    }
    # Ids back staff removal and client dedup; reissuing them would delete the wrong message.
    if ($chatText42 -notmatch '_nextId = Math\.Max\(') {
        Bad 'ChatStore does not recover _nextId on load - message ids would be reissued after a restart'; $bad42 = $true
    }
    # Clearing chat must stay an explicit season reset, never a side effect of a restart.
    $clear42 = [regex]::Match($chatText42, 'public static void ClearForNewSeason[\s\S]*?\n        \}')
    if ($clear42.Success -and $clear42.Value -notmatch 'SaveToDisk') {
        Bad 'ClearForNewSeason does not persist the wipe - the next boot would reload the messages it just cleared'; $bad42 = $true
    }

    # FlushAll only runs on `kmh save`, a backup and process exit - there is NO periodic FlushAll in normal operation.
    # Chat is the highest-churn store, so it needs its own debounced drain or a crash costs every message since boot.
    $sweeper42 = Join-Path $serverSrc 'Maintenance\ExpirySweeper.cs'
    if (Test-Path -LiteralPath $sweeper42) {
        $sweepText42 = [System.IO.File]::ReadAllText($sweeper42)
        if ($sweepText42 -notmatch 'ChatStore\.SaveIfDirty') {
            Bad 'no periodic chat drain is scheduled - a crash or force-kill would lose every message since boot'; $bad42 = $true
        }
    }
    if ($chatText42 -notmatch '_dirty = true') {
        Bad 'posting a message does not mark chat dirty - the periodic drain would never write it'; $bad42 = $true
    }

    if (-not $bad42) { Good 'chat history is force-flushed, drained periodically, loaded at boot, trimmed on load, and keeps its message ids' }
}


Section '43. World markers use KMH world art, in the right slot'

# Two rules, both learned from real breakage.
#
# 1. RimWorld draws a world object TWICE from two different images: the small always-visible dot
#    (<texture>) and the zoomed-out icon (<expandingIconTexture>). Every vanilla WorldObjectDef pairs
#    World/WorldObjects/X with World/WorldObjects/Expanding/X. KMHSite and KMHGuildHall used to point
#    BOTH slots at one vanilla quad texture, which is what made KMH markers read wrong on the map.
#
# 2. KMH's marker code returned KMHPatch/UI icons - the coloured Noto Emoji glyphs the tab buttons
#    use. Being coloured, they fought the state tint: a hostile marker could not actually go red, so
#    the colour carried no information at all.

$defDir43 = Join-Path $ClientRoot '1.6\Defs\WorldObjectDefs'
$markerArt43 = Join-Path $clientSrc 'Features\Sites\KmhMarkerArt.cs'
if (-not (Test-Path -LiteralPath $defDir43)) {
    Warn 'no WorldObjectDefs folder - verify KMH world marker art by hand'
} else {
    $bad43 = $false

    # Windows resolves KMHPatch/world/worldobjects/expanding/Farmland happily; a player on Linux gets no texture and
    # the marker silently drops to whatever the def names. Compare every segment against the real directory entry.
    function Resolve-CaseExact43 {
        param([string]$Root, [string]$Relative)
        $at = $Root
        foreach ($seg in ($Relative -split '[\\/]' | Where-Object { $_ -ne '' })) {
            $entry = Get-ChildItem -LiteralPath $at -Force -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -ceq $seg } | Select-Object -First 1
            if (-not $entry) { return $null }
            $at = $entry.FullName
        }
        return $at
    }

    foreach ($def43 in Get-ChildItem -LiteralPath $defDir43 -Filter *.xml) {
        $x43 = [System.IO.File]::ReadAllText($def43.FullName)
        $tex43  = [regex]::Match($x43, '<texture>([^<]+)</texture>')
        $exp43  = [regex]::Match($x43, '<expandingIconTexture>([^<]+)</expandingIconTexture>')
        if (-not $tex43.Success -or -not $exp43.Success) { continue }
        $t43 = $tex43.Groups[1].Value.Trim()
        $e43 = $exp43.Groups[1].Value.Trim()

        if ($t43 -eq $e43) {
            Bad ("$($def43.Name): <texture> and <expandingIconTexture> are the same image ($t43) - the two slots are drawn differently and vanilla never shares one"); $bad43 = $true
        }
        if ($e43 -notmatch '/Expanding/') {
            Bad ("$($def43.Name): expandingIconTexture '$e43' is not an Expanding/ path - that slot needs the zoomed-out art"); $bad43 = $true
        }
        foreach ($pair43 in @(@($t43,'texture'), @($e43,'expandingIconTexture'))) {
            if ($pair43[0] -notmatch '^KMHPatch/') {
                Bad ("$($def43.Name): $($pair43[1]) '$($pair43[0])' is vanilla art - a KMH world object must ship its own"); $bad43 = $true
            }
            $rel43 = $pair43[0] -replace '^KMHPatch/','' -replace '/','\'
            $stem43 = Join-Path (Join-Path $ClientRoot 'Textures\KMHPatch') $rel43
            if (-not ((Test-Path -LiteralPath "$stem43.png"))) {
                Bad ("$($def43.Name): $($pair43[1]) '$($pair43[0])' names art that does not exist"); $bad43 = $true
            } else {
                $texRootCase43 = Join-Path $ClientRoot 'Textures\KMHPatch'
                $anyCase43 = $false
                foreach ($ext43 in @('.png')) {
                    if (Resolve-CaseExact43 -Root $texRootCase43 -Relative ($rel43 + $ext43)) { $anyCase43 = $true; break }
                }
                if (-not $anyCase43) {
                    Bad ("$($def43.Name): $($pair43[1]) '$($pair43[0])' differs in CASE from the file on disk - it resolves on Windows and fails on Linux"); $bad43 = $true
                }
            }
        }
    }

    if (Test-Path -LiteralPath $markerArt43) {
        $art43 = [System.IO.File]::ReadAllText($markerArt43)
        # UI icons are coloured and authored for GUI buttons; using one as a marker kills the state tint.
        if ($art43 -match 'UI\.KMHTextures\.') {
            Bad 'KmhMarkerArt returns a KMHPatch/UI button icon - coloured art cannot carry the hostile/claimable tint'; $bad43 = $true
        }
        # A marker that swaps to vanilla art stops reading as a KMH object - state belongs in tint/label, not shape.
        foreach ($vanilla43 in @('Expanding/Sites/Turrets','Expanding/DestroyedSettlement','Expanding/Camp')) {
            if ($art43 -match [regex]::Escape("World/$vanilla43")) {
                Bad "KmhMarkerArt substitutes vanilla art ($vanilla43) for a KMH marker state"; $bad43 = $true
            }
        }
        # Each slot must be resolved from its own prefix constant by name. Inlining the prefix at each call is how
        # one of the two slots quietly ends up pointing at the other, with nothing left to test. Case-sensitive so
        # the constant Expanding is not confused with a field like _expandingPaths that merely caches it.
        foreach ($pathSlot43 in @(@('ExpandingPathFor', 'Expanding', 'Normal'), @('NormalPathFor', 'Normal', 'Expanding'))) {
            if ($art43 -cmatch "$($pathSlot43[0])\([^)]*\)\s*=>([^;]*);") {
                $body43 = $Matches[1]
                if ($body43 -cnotmatch "\b$($pathSlot43[1])\b" -or $body43 -cmatch "\b$($pathSlot43[2])\b") {
                    Bad "KmhMarkerArt.$($pathSlot43[0]) does not resolve the $($pathSlot43[1]) set"; $bad43 = $true
                }
            } else {
                Bad "KmhMarkerArt.$($pathSlot43[0]) is not one expression resolving a single slot"; $bad43 = $true
            }
        }
        if ($art43 -notmatch 'IconFor\([^)]*\)\s*=>\s*Tex\(ExpandingPathFor') {
            Bad 'KmhMarkerArt.IconFor does not load the expanding slot'; $bad43 = $true
        }
        # ContentFinder answers nothing until mod content is loaded, so a miss cached on the first lookup is
        # permanent for the session and the marker wears its def texture with nothing in the log.
        if ($art43 -notmatch '_misses\[path\]\s*=\s*tried \+ 1') {
            Bad 'KmhMarkerArt caches a failed texture lookup instead of retrying it'; $bad43 = $true
        }

        # Every archetype the server can send needs its own art key, and BOTH zoom ranges must actually ship: a
        # missing normal file made archetype sites draw the Generic dot up close and their own icon zoomed out.
        foreach ($arch43 in @('Farmland','Quarry','Woodland','Ranch','Roadworks','Custom','Outpost','Generic')) {
            if ($art43 -notmatch "return ""$arch43""") {
                Bad "KmhMarkerArt has no marker for the $arch43 archetype"; $bad43 = $true
            }
            foreach ($slot43 in @("WorldObjects\$arch43", "WorldObjects\Expanding\$arch43")) {
                $stem43 = Join-Path (Join-Path $ClientRoot 'Textures\KMHPatch\World') $slot43
                if (-not ((Test-Path -LiteralPath "$stem43.png"))) {
                    Bad "KmhMarkerArt: $arch43 is missing its $slot43 art"; $bad43 = $true
                } else {
                    $worldRootCase43 = Join-Path $ClientRoot 'Textures\KMHPatch\World'
                    $anySlotCase43 = $false
                    foreach ($ext43 in @('.png')) {
                        if (Resolve-CaseExact43 -Root $worldRootCase43 -Relative ($slot43 + $ext43)) { $anySlotCase43 = $true; break }
                    }
                    if (-not $anySlotCase43) {
                        Bad "KmhMarkerArt: $arch43's $slot43 art differs in CASE from the file on disk - Linux will not find it"; $bad43 = $true
                    }
                }
            }
        }
    }

    try { Add-Type -AssemblyName System.Drawing -ErrorAction Stop; $gfx43 = $true } catch { $gfx43 = $false }
    function Get-AlphaGrid43 {
        param([string]$Path, [int]$N = 24)
        $bmp = [System.Drawing.Bitmap]::FromFile($Path)
        try {
            $minX = $bmp.Width; $minY = $bmp.Height; $maxX = -1; $maxY = -1
            for ($y = 0; $y -lt $bmp.Height; $y++) {
                for ($x = 0; $x -lt $bmp.Width; $x++) {
                    if ($bmp.GetPixel($x, $y).A -gt 8) {
                        if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                        if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
                    }
                }
            }
            if ($maxX -lt $minX) { return $null }
            # Cropped to the alpha bounding box first: the two images are inset differently, so raw
            # pixels would differ everywhere for reasons unrelated to which way up they are.
            $bw = $maxX - $minX + 1; $bh = $maxY - $minY + 1
            $g = New-Object 'double[]' ($N * $N)
            for ($gy = 0; $gy -lt $N; $gy++) {
                for ($gx = 0; $gx -lt $N; $gx++) {
                    $sx = $minX + [int](($gx + 0.5) * $bw / $N)
                    $sy = $minY + [int](($gy + 0.5) * $bh / $N)
                    $g[$gy * $N + $gx] = $bmp.GetPixel($sx, $sy).A / 255.0
                }
            }
            return $g
        } finally { $bmp.Dispose() }
    }

    # PNG only. The DDS variants are gone on purpose: RimWorld's DDS loader reads rows bottom-up, so a .dds has to
    # be stored vertically flipped to look right, and KMH shipped three files per texture (.png, .dds, .dds.zstd) of
    # which only the .dds was ever verified. One wrong copy renders the marker upside down and the check still passed.
    # A PNG has no such convention, so the whole class is removed rather than policed.
    $strayTex43 = @(Get-ChildItem -LiteralPath (Join-Path $ClientRoot 'Textures\KMHPatch') -Recurse -File |
                    Where-Object { $_.Name -like '*.dds' -or $_.Name -like '*.dds.zstd' })
    if ($strayTex43.Count -gt 0) {
        Bad "$($strayTex43.Count) DDS texture(s) are back (e.g. $($strayTex43[0].Name)) - KMH ships PNG only, so orientation cannot be got wrong"
        $bad43 = $true
    } else {
        $pngs43 = @(Get-ChildItem -LiteralPath (Join-Path $ClientRoot 'Textures\KMHPatch') -Recurse -File -Filter *.png)
        Good "textures are PNG only - $($pngs43.Count) file(s), no DDS orientation convention to get wrong"
    }

    if (-not $bad43) { Good 'KMH world markers ship their own art, in two slots, and no UI button icon reaches the map' }
}

# A config KMH declares "regenerable" has to actually be regenerated, and a config that is only written when it is
# missing never gains the fields a later version adds - which is how an owner ends up with a file that silently
# lacks half the settings the release notes describe.
Section '44. Owner configs are seeded at boot, topped up on upgrade, and inspectable'

$paths44   = Get-Content (Join-Path $serverSrc 'Persistence\KmhDataPaths.cs') -Raw
$main44    = Get-Content (Join-Path $serverSrc 'Main.cs') -Raw
$inspect44 = Get-Content (Join-Path $serverSrc 'AdminCommands\KmhConfigInspect.cs') -Raw

$configFiles44 = Get-ChildItem $serverSrc -Recurse -Filter *Config.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

$declared44 = [regex]::Matches($paths44, 'new\s+DataFile\(\s*"Config/([^"]+)"\s*,\s*(\w+)\s*,\s*(true|false)\s*\)')
$seeded44 = 0
$bad44 = $false
foreach ($m44 in $declared44) {
    $label44 = $m44.Groups[1].Value
    $prop44  = $m44.Groups[2].Value
    if ($m44.Groups[3].Value -ne 'true') { continue }   # owner overrides nothing regenerates, by design

    $ownerFile44 = $null
    foreach ($f44 in $configFiles44) {
        $txt44 = Get-Content $f44.FullName -Raw
        if (($txt44 -match ('KmhDataPaths\.' + [regex]::Escape($prop44) + '\b')) -and
            ($txt44 -match 'static\s+void\s+EnsureGenerated\s*\(')) { $ownerFile44 = $f44; break }
    }
    if ($null -eq $ownerFile44) {
        Bad "Config/$label44 is declared regenerable but no config class with an EnsureGenerated writes $prop44"
        $bad44 = $true
        continue
    }

    $ownerName44 = [System.IO.Path]::GetFileNameWithoutExtension($ownerFile44.Name)
    if ($main44 -notmatch ([regex]::Escape($ownerName44) + '\.EnsureGenerated\(\)')) {
        Bad "$ownerName44.EnsureGenerated() is never called from Main - Config/$label44 is not seeded at boot"
        $bad44 = $true
        continue
    }

    # Upgrade path: either Main backfills the file's missing fields, or the config re-serialises itself.
    $ownerSrc44   = Get-Content $ownerFile44.FullName -Raw
    $backfilled44 = $main44 -match ('BackfillMissingFields\(\s*Persistence\.KmhDataPaths\.' + [regex]::Escape($prop44))
    $selfTops44   = $ownerSrc44 -match 'ToJson\('
    if (-not ($backfilled44 -or $selfTops44)) {
        Bad "Config/$label44 gains no new fields on upgrade - add it to the BackfillMissingFields block in Main.cs"
        $bad44 = $true
        continue
    }
    # An owner who cannot see a config's values with `kmh config` has to open the file to find out what is set,
    # and a hand-maintained area list is exactly the thing that drifts as configs are added.
    $area44 = $label44.ToLowerInvariant()
    if ($inspect44 -notmatch ('\(\s*"' + [regex]::Escape($area44) + '"\s*,')) {
        Bad "Config/$label44 has no 'kmh config $area44' area - add it to KmhConfigInspect.Areas"
        $bad44 = $true
        continue
    }
    $seeded44++
}

if (-not $bad44) { Good "$seeded44 regenerable owner config(s) are seeded at boot, gain new fields on upgrade, and are inspectable" }


# The hello is the only state a client gets before it draws anything, so a field the server puts there and the
# client never reads is a setting that silently does nothing - staff_roles shipped that way, which is why an Owner
# badge only appeared after `kmh reload all`.
Section '45. Every hello field the server sends is read by the client'

$srvHello45 = Get-Content (Join-Path $serverSrc 'SubProtocol\KmhHandshakeHandler.cs') -Raw
$cliHello45 = Get-Content (Join-Path $clientSrc 'SubProtocol\KmhHandshakeHandler.cs') -Raw

$from45 = $srvHello45.IndexOf('SendHelloTo')
$to45   = $srvHello45.IndexOf('private static void OnHelloAck')
if ($from45 -lt 0 -or $to45 -le $from45) {
    Bad 'could not locate SendHelloTo in the server handshake - this check needs updating'
} else {
    $block45 = $srvHello45.Substring($from45, $to45 - $from45)
    $fields45 = [regex]::Matches($block45, '(?m)^\s+([a-z][a-z0-9_]*)\s*=\s*') |
                ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $unread45 = @()
    foreach ($f45 in $fields45) { if ($cliHello45 -notmatch ('"' + [regex]::Escape($f45) + '"')) { $unread45 += $f45 } }
    if ($unread45.Count -gt 0) {
        Bad ("the hello carries field(s) the client never reads: " + ($unread45 -join ', '))
    } else {
        Good "all $($fields45.Count) hello field(s) are read by the client"
    }
}



# RimWorld's camera driver consumes the scroll wheel before any window is drawn, so a window that does not absorb
# input has to hold camera motion while the pointer is inside it or its scroll views never receive one - which is
# exactly how the pop-out chat log became unscrollable. The rule lives in Window_KMHBase and is DERIVED from
# absorbInputAroundWindow, so a window pinning preventCameraMotion by hand quietly opts back out of it.
Section '46. Floating windows keep the scroll wheel'

$base46 = Get-Content (Join-Path $clientSrc 'UI\Window_KMHBase.cs') -Raw
$bad46 = $false

if ($base46 -notmatch 'HoldCameraWhileHovered' -or $base46 -notmatch 'GetWindowAt') {
    Bad 'Window_KMHBase no longer holds camera motion while hovered - floating windows cannot scroll'
    $bad46 = $true
}
# The CALL, inside WindowUpdate's own body - matching the name anywhere in the file also matched its declaration.
if ($base46 -notmatch '(?s)public override void WindowUpdate\(\)\s*\{[^}]*HoldCameraWhileHovered\(\);') {
    Bad 'HoldCameraWhileHovered is not called from WindowUpdate, so it never runs'
    $bad46 = $true
}

$hand46 = @()
foreach ($f46 in Get-ChildItem $clientSrc -Recurse -Filter '*.cs') {
    if ($f46.Name -eq 'Window_KMHBase.cs') { continue }
    $t46 = Get-Content $f46.FullName -Raw
    if ($t46 -match 'preventCameraMotion\s*=') { $hand46 += $f46.Name }
}
if ($hand46.Count -gt 0) {
    Bad ("window(s) set preventCameraMotion by hand, overriding the shared rule: " + ($hand46 -join ', '))
    $bad46 = $true
}

if (-not $bad46) { Good 'floating windows hold camera motion while hovered, and no window overrides the rule by hand' }


# --- 47. Every value the server accepts is reachable from the client UI ---
# A setting the server honours but no menu ever offers is a feature that silently does not exist. The site reward
# destination shipped that way: storage buildings could be built, output could never be sent to them, and nothing
# anywhere reported an error. Each value below must appear in a client FloatMenuOption or radio/toggle.
Section '47. Server-accepted values are reachable from the client UI'

$bad47 = $false
$cliUiText = ''
foreach ($f47 in Get-ChildItem $clientSrc -Recurse -Filter 'Dialog_*.cs') {
    $cliUiText += [System.IO.File]::ReadAllText($f47.FullName) + "`n"
}

# Vocabulary -> the server method that accepts it. Parsed from the server so a new value joins the check for free.
$vocab47 = @(
    @{ Name = 'site reward destination'
       File = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
       Fn   = 'private static string NormalizeDest'
       Pat  = 'SiteEntry\.(Dest\w+)' }
    @{ Name = 'site access mode'
       File = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
       Fn   = 'private static string NormalizeAccess'
       Pat  = 'SiteEntry\.(Access\w+)' }
)

foreach ($v47 in $vocab47) {
    if (-not (Test-Path -LiteralPath $v47.File)) { Warn "$($v47.Name): source not found - verify by hand"; continue }
    $txt47 = [System.IO.File]::ReadAllText($v47.File)
    $fn47  = [regex]::Match($txt47, [regex]::Escape($v47.Fn) + '[\s\S]*?\r?\n        \}')
    if (-not $fn47.Success) { Warn "$($v47.Name): normalizer not found in the expected shape - verify by hand"; continue }

    $accepted = @([regex]::Matches($fn47.Value, $v47.Pat) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($accepted.Count -eq 0) { Warn "$($v47.Name): no values parsed - verify by hand"; continue }

    # An OFFER, not a mention: a comparison against the value ("is this site set to storage") proves nothing about
    # whether a menu can ever set it, so == and != occurrences do not count.
    $missing = @()
    foreach ($val in $accepted) {
        if ($cliUiText -notmatch "(?<![=!]=\s{0,8})SiteEntry\.$val\b") { $missing += $val }
    }
    if ($missing.Count -gt 0) {
        Bad ("$($v47.Name): the server accepts " + ($missing -join ', ') + " but no client dialog offers it")
        $bad47 = $true
    }
    else { Good "$($v47.Name): all $($accepted.Count) accepted value(s) are offered by a client dialog" }
}
if (-not $bad47) { Good 'no server-accepted setting is unreachable from the UI' }

# --- 48. A value move is not acknowledged before it is durable ---
# TreasuryStore returned true for a withdrawal whose save had failed: memory said 900, the disk still said 1000, and
# the client had already been sent the silver. One restart minted it. The rule is that every value path commits
# through CommitLocked (mutate -> save -> put the vault back if the write failed), and that SaveToDisk keeps
# reporting the write - a void signature is what made the failure invisible in the first place.
Section '48. A value move is not acknowledged before it is durable'

# Every store that holds player value or escrow. A void SaveToDisk on any of them is a save whose failure nobody
# can see, which is how a withdrawal gets granted against a balance the disk never lost.
$valueStores = @(
    'Features\Treasury\TreasuryStore.cs',
    'Features\Marketplace\MarketplaceStore.cs',
    'Features\Auctions\AuctionStore.cs',
    'Features\WantBoard\WantStore.cs',
    'Features\Mail\MailStore.cs',
    'Features\Quests\QuestStore.cs',
    'Features\Sites\SiteStore.cs',
    'Features\Roadworks\RoadworksStore.cs',
    'Features\Guilds\GuildStore.cs',
    'Features\Recovery\RecoveryStore.cs')
$voidSavers = @()
foreach ($rel in $valueStores) {
    $p = Join-Path $serverSrc $rel
    if (-not (Test-Path $p)) { Warn "$rel not found - verify its persistence by hand"; continue }
    $t = [System.IO.File]::ReadAllText($p)
    if ($t -match '(public|internal)\s+static\s+void\s+SaveToDisk\s*\(') { $voidSavers += (Split-Path $rel -Leaf) }
    elseif ($t -notmatch '(public|internal)\s+static\s+bool\s+SaveToDisk\s*\(') { Warn "$(Split-Path $rel -Leaf): no SaveToDisk found - was it renamed?" }
}
if ($voidSavers.Count -gt 0) {
    Bad ('these value stores cannot report a failed write: ' + ($voidSavers -join ', '))
} else { Good "all $($valueStores.Count) value store(s) report whether their write happened" }

$treasuryFile = Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs'
if (-not (Test-Path $treasuryFile)) { Warn 'TreasuryStore.cs not found - verify durability by hand' }
else {
    $tText  = [System.IO.File]::ReadAllText($treasuryFile)
    $tLines = [System.IO.File]::ReadAllLines($treasuryFile)

    if ($tText -notmatch 'public\s+static\s+bool\s+SaveToDisk\s*\(') {
        Bad 'TreasuryStore.SaveToDisk no longer reports whether the write happened - every caller is blind again'
    } else { Good 'TreasuryStore.SaveToDisk reports its write' }

    if ($tText -notmatch 'private\s+static\s+bool\s+CommitLocked\s*\(' -or
        $tText -notmatch 'private\s+static\s+TreasurySnapshot\s+RollbackPointLocked\s*\(') {
        Bad 'the commit-or-roll-back helpers are gone from TreasuryStore - value moves have nothing to refuse with'
    } else { Good 'TreasuryStore commits value moves through a rollback point' }

    # Every value-moving entry point, by name, must reach CommitLocked. Named rather than inferred: a heuristic
    # that guesses which methods move value would quietly stop covering the next one that is added.
    $valuePaths = @(
        'DepositSilver', 'WithdrawSilver', 'DepositItem', 'WithdrawItem',
        'DepositGuildSilver', 'WithdrawGuildSilver', 'DepositGuildItem',
        'DepositPayload', 'WithdrawPayloads', 'TryWithdrawMatchingPayloads', 'TryWithdrawMatching',
        'BeginPendingDeposit', 'BeginPendingGuildDonation', 'ConfirmDeposits', 'ReconcileDeposits')
    $uncommitted = @()
    foreach ($m in $valuePaths) {
        $start = -1
        for ($i = 0; $i -lt $tLines.Count; $i++) {
            if ($tLines[$i] -match ("(public|internal)\s+static\s+[\w<>,\.\(\)\s\[\]]+\s" + [regex]::Escape($m) + "\s*\(")) { $start = $i; break }
        }
        if ($start -lt 0) { Warn "$m not found in TreasuryStore - was it renamed?"; continue }
        # Read to the end of the method by brace depth, starting from its opening brace.
        $depth = 0; $seen = $false; $body = ''
        for ($i = $start; $i -lt $tLines.Count; $i++) {
            $body += $tLines[$i] + "`n"
            $depth += ([regex]::Matches($tLines[$i], '\{')).Count
            $depth -= ([regex]::Matches($tLines[$i], '\}')).Count
            if ($depth -gt 0) { $seen = $true }
            if ($seen -and $depth -le 0) { break }
        }
        # The result has to be ACTED ON, not merely obtained: a discarded CommitLocked is the same silent
        # acknowledgement the void SaveToDisk was, so the negated form is what counts.
        if ($body -notmatch '!CommitLocked\(') { $uncommitted += $m }
    }
    if ($uncommitted.Count -gt 0) {
        Bad ('these Treasury value paths do not refuse on a failed commit: ' + ($uncommitted -join ', '))
    } else { Good "all $($valuePaths.Count) Treasury value path(s) refuse when the write fails" }
}

# --- 49. KMH never writes to an RWT socket itself ---
# RWT's Listener is the serialization boundary: EnqueuePacket only appends to a ConcurrentQueue, and one
# RunAllListenerTasks thread per client drains it. That is why KMH needs no send lock of its own - and why a
# direct Stream.Write from KMH would interleave bytes with RWT's own traffic on the same socket.
Section '49. KMH never writes to an RWT socket itself'
$directWrites = @()
foreach ($f in Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and
                               $_.Name -ne 'KmhApiServer.cs' -and $_.Name -ne 'KmhMediaFetch.cs' -and
                               $_.Name -ne 'KmhVideoRelay.cs' }) {
    $txt = [System.IO.File]::ReadAllText($f.FullName)
    if ($txt -match '\.Listener[\?\.]*\s*\.\s*(Tcp|Stream)\b' -or $txt -match 'client\s*\.\s*Tcp\b') {
        $directWrites += $f.FullName.Substring($serverSrc.Length + 1)
    }
}
if ($directWrites.Count -gt 0) {
    Bad ('KMH reaches past EnqueuePacket to an RWT socket in: ' + ($directWrites -join ', '))
} else {
    $enq = 0
    foreach ($f in Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs -File |
                    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
        $enq += ([regex]::Matches([System.IO.File]::ReadAllText($f.FullName), 'EnqueuePacket\(')).Count
    }
    Good "all $enq KMH write(s) onto RWT transport go through EnqueuePacket, which RWT serializes on one thread"
}

# --- 50. A client's claim about itself never sizes authoritative economy ---
# Everything a client reports about its own colony is a claim: colony wealth, a pawn's skill, an item's market
# value, and "I delivered these goods". Each of these was wired into something the server pays out or prices, so
# one modified client could pin every generated reward at the cap, double its site output, build a site for
# nothing, or complete a global quest with an empty stockpile. Each rule below is one of those wires, cut.
Section "50. A client's claim about itself never sizes authoritative economy"

$engineFile   = Join-Path $serverSrc 'Features\World\WorldEngine.cs'
$siteStoreF   = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
$safetyFile   = Join-Path $serverSrc 'Items\KmhItemSafety.cs'
$workerDtoF   = Join-Path $serverSrc 'Features\Sites\Dto\SiteSnapshot.cs'

if (-not (Test-Path $engineFile) -or -not (Test-Path $siteStoreF)) { Warn 'World/Site source not found - verify the trust boundary by hand' }
else {
    $engine = [System.IO.File]::ReadAllText($engineFile)
    $store  = [System.IO.File]::ReadAllText($siteStoreF)
    $safety = [System.IO.File]::ReadAllText($safetyFile)
    $wdto   = [System.IO.File]::ReadAllText($workerDtoF)

    # Reward size
    if ($engine -match 'ComputeAutoReward[\s\S]{0,600}?TotalReportedWealth') {
        Bad 'ComputeAutoReward reads client-reported colony wealth - one client can pin every generated reward at the cap'
    } else { Good 'a generated reward is sized from server-owned inputs only' }

    # Deliver objective
    if ($engine -notmatch 'ApplyDelivery[\s\S]{0,1400}?TryWithdrawMatching') {
        Bad 'ApplyDelivery no longer withdraws from the treasury - a crafted packet credits quest progress with no goods'
    } else { Good 'a global-quest delivery is paid out of the treasury before it credits' }

    # Self-reported objectives
    if ($engine -notmatch 'IsSelfReportedObjective' -or $engine -notmatch 'PayRewardsForSelfReportedObjectives') {
        Bad 'hunt/build completion no longer checks the self-reported-objective payout gate'
    } else { Good 'a self-reported objective pays only when the owner has opted in' }

    # Site pricing
    if ($safety -match 'ValidateClientMarketValue') {
        Bad 'ValidateClientMarketValue is back - its no-trusted-value branch returned the client''s own price'
    } elseif ($store -notmatch 'TrustedMarketValueOrZero') {
        Bad 'SiteStore no longer prices from the trusted catalog value'
    } else { Good 'a site is priced from the trusted catalog value or refused' }

    # SDK value moves. 'kmh maintenance on' promises the economy is paused; the router only enforces that for
    # client packets, and an extension reaches the same stores directly.
    $apisFile = Join-Path $serverSrc 'Extensibility\Apis.cs'
    if (Test-Path $apisFile) {
        $apis = [System.IO.File]::ReadAllText($apisFile)
        $ungated = @()
        foreach ($m in @('DepositSilver','WithdrawSilver','DepositItem','WithdrawItem',
                         'DepositGuildSilver','WithdrawGuildSilver',
                         'CreateGuild','AddMember','SetMotd')) {
            if ($apis -notmatch ("public bool $m[\s\S]{0,260}?KmhSdkGate\.Allow")) { $ungated += $m }
        }
        # A world quest reserves house-pool silver and ending one returns it, so both are value moves.
        foreach ($m in @('FireEvent','EndEvent','CreateQuest','EndQuest')) {
            if ($apis -notmatch ("public bool $m\([\s\S]{0,400}?KmhSdkGate\.Allow")) { $ungated += $m }
        }
        if ($apis -notmatch 'KmhAdmission\.AllowsValueMutation\(Maintenance\.KmhIngress\.Sdk') {
            $ungated += 'KmhSdkGate no longer asks the admission layer'
        }
        if ($ungated.Count -gt 0) { Bad ('these SDK value moves bypass the maintenance gate: ' + ($ungated -join ', ')) }
        else { Good 'an extension cannot move value while maintenance is on' }
    }

    # Site output
    if ($wdto -notmatch 'EarnedLevel') {
        Bad 'WorkerProgressDto.EarnedLevel is gone - client-reported skill is back in the production path'
    } elseif ($store -match 'total \+= wp\.CurrentLevel' -or $store -match 'wp\.CurrentLevel \* RoadworkWorkPerSkillLevel') {
        Bad 'site output or road progress reads CurrentLevel, which folds in the client''s claimed pawn skill'
    } else { Good 'site output and road progress count server-earned XP only' }
}

# --- 51. A mutable snapshot cannot go backwards on the client ---
# A request builds snapshot A, a mutation pushes newer B, and the two cross in flight over two transports. Without a
# revision the client applies whichever lands last, so a stale reply puts spent silver or a sold listing back on
# screen. Three things have to hold together: the DTO carries the number on both sides, the server stamps it inside
# the lock it read state under (a stamp taken outside proves nothing about ordering), and the cache drops an older
# one. Any of the three missing and the other two are decoration.
Section '51. A mutable snapshot cannot go backwards on the client'

# feature = server DTO, server store, client cache, and the field the cache holds.
$revFeatures = @(
    @{ N='Marketplace'; D='Features\Marketplace\Dto\MarketplaceSnapshot.cs'; S='Features\Marketplace\MarketplaceStore.cs'; C='Features\Marketplace\MarketplaceCache.cs'; H='Snapshot' },
    @{ N='Auctions';    D='Features\Auctions\Dto\AuctionSnapshot.cs';        S='Features\Auctions\AuctionStore.cs';        C='Features\Auctions\AuctionCache.cs';       H='Snapshot' },
    @{ N='WantBoard';   D='Features\WantBoard\Dto\WantSnapshot.cs';          S='Features\WantBoard\WantStore.cs';          C='Features\WantBoard\WantCache.cs';         H='Snapshot' },
    @{ N='Quests';      D='Features\Quests\Dto\QuestSnapshot.cs';            S='Features\Quests\QuestStore.cs';            C='Features\Quests\QuestCache.cs';           H='Snapshot' },
    @{ N='Sites';       D='Features\Sites\Dto\SiteSnapshot.cs';              S='Features\Sites\SiteStore.cs';              C='Features\Sites\SiteCache.cs';             H='Snapshot' },
    @{ N='World';       D='Features\World\Dto\WorldSnapshot.cs';             S='Features\World\WorldStore.cs';             C='Features\World\WorldCache.cs';            H='Snapshot' },
    @{ N='Guilds';      D='Features\Guilds\Dto\GuildSnapshot.cs';            S='Features\Guilds\GuildStore.cs';            C='Features\Guilds\GuildCache.cs';           H='Envelope' },
    @{ N='Treasury';    D='Features\Treasury\Dto\TreasurySnapshot.cs';       S='Features\Treasury\TreasuryStore.cs';       C='Features\Treasury\TreasuryCache.cs';      H='' },
    @{ N='Mail';        D='Features\Mail\Dto\MailSnapshot.cs';               S='Features\Mail\MailStore.cs';               C='Features\Mail\MailCache.cs';              H='Snapshot' },
    @{ N='Roadworks';   D='Features\Roadworks\Dto\RoadNetworkState.cs';      S='Features\Roadworks\RoadworksStore.cs';     C='Features\Roadworks\RoadworksCache.cs';    H='Snapshot' }
)

$revBad = @()
foreach ($rf in $revFeatures) {
    $sd = Join-Path $serverSrc $rf.D
    $ss = Join-Path $serverSrc $rf.S
    $cc = Join-Path $clientSrc $rf.C
    if (-not (Test-Path $ss) -or -not (Test-Path $cc)) { Warn "$($rf.N): source not found - verify its revision by hand"; continue }

    # The server DTO. Roadworks predates the shared helper and carries its own counter, which is fine.
    if ((Test-Path $sd) -and ([System.IO.File]::ReadAllText($sd) -notmatch 'JsonProperty\("revision"\)') -and $rf.N -ne 'Mail') {
        $revBad += "$($rf.N): the server snapshot carries no revision"
    }

    # Stamped INSIDE a lock. Checked by finding the stamp and walking back for the nearest lock/brace.
    $sTxt = [System.IO.File]::ReadAllLines($ss)
    $stampLine = -1
    for ($i = 0; $i -lt $sTxt.Count; $i++) {
        if ($sTxt[$i] -match 'Revision\s*=\s*(Util\.)?KmhSnapshotRevision\.Next\(\)' -or $sTxt[$i] -match '_revision\+\+') { $stampLine = $i; break }
    }
    if ($stampLine -lt 0) { $revBad += "$($rf.N): nothing stamps a snapshot revision"; continue }
    # Bounded by the enclosing method rather than a fixed window: the copy a snapshot is built into can be dozens
    # of lines below the lock that opened, and a fixed look-back reports that as unlocked.
    $inLock = $false
    for ($j = $stampLine; $j -ge 0; $j--) {
        if ($sTxt[$j] -match '^\s*lock\s*\(') { $inLock = $true; break }
        if ($sTxt[$j] -match '^\s{4,8}(public|private|internal|protected)\s') { break }   # walked out of the method
    }
    if (-not $inLock -and $rf.N -ne 'Roadworks') { $revBad += "$($rf.N): the revision is stamped outside the store lock, so it proves nothing about ordering" }

    # And the client drops an older one.
    $cTxt = [System.IO.File]::ReadAllText($cc)
    $held = if ($rf.H) { [regex]::Escape($rf.H) } else { 'applied' }
    if ($cTxt -notmatch "Revision\s*<\s*$held(\.Revision)?") {
        $revBad += "$($rf.N): the client cache applies a snapshot without comparing its revision"
    }
}
if ($revBad.Count -gt 0) { foreach ($b in $revBad) { Bad $b } }
else { Good "all $($revFeatures.Count) mutable snapshot(s) carry a revision, stamped under the store lock and honoured by the client" }

# --- 52. One logical action delivered twice moves value once ---
# A request can reach the server twice: the client re-sent a write it could not confirm, or the player clicked again.
# For an action that names a row and ends it - a cancel, a state-machine step, an absolute total - the second copy is
# already harmless. For one that moves a quantity or creates a row it is a second purchase, a second escrow, a second
# withdrawal. Those carry an op id on the envelope, the server claims it before acting and releases it if nothing
# happened, and the client reuses the id when it retries rather than minting a new one.
Section '52. One logical action delivered twice moves value once'

# scope = the server's claim scope, which is also the prefix of the client's action key.
$opGuarded = @(
    @{ Scope='mkt.buy';                  S='Features\Marketplace\MarketplaceHandler.cs'; C='Features\Marketplace\MarketplaceHandler.cs' },
    @{ Scope='mkt.post';                 S='Features\Marketplace\MarketplaceHandler.cs'; C='Features\Marketplace\MarketplaceHandler.cs' },
    @{ Scope='auction.post';             S='Features\Auctions\AuctionHandler.cs';        C='Features\Auctions\AuctionHandler.cs' },
    @{ Scope='want.post';                S='Features\WantBoard\WantHandler.cs';          C='Features\WantBoard\WantHandler.cs' },
    @{ Scope='want.fulfill';             S='Features\WantBoard\WantHandler.cs';          C='Features\WantBoard\WantHandler.cs' },
    @{ Scope='quest.post';               S='Features\Quests\QuestHandler.cs';            C='Features\Quests\QuestHandler.cs' },
    @{ Scope='site.building_add';        S='Features\Sites\SiteHandler.cs';              C='Features\Sites\SiteHandler.cs' },
    @{ Scope='roadworks.start';          S='Features\Roadworks\RoadworksHandler.cs';     C='Features\Roadworks\RoadworksHandler.cs' },
    @{ Scope='mail.send';                S='Features\Mail\MailHandler.cs';               C='Features\Mail\MailHandler.cs' },
    @{ Scope='guild.buy_perk';           S='Features\Guilds\GuildHandler.cs';            C='Features\Guilds\GuildHandler.cs' },
    @{ Scope='guild.donate';             S='Features\Guilds\GuildHandler.cs';            C='Features\Guilds\GuildHandler.cs' },
    @{ Scope='guild.withdraw';           S='Features\Guilds\GuildHandler.cs';            C='Features\Guilds\GuildHandler.cs' },
    @{ Scope='treasury.withdraw_silver'; S='Features\Treasury\TreasuryHandler.cs';       C='Features\Treasury\TreasuryHandler.cs' },
    @{ Scope='treasury.withdraw_item';   S='Features\Treasury\TreasuryHandler.cs';       C='Features\Treasury\TreasuryHandler.cs' },
    @{ Scope='world.deliver';            S='Features\World\WorldHandler.cs';             C='Features\World\WorldHandler.cs' }
)

$opBad = @()
foreach ($og in $opGuarded) {
    $sp = Join-Path $serverSrc $og.S
    $cp = Join-Path $clientSrc $og.C
    if (-not (Test-Path $sp) -or -not (Test-Path $cp)) { Warn "$($og.Scope): source not found - verify its op id by hand"; continue }
    $sTxt = [System.IO.File]::ReadAllText($sp)
    $cTxt = [System.IO.File]::ReadAllText($cp)
    $q = [regex]::Escape($og.Scope)
    if ($sTxt -notmatch "KmhOpClaim\(`"$q`"") { $opBad += "$($og.Scope): the server acts on this without claiming the op id" }
    # Two conditions rather than one adjacency: a call site that needs its action key more than once holds it in a
    # local, and repeating the literal to satisfy a regex is the drift this is meant to catch.
    if ($cTxt -notmatch 'KmhOpId\.For\(') { $opBad += "$($og.Scope): the client file mints no op id" }
    if ($cTxt -notmatch "`"$q\|")         { $opBad += "$($og.Scope): the client has no action key under this scope" }
}

# Every claim in the server source is in the table above, or the table stops describing what is guarded.
$claimScopes = @()
foreach ($f in (Get-ChildItem $serverSrc -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
    foreach ($m in [regex]::Matches([System.IO.File]::ReadAllText($f.FullName), 'KmhOpClaim\("([^"]+)"')) {
        $claimScopes += $m.Groups[1].Value
    }
}
foreach ($s in ($claimScopes | Sort-Object -Unique)) {
    if ($opGuarded.Scope -notcontains $s) { $opBad += "scope '$s' is claimed in the server but not listed here" }
}

# A refused operation has to give its id back, or a withdrawal turned down for want of silver stays refused.
foreach ($sf in ($opGuarded.S | Sort-Object -Unique)) {
    $p = Join-Path $serverSrc $sf
    if (-not (Test-Path $p)) { continue }
    $t = [System.IO.File]::ReadAllText($p)
    $claims   = ([regex]::Matches($t, 'KmhOpClaim\(')).Count
    $releases = ([regex]::Matches($t, '\.Release\(\)')).Count
    if ($releases -lt $claims) { $opBad += "$sf : $claims claim(s) but only $releases release(s) - a refusal would burn the id" }
}

# The envelope carries it at the top level on both sides, so the transport can see it without parsing the payload.
foreach ($pair in @(@{ R='server'; P=(Join-Path $serverSrc 'SubProtocol\KmhEnvelope.cs') },
                    @{ R='client'; P=(Join-Path $clientSrc 'SubProtocol\KmhEnvelope.cs') })) {
    if (-not (Test-Path $pair.P)) { $opBad += "$($pair.R): KmhEnvelope.cs not found"; continue }
    $t = [System.IO.File]::ReadAllText($pair.P)
    if ($t -notmatch 'JsonProperty\("op"') { $opBad += "$($pair.R): the envelope carries no op id" }
    if ($t -notmatch 'Wire[\s\S]*JsonProperty\("op"') { $opBad += "$($pair.R): the outbound wire shape drops the op id" }
}

if ($opBad.Count -gt 0) { foreach ($b in $opBad) { Bad $b } }
else { Good "all $($opGuarded.Count) duplicable value move(s) claim an op id on the server and reuse it on the client" }

# The operations deliberately NOT guarded are the ones whose repeat is already harmless. Two of them are only
# harmless because of how their argument is shaped, and a change to either would make them duplicable in silence.
$opShape = @()
$wePath = Join-Path $serverSrc 'Features\World\WorldEngine.cs'
if (Test-Path $wePath) {
    if ([System.IO.File]::ReadAllText($wePath) -notmatch 'ApplyContribution\(string user, long questId, int cumulative\)') {
        $opShape += "world.contribute no longer takes a cumulative total, so repeating it now adds twice"
    }
} else { Warn 'WorldEngine.cs not found - verify the contribution shape by hand' }
$abPath = Join-Path $serverSrc 'Features\Auctions\AuctionStore.cs'
if (Test-Path $abPath) {
    if ([System.IO.File]::ReadAllText($abPath) -notmatch 'CurrentBid\s*\+\s*pa\.MinIncrement[\s\S]{0,200}amount\s*<\s*need') {
        $opShape += "auction.bid no longer requires an increment over the standing bid, so a repeated bid can re-escrow"
    }
} else { Warn 'AuctionStore.cs not found - verify the bid guard by hand' }
if ($opShape.Count -gt 0) { foreach ($b in $opShape) { Bad $b } }
else { Good "the unguarded repeats stay harmless: a contribution is an absolute total, a bid must beat the standing one" }

# --- 53. Every ingress that moves value asks the admission layer ---
# The router is not the only way into the stores. Discord commands and the scheduler call them directly, so an
# owner's "value moves are paused" was true of game clients and false of everything else. Any file that calls a
# value-moving store method without going through the router has to ask KmhAdmission first.
Section '53. Every ingress that moves value asks the admission layer'

$valueCall = 'MarketplaceStore\.(Buy|Post|Cancel)\(|AuctionStore\.(Post|Bid)\(|WantStore\.(Post|Fulfill)\(|TreasuryStore\.(WithdrawSilver|WithdrawItem|DepositSilver)\('
$admBad = @()
$admScanned = 0
foreach ($f in (Get-ChildItem (Join-Path $serverSrc 'Features\Discord') -Recurse -Filter *.cs -File)) {
    $t = [System.IO.File]::ReadAllText($f.FullName)
    if ($t -notmatch $valueCall) { continue }
    $admScanned++
    if ($t -notmatch 'KmhAdmission\.') {
        $admBad += "Discord\$($f.Name): moves value without asking KmhAdmission"
    }
}
# The scheduler starts value transitions on a timer rather than on a request, so it needs the same question.
$sweep = Join-Path $serverSrc 'Maintenance\ExpirySweeper.cs'
if (Test-Path $sweep) {
    $st = [System.IO.File]::ReadAllText($sweep)
    $admScanned++
    if ($st -notmatch 'KmhAdmission\.AllowsValueMutation') { $admBad += "ExpirySweeper: settles value without asking KmhAdmission" }
    # ...but the retry that ends a persistence freeze must stay outside that check or the freeze outlives its cause.
    if ($st -notmatch 'ShouldRetrySaves')                  { $admBad += "ExpirySweeper: the persistence retry is no longer separable from the value jobs" }
}
if ($admBad.Count -gt 0) { foreach ($b in $admBad) { Bad $b } }
else { Good "all $admScanned non-router value ingress(es) ask the admission layer, and the persistence retry stays outside it" }

# --- 54. A sale split is taken once per settlement ---
# SaleSplit.Compute reads like a calculation but debits the house pool for a market-boom boost, so calling it twice
# for one sale takes that boost twice and can plan a payout that differs from the one actually paid.
Section '54. A sale split is taken once per settlement'

# One settlement path per file, so a second call inside one is visible as a second call in the file.
$splitExpected = @{
    'Features\Auctions\AuctionStore.cs'        = 1
    'Features\Marketplace\MarketplaceStore.cs' = 1
    'Features\WantBoard\WantStore.cs'          = 2   # fulfil and payload-fulfil, one each
}
$splitBad = @()
foreach ($sf in $splitExpected.Keys) {
    $p = Join-Path $serverSrc $sf
    if (-not (Test-Path $p)) { Warn "$sf not found - verify the split by hand"; continue }
    $calls = ([regex]::Matches([System.IO.File]::ReadAllText($p), 'SaleSplit\.Compute\(')).Count
    if ($calls -ne $splitExpected[$sf])
        { $splitBad += "$sf computes the sale split $calls time(s), expected $($splitExpected[$sf]) - each extra call debits the boom boost again" }
}
if ($splitBad.Count -gt 0) { foreach ($b in $splitBad) { Bad $b } }
else { Good "each settlement path computes its sale split once" }

# Planning must stay planning: a crash cannot run an unwind helper, so Compute may not move value at all.
$sp = Join-Path $serverSrc 'Features\Economy\SaleSplit.cs'
if (-not (Test-Path $sp)) { Warn 'SaleSplit.cs not found - verify Compute purity by hand' }
else {
    $spText = [System.IO.File]::ReadAllText($sp)
    $computeBody = ''
    $ci = $spText.IndexOf('public static SaleSplit Compute(')
    if ($ci -ge 0) {
        $ce = $spText.IndexOf('public bool CommitBoost(')
        if ($ce -gt $ci) { $computeBody = $spText.Substring($ci, $ce - $ci) } else { $computeBody = $spText.Substring($ci) }
    }
    if ($computeBody -match 'TryDebitHousePool\(|CreditHousePool\(|Deposit[A-Za-z]*\(|Withdraw[A-Za-z]*\(')
        { Bad 'SaleSplit.Compute moves value - a crash before the settlement record cannot be unwound' }
    else { Good "SaleSplit.Compute plans without moving value; the boom boost is taken by CommitBoost" }
}

# The thing a boot failure most often IS, is an incompatible host. Reporting it through the host's own logger
# resolves a host API at JIT time, and that throw escapes the boot catch and kills the server process.
Section '55. A failed boot is reported without calling into the host'
$readyFile = Join-Path $serverSrc 'Maintenance\KmhReadiness.cs'
if (-not (Test-Path $readyFile)) { Warn 'KmhReadiness.cs not found - verify the failure path by hand' }
else {
    $rt = [System.IO.File]::ReadAllText($readyFile)
    $fi = $rt.IndexOf('public static void Failed(')
    $failedBody = ''
    if ($fi -ge 0) {
        $fe = $rt.IndexOf('internal static void ResetForTest', $fi)
        $failedBody = if ($fe -gt $fi) { $rt.Substring($fi, $fe - $fi) } else { $rt.Substring($fi) }
    }
    if ($failedBody -match 'ServerLog\.')
        { Bad 'KmhReadiness.Failed reports through ServerLog - a host whose Printer differs turns the report into a crash' }
    else { Good 'KmhReadiness.Failed writes to the KMH sink and stderr only' }

    $mainFile = Join-Path $serverSrc 'Main.cs'
    $mt = if (Test-Path $mainFile) { [System.IO.File]::ReadAllText($mainFile) } else { '' }
    if ($mt -match 'try \{ Maintenance\.KmhReadiness\.Failed\([\s\S]{0,120}?\} catch \{ \}')
        { Good 'the boot catch cannot itself throw out of the handler' }
    else { Bad 'the boot failure handler is not exception-proof - a throw there aborts RWT' }
}


# Exact payload instances only exist after they leave the vault, so the transaction row naming them is written
# second. In between, the vault holds them as a pending take - and exactly one of the two owns them at all times.
Section '56. A payload take is owned by the vault or by a row, never by neither and never by both'

$takeFail = $false
$vaultFile56 = Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs'
$v56 = if (Test-Path $vaultFile56) { [System.IO.File]::ReadAllText($vaultFile56) } else { '' }

# Recorded in the SAME commit that removes them, or the gap it exists to cover is still open.
if ($v56 -notmatch 'v\.PendingTakes\.Add\(new Dto\.PendingTake') {
    Bad 'TreasuryStore does not record a pending take when it withdraws payloads under a marker'; $takeFail = $true
}
if ($v56 -notmatch 'PendingTakes[\s\S]{0,400}?CommitLocked') {
    Bad 'the pending take is not written in the same commit as the removal it describes'; $takeFail = $true
}

foreach ($pair56 in @(
    @{ File = 'Features\Marketplace\MarketplaceStore.cs'; What = 'marketplace post' },
    @{ File = 'Features\Auctions\AuctionStore.cs';        What = 'auction post'     }
)) {
    $p56 = Join-Path $serverSrc $pair56.File
    $t56 = if (Test-Path $p56) { [System.IO.File]::ReadAllText($p56) } else { '' }

    if ($t56 -notmatch 'refundMarker:\s*post\.RefundMarker') {
        Bad "$($pair56.What) takes payloads without naming the key its return is deduplicated on"; $takeFail = $true
    }
    # The settled row is prunable. A claim that outlives it is handed back while the listing still holds the goods,
    # so the post must REFUSE when the vault will not release - never carry on and settle anyway.
    if ($t56 -notmatch 'if \(!Treasury\.TreasuryStore\.ClearPendingTake\(') {
        Bad "$($pair56.What) does not act on ClearPendingTake failing - the vault's claim would outlive its row"; $takeFail = $true
    }
    # Returning POST escrow must be idempotent, or the inline refund and boot recovery both pay it back. Scoped to
    # the seller by name: the settlement paths hand an auction's escrow to a winner and are guarded by op ids
    # instead (section 52), so a blanket ban on RefundTo here would fail on the wrong thing.
    if ($t56 -cmatch 'KmhPayloadEscrow\.RefundTo\(\s*(seller|sellerUsername)\b') {
        Bad "$($pair56.What) returns post escrow without a dedup marker - a crash before Settle pays it twice"; $takeFail = $true
    }
}

$rec56 = Join-Path $serverSrc 'Transactions\KmhPayloadTakeRecovery.cs'
if (-not (Test-Path $rec56)) { Bad 'no boot recovery for pending payload takes'; $takeFail = $true }
else {
    $r56 = [System.IO.File]::ReadAllText($rec56)
    if ($r56 -notmatch 'named\.Contains\(p\.TakeMarker\)') {
        Bad 'take recovery does not check whether a row already names the take - that is how one crash becomes two copies'; $takeFail = $true
    }
}

$boot56 = Join-Path $serverSrc 'Main.cs'
$b56 = if (Test-Path $boot56) { [System.IO.File]::ReadAllText($boot56) } else { '' }
# A restore runs before EconomyReset.json is read. Bumping the generation inside the restore would write 1 over the
# value it just restored, and every pre-restore action would then read as current instead of stale.
if ($b56 -notmatch 'RestoredThisBoot[\s\S]{0,400}?BumpDataGeneration') {
    Bad 'a restored KMH-Data does not advance the data generation after the generation store is loaded'; $takeFail = $true
}
$bk56 = Join-Path $serverSrc 'Persistence\KmhDataBackup.cs'
$k56 = if (Test-Path $bk56) { [System.IO.File]::ReadAllText($bk56) } else { '' }
if ($k56 -match 'BumpDataGeneration') {
    Bad 'KmhDataBackup bumps the data generation itself - at boot that clobbers the value it just restored'; $takeFail = $true
}
if ($k56 -notmatch 'KmhReadiness\.IsReady') {
    Bad 'a backup flushes the stores without checking they are loaded - before Ready that writes empty over live data'; $takeFail = $true
}
if ($b56 -notmatch 'KmhPayloadTakeRecovery\.RecoverOnBoot\(\)') {
    Bad 'boot never runs payload take recovery'; $takeFail = $true
}
elseif ($b56.IndexOf('KmhTransactionRepository.LoadFromDisk()') -ge $b56.IndexOf('KmhPayloadTakeRecovery.RecoverOnBoot()')) {
    Bad 'payload take recovery runs before the transaction rows are loaded, so every take looks unnamed'; $takeFail = $true
}

if (-not $takeFail) { Good 'a payload take is recorded with its removal, released before anything else owns it, and reconciled at boot' }


# RWT changes the SHAPE of members between generations, not just their names: ExecutableVersion went property ->
# field and DisconnectToMenu went public -> private in 26.8.31.1. A direct call binds when the CALLING method is
# JIT-compiled and throws MissingMethodException before that method's own try block is entered, so a call site
# "protected" by try/catch is not protected at all. Both must go through reflection in RwtCompat.
Section '57. RWT members that changed shape are reached by reflection, not called directly'

$shim57 = Join-Path $clientSrc 'VersionShim.cs'
$bad57 = $false
if (-not (Test-Path -LiteralPath $shim57)) {
    Bad 'VersionShim.cs is missing - the RWT compatibility shims live there'; $bad57 = $true
} else {
    $s57 = [System.IO.File]::ReadAllText($shim57)
    if ($s57 -notmatch 'AccessTools\.Property\(typeof\(CommonValues\)') {
        Bad 'RwtCompat.ExecutableVersion does not resolve the property by reflection'; $bad57 = $true
    }
    if ($s57 -notmatch 'AccessTools\.Field\(typeof\(CommonValues\)') {
        Bad 'RwtCompat.ExecutableVersion does not fall back to the field shape (26.8.31.1)'; $bad57 = $true
    }
    if ($s57 -notmatch 'AccessTools\.Method\(t,\s*"DisconnectToMenu"\)') {
        Bad 'RwtCompat.DisconnectToMainMenu does not resolve DisconnectToMenu by reflection'; $bad57 = $true
    }
}

# No feature code may name either member directly - that is what re-introduces the JIT-time binding.
foreach ($f57 in Get-ChildItem $clientSrc -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    if ($f57.Name -eq 'VersionShim.cs') { continue }
    $t57 = [System.IO.File]::ReadAllText($f57.FullName)
    $rel57 = $f57.FullName.Substring($clientSrc.Length).TrimStart('\','/')
    if ($t57 -match 'CommonValues\.ExecutableVersion') {
        Bad "$rel57 reads CommonValues.ExecutableVersion directly - use RwtCompat.ExecutableVersion"; $bad57 = $true
    }
    if ($t57 -match 'DisconnectionManager\.DisconnectToMenu') {
        Bad "$rel57 calls DisconnectionManager.DisconnectToMenu directly - use RwtCompat.DisconnectToMainMenu"; $bad57 = $true
    }
}

# A release older than KMH supports must be refused in words, not as a Harmony TypeLoadException nobody can act on.
$boot57 = Join-Path $serverSrc 'Bootstrap.cs'
if (-not (Test-Path $boot57)) { Bad 'Bootstrap.cs is missing'; $bad57 = $true }
else {
    $b57 = [System.IO.File]::ReadAllText($boot57)
    $old57 = [regex]::Match($b57, 'case "old":[\s\S]{0,400}?RunAndStartServer')
    if (-not $old57.Success) { Bad 'the old-generation branch could not be read out of Bootstrap'; $bad57 = $true }
    elseif ($old57.Value -notmatch 'OldGenerationIsSupported') {
        Bad 'the old-generation branch starts without checking the RWT API it patches actually exists'; $bad57 = $true
    }
    # Scanning loaded assemblies finds nothing: RWT resolves lazily, so the probe has to pull the assembly in itself.
    if ($b57 -match 'OldGenerationIsSupported' -and $b57 -notmatch 'Assembly\.Load\(') {
        Bad 'the support probe does not load the assembly it asks about, so it answers before RWT is resolved'
        $bad57 = $true
    }
}
if (-not $bad57) { Good 'the two members RWT reshaped are reached by reflection, and an unsupported release is refused in words' }

Section '58. kmh.site_meta carries the same fields on both sides'
# Section 3 compares DTO folders file-to-file, and the server's half of this one is a private nested class inside
# its handler rather than a Dto file - so the pair is invisible there. Compare the two directly instead.
$bad58 = $false
$srv58 = Join-Path $serverSrc 'Features\Sites\SiteMetadataHandler.cs'
$cli58 = Join-Path $clientSrc 'Features\Catalog\Dto\SiteMetadataPush.cs'
foreach ($p58 in @($srv58, $cli58)) {
    if (-not (Test-Path -LiteralPath $p58)) { Bad "kmh.site_meta: missing $p58"; $bad58 = $true }
}
if (-not $bad58) {
    $sf58 = @([regex]::Matches([System.IO.File]::ReadAllText($srv58), '\[JsonProperty\(\s*"([^"]+)"') |
             ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $cf58 = @([regex]::Matches([System.IO.File]::ReadAllText($cli58), '\[JsonProperty\(\s*"([^"]+)"') |
             ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $only58 = @(Compare-Object $sf58 $cf58 | ForEach-Object { "$($_.InputObject) ($(if ($_.SideIndicator -eq '<=') { 'server' } else { 'client' }) only)" })
    if ($only58.Count -gt 0) {
        Bad "kmh.site_meta field mismatch: $($only58 -join ', ')"; $bad58 = $true
    }
    elseif ($sf58.Count -lt 4) {
        Bad "kmh.site_meta: only $($sf58.Count) field(s) found - the regex stopped matching, so this check is blind"; $bad58 = $true
    }
}
if (-not $bad58) { Good "site_meta: server and client agree on all $($sf58.Count) field(s)" }

Section '59. The hello advertises the listener that exists, not the config that asked for one'
# A failed bind, or the transport stopped, used to still send api_enabled=true and the CONFIGURED port. Every client
# then dialled a dead socket, timed out, and lived on chat while the server looked healthy. Both values must come
# from KmhApiServer, which knows what actually bound.
$hs59 = Join-Path $serverSrc 'SubProtocol\KmhHandshakeHandler.cs'
$bad59 = $false
if (-not (Test-Path -LiteralPath $hs59)) { Bad 'KmhHandshakeHandler.cs not found'; $bad59 = $true }
else {
    $t59 = [System.IO.File]::ReadAllText($hs59)
    if ($t59 -notmatch 'bool\s+apiOn\s*=[^;]*KmhApiServer\.Running') {
        Bad 'the hello decides api_enabled from config alone - a failed bind would still advertise the transport'; $bad59 = $true
    }
    if ($t59 -notmatch 'api_port\s*=[^,]*KmhApiServer\.Port') {
        Bad 'the hello advertises a configured port instead of the bound one'; $bad59 = $true
    }
    if ($t59 -match 'api_port\s*=[^,]*tcfg\.KmhApiPort') {
        Bad 'the hello still reads tcfg.KmhApiPort - that is the requested port, not the live one'; $bad59 = $true
    }
}
# And the server must only ever report a port while something is listening on it.
$api59 = Join-Path $serverSrc 'Features\Transport\KmhApiServer.cs'
if (Test-Path -LiteralPath $api59) {
    $a59 = [System.IO.File]::ReadAllText($api59)
    if ($a59 -notmatch 'Port\s*=>\s*_running\s*\?\s*_boundPort\s*:\s*0') {
        Bad 'KmhApiServer.Port can report a port while nothing is listening'; $bad59 = $true
    }
    if ($a59 -notmatch '_boundPort\s*=\s*\(\(IPEndPoint\)_listener\.LocalEndpoint\)\.Port') {
        Bad 'KmhApiServer takes its bound port from config rather than from the socket'; $bad59 = $true
    }
}
if (-not $bad59) { Good 'api_enabled and api_port both come from the live listener, and the port is 0 when it is down' }

Section '60. One authority decides the client transport endpoint'
# The hello decides, with only the player's own override ahead of it. Nothing discovered may be written back to
# settings or replayed from the seen-servers list, or one server's port follows the player to the next.
$hs60 = Join-Path $clientSrc 'SubProtocol\KmhHandshakeHandler.cs'
$bad60 = $false
if (-not (Test-Path -LiteralPath $hs60)) { Bad 'client KmhHandshakeHandler.cs not found'; $bad60 = $true }
else {
    $t60 = [System.IO.File]::ReadAllText($hs60)
    if ($t60 -notmatch 'env\.GetInt\("api_port"') {
        Bad 'the client does not take its port from the hello'; $bad60 = $true
    }
    if ($t60 -notmatch 'env\.GetString\("api_host"') {
        Bad 'the client does not take its host from the hello'; $bad60 = $true
    }
}
# A discovered endpoint that is persisted becomes the next server's endpoint too. Only the settings screen may write these.
foreach ($leak60 in @('KmhApiPort', 'KmhApiHostOverride')) {
    $writers60 = @()
    foreach ($f60 in Get-ChildItem -Path $clientSrc -Recurse -Filter *.cs -File) {
        if ($f60.FullName -match '\\(obj|bin)\\') { continue }
        foreach ($line60 in [System.IO.File]::ReadAllLines($f60.FullName)) {
            if ($line60 -match "Settings\.$leak60\s*=" -or $line60 -match "settings\.$leak60\s*=") { $writers60 += $f60.Name }
        }
    }
    $writers60 = @($writers60 | Sort-Object -Unique)
    $stray60 = @($writers60 | Where-Object { $_ -ne 'KmhEntry.cs' })
    if ($stray60.Count -gt 0) {
        Bad "$leak60 is written outside the settings screen ($($stray60 -join ', ')) - a discovered endpoint must not be persisted"; $bad60 = $true
    }
}
# The seen-servers list is branding for the browser; reading it to pick a transport would be the second authority.
$seen60 = Join-Path $clientSrc 'Features\Servers\SeenServersStore.cs'
if ((Test-Path -LiteralPath $seen60) -and ([System.IO.File]::ReadAllText($seen60) -match 'ApiPort|api_port|TransportPort')) {
    Bad 'SeenServersStore carries transport endpoint data - the browser must not become a second endpoint source'; $bad60 = $true
}
if (-not $bad60) { Good 'the hello decides the endpoint; nothing discovered is persisted or replayed from the browser' }

Section '61. No timer is counted down from a draw'
# RimWorld calls DoWindowContents more than once per frame (layout, repaint, each input event), so a
# `timer -= deltaTime` inside one runs several times a frame and fires at a multiple of its real rate.
# A 12s site refresh became a 6s poll of the server this way. Tick from WindowUpdate, or use a deadline.
$bad61 = $false
$countdowns61 = @()
foreach ($f61 in Get-ChildItem -Path $clientSrc -Recurse -Filter *.cs -File) {
    if ($f61.FullName -match '\\(obj|bin)\\') { continue }
    $lines61 = [System.IO.File]::ReadAllLines($f61.FullName)
    # Find each method that draws, then look for a countdown anywhere inside the file's draw region.
    $inDraw61 = $false; $depth61 = 0
    for ($i61 = 0; $i61 -lt $lines61.Count; $i61++) {
        $ln61 = $lines61[$i61]
        if (-not $inDraw61 -and $ln61 -match '(void\s+(DrawContents|DoWindowContents|DrawList|DrawRow)\s*\()') {
            $inDraw61 = $true; $depth61 = 0
        }
        if ($inDraw61) {
            $depth61 += ([regex]::Matches($ln61, '\{')).Count - ([regex]::Matches($ln61, '\}')).Count
            if ($ln61 -match '-=\s*Time\.(unscaled)?deltaTime') {
                $countdowns61 += "$($f61.Name):$($i61 + 1)"
            }
            if ($depth61 -le 0 -and $i61 -gt 0 -and $ln61 -match '\}') { $inDraw61 = $false }
        }
    }
}
if ($countdowns61.Count -gt 0) {
    Bad "a timer is counted down from a draw method ($($countdowns61 -join ', ')) - it will fire faster than it reads"
    $bad61 = $true
}
if (-not $bad61) { Good 'no draw method counts a timer down; refreshes tick once per frame or off a deadline' }

Section '62. KMH opens exactly one port'
# Owner-facing rule, not a preference: a second listener is another thing to know about and forward, and when
# nobody has, the feature behind it fails as a bare connection timeout. Video rides the transport socket.
$bad62 = $false
$binds62 = @()
foreach ($f62 in Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs -File) {
    if ($f62.FullName -match '\\(obj|bin)\\') { continue }
    if ($f62.Name -match 'SelfTest|Test\.cs$') { continue }   # tests bind loopback to prove a guard
    $lines62 = [System.IO.File]::ReadAllLines($f62.FullName)
    for ($i62 = 0; $i62 -lt $lines62.Count; $i62++) {
        if ($lines62[$i62] -match 'new\s+TcpListener\s*\(' -or $lines62[$i62] -match 'new\s+HttpListener\s*\(') {
            $binds62 += "$($f62.Name):$($i62 + 1)"
        }
    }
}
if ($binds62.Count -ne 1 -or $binds62[0] -notmatch '^KmhApiServer\.cs:') {
    Bad "the server binds $($binds62.Count) listener(s) ($($binds62 -join ', ')) - only KmhApiServer may open a port"
    $bad62 = $true
}
# And no config may ask for one.
foreach ($f62b in Get-ChildItem -Path $serverSrc -Recurse -Filter *Config.cs -File) {
    if ($f62b.FullName -match '\\(obj|bin)\\') { continue }
    $t62 = [System.IO.File]::ReadAllText($f62b.FullName)
    if ($t62 -match '(VideoServerPort|RelayPort|SecondaryPort|MediaPort)\s*\{\s*get') {
        Bad "$($f62b.Name) exposes a second listening port as a setting"; $bad62 = $true
    }
}
if (-not $bad62) { Good 'one listener, in KmhApiServer, and no config offers a second port' }

Section '63. A world marker draws its own quad, upright, with ownership on a rim'
# Vanilla lays a world quad with its texture-up along Cross(normal, Vector3.up) - an east-west tangent. Vanilla art is
# symmetric so it never shows; KMH art is a castle, a shield, a factory, and it landed on its side. Ownership rides on
# a halo behind the icon because tinting the icon itself washed a shield into a green blob.
$bad63 = $false
$renderPath63 = Join-Path $clientSrc 'Features\Sites\KmhMarkerRender.cs'
if (-not (Test-Path $renderPath63)) {
    Bad 'KmhMarkerRender.cs is gone - the close-zoom markers are back on vanilla orientation'
    $bad63 = $true
} else {
    $render63 = [System.IO.File]::ReadAllText($renderPath63)
    if ($render63 -notmatch 'DrawQuadTangentialToPlanet\([^)]*angle') {
        Bad 'KmhMarkerRender does not pass a rotation angle to DrawQuadTangentialToPlanet'; $bad63 = $true
    }
    if ($render63 -notmatch 'internal static float AngleFor\(') {
        Bad 'the upright angle is not exposed as a pure function - the offline suite cannot check the geometry'
        $bad63 = $true
    }
    if ($render63 -notmatch 'Plain\(Texture2D' -or $render63 -notmatch 'Halo\(Color') {
        Bad 'KmhMarkerRender no longer separates the plain icon material from the border one'; $bad63 = $true
    }
    # Hand-rolled outlines read as a second icon or a blob; the border has to be the shader RimWorld uses itself.
    if ($render63 -notmatch 'ShaderDatabase\.ExpandingIconUI' -or $render63 -notmatch 'needsMainTex\s*=\s*false') {
        Bad 'the border is not built from ShaderDatabase.ExpandingIconUI - KMH is imitating the ring again'
        $bad63 = $true
    }
    $plainBody63 = [regex]::Match($render63, 'Plain\(Texture2D[\s\S]{0,240}')
    if (-not $plainBody63.Success -or $plainBody63.Value -notmatch 'Color\.white') {
        Bad 'the plain icon material is not built white - the art is being tinted again'; $bad63 = $true
    }
}
foreach ($marker63 in @('Features\Sites\KMHSiteWorldObject.cs', 'Features\Guilds\KMHGuildHallWorldObject.cs')) {
    $p63 = Join-Path $clientSrc $marker63
    if (-not (Test-Path $p63)) { Bad "$marker63 is missing"; $bad63 = $true; continue }
    $t63 = [System.IO.File]::ReadAllText($p63)
    if ($t63 -notmatch 'override\s+void\s+Draw\s*\(') {
        Bad "$marker63 does not override Draw - its close-zoom art keeps vanilla's east-west orientation"
        $bad63 = $true
    }
    # ExpandingMaterial is the hook vanilla already reads, so no patch on the world GUI is needed to get the border.
    if ($t63 -notmatch 'override\s+Material\s+ExpandingMaterial') {
        Bad "$marker63 overrides no ExpandingMaterial - the zoomed-out icon gets no border"; $bad63 = $true
    }
    if ($t63 -notmatch 'KmhMarkerRender\.Plain\(') {
        Bad "$marker63 does not build its icon material plain - the artwork is being tinted"; $bad63 = $true
    }
    if ($t63 -notmatch 'ExpandingIconColor\s*=>\s*Color\.white') {
        Bad "$marker63 still tints its zoomed-out icon instead of leaving the artwork alone"; $bad63 = $true
    }
}
# A per-frame sweep over every world object is what the ExpandingMaterial hook exists to avoid.
if (Test-Path (Join-Path $clientSrc 'Patches\Patch_ExpandableWorldObjects_KmhHalo.cs')) {
    Bad 'the expanding-icon GUI patch is back - the border belongs on ExpandingMaterial, not a per-frame sweep'
    $bad63 = $true
}
if (-not $bad63) { Good "both marker classes draw their own quad upright, and wear RimWorld's own border colour" }

Section '64. A log record is one line on both sides'
# An OS error message can arrive with its raw FormatMessage tail attached - a CRLF and NUL padding - and player names,
# item labels and chat all reach diagnostics too. Either forges log lines in a file someone later reads or uploads.
$bad64 = $false
$sinks64 = @{
    'server ServerLog'  = (Join-Path $serverSrc 'Diagnostics\ServerLog.cs')
    'server client-log' = (Join-Path $serverSrc 'Diagnostics\KmhClientDebugLog.cs')
    'client KmhLog'     = (Join-Path $clientSrc 'Diagnostics\KmhLog.cs')
}
foreach ($name64 in $sinks64.Keys) {
    $p64 = $sinks64[$name64]
    if (-not (Test-Path $p64)) { Bad "$name64 sink is missing ($p64)"; $bad64 = $true; continue }
    if ([System.IO.File]::ReadAllText($p64) -notmatch 'OneLine\(') {
        Bad "$name64 writes records without collapsing them to one line"; $bad64 = $true
    }
}
# And the client's own cleaner must actually drop control characters, not only newlines.
$clean64 = Join-Path $clientSrc 'Diagnostics\KmhLog.cs'
if ((Test-Path $clean64) -and ([System.IO.File]::ReadAllText($clean64) -notmatch "c < ' '")) {
    Bad 'the client log cleaner does not drop C0 control characters - a NUL run reaches the file and the uplink'
    $bad64 = $true
}
if (-not $bad64) { Good 'every diagnostic sink collapses a record to one line and drops control characters' }

Section '65. Animated media survives every path that can attach it'
# Discord attaches an embed by EDITING the message afterwards, and that second path skipped the resolver, so a gif
# posted as a link reached players as Discord's flattened still while the same gif posted as an attachment animated.
$bad65 = $false
$store65 = Join-Path $serverSrc 'Features\Chat\ChatStore.cs'
if (-not (Test-Path $store65)) { Bad 'ChatStore.cs is missing'; $bad65 = $true }
else {
    $t65 = [System.IO.File]::ReadAllText($store65)
    $setImage65 = [regex]::Match($t65, 'bool\s+SetImage\s*\([\s\S]{0,2000}?\r?\n        \}')
    if (-not $setImage65.Success) { Bad 'SetImage could not be read out of ChatStore'; $bad65 = $true }
    elseif ($setImage65.Value -notmatch 'MediaId\s*=') {
        Bad 'SetImage attaches a late embed without a resolver id - every late gif arrives as a flattened still'
        $bad65 = $true
    }
    if ($t65 -notmatch 'ResolverIdFor\(') {
        Bad 'the resolver decision is not shared - Post and SetImage will drift apart again'; $bad65 = $true
    }
}
# The fallback url matters too: measured, ?format=png flattens an animated webp and inflates a jpeg over five-fold.
$policy65 = Join-Path $serverSrc 'Features\Chat\ChatImagePolicy.cs'
if (-not (Test-Path $policy65)) { Bad 'ChatImagePolicy.cs is missing'; $bad65 = $true }
else {
    $p65 = [System.IO.File]::ReadAllText($policy65)
    if ($p65 -notmatch 'ClientCanDecode\(') {
        Bad 'a transcode is asked for without checking the client could already decode the source'; $bad65 = $true
    }
    if ($p65 -notmatch 'IsWrapped\(') {
        Bad 'wrapped and unwrapped sources ask for the same format - one of them is answered 415'; $bad65 = $true
    }
}
# And the client must not answer a settled refusal by asking again: one dead link cost 11 requests in 7 seconds.
$cache65 = Join-Path $clientSrc 'Features\Chat\ChatImageCache.cs'
if (-not (Test-Path $cache65)) { Bad 'ChatImageCache.cs is missing'; $bad65 = $true }
else {
    $c65 = [System.IO.File]::ReadAllText($cache65)
    $request65 = [regex]::Match($c65, 'static\s+void\s+Request\s*\([\s\S]{0,2000}?\r?\n        \}')
    if (-not $request65.Success) { Bad 'Request could not be read out of ChatImageCache'; $bad65 = $true }
    elseif ($request65.Value -match 'State\s*=\s*State\.None\s*;\s*\w*\.?Error') {
        Bad 'Request still clears a failed entry - a drawn row turns into a request loop'; $bad65 = $true
    }
    if ($c65 -notmatch 'IsPermanentStatus\(') {
        Bad 'no failure is treated as settled, so a 404 is retried like a timeout'; $bad65 = $true
    }
}
$panel65 = Join-Path $clientSrc 'Features\Chat\ChatPanel.cs'
if ((Test-Path $panel65) -and ([System.IO.File]::ReadAllText($panel65) -notmatch 'ChatImageCache\.Retry\(')) {
    Bad 'the failed-image row does not call Retry - it either loops or offers a button that does nothing'
    $bad65 = $true
}
if (-not $bad65) { Good 'a late embed resolves, the fallback keeps its frames, and a settled refusal is asked once' }

Section '66. Work that is identical for every recipient is done once'
# One snapshot going to fifty clients is byte-identical for all of them, and one chat log is measured the same way on
# every frame it has not changed. Both were being redone per client and per frame, which is invisible at one player.
$bad66 = $false
$router66 = Join-Path $serverSrc 'SubProtocol\KmhRouter.cs'
if (-not (Test-Path $router66)) { Bad 'KmhRouter.cs is missing'; $bad66 = $true }
else {
    $t66 = [System.IO.File]::ReadAllText($router66)
    $loop66 = [regex]::Match($t66, 'BroadcastToInterested\(string kind, Func<string, string> shareKey[\s\S]{0,2400}?\r?\n        \}')
    if (-not $loop66.Success) { Bad 'the broadcast loop could not be read out of KmhRouter'; $bad66 = $true }
    elseif ($loop66.Value -match '\.Serialize\(\)|KmhApiServer\.Encode\(') {
        Bad 'the broadcast loop encodes inside itself - one payload is being serialized once per client'; $bad66 = $true
    }
    if ($t66 -notmatch 'class Prepared') {
        Bad 'nothing carries an encoded payload between recipients, so every client pays for the same bytes'; $bad66 = $true
    }
}
$api66 = Join-Path $serverSrc 'Features\Transport\KmhApiServer.cs'
if ((Test-Path $api66) -and ([System.IO.File]::ReadAllText($api66) -notmatch 'Framed\s+Encode\(')) {
    Bad 'the API transport offers no encode-once path, so a broadcast must re-serialize per peer'; $bad66 = $true
}
$roads66 = Join-Path $serverSrc 'Features\Roadworks\RoadworksStore.cs'
if ((Test-Path $roads66)) {
    $r66 = [System.IO.File]::ReadAllText($roads66)
    if ($r66 -notmatch '_sharedSegmentsRevision') {
        Bad 'the world road list is rebuilt per recipient - it is the same planet for all of them'; $bad66 = $true
    }
    # A cache nothing throws away is worse than no cache: it serves a road map that no longer exists.
    if ($r66 -notmatch '_sharedSegmentsRevision\s*=\s*-1') {
        Bad 'the shared road list is never invalidated on load, where the revision counter does not carry over'
        $bad66 = $true
    }
}
# The client half: a chat log measured every frame built and laid out a rich-text line per message, per frame.
$cache66 = Join-Path $clientSrc 'Features\Chat\ChatCache.cs'
if (-not (Test-Path $cache66)) { Bad 'ChatCache.cs is missing'; $bad66 = $true }
else {
    $c66 = [System.IO.File]::ReadAllText($cache66)
    if ($c66 -notmatch 'long Generation') {
        Bad 'ChatCache exposes no generation, so a reader cannot tell "nothing changed" without copying the log'
        $bad66 = $true
    }
    # Derived from what a method actually writes, not from a list of method names that would drift: anything that
    # replaces a channel list, appends through Ring, or clears the lot must bump, or readers cache stale rows for ever.
    foreach ($m66 in [regex]::Split($c66, '(?m)^\s{8}(?:public|internal|private)\s+static\s')) {
        if ($m66 -match '^List<ChatMessage> Ring\(') { continue }   # the helper itself; its callers carry the bump
        $writes66 = $m66 -match 'Ring\(' -or $m66 -match '_byChannel\[[^\]]+\]\s*=' -or $m66 -match '_byChannel\.Clear\('
        if ($writes66 -and $m66 -notmatch '_generation\+\+') {
            $name66 = ([regex]::Match($m66, '^[\w<>,\[\] ]+?(\w+)\s*\(')).Groups[1].Value
            Bad "ChatCache.$name66 changes the log without bumping the generation - readers will cache stale rows"
            $bad66 = $true
        }
    }
}
$panel66 = Join-Path $clientSrc 'Features\Chat\ChatPanel.cs'
if ((Test-Path $panel66)) {
    $p66 = [System.IO.File]::ReadAllText($panel66)
    $draw66 = [regex]::Match($p66, 'void DrawLog\(Rect box\)[\s\S]{0,6000}?\r?\n        \}')
    if (-not $draw66.Success) { Bad 'DrawLog could not be read out of ChatPanel'; $bad66 = $true }
    elseif ($draw66.Value -notmatch 'ChatCache\.Generation') {
        Bad 'DrawLog measures without consulting the chat generation - it lays out every line on every frame'
        $bad66 = $true
    }
}
if (-not $bad66) { Good 'a broadcast encodes once, the road list is shared, and a chat log is laid out only when it changes' }

Section "67. Per-frame work cannot take the frame down with it"
# Root.Update is where KMH pumps the main thread, the image queue, video and markers. An unguarded step that throws
# stops every step after it AND reports itself sixty times a second, which is what a Root-level exception costs.
$bad67 = $false
$frameHelper67 = Join-Path $clientSrc 'Diagnostics\KmhFrameSteps.cs'
if (-not (Test-Path $frameHelper67)) { Bad 'KmhFrameSteps.cs is missing - nothing guards the per-frame steps'; $bad67 = $true }
elseif ([System.IO.File]::ReadAllText($frameHelper67) -notmatch 'catch \(Exception') {
    Bad 'KmhFrameSteps does not catch, so one step still takes the rest of the frame with it'; $bad67 = $true
}
$updatePatches67 = @(Get-ChildItem (Join-Path $clientSrc 'Patches') -Filter 'Patch_Root_Update_*.cs' -ErrorAction SilentlyContinue)
if ($updatePatches67.Count -eq 0) { Bad 'no Root.Update patch found where one is expected'; $bad67 = $true }
foreach ($p67 in $updatePatches67) {
    $t67 = [System.IO.File]::ReadAllText($p67.FullName)
    $body67 = [regex]::Match($t67, 'static void Postfix\([\s\S]{0,900}?\r?\n        \}')
    if ($body67.Success) { $line67 = $body67.Value }
    else { $line67 = [regex]::Match($t67, 'static void Postfix\(\)[^\r\n]*').Value }
    if ($line67 -notmatch 'KmhFrameSteps\.Run') {
        Bad "$($p67.Name) runs per-frame work without KmhFrameSteps - one throw stops the rest"; $bad67 = $true
    }
}
if (-not $bad67) { Good "all $($updatePatches67.Count) Root.Update postfix(es) run their steps guarded and report a fault once" }

Section '68. Asking the engine where to deliver never throws'
# Find.AnyPlayerHomeMap goes Game -> Faction.OfPlayerSilentFail -> FactionManager -> World. Mid-load the Game exists
# and the World does not, so the getter NREs instead of returning null. A replayed delivery arrives in exactly that
# window: the throw escaped the post-long-event action, so nothing was held and nothing was acked, and the server
# replayed the same three deliveries on every join for ever. Every delivery-path lookup goes through a guarded helper.
$bad68 = $false
$goods68 = Join-Path $clientSrc 'UI\ColonyGoods.cs'
if (-not (Test-Path $goods68)) { Bad 'ColonyGoods.cs is missing'; $bad68 = $true }
else {
    $g68 = [System.IO.File]::ReadAllText($goods68)
    foreach ($helper68 in @('DeliveryMap', 'DepositMap')) {
        $body68 = [regex]::Match($g68, "Map $helper68\(\)[\s\S]{0,700}?\r?\n        \}")
        if (-not $body68.Success) { Bad "$helper68 could not be read out of ColonyGoods"; $bad68 = $true; continue }
        if ($body68.Value -notmatch 'Current\.Game\?\.World') {
            Bad "$helper68 does not check for a World - mid-load the map getter throws rather than returning null"
            $bad68 = $true
        }
        if ($body68.Value -notmatch 'catch') {
            Bad "$helper68 has no backstop catch, and the World check alone is not the whole of that window"
            $bad68 = $true
        }
    }
    # Only the two helpers may ask the engine directly - three lines, all inside them. A fourth is a new caller
    # that skipped the guard, which is all it takes to bring the replay loop back.
    $stray68 = @([regex]::Matches($g68, '(?m)^(?!\s*//).*Find\.(AnyPlayerHomeMap|CurrentMap).*$')).Count
    if ($stray68 -ne 3) {
        Bad "$stray68 direct map lookups in ColonyGoods, expected 3 - only the two guarded helpers may ask the engine"
        $bad68 = $true
    }
}
# Arguments evaluate left to right, so Decide(Deliver(...), CanDeliverNow()) BUILDS the goods before asking whether
# there is anywhere to put them - and a map appearing between the two reads as Undeliverable, which never retries.
$handler68 = Join-Path $clientSrc 'Features\Treasury\TreasuryHandler.cs'
$g68b = if (Test-Path $handler68) { [System.IO.File]::ReadAllText($handler68) } else { '' }
$decide68 = @([regex]::Matches($g68b, 'Decide\(\s*(?!canDeliver\s*&&)'))
if ($decide68.Count -gt 0) {
    Bad "$($decide68.Count) grant decision(s) attempt delivery before asking whether it can be delivered"
    $bad68 = $true
}
$receipts68 = Join-Path $clientSrc 'Features\Delivery\GameComponent_KMHDeliveryReceipts.cs'
if ((Test-Path $receipts68) -and ([System.IO.File]::ReadAllText($receipts68) -notmatch 'RecordDelivered')) {
    Bad 'no delivered-only record path - acking on arrival discharges goods that never landed'; $bad68 = $true
}
# The held copy must carry the id, or a grant that lands late can never be acknowledged and the server replays for ever.
$pending68 = Join-Path $clientSrc 'Features\Delivery\KmhPendingDelivery.cs'
if ((Test-Path $pending68)) {
    $p68b = [System.IO.File]::ReadAllText($pending68)
    if ($p68b -notmatch 'DeliveryId') { Bad 'a held delivery carries no id, so landing late can never be acked'; $bad68 = $true }
    if ($p68b -notmatch 'RecordDelivered') { Bad 'the held-delivery pump never records what it landed'; $bad68 = $true }
}
if (-not $bad68) {
    Good 'delivery map lookups are guarded, so a replay during a load is held instead of throwing'
    Good 'a grant asks before it builds, records only what landed, and a held one keeps its id'
}

Section '69. A new server generates every state file it says it needs'
# A store added without a seed line leaves a healthy new server reporting its own file as missing for ever.
$paths69 = Get-Content (Join-Path $serverSrc 'Persistence\KmhDataPaths.cs') -Raw
$main69  = Get-Content (Join-Path $serverSrc 'Main.cs') -Raw
$bad69   = $false
$seeded69 = 0
$declared69 = [regex]::Matches($paths69, 'new\s+DataFile\(\s*"([^"]+)"\s*,\s*(\w+)\s*,\s*(true|false)\s*(?:,\s*(true|false)\s*)?\)')
foreach ($m69 in $declared69) {
    $label69 = $m69.Groups[1].Value
    $prop69  = $m69.Groups[2].Value
    # Owner configs are seeded by EnsureGenerated instead, which section 44 already pins.
    if ($label69.StartsWith('Config/')) { continue }
    if ($m69.Groups[4].Success -and $m69.Groups[4].Value -eq 'true') { continue }
    if ($main69 -notmatch ('EnsureFile\(\s*Persistence\.KmhDataPaths\.' + [regex]::Escape($prop69) + '\b')) {
        Bad "$label69 is required but never seeded - add EnsureFile(Persistence.KmhDataPaths.$prop69, ...) to Main.cs, or declare it absentIsNormal"
        $bad69 = $true
        continue
    }
    $seeded69++
}
# The first boot has nothing to have lost, so the scan must not read its own empty folder back as damage.
$integrity69 = Join-Path $serverSrc 'Persistence\KmhDataIntegrity.cs'
if (Test-Path $integrity69) {
    $i69 = [System.IO.File]::ReadAllText($integrity69)
    if ($i69 -notmatch 'KmhDataMeta\.IsFreshInstall') {
        Bad 'the boot integrity report does not special-case a fresh install - a new owner is told files are missing'
        $bad69 = $true
    }
}
if (-not $bad69) { Good "$seeded69 required state file(s) are materialized at boot, and a fresh install is reported as new rather than damaged" }

Section 'RESULT'
if ($fail) { Write-Host '  CONTRACT BROKEN - fix the FAILs above before shipping.' -ForegroundColor Red; exit 1 }
Write-Host '  Contract holds.' -ForegroundColor Green
exit 0
