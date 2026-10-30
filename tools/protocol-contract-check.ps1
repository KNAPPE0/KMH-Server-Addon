<#
.SYNOPSIS
  KMH protocol contract check: catches silent client/server/Global drift before live testing.

.DESCRIPTION
  Compares KMH-Server-Addon, KMH-Patch and (when present) KMH-Global: wire kinds, handler coverage, DTO names,
  mirrored rules and the numbered invariants below. Read-only. Exits 1 on any FAIL, 0 when the contract holds.

.PARAMETER ServerRoot
  KMH-Server-Addon repo root. Defaults to the parent of this script's tools/ folder.

.PARAMETER ClientRoot
  KMH-Patch repo root. Defaults to a sibling KMH-Patch next to the server repo.

.PARAMETER WithoutGlobal
  Skips the KMH-Global sections when it is absent (public checkout); without it, a missing KMH-Global fails them.
#>
param(
    [string]$ServerRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ClientRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'KMH-Patch'),
    [switch]$WithoutGlobal
)

$ErrorActionPreference = 'Stop'
$fail = $false
function Section($t) { Write-Host ''; Write-Host "=== $t ===" -ForegroundColor Cyan }
function Bad($t)     { Write-Host "  FAIL: $t" -ForegroundColor Red;    $script:fail = $true }
function Warn($t)    { Write-Host "  warn: $t" -ForegroundColor Yellow }
function Good($t)    { Write-Host "  ok:   $t" -ForegroundColor Green }
function Note($t)    { Write-Host "  note: $t" -ForegroundColor DarkGray }
# A check that needs KMH-Global and cannot find it did not run, which only -WithoutGlobal makes acceptable.
function NoGlobal($t) {
    if ($WithoutGlobal) { Note "KMH-Global is not checked out beside this repo; $t" }
    else { Bad "KMH-Global is not beside this repo, so $t (pass -WithoutGlobal on a public checkout)" }
}

# Use for every pattern check: a scan that matches comments flags the file documenting a rule as breaking it.
function Code($path) {
    if (-not (Test-Path $path)) { return '' }
    return ([System.IO.File]::ReadAllText($path) -replace '(?s)/\*.*?\*/', '' -replace '(?m)//.*$', '')
}

$serverSrc = Join-Path $ServerRoot 'Source'
$clientSrc = Join-Path $ClientRoot 'Source'
$globalRoot = Join-Path (Split-Path -Parent $ServerRoot) 'KMH-Global'
foreach ($p in @($serverSrc, $clientSrc)) {
    if (-not (Test-Path $p)) { Write-Host "Source not found: $p" -ForegroundColor Red; exit 2 }
}

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

# Direction comes from the server KmhProtocol.cs, the source of truth.
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
    if (-not $srvRefs.Contains($n)) { Warn "$n is defined but never referenced in the server" }
    if ($cliKinds.ContainsKey($n) -and -not $cliRefs.Contains($n)) { Warn "$n is defined but never referenced in the client" }
}
if (-not $fail) { Good 'every directional kind has a handler on the receiving side' }

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

# A stale alias silently drops its pair from comparison, so an alias whose two ends do not both exist is a FAIL.
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

# Anonymous payloads have no DTO, and a name typo leaves the client reading defaults forever. Heuristic, so warn-only.
Section '4. Anonymous payload field drift (heuristic)'

function Get-Names([string]$root, [string]$pattern, [int]$group = 1) {
    $set = New-Object System.Collections.Generic.HashSet[string]
    Get-ChildItem -Path $root -Recurse -Filter *.cs | ForEach-Object {
        foreach ($mm in [regex]::Matches((Get-Content $_.FullName -Raw), $pattern)) { [void]$set.Add($mm.Groups[$group].Value) }
    }
    return ,$set   # comma stops PowerShell unrolling the set into an array
}

# Any `name =` the server writes; deliberately permissive, as over-collecting only suppresses false warnings.
$srvEmits = Get-Names $serverSrc '\[JsonProperty\(\s*"([^"]+)"'
foreach ($n in (Get-Names $serverSrc '([a-z][a-z0-9_]*)\s*=[^=]')) { [void]$srvEmits.Add($n) }

# Client anonymous members are excluded: those are client->server sends, the other direction.
$cliReads = Get-Names $clientSrc '\[JsonProperty\(\s*"([^"]+)"'
foreach ($n in (Get-Names $clientSrc 'Get(?:Int|String|Bool|IntArray)\(\s*"([^"]+)"')) { [void]$cliReads.Add($n) }

# Only wire-shaped names (contain an underscore) are checked; single words are too noisy to distinguish from locals.
$missing = @($cliReads | Where-Object { $_ -match '_' -and -not $srvEmits.Contains($_) }) | Sort-Object
if ($missing.Count) {
    Warn "client expects field(s) the server never emits (verify by hand): $($missing -join ', ')"
} else {
    Good 'every underscore-shaped field the client reads is emitted somewhere on the server'
}

# The repos share no code, so hand-mirrored rules drift silently: DM ids stop matching, escrow states disagree.
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

# Id/key-producing bodies are compared outright (token presence let different ids pass). Predicates like TryParseDm
# are not: they agree in behaviour while differing in expression, and a noisy guard gets ignored.
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
    @{ A = (Join-Path $serverSrc 'SubProtocol\KmhFrameCodec.cs'); B = (Join-Path $clientSrc 'SubProtocol\KmhFrameCodec.cs')
       Sig = 'public static byte[] Frame(byte[] body, bool compressed)'
       Why = 'one side would write a length prefix the other reads as a different length, or as a bomb' }
    @{ A = (Join-Path $serverSrc 'SubProtocol\KmhFrameCodec.cs'); B = (Join-Path $clientSrc 'SubProtocol\KmhFrameCodec.cs')
       Sig = 'public static void ReadHeader(byte[] len4, out int bytes, out bool compressed)'
       Why = 'a compressed frame would be handed to the JSON parser as raw bytes and drop the receive loop' }
    @{ A = (Join-Path $serverSrc 'SubProtocol\KmhFrameCodec.cs'); B = (Join-Path $clientSrc 'SubProtocol\KmhFrameCodec.cs')
       Sig = 'public static byte[] Inflate(byte[] packed, int maxBytes)'
       Why = 'the decompression-bomb cap would exist on only one side of the link' }
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

# The notice queue and Player Mail (escrowed value) are different systems: guard the names, not each mislabelled site.
Section '6. Notifications / Player Mail naming separation'

$nameBad = $false

# Only DECLARED names count: code that merely CALLS the other system is legitimate.
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

# Located by where its types live, not a fixed folder: a renamed folder would silently degrade to "skipped".
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

# ClearAll is a hand list with no headless test; any cache raising `event Action Updated` holds one server's data.
Section '7. Client cache clear-on-server-switch coverage'

# Reset by a different path on purpose; each reason is verified below.
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
    foreach ($k in $cacheExempt.Keys) {
        if (-not ($cacheFiles | Where-Object { $_.BaseName -eq $k })) {
            Bad "cache exemption '$k' names a cache that no longer exists"
            $cacheBad = $true
        }
    }
    if (-not $cacheBad) { Good "all $($cacheFiles.Count) server-data caches are cleared on server switch (or exempt with a verified reason)" }
}

# A cache never forwarded on KmhClientEventBus is invisible to every client extension.
Section '8. Client SDK event-bus coverage'

# Not forwarded on purpose, each with a reason; a stale entry fails below.
$busExempt = @{
    'ColonistProfileCache' = 'per-colonist detail fetched on demand, not ambient player state (PlayerStatsCacheUpdated covers the roster signal)'
    'ColonistRosterCache'  = 'same family as ColonistProfileCache; roster changes surface via PlayerStatsCacheUpdated'
    'SiteCatalogCache'     = 'UI picker catalog of offerable defs - server config, not player state'
    'EnforcementCache'     = 'config-enforcement posture, not player data; extensions read EnforcementCache directly'
    'ServerConfigCache'    = 'owner-only server settings behind a console-issued grant - not ambient player state, and forwarding it would hand every loaded extension the whole server configuration'
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

# IsEnabled ends in `default: return true`, so a typo'd key fails OPEN: a disabled feature keeps serving, unlogged.
# IsEnabled, FeatureForKind and the client dashboard rows must therefore share one vocabulary.
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

# ComputeLeaderboard nests Guild -> Treasury; TreasuryStore calling GuildStore under its lock is an AB-BA deadlock.
Section '10. Treasury/Guild lock order'

$tsFile = Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs'
if (-not (Test-Path $tsFile)) {
    Warn 'TreasuryStore.cs not found - verify lock order by hand'
} else {
    # `lock (_lock)` and `{` are on separate lines, so the block opens only once depth exceeds the lock line's depth.
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

# MaxWorkers and OwnerGuild are frozen at creation; decisions must use MaxWorkersLive / OwnerCurrentGuild instead.
Section '11. Site guild-derived values are live'

$siteFile = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
if (-not (Test-Path $siteFile)) {
    Warn 'SiteStore.cs not found - verify the derived site values by hand'
} else {
    $siteBad = $false
    foreach ($helper in @('MaxWorkersLive', 'OwnerCurrentGuild')) {
        if (-not (Select-String -Path $siteFile -Pattern $helper -Quiet)) { Bad "SiteStore.$helper is gone - that site value is no longer derived"; $siteBad = $true }
    }
    # Only reads are the defect: assigning a field from its own live helper is allowed.
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

# EnsureBuiltins is a hand list; an unregistered IKmhWealthSource lets wealth parked there dodge raid scaling.
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

# Amounts are client-claimed and caps treat 0 as unlimited, so a raw += can wrap an int balance or count NEGATIVE.
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

# The offline runner finds suites by reflection; kmh smoketest/selftest run a hand list in KmhSmokeTest.cs.
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
        # `X.Run(` not `X.Run()`: the discovery suite takes (log, out summary).
        if ($smokeText -notmatch ([regex]::Escape($s) + '\.Run\(')) { $unwired += $s }
    }
    foreach ($s in $unwired) { Bad "$s is never run by kmh smoketest or kmh selftest - it passes offline and is absent in-game" }
    if ($suites.Count -eq 0) { Warn 'no SelfTest suites found - check the scan pattern' }
    elseif ($unwired.Count -eq 0) { Good "$($suites.Count) self-test suite(s) all wired into kmh smoketest / kmh selftest" }
}

# A raw NUL makes grep skip the whole file as binary, hiding it from every sweep; write it as the \0 or \u0000 escape.
# Every tree, no exemptions; a scanned root that does not exist is a FAILURE, not a quiet pass.
$ctrlRoots = @(
    @{ Name = 'KMH-Patch';          Path = $clientSrc },
    @{ Name = 'KMH-Server-Addon';   Path = $serverSrc },
    @{ Name = 'KMH-Global src';     Path = (Join-Path $globalRoot 'src') },
    @{ Name = 'KMH-Global tests';   Path = (Join-Path $globalRoot 'tests') }
)

Section '15. Source files are searchable (no raw NUL bytes)'
$nulFiles = @()
$nulScanned = 0
$nulTrees = 0
foreach ($r in $ctrlRoots) {
    if (-not (Test-Path -LiteralPath $r.Path)) {
        if ($r.Name -like 'KMH-Global*' -and -not (Test-Path -LiteralPath $globalRoot)) { NoGlobal "$($r.Name) was not scanned for NUL bytes" }
        else { Bad "cannot scan $($r.Name) for NUL bytes: $($r.Path) not found" }
        continue
    }
    $nulTrees++
    Get-ChildItem -LiteralPath $r.Path -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $nulScanned++
            if ([Array]::IndexOf([System.IO.File]::ReadAllBytes($_.FullName), [byte]0) -ge 0) { $nulFiles += $_.FullName }
        }
}
foreach ($f in $nulFiles) {
    Bad "$f contains a raw NUL byte - grep skips it as binary. Write the character as \u0000 instead."
}
if ($nulFiles.Count -eq 0) { Good "no raw NUL bytes in $nulScanned file(s) across $nulTrees tree(s) - all searchable" }

# A raw control byte is invisible in an editor: a heredoc can turn regex \b into a backspace that never matches.
Section '15a. Source carries no stray control characters'
$ctrl15a = @(0x00, 0x07, 0x08, 0x0B, 0x0C, 0x1A, 0x1B)
$hits15a = @()
$scanned15a = 0
foreach ($r in $ctrlRoots) {
    if (-not (Test-Path -LiteralPath $r.Path)) {
        if ($r.Name -like 'KMH-Global*' -and -not (Test-Path -LiteralPath $globalRoot)) { NoGlobal "$($r.Name) was not scanned for control characters" }
        else { Bad "cannot scan $($r.Name) for control characters: $($r.Path) not found" }
        continue
    }
    foreach ($f15a in Get-ChildItem -LiteralPath $r.Path -Filter *.cs -Recurse -File) {
        if ($f15a.FullName -match '\\(bin|obj)\\') { continue }
        $scanned15a++
        $bytes15a = [System.IO.File]::ReadAllBytes($f15a.FullName)
        foreach ($b15a in $ctrl15a) {
            if ($bytes15a -contains $b15a) { $hits15a += ("{0}: 0x{1:X2}" -f $f15a.Name, $b15a) }
        }
    }
}
if ($hits15a.Count -gt 0) { Bad ("stray control character(s) in source: " + ($hits15a -join ', ')); }
else { Good "no stray control characters in $scanned15a file(s) - write them as \uXXXX escapes" }

# A re-derived split rule can diverge and charge a buyer for goods sent back to the seller; every split decision must
# route through KmhPayloadEscrow.IsSplittable (usually via KmhItemService.PlanTake).
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

# A lookup via get-or-create persists an empty row per name read; only the action (a deposit) may create a row.
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

# HasAny (does a reset run?) and BurnAll must name the same stores, or a reset skips real value or fires for nothing.
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

# A token spelled differently on one side reads as "the server cannot", so the feature silently never appears.
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
    # Only tokens in the manifest list reach the wire; a declared-but-unlisted one never arrives.
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
    # Roadworks sends funnel through one gated helper; a second Send reaches servers with no roadworks handler.
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

# Section 3 skips DTO string constants (modes, states); a drifted one never matches, so the UI silently misbehaves.
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

# An undispatched subcommand still appears and autocompletes in Discord, then answers "Unknown command".
Section '21. Discord subcommands are all dispatched'
# A path is answered by a `case "path":` or a `path == "path"` anywhere under Features\Discord.
$tree21 = Code (Join-Path $serverSrc 'Features\Discord\Contract\KmhSlashTree.cs')
if (-not $tree21) { Bad 'KmhSlashTree.cs not found - the /kmh command tree moved, so check 21 cannot see it' }
else {
    $groups21 = @{}
    foreach ($m in [regex]::Matches($tree21, 'SlashCommandOptionBuilder\s+(\w+)\s*=\s*Group\(\s*"([^"]+)"')) { $groups21[$m.Groups[1].Value] = $m.Groups[2].Value }
    $paths21 = @(foreach ($m in [regex]::Matches($tree21, '(\w+)\.AddOption\(\s*(?:Routed\(\s*)?Sub\(\s*"([^"]+)"')) {
        $g21 = $groups21[$m.Groups[1].Value]
        if ($g21) { "$g21.$($m.Groups[2].Value)" } else { $m.Groups[2].Value }
    })
    $handlers21 = (Get-ChildItem (Join-Path $serverSrc 'Features\Discord') -Recurse -Filter '*.cs' |
        Where-Object { $_.Name -ne 'KmhSlashTree.cs' -and $_.Name -notmatch 'SelfTest' } | ForEach-Object { Code $_.FullName }) -join "`n"
    $undispatched = @($paths21 | Where-Object { $handlers21 -notmatch ('(case\s+|path\s*==\s*)"' + [regex]::Escape($_) + '"') })
    foreach ($u in $undispatched) { Bad "Discord subcommand '/kmh $($u -replace '\.', ' ')' is declared but never dispatched - it would answer 'Unknown command'" }
    if ($paths21.Count -eq 0) { Bad 'no Discord subcommands found in KmhSlashTree.cs - check 21 would verify nothing' }
    elseif ($undispatched.Count -eq 0) { Good "$($paths21.Count) Discord subcommand(s) all reach a handler" }
}

# A system Outpost's OwnerUsername is empty and matches an empty caller; only SiteOwnership may compare it.
Section '22. Site ownership goes through one authority'
# Comparisons on a different type's OwnerUsername, each with a reason; a stale entry fails below.
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
foreach ($k in $ownExempt.Keys) {
    $f = Get-ChildItem $serverSrc -Recurse -File -Filter $k | Select-Object -First 1
    if (-not $f) { Bad "ownership exemption '$k' names a file that no longer exists"; continue }
    if (-not (Select-String -Path $f.FullName -Pattern 'OwnerUsername\s*(==|!=)|string\.Equals\s*\([^)]*OwnerUsername' -Quiet)) {
        Bad "ownership exemption '$k' is stale - it no longer compares OwnerUsername at all"
    }
}
if ($ownBad.Count -eq 0) { Good "$ownScanned OwnerUsername comparison(s) scanned, all inside SiteOwnership or exempt with a reason" }

# Copies of one loop scaffold get a scheduling bug fixed in one and missed in another; jobs register with KmhScheduler.
Section '23. Background work runs on one scheduler'
# Not scheduled work, each with a reason; a stale entry fails below.
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
foreach ($k in $loopExempt.Keys) {
    $f = Get-ChildItem $serverSrc -Recurse -File -Filter $k | Select-Object -First 1
    if (-not $f) { Bad "scheduler exemption '$k' names a file that no longer exists"; continue }
    $t = [System.IO.File]::ReadAllText($f.FullName)
    if ($t -notmatch 'while\s*\([^)]*\)\s*\{(?:[^{}]|\{[^{}]*\})*Task\.Delay') {
        Bad "scheduler exemption '$k' is stale - it no longer runs its own loop at all"
    }
}
if ($loopBad.Count -eq 0) { Good "no periodic loop outside KmhScheduler ($($loopExempt.Count) exempt with a reason)" }

# Without the dispatch call, Frontier operations still complete and pay out while the world silently never changes.
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

# Section 4's other direction: an unread request key is silently ignored, even on value paths (txn_id, fingerprint).
Section '25. Client request keys are read by the server'

# Wire-shaped keys the client SENDS: dictionary entries { "a_b", x } and anonymous members a_b = x.
$cliSends = Get-Names $clientSrc '\{\s*"([a-z][a-z0-9]*(?:_[a-z0-9]+)+)"\s*,'
foreach ($n in (Get-Names $clientSrc '(?<![\w."])([a-z][a-z0-9]*(?:_[a-z0-9]+)+)\s*=[^=]')) { [void]$cliSends.Add($n) }

$srvReads = Get-Names $serverSrc '\[JsonProperty\(\s*"([^"]+)"'
# Every KmhEnvelope accessor must be listed, or a key read by a missing one raises a false alarm.
foreach ($n in (Get-Names $serverSrc 'Get(?:Int|Long|String|Bool|IntArray|IntMap|StringList)\(\s*"([^"]+)"')) { [void]$srvReads.Add($n) }

# Names the client also declares as its own DTO properties are server->client reads, not sends - section 3 pairs those.
$cliDtoProps = Get-Names $clientSrc '\[JsonProperty\(\s*"([^"]+)"'

$unread = @($cliSends | Where-Object { -not $srvReads.Contains($_) -and -not $cliDtoProps.Contains($_) }) | Sort-Object
if ($unread.Count) {
    Warn "client sends key(s) the server never reads (verify by hand): $($unread -join ', ')"
} else {
    Good "every wire-shaped key the client sends is read somewhere on the server ($($cliSends.Count) checked)"
}

# Readers enumerate _state outside the lock while the network thread mutates it, so hand out copies, never the live object.
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
        if ($line -match '=\s*_state\s*;') {
            Bad "KmhWorldDirector.cs:${lineNo} hands out the live director state - use _state.CopyForRead(): $($line.Trim())"
            $dirBad = $true
        }
    }
    if (-not (Select-String -Path $directorFile -Pattern 'CopyForRead' -Quiet)) {
        Bad 'KmhWorldDirector no longer copies its state for readers'
        $dirBad = $true
    }
    # The persisted cooldown map only grows; an unwired prune helper leaves it growing with every test still green.
    $dirText = [System.IO.File]::ReadAllText($directorFile)
    $pruneCalls = ([regex]::Matches($dirText, 'PruneExpiredCooldowns\s*\(')).Count
    if ($pruneCalls -lt 3) {   # the definition, the place that adds one, and the load path
        Bad "KmhWorldDirector calls PruneExpiredCooldowns $pruneCalls time(s) - the persisted cooldown map is no longer pruned where it grows"
        $dirBad = $true
    }
    if (-not $dirBad) { Good 'director state is copied for readers, and its cooldown map is pruned where it grows' }
}

# Without expiry an unclaimed outpost holds its slot forever, and after MaxOutposts the director never acts again.
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
    # An expiry has no operation; stamping the origin unconditionally would erase which operation created the place.
    if ([System.IO.File]::ReadAllText($siteStoreFile) -notmatch 'if\s*\(\s*operationId\s*>\s*0\s*\)\s*s\.OriginOperationId\s*=') {
        Bad 'TryAdvanceOutpost stamps OriginOperationId unconditionally - a claim-window expiry would erase the origin'
        $expBad = $true
    }
    if (-not $expBad) { Good 'an unclaimed outpost goes dormant, releases its slot, and keeps its origin' }
}

# Only AdvanceProject lays road; without a caller from the site work cycle, projects take escrow and never build.
Section '28. Roadworks work reaches AdvanceProject'

$siteStore28 = Join-Path $serverSrc 'Features\Sites\SiteStore.cs'
$roadStore28 = Join-Path $serverSrc 'Features\Roadworks\RoadworksStore.cs'
if (-not (Test-Path -LiteralPath $siteStore28) -or -not (Test-Path -LiteralPath $roadStore28)) {
    Warn 'SiteStore or RoadworksStore not found - verify Roadworks progression by hand'
} else {
    $rwBad = $false
    $siteText28 = [System.IO.File]::ReadAllText($siteStore28)
    # The cycle's work is owed with its timers and laid by ApplyWorkOnce, which advances the same projects.
    $roadText28 = [System.IO.File]::ReadAllText($roadStore28)
    if ($siteText28 -notmatch 'ApplyWorkOnce\s*\(' -or $roadText28 -notmatch 'ApplyWorkOnce[\s\S]{0,1600}?AdvanceLocked\s*\(') {
        Bad 'the site work cycle never reaches project progress - Roadworks projects would take escrow and never build'
        $rwBad = $true
    }
    if ($siteText28 -notmatch 'RoadworkPerCycle\s*\(') {
        Bad 'nothing computes Roadworks construction work - the archetype would produce goods only'
        $rwBad = $true
    }
    # Labour, not yield. Scoped to the helper's body: the goods path in RunRewardCycle uses OutputFactor legitimately.
    $bodyMatch = [regex]::Match($siteText28, 'internal static double RoadworkPerCycle[\s\S]*?\r?\n        \}')
    if (-not $bodyMatch.Success) {
        Bad 'RoadworkPerCycle not found in the expected shape - verify the Roadworks labour model by hand'
        $rwBad = $true
    }
    elseif ($bodyMatch.Value -match '(?<!SiteStability\.)OutputFactor\s*\(\s*s\s*\)') {
        Bad 'RoadworkPerCycle uses SiteStore.OutputFactor - yield multipliers must not accelerate road construction'
        $rwBad = $true
    }
    if (-not $rwBad) { Good 'site labour reaches project progress, and road speed is not driven by yield multipliers' }
}

# Unwritten stats read as permanent zeros: a counter needs its production caller, a derivation the right identity.
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
    # A helper with tests but no production caller is the most repeated defect here.
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

# Item donations must be recorded like silver, and only into guild vaults: a personal deposit is no contribution.
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

# A capture cannot be rebuilt later (ownership changes, KmhHistory is pruned), so count it in the committed claim only.
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

# DepositSilver refuses an empty owner, so settling a seller-less (malformed) auction would discard the winner's bid;
# auctions refuse it as MarketplaceStore does, and uncreditable silver lands in recovery.
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

    # Uncreditable silver must be held via the one shared primitive, not dropped.
    # Anchored on the signature: a name-prefix match can read the wrong method's body.
    $depBody32 = [regex]::Match($aucText32, 'private static bool DepositLong\s*\([\s\S]*?\r?\n        \}')
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

# Only visible after something broke: a reloaded vault must match item keys like live paths, and an unmovable corrupt
# file must survive the next save without freezing the economy longer than it exists.
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

    # Reconcile acts only on Pending and commit/revert remove the row, so any other resting state is stuck and silent.
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
        # The seal must be able to end: RetryUnwritableStores lifts the freeze only if a later save can pass.
        elseif ($save33.Value -notmatch 'TryRemove') {
            Bad 'the seal never clears - the persistence freeze would outlive the corrupt file it protects'; $bad33 = $true
        }
    }

    # SiteStore keeps the same guarantee itself: a reloaded site whose workers stop matching reads as empty and pauses.
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

# The score uses live-joined fields, so it is derived after the joins, once, on the server; a client copy drifts.
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

    if ($c34 -match 'SilverDonated[\s\S]{0,200}SitesBuilt' -or $d34 -match 'SilverDonated[\s\S]{0,200}SitesBuilt') {
        Bad 'the client recomputes the economy score - the server owns the weighting and ranks the same number on Discord'; $bad34 = $true
    }

    if (-not $bad34) { Good 'the economy score is derived once, on the server, after the live joins' }
}

# Reading mail does not claim its attachment, so inbox-cap eviction must refund escrow like Delete/PruneOld do, and
# silver must go through DeliverSilver: a discarded DepositSilver result burns it.
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

    if ($m35 -match 'TreasuryStore\.DepositSilver') {
        Bad 'mail credits silver with a raw DepositSilver - a refused deposit is discarded instead of held'; $bad35 = $true
    }
    if ($m35 -notmatch 'KmhPayloadEscrow\.DeliverSilver') {
        Bad 'mail never uses DeliverSilver - attachment silver has no recovery path'; $bad35 = $true
    }

    if (-not $bad35) { Good 'an evicted or released mail attachment is refunded, and its silver can never be dropped' }
}

# A refused Deposit* moves nothing, so a discarded result burns value; server payouts go through KmhPayloadEscrow,
# which parks what it cannot credit. Player-initiated deposits stay direct: the refusal reaches the caller.
Section '36. A server-initiated credit is never dropped'

$dropped36 = New-Object System.Collections.Generic.List[string]
foreach ($f36 in Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs -File) {
    $n = 0
    foreach ($line in [System.IO.File]::ReadAllLines($f36.FullName)) {
        $n++
        # A result is discarded only when the call opens the statement; anything consuming it puts other tokens first.
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

# Expiry and an unapplied consequence must also free the operation and outpost slots, or the director stalls for good.
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

    # An unfunded outpost has no quest to finish or expire and holds its slot for good, so funding is decided first.
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
            # The race where the pool drains between that check and the debit.
            elseif ($est37.Value -notmatch 'OutpostDormant') {
                Bad 'a reclaim that could not be funded leaves its location live - nothing will ever advance it'; $bad37 = $true
            }
        }
    }

    if (-not $bad37) { Good 'an operation that never starts, expires, or fails to apply all release their outpost and operation slots' }
}

# Client previews restate server rules; drift is not exploitable, but the UI quietly lies about what an action does.
Section '38. Client previews match the rules the server enforces'

$srvBld38 = Join-Path $serverSrc 'Features\Sites\SiteBuildings.cs'
$cliBld38 = Join-Path $clientSrc 'Features\Sites\Dialog_KMHSiteBuildings.cs'
if (-not (Test-Path -LiteralPath $srvBld38) -or -not (Test-Path -LiteralPath $cliBld38)) {
    Warn 'SiteBuildings or the client buildings dialog not found - verify the slot/storage mirror by hand'
} else {
    $s38 = [System.IO.File]::ReadAllText($srvBld38)
    $c38 = [System.IO.File]::ReadAllText($cliBld38)
    $bad38 = $false

    # The client may read the figures the server sends (preferred) or restate the same literals; its own numbers fail.
    $perStorage = [regex]::Match($s38, 'UnitsPerStorage\s*=\s*(\d+)')
    $maxStorage = [regex]::Match($s38, 'MaxStorageUnits\s*=\s*(\d+)')
    $cliStorage = [regex]::Match($c38, 'private static int StorageCapacity\([\s\S]*?;')
    if (-not $perStorage.Success -or -not $maxStorage.Success -or -not $cliStorage.Success) {
        Warn 'storage constants not found in the expected shape - verify the mirror by hand'
    }
    elseif ($cliStorage.Value -match 'StoragePerBuilding' -and $cliStorage.Value -match 'MaxStorageUnits') {
        # Read from the snapshot, so the server must fill those fields from its own constants.
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

    # A drift tripwire, not a proof: it catches a changed slot number, not a rearranged tier boundary.
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

# Extensions only hold IKmhEvents, so a bus event missing from the interface fires into nothing.
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

# A record field no adapter sets reads as a silent zero; assignments are unioned, since partial builds are deliberate.
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

# The SDK promises main-thread handlers; dispatching from a network thread touches Verse mid-draw and corrupts silently.
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

# Only this wiring keeps a want's material/quality real on the compact path: without it the buyer pays for goods they
# excluded, while the pure matcher's tests still pass.
Section "41. A want's requirements reach the compact withdraw"

$wantStore41 = Join-Path $serverSrc 'Features\WantBoard\WantStore.cs'
$treasury41  = Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs'
if (-not (Test-Path -LiteralPath $wantStore41) -or -not (Test-Path -LiteralPath $treasury41)) {
    Warn 'WantStore or TreasuryStore not found - verify want requirement wiring by hand'
} else {
    $bad41 = $false
    $wantText41 = [System.IO.File]::ReadAllText($wantStore41)
    $treaText41 = [System.IO.File]::ReadAllText($treasury41)

    # The compact plan must carry the want's own values, not literals; the take then withdraws exactly that plan.
    $call41 = [regex]::Match($wantText41, 'PlanMatching\(\s*seller,[\s\S]{0,240}?\);')
    if (-not $call41.Success) {
        Bad 'WantStore no longer plans its compact take with PlanMatching in the expected shape - verify by hand'
        $bad41 = $true
    } else {
        if ($call41.Value -notmatch '\bMinQuality\b')    { Bad "the want's quality floor is not passed to the compact withdraw - it would be ignored";  $bad41 = $true }
        if ($call41.Value -notmatch '\bRequiredStuff\b') { Bad "the want's material requirement is not passed to the compact withdraw - it would be ignored"; $bad41 = $true }
    }
    if ($wantText41 -notmatch 'WithdrawItemsForTxn\(\s*seller,\s*plan,\s*tx\.TakeMarker') {
        Bad 'WantStore no longer takes the planned stacks on the fill row''s marker - a crash between take and row loses them'; $bad41 = $true
    }

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

# The self-test proves only the in-memory shape; the store must also be flushed, loaded at boot and wiped explicitly.
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
    # Retention applies on load too, or a server down for days resurrects messages past the retention window.
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

    # No periodic FlushAll exists, so chat (highest churn) needs its own drain or a crash loses every message since boot.
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

# RimWorld draws the dot (<texture>) and zoomed-out icon (<expandingIconTexture>) from different images, paired like
# vanilla's X and Expanding/X; markers need KMH world art, as coloured UI glyphs defeat the state tint.

$defDir43 = Join-Path $ClientRoot '1.6\Defs\WorldObjectDefs'
$markerArt43 = Join-Path $clientSrc 'Features\Sites\KmhMarkerArt.cs'
if (-not (Test-Path -LiteralPath $defDir43)) {
    Warn 'no WorldObjectDefs folder - verify KMH world marker art by hand'
} else {
    $bad43 = $false

    # Windows paths ignore case but Linux loses the texture on a mismatch, so compare each segment exactly.
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
        # Each slot must name its own prefix constant, or one quietly points at the other; -cmatch skips _expandingPaths.
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
        # ContentFinder misses until mod content loads, so a cached first miss would stick for the session, unlogged.
        if ($art43 -notmatch '_misses\[path\]\s*=\s*tried \+ 1') {
            Bad 'KmhMarkerArt caches a failed texture lookup instead of retrying it'; $bad43 = $true
        }

        # Every archetype needs its own art in BOTH zoom slots, or it draws the Generic dot up close.
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
            # Cropped to the alpha bounding box first: the images are inset differently, so raw pixels always differ.
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

    # PNG only: RimWorld's DDS loader reads rows bottom-up, so a .dds must be stored flipped or it renders upside down.
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

# A config written only when missing never gains the fields a later version adds, so regenerable ones are topped up.
Section '44. Owner configs are seeded at boot, topped up on upgrade, and inspectable'

$paths44   = Get-Content (Join-Path $serverSrc 'Persistence\KmhDataPaths.cs') -Raw
$main44    = Get-Content (Join-Path $serverSrc 'Main.cs') -Raw
# The one canonical config list: Main's backfill loops over it, so being absent here means being skipped.
$targets44 = Get-Content (Join-Path $serverSrc 'Maintenance\KmhConfigValidation.cs') -Raw
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

    # Upgrade path: Main's backfill loops over KmhConfigValidation.ConfigTargets(), so this config must be one of them.
    $ownerSrc44   = Get-Content $ownerFile44.FullName -Raw
    $loops44      = ($main44 -match 'KmhConfigValidation\.ConfigTargets\(\)') -and ($main44 -match 'BackfillMissingFields')
    $backfilled44 = $loops44 -and ($targets44 -match ('KmhDataPaths\.' + [regex]::Escape($prop44) + '\b'))
    $selfTops44   = $ownerSrc44 -match 'ToJson\('
    if (-not ($backfilled44 -or $selfTops44)) {
        Bad "Config/$label44 is not in KmhConfigValidation.ConfigTargets(), so the boot backfill never reaches it"
        $bad44 = $true
        continue
    }
    # The `kmh config` area list is hand-maintained and drifts as configs are added.
    $area44 = $label44.ToLowerInvariant()
    if ($inspect44 -notmatch ('\(\s*"' + [regex]::Escape($area44) + '"\s*,')) {
        Bad "Config/$label44 has no 'kmh config $area44' area - add it to KmhConfigInspect.Areas"
        $bad44 = $true
        continue
    }
    $seeded44++
}

if (-not $bad44) { Good "$seeded44 regenerable owner config(s) are seeded at boot, gain new fields on upgrade, and are inspectable" }


# The hello is the only state before first draw; a field the client never reads is a setting that silently does nothing.
Section '45. Every hello field the server sends is read by the client'

$srvHello45 = Get-Content (Join-Path $serverSrc 'SubProtocol\KmhHandshakeHandler.cs') -Raw
$cliHello45 = Get-Content (Join-Path $clientSrc 'SubProtocol\KmhHandshakeHandler.cs') -Raw
$cliHello45 += Get-Content (Join-Path $clientSrc 'Features\Enforcement\EnforcementAdmission.cs') -Raw

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



# RimWorld's camera eats the scroll wheel first, so a non-absorbing window must hold camera motion while hovered;
# Window_KMHBase derives that from absorbInputAroundWindow, and setting preventCameraMotion by hand opts out.
Section '46. Floating windows keep the scroll wheel'

$base46 = Get-Content (Join-Path $clientSrc 'UI\Window_KMHBase.cs') -Raw
$bad46 = $false

if ($base46 -notmatch 'HoldCameraWhileHovered' -or $base46 -notmatch 'GetWindowAt') {
    Bad 'Window_KMHBase no longer holds camera motion while hovered - floating windows cannot scroll'
    $bad46 = $true
}
# The call inside WindowUpdate's body: matching the name anywhere also matched its declaration.
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


# A value the server accepts but no client dialog offers is a feature that silently does not exist.
Section '47. Server-accepted values are reachable from the client UI'

$bad47 = $false
$cliUiText = ''
foreach ($f47 in Get-ChildItem $clientSrc -Recurse -Filter 'Dialog_*.cs') {
    $cliUiText += [System.IO.File]::ReadAllText($f47.FullName) + "`n"
}

# Parsed from the server's normalizer, so a new value joins the check for free.
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

    # An offer, not a mention: == and != comparisons prove nothing about whether a menu can set the value.
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

# A withdrawal granted on a failed save is minted on restart: value paths commit via CommitLocked (mutate, save,
# restore the vault if the write failed), and SaveToDisk must keep reporting the write.
Section '48. A value move is not acknowledged before it is durable'

# Every store holding player value or escrow; a void SaveToDisk hides a failed write.
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

    # Named, not inferred: a heuristic would quietly stop covering the next value path added.
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
        $depth = 0; $seen = $false; $body = ''
        for ($i = $start; $i -lt $tLines.Count; $i++) {
            $body += $tLines[$i] + "`n"
            $depth += ([regex]::Matches($tLines[$i], '\{')).Count
            $depth -= ([regex]::Matches($tLines[$i], '\}')).Count
            if ($depth -gt 0) { $seen = $true }
            if ($seen -and $depth -le 0) { break }
        }
        # The result must be acted on: a discarded CommitLocked is as silent as a void save, so only !CommitLocked counts.
        if ($body -notmatch '!CommitLocked\(') { $uncommitted += $m }
    }
    if ($uncommitted.Count -gt 0) {
        Bad ('these Treasury value paths do not refuse on a failed commit: ' + ($uncommitted -join ', '))
    } else { Good "all $($valuePaths.Count) Treasury value path(s) refuse when the write fails" }
}

# RWT serializes sends through EnqueuePacket's queue; a direct Stream.Write would interleave bytes with RWT's traffic.
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

# Client reports (wealth, pawn skill, market value, "delivered") are claims; letting one size a reward, site output,
# a price or quest progress lets a modified client mint value.
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

    if ($engine -match 'ComputeAutoReward[\s\S]{0,600}?TotalReportedWealth') {
        Bad 'ComputeAutoReward reads client-reported colony wealth - one client can pin every generated reward at the cap'
    } else { Good 'a generated reward is sized from server-owned inputs only' }

    if ($engine -notmatch 'ApplyDelivery[\s\S]{0,2400}?WithdrawItemsForTxn\(user, plan, tx\.TakeMarker') {
        Bad 'ApplyDelivery no longer withdraws from the treasury - a crafted packet credits quest progress with no goods'
    } else { Good 'a global-quest delivery is paid out of the treasury before it credits' }

    if ($engine -notmatch 'IsSelfReportedObjective' -or $engine -notmatch 'PayForSelfReported') {
        Bad 'hunt/build completion no longer checks the self-reported-objective payout gate'
    } else { Good 'a self-reported objective pays only when the owner has opted in' }

    if ($safety -match 'ValidateClientMarketValue') {
        Bad 'ValidateClientMarketValue is back - its no-trusted-value branch returned the client''s own price'
    } elseif ($store -notmatch 'TrustedMarketValueOrZero') {
        Bad 'SiteStore no longer prices from the trusted catalog value'
    } else { Good 'a site is priced from the trusted catalog value or refused' }

    # The router pauses value moves only for client packets during maintenance; extensions reach the stores directly.
    $apisFile = Join-Path $serverSrc 'Extensibility\Apis.cs'
    if (Test-Path $apisFile) {
        $apis = [System.IO.File]::ReadAllText($apisFile)
        $ungated = @()
        # Counted, not just asked: a reset or backup waits for an extension's move only if KmhValueOps counts it.
        foreach ($m in @('DepositSilver','WithdrawSilver','DepositItem','WithdrawItem',
                         'DepositGuildSilver','WithdrawGuildSilver',
                         'CreateGuild','AddMember','SetMotd')) {
            if ($apis -notmatch ("public bool $m[\s\S]{0,260}?KmhSdkGate\.(Run|Begin)")) { $ungated += $m }
        }
        # A quest post escrows the poster's vault; ungated, one landing during a wipe survives it as a refundable bounty.
        foreach ($m in @('PostDeliverItem','PostBounty')) {
            if ($apis -notmatch ("public long $m\([\s\S]{0,400}?KmhSdkGate\.(Run|Begin)")) { $ungated += $m }
        }
        # A world quest reserves house-pool silver and ending one returns it, so both are value moves.
        foreach ($m in @('FireEvent','EndEvent','CreateQuest','EndQuest')) {
            if ($apis -notmatch ("public bool $m\([\s\S]{0,400}?(KmhSdkGate\.(Run|Begin)|Gated\()")) { $ungated += $m }
        }
        if ($apis -match 'Gated\(' -and $apis -notmatch 'private static bool Gated\([\s\S]{0,300}?KmhSdkGate\.Begin') {
            $ungated += 'Gated no longer goes through KmhSdkGate'
        }
        if ($apis -notmatch 'KmhValueOps\.Begin\(Maintenance\.KmhIngress\.Sdk') {
            $ungated += 'KmhSdkGate no longer counts and admits through KmhValueOps'
        }
        if ($ungated.Count -gt 0) { Bad ('these SDK value moves bypass the maintenance gate: ' + ($ungated -join ', ')) }
        else { Good 'an extension cannot move value while maintenance is on' }
    }

    if ($wdto -notmatch 'EarnedLevel') {
        Bad 'WorkerProgressDto.EarnedLevel is gone - client-reported skill is back in the production path'
    } elseif ($store -match 'total \+= wp\.CurrentLevel' -or $store -match 'wp\.CurrentLevel \* RoadworkWorkPerSkillLevel') {
        Bad 'site output or road progress reads CurrentLevel, which folds in the client''s claimed pawn skill'
    } else { Good 'site output and road progress count server-earned XP only' }
}

# Replies and pushes cross in flight, so a stale snapshot can put spent silver back on screen: the DTO carries a
# revision on both sides, the server stamps it under its read lock, and the client cache drops older ones.
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

    # Roadworks predates the shared helper and carries its own counter.
    if ((Test-Path $sd) -and ([System.IO.File]::ReadAllText($sd) -notmatch 'JsonProperty\("revision"\)') -and $rf.N -ne 'Mail') {
        $revBad += "$($rf.N): the server snapshot carries no revision"
    }

    # Must be stamped inside a lock: walk back from the stamp to the nearest lock or method start.
    $sTxt = [System.IO.File]::ReadAllLines($ss)
    $stampLine = -1
    for ($i = 0; $i -lt $sTxt.Count; $i++) {
        if ($sTxt[$i] -match 'Revision\s*=\s*(Util\.)?KmhSnapshotRevision\.Next\(\)' -or $sTxt[$i] -match '_revision\+\+') { $stampLine = $i; break }
    }
    if ($stampLine -lt 0) { $revBad += "$($rf.N): nothing stamps a snapshot revision"; continue }
    # Bounded by the enclosing method, not a fixed window: the stamp can sit dozens of lines below its lock.
    $inLock = $false
    for ($j = $stampLine; $j -ge 0; $j--) {
        if ($sTxt[$j] -match '^\s*lock\s*\(') { $inLock = $true; break }
        if ($sTxt[$j] -match '^\s{4,8}(public|private|internal|protected)\s') { break }   # walked out of the method
    }
    if (-not $inLock -and $rf.N -ne 'Roadworks') { $revBad += "$($rf.N): the revision is stamped outside the store lock, so it proves nothing about ordering" }

    $cTxt = [System.IO.File]::ReadAllText($cc)
    $held = if ($rf.H) { [regex]::Escape($rf.H) } else { 'applied' }
    if ($cTxt -notmatch "Revision\s*<\s*$held(\.Revision)?") {
        $revBad += "$($rf.N): the client cache applies a snapshot without comparing its revision"
    }
}
# Federation reads several stores under no one lock, so the stamp must precede the first read to stay ordered.
$fedSrv51 = Join-Path $serverSrc 'Features\Global\Federation\FederationHandler.cs'
$fedCli51 = Join-Path $clientSrc 'Features\Federation\FederationCache.cs'
if (-not (Test-Path $fedSrv51) -or -not (Test-Path $fedCli51)) { $revBad += 'Federation: snapshot source not found' }
else {
    $fb51 = [regex]::Match([System.IO.File]::ReadAllText($fedSrv51), 'object BuildPayload\([^)]*\)\s*\{([\s\S]*?)\n        \}')
    $firstStmt = if ($fb51.Success) { ($fb51.Groups[1].Value -split ';')[0] } else { '' }
    if ($firstStmt -notmatch 'KmhSnapshotRevision\.Next\(\)') { $revBad += 'Federation: the snapshot revision is not stamped before the first read' }
    if ($fb51.Success -and $fb51.Groups[1].Value -notmatch '\brevision,') { $revBad += 'Federation: the stamped revision is not the one sent' }
    if ([System.IO.File]::ReadAllText($fedCli51) -notmatch 'Revision\s*<\s*Snapshot\.Revision') { $revBad += 'Federation: the client cache applies a snapshot without comparing its revision' }
}

if ($revBad.Count -gt 0) { foreach ($b in $revBad) { Bad $b } }
else { Good "all $($revFeatures.Count + 1) mutable snapshot(s) carry a revision the client honours - stamped under the store lock, or before the first read where no one lock covers it" }

# A retried or double-clicked request arrives twice; actions that move a quantity or create a row carry an op id the
# server claims before acting (releasing it if nothing happened) and the client reuses on retry.
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
    @{ Scope='guild.withdraw_item';      S='Features\Guilds\GuildHandler.cs';            C='Features\Guilds\GuildHandler.cs' },
    @{ Scope='treasury.withdraw_silver'; S='Features\Treasury\TreasuryHandler.cs';       C='Features\Treasury\TreasuryHandler.cs' },
    @{ Scope='treasury.withdraw_item';   S='Features\Treasury\TreasuryHandler.cs';       C='Features\Treasury\TreasuryHandler.cs' },
    # NoArgs: the scope is the whole key; every other entry must append its arguments or two withdrawals share an id.
    @{ Scope='treasury.open';            S='Features\Treasury\TreasuryHandler.cs';       C='Features\Treasury\TreasuryHandler.cs'; NoArgs=$true },
    @{ Scope='world.deliver';            S='Features\World\WorldHandler.cs';             C='Features\World\WorldHandler.cs' },
    # Federated silver moves across two parties; both keys carry the amount so withdrawals of different sizes differ.
    @{ Scope='connected.deposit';        S='Features\Global\Treasury\ConnectedTreasuryHandler.cs'; C='Features\ConnectedTreasury\ConnectedTreasuryHandler.cs' },
    @{ Scope='connected.withdraw';       S='Features\Global\Treasury\ConnectedTreasuryHandler.cs'; C='Features\ConnectedTreasury\ConnectedTreasuryHandler.cs' },
    @{ Scope='guild.connected.deposit';  S='Features\Global\Guilds\ConnectedGuildHandler.cs';      C='Features\ConnectedGuild\ConnectedGuildHandler.cs' },
    @{ Scope='guild.connected.withdraw'; S='Features\Global\Guilds\ConnectedGuildHandler.cs';      C='Features\ConnectedGuild\ConnectedGuildHandler.cs' },
    # Federation changes carry no amount, but a repeat still must not run twice, so they are claimed too.
    @{ Scope='federation.create';        S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.join';          S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.invite';        S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.invite_revoke'; S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.leave';         S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.remove';        S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.transfer';      S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.update';        S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.disband';       S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' },
    @{ Scope='federation.reprove';       S='Features\Global\Federation\FederationHandler.cs';      C='Features\Federation\FederationHandler.cs' }
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
    # Two conditions, not one adjacency: a call site may hold its action key in a local instead of repeating the literal.
    if ($cTxt -notmatch 'KmhOpId\.For\(') { $opBad += "$($og.Scope): the client file mints no op id" }
    $wantKey = if ($og.NoArgs) { "`"$q`"" } else { "`"$q\|" }
    if ($cTxt -notmatch $wantKey)         { $opBad += "$($og.Scope): the client has no action key under this scope" }
}

# Every claim in the server must be in the table above, or the table stops describing what is guarded.
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

# Two unguarded actions are harmless twice only because of their argument shape; changing it makes them duplicable.
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

# Discord and the scheduler bypass the router, so every value move there must ask the admission layer too.
Section '53. Every ingress that moves value asks the admission layer'

$valueCall = 'MarketplaceStore\.(Buy|Post|Cancel)\(|AuctionStore\.(Post|Bid)\(|WantStore\.(Post|Fulfill)\(|TreasuryStore\.(WithdrawSilver|WithdrawItem|DepositSilver)\('
$admBad = @()
$admScanned = 0
foreach ($f in (Get-ChildItem (Join-Path $serverSrc 'Features\Discord') -Recurse -Filter *.cs -File)) {
    $t = [System.IO.File]::ReadAllText($f.FullName)
    if ($t -notmatch $valueCall) { continue }
    $admScanned++
    # Counted at the move, not only asked before it: a reset or backup that began in between must wait for it.
    if ($t -notmatch 'KmhValueOps\.Begin(Feature)?\(') {
        $admBad += "Discord\$($f.Name): moves value without a counted KmhValueOps admission"
    }
}
# The scheduler starts value transitions on a timer rather than on a request, so it needs the same question.
$sweep = Join-Path $serverSrc 'Maintenance\ExpirySweeper.cs'
if (Test-Path $sweep) {
    $st = [System.IO.File]::ReadAllText($sweep)
    $admScanned++
    if ($st -notmatch 'KmhValueOps\.Begin\(KmhIngress\.Scheduler') { $admBad += "ExpirySweeper: settles value without a counted KmhValueOps admission" }
    # ...but the retry that ends a persistence freeze must stay outside that check or the freeze outlives its cause.
    if ($st -notmatch 'ShouldRetrySaves')                  { $admBad += "ExpirySweeper: the persistence retry is no longer separable from the value jobs" }
}
if ($admBad.Count -gt 0) { foreach ($b in $admBad) { Bad $b } }
else { Good "all $admScanned non-router value ingress(es) ask the admission layer, and the persistence retry stays outside it" }

# Each SaleSplit.Compute call takes the boom boost from the house pool, so a second call per sale takes it twice.
Section '54. A sale split is taken once per settlement'

# One settlement path per file, so a second call inside one is visible as a second call in the file.
$splitExpected = @{
    'Features\Auctions\AuctionStore.cs'        = 1
    'Features\Marketplace\MarketplaceStore.cs' = 1
    'Features\WantBoard\WantStore.cs'          = 1   # fulfil and payload-fulfil share CommitFill
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

# Boot fails most often on an incompatible host; logging via the host resolves its API at JIT and kills the process.
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
if ($v56 -notmatch 'v\.PendingTakes\.Add\(new Dto\.PendingTake[\s\S]{0,800}?CommitLocked') {
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
    # The settled row is prunable, and a claim that outlives it is handed back while the listing still holds the goods.
    # The clear stays cleared when its save fails (checked below), so this branch is left for a missing owner or marker.
    if ($t56 -notmatch 'if \(!Treasury\.TreasuryStore\.ClearPendingTake\(') {
        Bad "$($pair56.What) does not act on ClearPendingTake failing - the vault's claim would outlive its row"; $takeFail = $true
    }
    # Returning post escrow must be deduplicated, or the inline refund and boot recovery both pay it. Seller-only:
    # settlement hands escrow to a winner under op ids instead (section 52).
    if ($t56 -cmatch 'KmhPayloadEscrow\.RefundTo\(\s*(seller|sellerUsername)\b') {
        Bad "$($pair56.What) returns post escrow without a dedup marker - a crash before Settle pays it twice"; $takeFail = $true
    }
}

# A refused save must not roll the clear back to pending: callers settle their row anyway, and once later rows prune
# it, boot hands the goods back while the new owner keeps them.
$clear56 = [regex]::Match($v56, 'public static bool ClearPendingTake\([\s\S]*?\r?\n        \}')
if (-not $clear56.Success) {
    Bad 'ClearPendingTake not found in the expected shape - verify by hand that a refused save does not restore the claim'; $takeFail = $true
}
elseif ($clear56.Value -match 'CommitLocked|RollbackPointLocked') {
    Bad 'ClearPendingTake rolls a refused save back to pending - the claim then outlives its row once later rows prune it'; $takeFail = $true
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


# RWT reshapes members between generations (property -> field, public -> private); a direct call binds at JIT and
# throws before the caller's own try block runs, so both go through reflection in RwtCompat.
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

# The server reads it for its Global listing; a direct read fails every heartbeat on the reshaped RWT.
$srvShim57 = Join-Path $serverSrc 'VersionShim.cs'
if (-not (Test-Path -LiteralPath $srvShim57)) { Bad 'the server VersionShim.cs is missing'; $bad57 = $true }
else {
    $ss57 = [System.IO.File]::ReadAllText($srvShim57)
    if ($ss57 -notmatch 'GetProperty\("ExecutableVersion"' -or $ss57 -notmatch 'GetField\("ExecutableVersion"') {
        Bad 'the server RwtCompat.ExecutableVersion does not resolve BOTH the property and the field shape'
        $bad57 = $true
    }
}
foreach ($f57s in Get-ChildItem $serverSrc -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    if ($f57s.Name -eq 'VersionShim.cs') { continue }
    $rel57s = $f57s.FullName.Substring($serverSrc.Length).TrimStart('\','/')
    if ([System.IO.File]::ReadAllText($f57s.FullName) -match 'CommonValues\.ExecutableVersion') {
        Bad "$rel57s reads CommonValues.ExecutableVersion directly - use RwtCompat.ExecutableVersion"; $bad57 = $true
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
# Section 3 cannot pair it: the server half is a private class nested in its handler, not a Dto file.
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
# Both values must come from what actually bound, or after a failed bind every client dials a dead socket.
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
# Only the hello decides (after the player's override); a persisted discovered endpoint follows the player elsewhere.
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
# Only the settings screen may write these.
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
# DoWindowContents runs several times a frame, so a deltaTime countdown there fires too fast; tick from WindowUpdate.
$bad61 = $false
$countdowns61 = @()
foreach ($f61 in Get-ChildItem -Path $clientSrc -Recurse -Filter *.cs -File) {
    if ($f61.FullName -match '\\(obj|bin)\\') { continue }
    $lines61 = [System.IO.File]::ReadAllLines($f61.FullName)
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
# Owner rule: a second listener is another port to forward, and unforwarded its feature fails as a bare timeout.
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
foreach ($f62b in Get-ChildItem -Path $serverSrc -Recurse -Filter *Config.cs -File) {
    if ($f62b.FullName -match '\\(obj|bin)\\') { continue }
    $t62 = [System.IO.File]::ReadAllText($f62b.FullName)
    if ($t62 -match '(VideoServerPort|RelayPort|SecondaryPort|MediaPort)\s*\{\s*get') {
        Bad "$($f62b.Name) exposes a second listening port as a setting"; $bad62 = $true
    }
}
if (-not $bad62) { Good 'one listener, in KmhApiServer, and no config offers a second port' }

Section '63. A world marker draws its own quad, upright, with ownership on a rim'
# Vanilla lays world quads east-west, which only symmetric art hides; ownership rides a halo, as tinting blurs the icon.
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
# OS errors carry CRLF/NUL tails and player text reaches diagnostics; either can forge lines in a log someone uploads.
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
$clean64 = Join-Path $clientSrc 'Diagnostics\KmhLog.cs'
if ((Test-Path $clean64) -and ([System.IO.File]::ReadAllText($clean64) -notmatch "c < ' '")) {
    Bad 'the client log cleaner does not drop C0 control characters - a NUL run reaches the file and the uplink'
    $bad64 = $true
}
if (-not $bad64) { Good 'every diagnostic sink collapses a record to one line and drops control characters' }

Section '65. Animated media survives every path that can attach it'
# Discord attaches embeds by a later EDIT; that path needs the resolver too, or linked gifs arrive as flattened stills.
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
# The client must not re-request a settled refusal, or one dead link becomes a request loop.
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
# A snapshot is byte-identical for every client and an unchanged chat log lays out the same; do that work once.
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
$cache66 = Join-Path $clientSrc 'Features\Chat\ChatCache.cs'
if (-not (Test-Path $cache66)) { Bad 'ChatCache.cs is missing'; $bad66 = $true }
else {
    $c66 = [System.IO.File]::ReadAllText($cache66)
    if ($c66 -notmatch 'long Generation') {
        Bad 'ChatCache exposes no generation, so a reader cannot tell "nothing changed" without copying the log'
        $bad66 = $true
    }
    # Derived from what a method writes, not a name list: any change to the log must bump, or readers cache stale rows.
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
# An unguarded Root.Update step that throws stops every later step and reports itself every frame.
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
# Mid-load Find.AnyPlayerHomeMap throws instead of returning null; a delivery landing then is neither held nor acked
# and replays on every join, so every delivery-path lookup goes through a guarded helper.
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
    # Only the two helpers may ask the engine (three lines); a fourth is a caller that skipped the guard.
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

Section '70. A snapshot is one moment, not several'
# Stores are read one after another, so without the gate a trade landing between two reads is captured half-applied.
$snap70 = Get-Content (Join-Path $serverSrc 'Persistence\KmhSnapshot.cs') -Raw
$bad70 = $false
foreach ($fn70 in @('SnapshotServer', 'SnapshotPlayer')) {
    # The body runs to the next public entry point; good enough to tell a wrapped gather from a bare one.
    $m70 = [regex]::Match($snap70, ('(?s)bool\s+' + $fn70 + '\s*\(.*?\n        \}'))
    if (-not $m70.Success) { Bad "could not find $fn70 to check its gather"; $bad70 = $true; continue }
    if ($m70.Value -notmatch 'KmhMaintenanceGate\.Run\(') {
        Bad "$fn70 gathers stores without holding the maintenance gate - the snapshot can span two different moments"
        $bad70 = $true
    }
}
# Holding the gate across parsing and hashing would freeze value moves for far longer than the reads need.
if ($snap70 -match '(?s)KmhMaintenanceGate\.Run\((?:[^)]|\)(?!\s*;))*JToken\.Parse') {
    Bad 'snapshot parsing happens inside the maintenance gate - only the reads belong there'
    $bad70 = $true
}
if (-not $bad70) { Good 'both snapshot gathers hold the maintenance gate, and only for the reads' }

Section '71. Every localized string has a key, and the key exists'
# English is compiled in as the fallback, so a missing entry is never visible to a player.
$langRoot71 = Join-Path (Split-Path $clientSrc -Parent) 'Languages'
$bad71 = $false
$used71 = @{}

# A raw <color=grey> is invalid XML, and RimWorld drops the WHOLE Keyed file it cannot parse; escape colour markup.
$malformed71 = @()
foreach ($x71 in (Get-ChildItem $langRoot71 -Recurse -Filter *.xml -ErrorAction SilentlyContinue)) {
    try { [xml](Get-Content $x71.FullName -Raw) | Out-Null }
    catch { $malformed71 += ('{0}\{1}' -f $x71.Directory.Parent.Name, $x71.Name) }
}
if ($malformed71.Count -gt 0) {
    Bad "language file is not valid XML, so every key in it is dead: $($malformed71 -join ', ')"
} else {
    Good 'every language file parses as XML'
}
# The self-test asserts the compiled fallback by asking for a key that must NOT exist, so its keys are fixtures.
foreach ($f71 in (Get-ChildItem $clientSrc -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.Name -ne 'KmhSelfTest.cs' })) {
    foreach ($m71 in [regex]::Matches((Get-Content $f71.FullName -Raw), 'KmhText\.Get\(\s*"([^"]+)"')) {
        $used71[$m71.Groups[1].Value] = $true
    }
}

function Keys-In($path) {
    $set = @{}
    if (-not (Test-Path $path)) { return $set }
    foreach ($k in [regex]::Matches((Get-Content $path -Raw), '<(KMH_[A-Za-z0-9_]+)>')) { $set[$k.Groups[1].Value] = $true }
    return $set
}

$en71 = Keys-In (Join-Path $langRoot71 'English\Keyed\KMH.xml')
$de71 = Keys-In (Join-Path $langRoot71 'German\Keyed\KMH.xml')
$es71 = Keys-In (Join-Path $langRoot71 'Spanish\Keyed\KMH.xml')

if ($used71.Count -gt 0) {
    $missingEn71 = @($used71.Keys | Where-Object { -not $en71.ContainsKey($_) })
    if ($missingEn71.Count) { Bad ("used in code but absent from English: " + ($missingEn71 -join ', ')); $bad71 = $true }
    # A key nobody uses is dead weight a translator still pays to translate.
    $deadEn71 = @($en71.Keys | Where-Object { -not $used71.ContainsKey($_) })
    if ($deadEn71.Count) { Bad ("in English but used nowhere: " + ($deadEn71 -join ', ')); $bad71 = $true }
}

if (-not $bad71) {
    # Not a failure: a partial translation falls back to English, so coverage is reported rather than enforced.
    $deCover = if ($used71.Count) { [Math]::Round(100.0 * @($used71.Keys | Where-Object { $de71.ContainsKey($_) }).Count / $used71.Count) } else { 100 }
    $esCover = if ($used71.Count) { [Math]::Round(100.0 * @($used71.Keys | Where-Object { $es71.ContainsKey($_) }).Count / $used71.Count) } else { 100 }
    Good "$($used71.Count) localized string(s), all present in English - German $deCover%, Spanish $esCover%"
}

# Translations get the English arguments: a dropped {0} leaves a hole, an invented {2} makes string.Format throw.
function Slots71($path) {
    $map = @{}
    if (-not (Test-Path $path)) { return $map }
    [xml]$d = Get-Content -Raw -LiteralPath $path
    foreach ($n in $d.LanguageData.ChildNodes) {
        if ($n.NodeType -ne 'Element') { continue }
        $set = @{}
        foreach ($m in [regex]::Matches([string]$n.InnerText, '\{(\d+)[^}]*\}')) { $set[$m.Groups[1].Value] = $true }
        $map[$n.Name] = (($set.Keys | Sort-Object) -join ',')
    }
    return $map
}
$enSlots71 = Slots71 (Join-Path $langRoot71 'English\Keyed\KMH.xml')
if ($enSlots71.Count -lt 100) {
    Bad "71: read placeholders for only $($enSlots71.Count) English key(s) - the scan did not run"
} else {
    $slotBad71 = @()
    foreach ($lang71 in @('German', 'Spanish')) {
        $other71 = Slots71 (Join-Path $langRoot71 "$lang71\Keyed\KMH.xml")
        foreach ($k71 in $other71.Keys) {
            if (-not $enSlots71.ContainsKey($k71)) { continue }   # 71 above already reports an unknown key
            if ($other71[$k71] -ne $enSlots71[$k71]) {
                $slotBad71 += "$lang71/$k71 uses [$($other71[$k71])] where English uses [$($enSlots71[$k71])]"
            }
        }
    }
    if ($slotBad71.Count -gt 0) { foreach ($s71 in $slotBad71) { Bad "71: $s71" } }
    else { Good "$($enSlots71.Count) key(s): every translation takes the same placeholders as its English" }
}

Section '72. One owner decides the KMH main menu layout'
# Harmony does not order postfixes, so independent ones can stay apart only by hardcoding each other's heights.
$menu72 = @(Get-ChildItem (Join-Path $clientSrc 'Patches') -Filter 'Patch_MainMenuDrawer*.cs' -ErrorAction SilentlyContinue)
$owners72 = @()   # the postfix that hands layout to the surface
$strays72 = @()   # any other postfix that still claims its own rectangle
foreach ($f72 in $menu72) {
    $t72 = Get-Content $f72.FullName -Raw
    if ($t72 -notmatch 'HarmonyPatch\(typeof\(MainMenuDrawer\)') { continue }
    if ($t72 -match 'KmhMainMenuSurface\.Draw\(') { $owners72 += $f72.Name; continue }
    # A postfix that draws is a layout claim; one that only opens a dialog has no rectangle to collide with.
    if ($t72 -match 'Widgets\.(Label|ButtonText|DrawWindowBackground|DrawBoxSolid)\(') { $strays72 += $f72.Name }
}
if ($owners72.Count -ne 1) {
    Bad "expected exactly one main-menu postfix delegating to KmhMainMenuSurface.Draw, found $($owners72.Count)"
} elseif ($strays72.Count -gt 0) {
    Bad ("main-menu postfix(es) still drawing their own rectangle: " + ($strays72 -join ', '))
} else {
    # The card is placed from RimWorld's menu rectangle; everything else the surface draws claims a spot clear of both.
    $surface72 = Code (Join-Path $clientSrc 'UI\KmhMainMenuSurface.cs')
    $restore72 = Code (Join-Path $clientSrc 'Patches\Patch_MainMenuDrawer_EnforcementRestore.cs')
    if (-not $surface72) { Bad 'KmhMainMenuSurface.cs is missing' }
    elseif ($surface72 -notmatch 'VanillaPane' -or $surface72 -notmatch 'static Rect Claim\(') { Bad 'the main-menu surface does not claim space, so elements can still overlap' }
    elseif ($restore72 -and $restore72 -notmatch 'KmhMainMenuSurface\.Claim\(') { Bad 'the recovery warning picks its own spot, so it can cover the card' }
    else { Good "one main-menu postfix ($($owners72[0])) delegating to a surface that places the card by RimWorld's menu and claims space for the rest" }
}

Section '73. The server and KMH Global build the same signed bytes'
# Both repos build the canonical heartbeat; a one-sided field change yields signatures that verify nowhere.
$globalRoot73 = Join-Path (Split-Path -Parent $ServerRoot) 'KMH-Global'
$srvBody73    = Join-Path $ServerRoot 'Source\Features\Global\HeartbeatBody.cs'
$gblBody73    = Join-Path $globalRoot73 'src\KmhGlobal\Servers\HeartbeatRequest.cs'
if (-not (Test-Path $srvBody73)) {
    Bad 'the server addon has no HeartbeatBody.cs - the Global heartbeat cannot be signed'
} elseif (-not (Test-Path $globalRoot73)) {
    NoGlobal 'the cross-repo signing comparison did not run'
} elseif (-not (Test-Path $gblBody73)) {
    Bad '73: KMH-Global has no HeartbeatRequest.cs - the cross-repo signing comparison did not run'
} else {
    # The .Add(...) chain inside Canonical() IS the wire contract, so the two chains are compared field by field.
    $chain73 = {
        param($path)
        $text = Get-Content $path -Raw
        $m = [regex]::Match($text, 'Canonical\(\)\s*=>[\s\S]*?\.Build\(\)')
        if (-not $m.Success) { return $null }
        ,@([regex]::Matches($m.Value, '\.Add\(\s*([A-Za-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value })
    }
    $srvChain73 = & $chain73 $srvBody73
    $gblChain73 = & $chain73 $gblBody73
    if ($null -eq $srvChain73 -or $null -eq $gblChain73) {
        Bad 'could not read a Canonical() field chain from one of the two heartbeat models'
    } elseif (($srvChain73 -join '|') -ne ($gblChain73 -join '|')) {
        Bad ("the signed field order differs - server [$($srvChain73 -join ', ')] vs Global [$($gblChain73 -join ', ')]")
    } else {
        Good "both repositories sign the same $($srvChain73.Count) heartbeat fields in the same order"
    }

    # The pinned known-answer vector must be identical on both sides, or the byte layout has drifted anyway.
    $srvVec73 = Select-String -Path (Join-Path $ServerRoot 'Source\Maintenance\KmhGlobalSigningSelfTest.cs') `
                              -Pattern 'ExpectedHeartbeatVector\s*=\s*"([0-9a-f]{64})"' -ErrorAction SilentlyContinue
    $gblVec73 = Select-String -Path (Join-Path $globalRoot73 'tests\KmhGlobal.Tests\CanonicalVectorTests.cs') `
                              -Pattern 'ExpectedSha256\s*=\s*"([0-9a-f]{64})"' -ErrorAction SilentlyContinue
    if (-not $srvVec73 -or -not $gblVec73) {
        Bad 'one side is missing its pinned canonical vector, so drift would not be detected'
    } elseif ($srvVec73.Matches[0].Groups[1].Value -ne $gblVec73.Matches[0].Groups[1].Value) {
        Bad 'the two pinned canonical vectors disagree - one side changed the signed byte layout'
    } else {
        Good "both repositories pin the same canonical vector ($($srvVec73.Matches[0].Groups[1].Value.Substring(0,12))...)"
    }

    # The mod manifest has its own signing domain and field list, so it needs its own pinned vector.
    $srvMod73 = Select-String -Path (Join-Path $ServerRoot 'Source\Maintenance\KmhGlobalSigningSelfTest.cs') `
                              -Pattern 'ExpectedModProfileVector\s*=\s*"([0-9a-f]{64})"' -ErrorAction SilentlyContinue
    $gblMod73 = Select-String -Path (Join-Path $globalRoot73 'tests\KmhGlobal.Tests\CanonicalVectorTests.cs') `
                              -Pattern 'ExpectedModProfileSha256\s*=\s*"([0-9a-f]{64})"' -ErrorAction SilentlyContinue
    if (-not $srvMod73 -or -not $gblMod73) {
        Bad 'one side is missing its pinned mod-manifest vector, so drift would not be detected'
    } elseif ($srvMod73.Matches[0].Groups[1].Value -ne $gblMod73.Matches[0].Groups[1].Value) {
        Bad 'the two pinned mod-manifest vectors disagree - one side changed the signed byte layout'
    } else {
        Good "both repositories pin the same mod-manifest vector ($($srvMod73.Matches[0].Groups[1].Value.Substring(0,12))...)"
    }

    # A federation report batch is a list of records; the field-chain check below sees only the list's name.
    $srvFed73 = Select-String -Path (Join-Path $ServerRoot 'Source\Maintenance\KmhGlobalSigningSelfTest.cs') `
                              -Pattern 'ExpectedFederationContributeVector\s*=\s*"([0-9a-f]{64})"' -ErrorAction SilentlyContinue
    $gblFed73 = Select-String -Path (Join-Path $globalRoot73 'tests\KmhGlobal.Tests\CanonicalVectorTests.cs') `
                              -Pattern 'ExpectedFederationContributeSha256\s*=\s*"([0-9a-f]{64})"' -ErrorAction SilentlyContinue
    if (-not $srvFed73 -or -not $gblFed73) {
        Bad 'one side is missing its pinned federation-report vector, so drift in the record layout would not be detected'
    } elseif ($srvFed73.Matches[0].Groups[1].Value -ne $gblFed73.Matches[0].Groups[1].Value) {
        Bad 'the two pinned federation-report vectors disagree - one side changed the signed record layout'
    } else {
        Good "both repositories pin the same federation-report vector ($($srvFed73.Matches[0].Groups[1].Value.Substring(0,12))...)"
    }

    # The rebind proof normalizes and sorts before signing, and two keys sign the rotation; each needs its own pin.
    foreach ($pair73 in @(@('ExpectedServerRebindVector', 'ExpectedServerRebindSha256', 'rebind-proof'),
                          @('ExpectedKeyRotationVector', 'ExpectedKeyRotationSha256', 'key-rotation'))) {
        $srvPin73 = Select-String -Path (Join-Path $ServerRoot 'Source\Maintenance\KmhGlobalSigningSelfTest.cs') `
                                  -Pattern "$($pair73[0])\s*=\s*""([0-9a-f]{64})""" -ErrorAction SilentlyContinue
        $gblPin73 = Select-String -Path (Join-Path $globalRoot73 'tests\KmhGlobal.Tests\CanonicalVectorTests.cs') `
                                  -Pattern "$($pair73[1])\s*=\s*""([0-9a-f]{64})""" -ErrorAction SilentlyContinue
        if (-not $srvPin73 -or -not $gblPin73) {
            Bad "one side is missing its pinned $($pair73[2]) vector, so drift would not be detected"
        } elseif ($srvPin73.Matches[0].Groups[1].Value -ne $gblPin73.Matches[0].Groups[1].Value) {
            Bad "the two pinned $($pair73[2]) vectors disagree - one side changed the signed byte layout"
        } else {
            Good "both repositories pin the same $($pair73[2]) vector ($($srvPin73.Matches[0].Groups[1].Value.Substring(0,12))...)"
        }
    }

    # The listing push is a second cross-repo signed payload and drifts exactly the same way the heartbeat can.
    $srvMeta73 = Join-Path $ServerRoot 'Source\Features\Global\MetadataBody.cs'
    $gblMeta73 = Join-Path $globalRoot73 'src\KmhGlobal\Servers\MetadataService.cs'
    if ((Test-Path $srvMeta73) -and (Test-Path $gblMeta73)) {
        $metaChain = {
            param($path)
            $text = Get-Content $path -Raw
            $m = [regex]::Match($text, 'Canonical\(\)\s*=>[\s\S]*?\.Build\(\)')
            if (-not $m.Success) { return $null }
            ,@([regex]::Matches($m.Value, '\.Add(?:List)?\(\s*([A-Za-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value })
        }
        $srvMetaChain = & $metaChain $srvMeta73
        $gblMetaChain = & $metaChain $gblMeta73
        if ($null -eq $srvMetaChain -or $null -eq $gblMetaChain) {
            Bad 'could not read a Canonical() field chain from one of the two metadata models'
        } elseif (($srvMetaChain -join '|') -ne ($gblMetaChain -join '|')) {
            Bad ("the signed metadata field order differs - server [$($srvMetaChain -join ', ')] vs Global [$($gblMetaChain -join ', ')]")
        } else {
            Good "both repositories sign the same $($srvMetaChain.Count) metadata fields in the same order"
        }
    } else {
        Bad 'the signed metadata payload is missing on one side, so a listing push cannot be verified'
    }

    # Every signed payload needs its own domain, or one message's bytes verify as another's.
    $srvDomains73 = [regex]::Matches((Get-Content (Join-Path $ServerRoot 'Source\Features\Global\KmhGlobalSigning.cs') -Raw),
                                     '"(kmh\.global\.[a-z0-9.\-]+)"') | ForEach-Object { $_.Groups[1].Value }
    $gblDomains73 = [regex]::Matches((Get-Content (Join-Path $globalRoot73 'src\KmhGlobal\Servers\CanonicalPayload.cs') -Raw),
                                     '"(kmh\.global\.[a-z0-9.\-]+)"') | ForEach-Object { $_.Groups[1].Value }
    if ((($srvDomains73 | Sort-Object) -join '|') -ne (($gblDomains73 | Sort-Object) -join '|')) {
        Bad ("signing domains differ - server [$($srvDomains73 -join ', ')] vs Global [$($gblDomains73 -join ', ')]")
    } elseif (($srvDomains73 | Sort-Object -Unique).Count -ne $srvDomains73.Count) {
        Bad 'a signing domain is reused for two payloads, so one message could verify as another'
    } else {
        Good "$($srvDomains73.Count) signing domain(s) agree across repos and none is reused"
    }

    # Field by field per domain: matching domain names alone let a one-sided field fail every federated value move.
    $canon73 = {
        param([string]$root)
        $map = @{}
        foreach ($f in Get-ChildItem $root -Recurse -Filter *.cs |
                 Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'Test' }) {
            $text = Code $f.FullName
            foreach ($m in [regex]::Matches($text, 'Canonical\(\)\s*=>([\s\S]*?)\.Build\(\)')) {
                $d = [regex]::Match($m.Groups[1].Value, 'For\(\s*(?:Kmh)?SigningDomains\.(\w+)')
                if (-not $d.Success) { continue }
                $items = @([regex]::Matches($m.Groups[1].Value, '\.Add\w*\(((?:[^()]|\([^()]*\))*)\)') | ForEach-Object {
                    (($_.Groups[1].Value -replace '\s+', '') -replace '\?\?""', '' -replace '\?\?0', '').ToLowerInvariant() })
                $key = $d.Groups[1].Value
                # Two builders for one domain is itself the drift this looks for.
                if ($map.ContainsKey($key)) { $map[$key] = '<two builders>' } else { $map[$key] = ($items -join ' | ') }
            }
        }
        return $map
    }
    # The same normalization under each repo's own name; their bodies are held equal below.
    $aliases73 = @{ 'guildinvites.normalize(' = 'normalizeinvite(' }
    # A domain only Global signs today, with the reason it has no addon builder.
    $globalOnly73 = @{
        # Older servers still send these; this addon creates, joins and leaves under the federation domains.
        'GuildLink'   = 'superseded by federation-create and federation-join'
        'GuildRevoke' = 'superseded by federation-leave'
    }

    $srvCanon73 = & $canon73 $serverSrc
    $gblCanon73 = & $canon73 (Join-Path $globalRoot73 'src')
    foreach ($k in @($gblCanon73.Keys)) { foreach ($a in $aliases73.Keys) { $gblCanon73[$k] = $gblCanon73[$k].Replace($a, $aliases73[$a]) } }
    $diff73 = @()
    foreach ($dom in (@($srvCanon73.Keys) + @($gblCanon73.Keys) | Sort-Object -Unique)) {
        $s = $srvCanon73[$dom]; $g = $gblCanon73[$dom]
        if ($null -eq $s -and $globalOnly73.ContainsKey($dom)) { continue }
        if ($null -eq $s -or $null -eq $g) { $diff73 += "$dom (built on one side only)"; continue }
        if ($s -eq '<two builders>' -or $g -eq '<two builders>') { $diff73 += "$dom (two builders on one side)"; continue }
        if ($s -ne $g) { $diff73 += "$dom - server [$s] vs Global [$g]" }
    }
    $normSrv73 = [regex]::Match((Code (Join-Path $serverSrc 'Features\Global\GuildBody.cs')), 'string NormalizeInvite\(string code\)\s*(\{[\s\S]*?\n\s*\})')
    $normGbl73 = [regex]::Match((Code (Join-Path $globalRoot73 'src\KmhGlobal\Treasury\GuildTreasuryService.cs')), 'string Normalize\(string\? code\)\s*(\{[\s\S]*?\n\s*\})')
    $sameNorm73 = $normSrv73.Success -and $normGbl73.Success -and
                  (($normSrv73.Groups[1].Value -replace '\s+', '') -eq ($normGbl73.Groups[1].Value -replace '\s+', ''))
    if ($srvCanon73.Count -lt 15) {
        Bad "73: found only $($srvCanon73.Count) server Canonical() builder(s) - the scan did not run"
    } elseif ($diff73.Count -gt 0) {
        Bad ("73: signed field chains differ - " + ($diff73 -join '; '))
    } elseif (-not $sameNorm73) {
        Bad '73: the invite-code normalization differs between the two repos, so a signed invite would not verify'
    } else {
        Good "73: all $($srvCanon73.Count) shared signing domains build the same field chain on both sides"
    }

    # One representation, chosen once: a DER signer talking to a P1363 verifier fails with no useful diagnostic.
    $srvFmt73 = (Get-Content (Join-Path $ServerRoot 'Source\Features\Global\KmhGlobalSigning.cs') -Raw)
    $gblFmt73 = (Get-Content (Join-Path $globalRoot73 'src\KmhGlobal\Servers\ServerSigning.cs') -Raw)
    if ($srvFmt73 -notmatch 'IeeeP1363FixedFieldConcatenation' -or $gblFmt73 -notmatch 'IeeeP1363FixedFieldConcatenation') {
        Bad 'the ECDSA signature format is not pinned to IEEE P1363 on both sides'
    } elseif ($srvFmt73 -match 'Rfc3279DerSequence' -or $gblFmt73 -match 'Rfc3279DerSequence') {
        Bad 'a DER signature path exists alongside the P1363 one - exactly one representation may be emitted'
    } else {
        Good 'both sides emit ES256 as fixed-width IEEE P1363, and neither can emit DER'
    }
}

Section '74. A game server registers with a scoped grant, never with the owner''s session'
# The credential crossing from the owner's browser to the game server is the whole boundary: a session or refresh
# token there exposes every server the owner runs to any machine that ever ran setup.
$globalRoot74 = Join-Path (Split-Path -Parent $ServerRoot) 'KMH-Global'
$grantSrc74   = Join-Path $globalRoot74 'src\KmhGlobal\Servers\RegistrationGrant.cs'
$regSrc74     = Join-Path $ServerRoot 'Source\Features\Global\KmhGlobalRegistration.cs'
$setup74      = Join-Path $ServerRoot 'Source\Features\Global\GlobalSetupState.cs'

if (-not (Test-Path $regSrc74)) {
    Bad 'the server addon has no KmhGlobalRegistration.cs - nothing drives Global registration'
} elseif (-not (Test-Path $globalRoot74)) {
    NoGlobal 'the registration-grant comparison did not run'
} elseif (-not (Test-Path $grantSrc74)) {
    Bad '74: KMH-Global has no RegistrationGrant.cs - the registration-grant comparison did not run'
} else {
    $grantText74 = Get-Content $grantSrc74 -Raw
    $regText74   = Get-Content $regSrc74 -Raw
    $setupText74 = Get-Content $setup74 -Raw

    # Pinned to one ServerId: a grant that authenticates any server id is a session with extra steps.
    if ($grantText74 -notmatch 'serverId\s*==\s*grant\.ServerId') {
        Bad 'Global does not pin a registration grant to the ServerId in the request body'
    } else {
        Good 'a registration grant authenticates exactly one ServerId'
    }

    # Single-use and short-lived, so a grant read out of a console scrollback later is worth nothing.
    if ($grantText74 -notmatch 'ConsumedAt is null' -or $grantText74 -notmatch 'Lifetime\s*=\s*TimeSpan\.FromMinutes') {
        Bad 'a registration grant is not both single-use and time-limited'
    } else {
        Good 'a registration grant is single-use and expires'
    }

    # Only the hash is persisted; the plaintext exists in one response and in the game server's memory.
    if ($grantText74 -notmatch 'TokenHash') {
        Bad 'registration grants are not stored as hashes'
    } else {
        Good 'only a grant hash is persisted'
    }

    # The game server must never be handed, or write down, a browser credential.
    if ($regText74 -match 'ownerAccessToken' -or $regText74 -match 'refresh_token') {
        Bad 'the game server still takes an owner access or refresh token for registration'
    } else {
        Good 'the game server registers with the grant alone, never an owner session'
    }

    if ($setupText74 -match 'Grant' -or $regText74 -notmatch 'private static string _grant') {
        Bad 'the registration grant is not held in memory only - GlobalSetupState is a file owners share'
    } else {
        Good 'the grant is memory-only and never written to the setup file'
    }

    # A registered server must resume on its own, or it drops out of the browser after its first restart.
    $mainText74 = Get-Content (Join-Path $ServerRoot 'Source\Main.cs') -Raw
    $downText74 = Get-Content (Join-Path $ServerRoot 'Source\Maintenance\KmhShutdown.cs') -Raw
    if ($mainText74 -notmatch 'KmhGlobalService\.Start\(\)') {
        Bad 'boot never starts the Global heartbeat, so a registered server stops being listed after a restart'
    } elseif ($downText74 -notmatch 'KmhGlobalService\.(Stop\(\)|StopAndWait\()') {
        Bad 'shutdown never stops the Global heartbeat, so a beat can persist a sequence after the config flush'
    } else {
        Good 'the heartbeat resumes at boot and is parked before the shutdown flush'
    }
}


Section '75. Crash recovery has a caller'
# Tests are no evidence a reconciler runs; the heartbeat (proof Global is reachable) must drive it, or deposits stay open.
$recoverBeat = Join-Path $ServerRoot 'Source\Features\Global\KmhGlobalService.cs'
$recoverSvc  = Join-Path $ServerRoot 'Source\Features\Global\Treasury\ConnectedTreasuryService.cs'
if (-not (Test-Path $recoverBeat) -or -not (Test-Path $recoverSvc)) {
    Bad 'could not find the heartbeat loop or the federated treasury service'
} else {
    $beatTxt = [System.IO.File]::ReadAllText($recoverBeat)
    $callers  = @()
    foreach ($f in (Get-ChildItem (Join-Path $ServerRoot 'Source') -Recurse -Filter *.cs -File |
                    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'SelfTest' })) {
        $t = [System.IO.File]::ReadAllText($f.FullName)
        if ($t -match 'ConnectedTreasuryService\.ReconcileAsync\(') { $callers += $f.Name }
    }
    if ($callers.Count -eq 0) {
        Bad 'nothing outside the self-tests ever calls ConnectedTreasuryService.ReconcileAsync - interrupted federated silver would never resume'
    } elseif ($beatTxt -notmatch 'ConnectedTreasuryService\.ReconcileAsync\(') {
        Bad 'the heartbeat no longer drives reconciliation, so recovery waits for whatever else happens to call it'
    } else {
        Good "the federated reconciler runs on a successful heartbeat, from $($callers -join ', ')"
    }

    # Guild operations must resume through that one reconciler, or a crashed guild operation has no driver.
    $svcTxt = [System.IO.File]::ReadAllText($recoverSvc)
    if ($svcTxt -notmatch 'ConnectedGuildService\.ResumeAsync\(') {
        Bad 'the shared reconciler does not drive guild operations, so a guild deposit interrupted by a crash never resumes'
    } else {
        Good 'guild operations resume through the same reconciler as personal ones'
    }

    # A restored server signs under an old generation; without the re-proof sweep links read Active while Global refuses them.
    $regTxt = ''
    $regPath = Join-Path $ServerRoot 'Source\Features\Global\KmhGlobalRegistration.cs'
    if (Test-Path $regPath) { $regTxt = [System.IO.File]::ReadAllText($regPath) }
    if ($regTxt -notmatch 'FederatedGuildStore\.InvalidateForGeneration\(') {
        Bad 'nothing sweeps guild links when this server adopts a new signing generation'
    } else {
        Good 'adopting a signing generation demands re-proof of the guild links proved under the old one'
    }

    # Without a production caller the directory key is never fetched, so no join ticket verifies and no ban enforces.
    $keyCallers = @()
    foreach ($f in (Get-ChildItem (Join-Path $ServerRoot 'Source') -Recurse -Filter *.cs -File |
                    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'SelfTest' })) {
        $t = [System.IO.File]::ReadAllText($f.FullName)
        if ($t -match 'KmhDirectoryKey\.EnsureAsync\(') { $keyCallers += $f.Name }
    }
    if ($keyCallers.Count -eq 0) {
        Bad 'nothing outside the self-tests fetches Global directory key, so no join ticket can ever verify'
    } else {
        Good "the directory key is fetched in production, from $($keyCallers -join ', ')"
    }
}

Section '76. Diagnostics never run on the handshake'
# Admission must never wait on diagnostics: a self-test on the handshake freezes the game until RWT drops the socket.
$handshake = Join-Path $ClientRoot 'Source\SubProtocol\KmhHandshakeHandler.cs'
$suite     = Join-Path $ClientRoot 'Source\Diagnostics\KmhSelfTest.cs'
$tools     = Join-Path $ClientRoot 'Source\Dialogs\Dialog_KMHAdvancedTools.cs'

if (-not (Test-Path $handshake) -or -not (Test-Path $suite) -or -not (Test-Path $tools)) {
    Bad 'could not find the client handshake, self-test or tools dialog'
} else {
    $hs = [System.IO.File]::ReadAllText($handshake)
    if ($hs -match 'KmhSelfTest\s*\.\s*Run') {
        Bad 'the handshake calls into KmhSelfTest - admission would wait on diagnostics again'
    } else {
        Good 'ActivateSession does not reach KmhSelfTest'
    }

    $onPath = @()
    foreach ($f in (Get-ChildItem (Join-Path $ClientRoot 'Source\SubProtocol') -Recurse -Filter *.cs -File)) {
        $t = [System.IO.File]::ReadAllText($f.FullName)
        if ($t -match 'KmhSelfTest\s*\.\s*Run') { $onPath += $f.Name }
    }
    if ($onPath.Count -gt 0) {
        Bad "the self-test is reachable from the protocol layer: $($onPath -join ', ')"
    } else {
        Good 'no file under SubProtocol runs the self-test'
    }

    $st = [System.IO.File]::ReadAllText($suite)
    # DebugLogging may still decide what is LOGGED; it must not decide what is RUN.
    if ($st -match 'RunOnceIfDebug') {
        Bad 'RunOnceIfDebug still exists - DebugLogging must not imply running the suite'
    } elseif ($st -match 'DebugLogging[^\r\n]*\)\s*return;[\s\S]{0,200}?RunSuite') {
        Bad 'the suite is still gated on DebugLogging rather than on an explicit request'
    } else {
        Good 'DebugLogging does not trigger the suite'
    }

    if ($st -notmatch 'public static void RunOnRequest\(') {
        Bad 'there is no explicit entry point left to run the suite from'
    } else {
        Good 'the suite has an explicit RunOnRequest entry point'
    }

    $tl = [System.IO.File]::ReadAllText($tools)
    if ($tl -notmatch 'KmhSelfTest\.RunOnRequest\(') {
        Bad 'nothing in Advanced & tools can run the self-test, so it is unreachable for a developer'
    } else {
        Good 'Advanced & tools exposes the self-test as an explicit action'
    }

    # Suppressions are set and restored in one scope, so a throw from any check cannot leave them on in live play.
    if ($st -notmatch 'using \(Suppression\.Begin\(\)\)') {
        Bad 'the suite no longer scopes its suppressions, so an exception could leave them set'
    } else {
        Good 'the suite sets and restores its suppressions in one scope'
    }
}

Section '77. The client never carries a Discord secret'
# Rich presence needs only an application id; a bot token or client secret in a shipped mod is handed to every player.
$presence = Join-Path $ClientRoot 'Source\Features\Discord'
if (-not (Test-Path $presence)) {
    Bad 'could not find the client Discord folder'
} else {
    $offenders = @()
    foreach ($f in (Get-ChildItem $clientSrc -Recurse -Filter *.cs -File |
                    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
        # Comments stripped: matching prose blames the file documenting the rule. Fix the scanner, never the comment.
        $t = [System.IO.File]::ReadAllText($f.FullName) -replace '(?s)/\*.*?\*/', '' -replace '(?m)//.*$', ''
        # The shapes that would matter: a stored bot token, a client secret field, or a bot Authorization header.
        if ($t -match '(?i)(discord[A-Za-z]{0,12}(bot)?token\s*=\s*"|client[_]?[Ss]ecret\s*=\s*"|"Bot\s+[A-Za-z0-9_\.\-]{40,}")') {
            $offenders += $f.Name
        }
    }
    if ($offenders.Count -gt 0) {
        Bad "client source looks like it carries a Discord credential: $($offenders -join ', ')"
    } else {
        Good 'no Discord bot token or client secret anywhere in the client'
    }

    $settings = Join-Path $ClientRoot 'Source\Loader\KMHPatchSettings.cs'
    $st = [System.IO.File]::ReadAllText($settings)
    if ($st -notmatch '1459619253010563327') {
        Bad 'the client default application id is not the KMH Multiplayer Suite application'
    } else {
        Good 'the default application id is the KMH Multiplayer Suite one'
    }

    $rp = [System.IO.File]::ReadAllText((Join-Path $presence 'KmhRichPresence.cs'))
    if ($rp -notmatch 'kmh_logo_v1' -or $rp -notmatch 'kmh_official_server_v1') {
        Bad 'the uploaded Rich Presence asset keys are not the ones the code asks for'
    } else {
        Good 'presence asks for the uploaded asset keys by name'
    }
    # The badge is applied in one place, and only under a test that the listed card says official.
    $fmt = [System.IO.File]::ReadAllText((Join-Path $presence 'KmhPresenceFormat.cs'))
    $uses = [regex]::Matches($fmt, 'OfficialAsset')
    $gated = [regex]::Matches($fmt, 'if \(session\.Card != null && session\.Card\.Official[^)]*\)\s*\{\s*p\.SmallImageKey = KmhRichPresence\.OfficialAsset;')
    if ($uses.Count -ne 1 -or $gated.Count -ne 1) {
        Bad "the official badge is no longer gated on card.Official ($($uses.Count) use(s), $($gated.Count) gated)"
    } else {
        Good 'the official badge is set only where the card says official'
    }
}

Section '78. Discord activity is opted into, and says nothing else'
# A player's open screen says more than "playing multiplayer": never shown for an unlisted server, cleared with the session.
$settings = Join-Path $ClientRoot 'Source\Loader\KMHPatchSettings.cs'
$rp       = Join-Path $ClientRoot 'Source\Features\Discord\KmhRichPresence.cs'
$act      = Join-Path $ClientRoot 'Source\Features\Discord\KmhPresenceActivity.cs'
$disp     = Join-Path $ClientRoot 'Source\SubProtocol\KmhDispatcher.cs'

if (-not (Test-Path $act)) {
    Bad 'the Discord activity source is missing'
} else {
    # Defaults are the owner's choice; what must hold is that a fresh install and a loaded file agree.
    $st = Code $settings
    $drift = @()
    foreach ($m in [regex]::Matches($st, 'public\s+bool\s+(Discord\w+|AllowDiscordJoin)\s*=\s*(true|false)\s*;')) {
        $name = $m.Groups[1].Value; $field = $m.Groups[2].Value
        $load = [regex]::Match($st, 'Scribe_Values\.Look\(ref ' + $name + '\s*,[^)]*defaultValue:\s*(true|false)')
        if (-not $load.Success -or $load.Groups[1].Value -ne $field) { $drift += $name }
    }
    if ($drift.Count -eq 0 -and $st -match 'DiscordShowActivity\s*=') { Good 'every Discord setting has the same default in the field and on load' }
    else { Bad ('a Discord setting defaults differently on load than in the field: ' + ($drift -join ', ')) }

    # A server that is not publicly listed gets the minimum status, without what the player is doing.
    $sig = Code (Join-Path $ClientRoot 'Source\Features\Discord\KmhPresenceSignals.cs')
    $server = [regex]::Match($sig, 'void Server\([\s\S]*?\n        \}').Value
    $fmtt = Code (Join-Path $ClientRoot 'Source\Features\Discord\KmhPresenceFormat.cs')
    $named = [regex]::Match($fmtt, 'DiscordShowPrivateServerName[\s\S]{0,200}?KMH_Presence_Private').Value
    if ($server.Length -eq 0 -or $named.Length -eq 0) { Bad 'could not find the private-server presence branch' }
    elseif ($server -notmatch 'CardForCurrentServer\(\)\s*==\s*null\)\s*return;[\s\S]*KmhPresenceActivity\.Current') {
        Bad 'the private-server status now publishes gameplay activity'
    }
    else { Good 'a private server never publishes what the player is doing, and is named only by opt-in' }

    if ((Code $disp) -match 'KmhPresenceActivity\.Clear\(\)') {
        Good 'leaving a server clears the activity it produced'
    } else {
        Bad 'the session reset no longer clears the Discord activity'
    }

    # Every line is a literal. An interpolation here is how an amount or an item name reaches Discord.
    $actt = Code $act
    $lines = [regex]::Matches($actt, 'KmhText\.Get\("KMH_Presence_Act[A-Za-z]+",\s*"[^"{}<>]+"\)')
    $calls = [regex]::Matches($actt, 'KMH_Presence_Act[A-Za-z]+')
    if ($lines.Count -gt 0 -and $lines.Count -eq $calls.Count) {
        Good "every activity line is a fixed string ($($lines.Count))"
    } else {
        Bad "an activity line is interpolated or malformed ($($lines.Count) literal of $($calls.Count))"
    }

    # The activity gate is read from the setting, not cached into a field that a toggle would not reach.
    if ($actt -match 'Enabled\s*=>\s*KMHPatchMod\.SettingsOrDefault\.DiscordShowActivity') {
        Good 'the activity gate reads the live setting'
    } else {
        Bad 'the activity gate no longer reads the setting directly'
    }
}


Section '79. Nested wire records use the same JSON names on both sides'
# Section 3 pairs DTO files, so a record nested in another DTO is compared by nobody; an unnamed field there is
# dropped in transit and the two sides then sign different bytes.
$globalRoot79 = Join-Path (Split-Path -Parent $ServerRoot) 'KMH-Global'

# addon file -> addon type, global file -> global type. One row per nested wire record.
$nested79 = @(
    @{ Name   = 'mod profile entry'
       SrvFile = 'Source\Features\Mods\ModEntry.cs';                SrvType = 'ModEntry'
       GblFile = 'src\KmhGlobal\Servers\ModProfileService.cs';      GblType = 'ModProfileEntry' }
)

if (-not (Test-Path $globalRoot79)) {
    NoGlobal 'nested wire records were not compared'
} else {
    foreach ($rec in $nested79) {
        $srvPath = Join-Path $ServerRoot $rec.SrvFile
        $gblPath = Join-Path $globalRoot79 $rec.GblFile
        if (-not (Test-Path $srvPath)) { Bad "$($rec.Name): $($rec.SrvFile) is missing"; continue }
        if (-not (Test-Path $gblPath)) { Bad "$($rec.Name): $($rec.GblFile) is missing"; continue }

        $bodyOf = {
            param($path, $type)
            $text = Get-Content $path -Raw
            # Ends at the first namespace-depth brace: Global's namespace is file-scoped (column 0), the addon's a block.
            $m = [regex]::Match($text, "class\s+$type\b[\s\S]*?\r?\n(?:    )?\}")
            if ($m.Success) { return $m.Value }
            return $null
        }
        $srvBody = & $bodyOf $srvPath $rec.SrvType
        $gblBody = & $bodyOf $gblPath $rec.GblType
        if ($null -eq $srvBody -or $null -eq $gblBody) {
            Bad "$($rec.Name): could not read one of the two class bodies"
            continue
        }

        $srvNames = @([regex]::Matches($srvBody, 'JsonProperty\("([a-z0-9_]+)"\)')     | ForEach-Object { $_.Groups[1].Value })
        $gblNames = @([regex]::Matches($gblBody, 'JsonPropertyName\("([a-z0-9_]+)"\)') | ForEach-Object { $_.Groups[1].Value })

        # Every member needs an attribute: an unnamed one serialises under its C# name, the failure this check exists for.
        $srvMembers = @([regex]::Matches($srvBody, '(?m)^\s*(?:\[[^\]]+\]\s*)*public\s+string\s+[A-Za-z0-9_]+\s*(?:=|;|\{)')).Count
        if ($srvNames.Count -lt $srvMembers) {
            Bad ("$($rec.Name): $($rec.SrvType) has $srvMembers member(s) but only $($srvNames.Count) " +
                 "JsonProperty name(s) - an unnamed member goes on the wire under its C# name")
            continue
        }

        $missing = @($gblNames | Where-Object { $srvNames -notcontains $_ })
        $extra   = @($srvNames | Where-Object { $gblNames -notcontains $_ })
        if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
            Bad ("$($rec.Name): the two sides disagree" +
                 $(if ($missing.Count) { " - Global reads $($missing -join ', ') and the server never sends it" }) +
                 $(if ($extra.Count)   { " - the server sends $($extra -join ', ') and Global never reads it" }))
        } else {
            Good "$($rec.Name): $($srvNames.Count) field name(s) agree across repos"
        }
    }
}


# Deleting the destination before moving the temp file over leaves a window where a process death destroys it;
# no in-process test can observe that, so the shape is refused in source.
Section '80. A file is replaced, never deleted and re-created'
$dtm = @()
foreach ($root in @($serverSrc, $clientSrc)) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            # Comments and docs come out first: a comment naming the pattern it warns against is not the pattern.
            $code = (Get-Content -Raw -LiteralPath $_.FullName) `
                        -replace '(?s)/\*.*?\*/', '' -replace '(?m)^\s*//.*$', ''
            foreach ($m in [regex]::Matches($code, 'File\.Delete\(\s*([A-Za-z0-9_\.]+)\s*\)')) {
                $target = $m.Groups[1].Value
                $after = $code.Substring($m.Index, [Math]::Min(400, $code.Length - $m.Index))
                if ($after -match ("File\.Move\(\s*[A-Za-z0-9_\.]+\s*,\s*" + [regex]::Escape($target) + "\s*\)")) {
                    $dtm += "$($_.FullName): File.Delete($target) followed by File.Move(.., $target)"
                }
            }
        }
}
foreach ($d in $dtm) { Bad "$d - use KmhAtomicFile/JsonFileStore, which replace in place" }
if ($dtm.Count -eq 0) { Good 'no writer deletes a file and moves another over it' }


# Advice naming a key that does not exist looks applied and does nothing, which is worse than no advice.
Section '81. Config advice names a field that exists'
$advice = @()
$adviceOk = 0
if (Test-Path $serverSrc) {
    $configFiles = @{}
    Get-ChildItem -Path (Join-Path $serverSrc 'Features') -Recurse -Filter '*Config.cs' -File |
        ForEach-Object { $configFiles[$_.BaseName] = (Get-Content -Raw -LiteralPath $_.FullName) }
    # File names differ from class names (Mods.json is ModPolicyConfig), so paths resolve against every config class.
    $allConfigSrc = ($configFiles.Values -join "`n")

    Get-ChildItem -Path $serverSrc -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $code = (Get-Content -Raw -LiteralPath $_.FullName) `
                        -replace '(?s)/\*.*?\*/', '' -replace '(?m)^\s*//.*$', ''
            foreach ($m in [regex]::Matches($code, 'Config/([A-Za-z]+)\.json\s*->\s*([A-Za-z][A-Za-z0-9_.]*)')) {
                $path = $m.Groups[2].Value
                # Every segment, nested or not, must be a declared config property, or no owner can set it.
                $bad = @($path -split '\.' | Where-Object {
                    $allConfigSrc -notmatch ("(?m)public\s+[A-Za-z0-9_<>\[\]\?]+\s+" + [regex]::Escape($_) + "\s*\{\s*get")
                })
                if ($bad.Count -gt 0) {
                    $advice += "$($_.Name):$($m.Groups[1].Value).json -> $path (no such field: $($bad -join ', '))"
                } else { $adviceOk++ }
            }
        }
}
foreach ($a in $advice) { Bad "$a - an owner following this edits a key nothing reads" }
if ($advice.Count -eq 0) { Good "$adviceOk config path(s) named in owner-facing text all exist" }


# C# runs static initializers in textual order, so a field built from one declared below silently captures its
# default (a zero poll interval floods the directory). Expression-bodied `=>` members run when called, so are exempt.
Section '82. No static field is built from one declared later'
$initOrder = @()
foreach ($root in @($serverSrc, $clientSrc)) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $code = (Get-Content -Raw -LiteralPath $_.FullName) `
                        -replace '(?s)/\*.*?\*/', '' -replace '(?m)^\s*//.*$', ''
            $decls = [regex]::Matches($code,
                '(?m)^\s*(?:public|internal|private|protected)\s+static\s+(?:readonly\s+)?' +
                '[A-Za-z_][A-Za-z0-9_<>,\.\[\]\?]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*=(?!>)\s*([^;]+);')
            $pos = @{}
            foreach ($d in $decls) { if (-not $pos.ContainsKey($d.Groups[1].Value)) { $pos[$d.Groups[1].Value] = $d.Index } }
            foreach ($d in $decls) {
                $name = $d.Groups[1].Value
                $init = $d.Groups[2].Value
                foreach ($other in $pos.Keys) {
                    if ($other -eq $name -or $pos[$other] -le $d.Index) { continue }
                    if ($init -match ("(?<![A-Za-z0-9_.])" + [regex]::Escape($other) + "(?![A-Za-z0-9_])")) {
                        $initOrder += "$($_.Name): '$name' is built from '$other', which is declared below it"
                    }
                }
            }
        }
}
foreach ($i in $initOrder) { Bad "$i - it reads the default value, not the initializer" }
if ($initOrder.Count -eq 0) { Good 'no static field is initialized from one declared later in its own file' }


Section '83. Config enforcement stays inside the managed set'
# Enforcement may only touch the flat managed set; walking the live Config folder sweeps in other launchers' caches.
$scope = @()
$scopeRoots = @(
    (Join-Path $clientSrc 'Features\Enforcement'),
    (Join-Path $clientSrc 'Patches')
)
$wholeTree = '(SearchOption\.AllDirectories|WipeContents|CopyDir)'
foreach ($root in $scopeRoots) {
    if (-not (Test-Path $root)) { Bad "contract 83 cannot read '$root'"; continue }
    Get-ChildItem -Path $root -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $name = $_.Name
            $code = (Get-Content -Raw -LiteralPath $_.FullName) `
                        -replace '(?s)/\*.*?\*/', '' -replace '(?m)^\s*//.*$', ''
            $n = 0
            foreach ($line in ($code -split "`r?`n")) {
                $n++
                if ($line -notmatch $wholeTree) { continue }
                # A walk of the server's downloaded profile folder is bounded by the profile, not by Config.
                if ($line -match 'profileFolder|GetProfileFolder|\bfolder\b|staging|Staging') { continue }
                $scope += "${name}:${n}: $($line.Trim())"
            }
        }
}
foreach ($i in $scope) { Bad "$i - enforcement must not walk or copy the whole Config folder" }
if ($scope.Count -eq 0) { Good 'no enforcement path walks, copies or wipes the whole Config folder' }

# The one rule that decides what is managed has to read the same on both sides of the wire.
$mgrClient = Join-Path $clientSrc 'Features\Enforcement\ConfigManifest.cs'
$mgrServer = Join-Path $serverSrc 'Features\Enforcement\ManagedPath.cs'
function Get-ManagedRule([string]$file) {
    $lines = Get-Content -LiteralPath $file
    $out = @()
    $depth = 0
    $inside = $false
    foreach ($line in $lines) {
        $t = $line.Trim()
        if (-not $inside) {
            if ($t -notmatch 'bool\s+IsManagedName\s*\(') { continue }
            $inside = $true
            continue
        }
        if ($t -eq '{' -and $depth -eq 0) { $depth = 1; continue }
        if ($t -eq '}' -and $depth -le 1) { break }
        if ($t -eq '' -or $t.StartsWith('//')) { continue }
        $out += ($t -replace '\s+', ' ')
    }
    return ($out -join ' | ')
}
if ((Test-Path $mgrClient) -and (Test-Path $mgrServer)) {
    $cRule = Get-ManagedRule $mgrClient
    $sRule = Get-ManagedRule $mgrServer
    if ($cRule.Length -lt 80) { Bad 'contract 83: IsManagedName could not be read out of the client source' }
    elseif ($cRule -ne $sRule) {
        Bad 'IsManagedName differs between the client and the server - a path one accepts the other would refuse'
        Bad "  client: $cRule"
        Bad "  server: $sRule"
    } else { Good "IsManagedName reads identically in both repos ($($cRule.Split('|').Count) statement(s))" }
} else { Bad 'contract 83 cannot find ConfigManifest.cs / ManagedPath.cs' }


Section '84. Every per-server client cache is dropped on a server switch'
# ClearAll is a hand list; a clearable cache missing from it shows the previous server's data, so name or excuse each.
$cacheFile = Join-Path $clientSrc 'SubProtocol\KmhClientCaches.cs'
# Not per-server, with the reason each one is kept across a switch.
$notPerServer = @{
    'KmhAccountStore'  = 'the account credential, which belongs to the player and not to any server'
    'KmhDirectoryCache' = 'the Global server list, cleared by ForgetDirectory when the directory changes'
    'ChatVideoCache'   = 'content-addressed video files on disk; re-downloading them per server is the cost it exists to avoid'
    'KmhPrepareJobStore' = 'the join being prepared, which has to outlive the restart it causes; cleared when that join completes or is cancelled'
}
if (-not (Test-Path $cacheFile)) { Bad "contract 84 cannot find KmhClientCaches.cs" }
else {
    $clearAll = Get-Content -Raw -LiteralPath $cacheFile
    $uncleared = @()
    $seen = @{}
    foreach ($f in (Get-ChildItem -Path (Join-Path $clientSrc 'Features') -Recurse -Filter *.cs -File |
                    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
        $code = (Get-Content -Raw -LiteralPath $f.FullName) `
                    -replace '(?s)/\*.*?\*/', '' -replace '(?m)^\s*//.*$', ''
        if ($code -notmatch '(?m)^\s*(?:public|internal)\s+static\s+void\s+(?:Clear|Reset)\s*\(\s*\)') { continue }
        foreach ($m in [regex]::Matches($code, '\bclass\s+(\w*(?:Cache|Store))\b')) {
            $name = $m.Groups[1].Value
            if ($seen.ContainsKey($name)) { continue }
            $seen[$name] = $true
            if ($clearAll -match [regex]::Escape($name)) { continue }
            if ($notPerServer.ContainsKey($name)) { continue }
            $uncleared += "$name ($($f.Name))"
        }
    }
    # An excuse for something that no longer exists hides the next real one.
    foreach ($k in $notPerServer.Keys) {
        if (-not $seen.ContainsKey($k)) { Bad "contract 84 excuses '$k', which no longer exists - drop the entry" }
        else { Good "not per-server: $k - $($notPerServer[$k])" }
    }
    foreach ($u in $uncleared) { Bad "$u is clearable but KmhClientCaches.ClearAll never names it - the next server would show this one's data" }
    if ($uncleared.Count -eq 0) { Good "every per-server cache ($($seen.Count) clearable type(s)) is dropped on a server switch" }
}


Section '85. Every message kind has a declared policy, and the policy matches the handler'
# Every kind is classified from source; one without a policy, or with a policy nothing enforces, fails rather than warns.
$trafficFile = Join-Path $serverSrc 'SubProtocol\KmhTrafficClass.cs'
$mutationFile = Join-Path $serverSrc 'Maintenance\KmhMutationClass.cs'
$featuresFile = Join-Path $serverSrc 'Features\FeaturesConfig.cs'
foreach ($f in @($trafficFile, $mutationFile, $featuresFile)) {
    if (-not (Test-Path $f)) { Bad "contract 85 cannot find $(Split-Path -Leaf $f)" }
}

# Which kinds each traffic set names. Anything unnamed is Interactive by the Of() default.
function Kinds-InSet($code, $setName) {
    $set = New-Object System.Collections.Generic.HashSet[string]
    $m = [regex]::Match($code, "HashSet<string>\s+$setName\s*=[^{]*\{(?<body>[^}]*)\}", 'Singleline')
    if (-not $m.Success) { return $set }
    foreach ($k in [regex]::Matches($m.Groups['body'].Value, 'KmhProtocol\.Kind\.(\w+)')) {
        [void]$set.Add($k.Groups[1].Value)
    }
    return $set
}
$traffic  = Code $trafficFile
$ctrlSet  = Kinds-InSet $traffic 'Control'
$bulkSet  = Kinds-InSet $traffic 'Bulk'
$bgSet    = Kinds-InSet $traffic 'Background'

$mutationCode = Code $mutationFile
$mutationKinds = New-Object System.Collections.Generic.HashSet[string]
foreach ($k in [regex]::Matches($mutationCode, 'KmhProtocol\.Kind\.(\w+)')) { [void]$mutationKinds.Add($k.Groups[1].Value) }

# FeatureForKind maps by wire PREFIX, not by constant, so the inventory counts the kinds each prefix covers.
$featureCode = Code $featuresFile
$featurePrefixes = @()
foreach ($m in [regex]::Matches($featureCode, 'StartsWith\("([^"]+)"\)')) { $featurePrefixes += $m.Groups[1].Value }
$featureGated = New-Object System.Collections.Generic.HashSet[string]
foreach ($n in $srvKinds.Keys) {
    foreach ($pfx in $featurePrefixes) { if ($srvKinds[$n].Value.StartsWith($pfx)) { [void]$featureGated.Add($n); break } }
}
if ($featurePrefixes.Count -eq 0) { Bad '85: no feature prefix is declared - every owner toggle would be inert' }

# Control is the class that must never be starved, so only the handshake and liveness kinds belong in it.
$controlAllowed = @('Hello', 'HelloAck', 'Ping', 'Pong', 'Incompatible')
foreach ($k in $ctrlSet) {
    if ($controlAllowed -notcontains $k) {
        Bad "85: '$k' is classed Control, which is the budget that keeps a session alive - only the handshake and liveness kinds belong there"
    }
}
Good "Control carries $($ctrlSet.Count) kind(s), all handshake/liveness"

# A Bulk kind is only worth the bulk budget while its transaction is open; without the authorisation hook it is a flood.
if ($traffic -notmatch 'BulkAuthorized') {
    Bad '85: a Bulk traffic class with no BulkAuthorized gate lets any client spend the transfer budget'
} else { Good "Bulk carries $($bulkSet.Count) kind(s), charged as Interactive until the sender holds an open transaction" }

# KmhMutationClass lists READS, so a new kind defaults to mutating and maintenance holds it; an enumerated list of
# mutations would fail open, so the check is that the default has not been inverted.
if ($mutationCode -notmatch '(?s)IsRead[^{]*\{.*?default:\s*return\s+false;') {
    Bad '85: KmhMutationClass no longer defaults an unknown kind to mutating - a new feature would move value through a freeze'
} else { Good 'an unclassified kind counts as a mutation, so maintenance holds it by default' }
# The reads it does name must still be reads: a write on this list passes straight through a freeze.
$namedReads = @()
foreach ($k in [regex]::Matches($mutationCode, 'KmhProtocol\.Kind\.(\w+)')) { $namedReads += $k.Groups[1].Value }
$writeShaped = @($namedReads | Where-Object { $srvKinds.ContainsKey($_) -and $srvKinds[$_].Value -match '\.(post|send|buy|bid|claim|deposit|withdraw|commit|apply|set|delete|remove|cancel)$' })
if ($writeShaped.Count -gt 0) {
    Bad "85: KmhMutationClass names write-shaped kind(s) as reads: $($writeShaped -join ', ')"
} else { Good "$($namedReads.Count) kind(s) named as reads, none of them write-shaped" }

# A server->client kind a client is allowed to SEND is how a modified client announces its own results.
$s2cWithServerHandler = @()
foreach ($n in ($srvKinds.Keys | Sort-Object)) {
    if ($srvKinds[$n].Dir -ne 's2c') { continue }
    if ($srvHandlers.Contains($n)) { $s2cWithServerHandler += $n }
}
if ($s2cWithServerHandler.Count -gt 0) {
    Bad "85: server->client kind(s) with a SERVER handler: $($s2cWithServerHandler -join ', ') - a client can send its own result back"
} else { Good 'no server->client kind is accepted from a client' }

# The reverse: a client handler for a client->server kind means the client accepts its own request shape as a response.
$c2sWithClientHandler = @()
foreach ($n in ($srvKinds.Keys | Sort-Object)) {
    if ($srvKinds[$n].Dir -ne 'c2s') { continue }
    if ($cliHandlers.Contains($n)) { $c2sWithClientHandler += $n }
}
if ($c2sWithClientHandler.Count -gt 0) {
    Bad "85: client->server kind(s) with a CLIENT handler: $($c2sWithClientHandler -join ', ') - the client would act on a forged request"
} else { Good 'no client->server kind is accepted by the client' }

# A kind with no direction and no handler is dead wire surface: a never-run branch waiting for a future field.
$dead = @()
foreach ($n in ($srvKinds.Keys | Sort-Object)) {
    if ($srvKinds[$n].Dir -ne 'none') { continue }
    if ($srvHandlers.Contains($n) -or $cliHandlers.Contains($n)) { continue }
    $dead += $n
}
if ($dead.Count -gt 0) { Warn "85: kinds with no direction and no handler: $($dead -join ', ')" }
else { Good 'no undirected, unhandled kinds' }

# Frames are capped by the codec and payloads by the inflate guard; losing either allows an unbounded allocation.
$codecPath = Join-Path $serverSrc 'SubProtocol\KmhFrameCodec.cs'
if (-not (Test-Path $codecPath)) { Bad '85: the frame codec is gone - there is nothing capping a frame' }
else {
    $codec = Code $codecPath
    if ($codec -notmatch 'Max') { Bad '85: the frame codec declares no maximum frame size' }
    else { Good 'the frame codec caps a single frame' }
    if ($codec -notmatch 'Inflate\s*\([^)]*\bint\s+maxBytes') {
        Bad '85: Inflate takes no output cap - a small frame can expand without bound'
    } else { Good 'inflating a frame is bounded by an explicit output cap' }
}

# An op-id makes a request retryable, so something has to decide whether a repeat is the same operation.
$routerCode = Code (Join-Path $serverSrc 'SubProtocol\KmhRouter.cs')
if ($routerCode -notmatch 'OpId') { Bad '85: the router never reads an op id - a retry cannot be told from a new request' }
elseif ($routerCode -notmatch 'KmhProtocol\.Kind\.OpResult') { Bad '85: the router never answers OpResult - a client cannot tell a deliberate repeat from a retry' }
else { Good 'an op-id request is always answered, so a client can retire it' }

$c2s = @($srvKinds.Keys | Where-Object { $srvKinds[$_].Dir -eq 'c2s' }).Count
$s2c = @($srvKinds.Keys | Where-Object { $srvKinds[$_].Dir -eq 's2c' }).Count
$none = @($srvKinds.Keys | Where-Object { $srvKinds[$_].Dir -eq 'none' }).Count
Good "inventory: $($srvKinds.Count) kind(s) - $c2s client->server, $s2c server->client, $none either/none; $($ctrlSet.Count) Control, $($bgSet.Count) Background, $($bulkSet.Count) Bulk, the rest Interactive; $($mutationKinds.Count) named as mutations; $($featureGated.Count) behind a feature toggle"


Section '86. A translated string says the same thing as the fallback compiled beside it'
# The Keyed English file always wins over the compiled fallback, so a fallback fix not copied into it is dead code.
$enKeyed = Join-Path $clientRoot 'Languages\English\Keyed\KMH.xml'
if (-not (Test-Path $enKeyed)) { Bad '86: the English Keyed file is missing' }
else {
    [xml]$doc = Get-Content -Raw -LiteralPath $enKeyed
    $en = @{}
    foreach ($n in $doc.LanguageData.ChildNodes) {
        if ($n.NodeType -eq 'Element') { $en[$n.Name] = [string]$n.InnerText }
    }

    # Colour markup lives in the call site only: a raw <color> tag in a Keyed file fails the WHOLE file (see 71).
    function NormText($t) {
        if ($null -eq $t) { return '' }
        $t = [regex]::Replace($t, '</?(?:color|b|i|size)(?:=[^>]*)?>', '')
        $t = $t -replace [char]0x2026, '...' -replace [char]0x2019, "'" -replace [char]0x2018, "'"
        $t = $t -replace [char]0x201C, '"' -replace [char]0x201D, '"'
        $t = $t -replace [char]0x2013, '-' -replace [char]0x2014, '-'
        return ($t -replace '\s+', ' ').Trim()
    }

    # One call, then any number of adjacent literals joined with +.
    $call = [regex]'KmhText\.Get\(\s*"(KMH[A-Za-z0-9_.]+)"\s*,\s*'
    $lit  = [regex]'\G\s*"((?:[^"\\]|\\.)*)"\s*(\+)?'
    $drift = @(); $checked = 0; $probe = 0
    foreach ($f in (Get-ChildItem -Path $clientSrc -Recurse -Filter *.cs -File |
                    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
        $src = [System.IO.File]::ReadAllText($f.FullName)
        foreach ($m in $call.Matches($src)) {
            $key = $m.Groups[1].Value
            $at  = $m.Index + $m.Length
            $text = ''
            while ($true) {
                $lm = $lit.Match($src, $at)
                if (-not $lm.Success) { break }
                $text += $lm.Groups[1].Value -replace '\\n', "`n" -replace '\\"', '"' -replace '\\\\', '\'
                $at = $lm.Index + $lm.Length
                if (-not $lm.Groups[2].Success) { break }
            }
            # The suite deliberately asks for a key that does not exist, to prove the miss is visible.
            if (-not $en.ContainsKey($key)) {
                if ($key -eq 'KMH_Definitely_Not_A_Real_Key') { $probe++; continue }
                $drift += "$key is used in $($f.Name) but the English Keyed file has no such key"
                continue
            }
            $checked++
            if ((NormText $text) -ne (NormText $en[$key])) {
                $drift += "$key ($($f.Name)): the code says '$((NormText $text))' but the file says '$((NormText $en[$key]))'"
            }
        }
    }
    foreach ($d in $drift) { Bad "86: $d" }
    if ($drift.Count -eq 0) {
        Good "$checked compiled-in fallback(s) match the English Keyed file ($probe deliberate probe key)"
    }
}

# Read anywhere but its own declaration; the declaring file counts, as clamping or deriving from a field reads it.
function FieldIsRead($name, $declPath, $declText, $allCode) {
    $word = [regex]("\b" + [regex]::Escape($name) + "\b")
    $decl = [regex]("public\s+[\w<>,\[\]\?\.]+\s+" + [regex]::Escape($name) + "\s*(?:\{\s*get;|=>)")

    foreach ($line in ($declText -split "`n")) {
        if (-not $word.IsMatch($line)) { continue }
        if ($decl.IsMatch($line)) { continue }
        return $true
    }
    foreach ($path in $allCode.Keys) {
        if ($path -eq $declPath) { continue }
        if ($path -match 'SelfTest') { continue }      # a test reading it is not a behaviour
        if ($word.IsMatch($allCode[$path])) { return $true }
    }
    return $false
}

Section '87. Every config field a server writes down is read by something'
# A generated field nothing reads is a setting an owner can change with no effect, indistinguishable from a bug.
$configFiles = Get-ChildItem -Path $serverSrc -Recurse -File |
               Where-Object { $_.Name -match '(Config|Tuning)\.cs$' -and $_.FullName -notmatch '\\(bin|obj)\\' }

# Every .cs in both trees, once - the read sites can be anywhere.
$allCode = @{}
foreach ($f in (Get-ChildItem -Path $serverSrc, $clientSrc -Recurse -Filter *.cs -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
    $allCode[$f.FullName] = [System.IO.File]::ReadAllText($f.FullName)
}

# Read only by reflection or by name from a file, with the reason each one is excused.
$reflectionOnly = @{
    'SchemaVersion' = 'read by the migration harness by name, not by property access'
}

$deadFields = @(); $deadAccessors = @(); $checkedFields = 0; $checkedAccessors = 0
foreach ($cf in $configFiles) {
    $text = Code $cf.FullName

    # Serialized settings: a public auto-property with a setter and no [JsonIgnore] on its line.
    foreach ($m in [regex]::Matches($text, '(?m)^(?<line>.*public\s+(?!static)[\w<>,\[\]\?\.]+\s+(?<name>\w+)\s*\{\s*get;\s*set;.*)$')) {
        if ($m.Groups['line'].Value -match 'JsonIgnore') { continue }
        $name = $m.Groups['name'].Value
        if ($reflectionOnly.ContainsKey($name)) { continue }
        $checkedFields++
        if (-not (FieldIsRead $name $cf.FullName $text $allCode)) { $deadFields += "$($cf.Name).$name" }
    }

    # Derived accessors: not owner-editable, but one with no consumer is dead code on a config model.
    foreach ($m in [regex]::Matches($text, '\[(?:Newtonsoft\.Json\.)?JsonIgnore\]\s*public\s+[\w<>,\[\]\?\.]+\s+(?<name>\w+)\s*(?:=>|\{)')) {
        $name = $m.Groups['name'].Value
        if ($reflectionOnly.ContainsKey($name)) { continue }
        $checkedAccessors++
        if (-not (FieldIsRead $name $cf.FullName $text $allCode)) { $deadAccessors += "$($cf.Name).$name" }
    }
}

foreach ($d in $deadFields)    { Bad "87: $d is generated into a config file but nothing reads it - an owner can change it with no effect" }
foreach ($d in $deadAccessors) { Bad "87: $d is a derived accessor on a config model with no consumer - dead code" }
if ($deadFields.Count -eq 0 -and $deadAccessors.Count -eq 0) {
    Good "$checkedFields config field(s) and $checkedAccessors derived accessor(s) all have a reader"
}

Section '88. The heartbeat payload the last release signs is still accepted'
# 73 proves the repos agree, not that they match what is deployed: Global updates alone, so a field added on both
# sides refuses every released server unless Global keeps and verifies the previous form.
$gblReq88 = Join-Path (Split-Path -Parent $ServerRoot) 'KMH-Global\src\KmhGlobal\Servers\HeartbeatRequest.cs'
$gblSvc88 = Join-Path (Split-Path -Parent $ServerRoot) 'KMH-Global\src\KmhGlobal\Servers\HeartbeatService.cs'
if (-not (Test-Path $globalRoot)) {
    NoGlobal 'the released-payload check did not run'
} elseif (-not (Test-Path $gblReq88)) {
    Bad '88: KMH-Global has no HeartbeatRequest.cs - the released-payload check did not run'
} else {
    $reqText88 = Code $gblReq88
    $m88 = [regex]::Match($reqText88, 'CanonicalV1\(\)\s*=>[\s\S]*?\.Build\(\)')
    if (-not $m88.Success) {
        Bad '88: KMH-Global has no CanonicalV1() - every server on the last release is refused as forged'
    } else {
        # Frozen: the fields the shipped release signs, so an edit here is always wrong.
        $released88 = @('ServerId','Generation','Sequence','IssuedAtUnix','Nonce','Status','Endpoint','Players',
                        'MaxPlayers','RimWorldVersion','RwtVersion','KmhVersion','Protocol','CapabilityHash',
                        'ManifestRevision','ManifestHash','MetadataRevision')
        $got88 = @([regex]::Matches($m88.Value, '\.Add\(\s*([A-Za-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value })
        if (($got88 -join '|') -ne ($released88 -join '|')) {
            Bad ("88: CanonicalV1() no longer matches what v1.3.1 signs - got [$($got88 -join ', ')]")
        } else {
            Good "CanonicalV1() still spells the $($released88.Count) fields v1.3.1 signs"
        }

        $svcText88 = Code $gblSvc88
        if ($svcText88 -notmatch 'CanonicalV1\(\)') {
            Bad '88: HeartbeatService never verifies against CanonicalV1(), so keeping it changes nothing'
        } else {
            Good 'the heartbeat verifier falls back to the released payload before refusing a signature'
        }

        # On the legacy path an unsigned field must not change stored state, or an old signature grants it unsigned.
        if ($svcText88 -notmatch 'legacySigned\s*\?') {
            Bad '88: the legacy path takes requires_password from the request - an unsigned field an old sender can set'
        } else {
            Good 'a field the released form does not sign cannot change stored state'
        }

        # Field names and order are not the bytes. The pinned vector is what a reorder actually changes.
        $vec88 = Select-String -Path (Join-Path (Split-Path -Parent $ServerRoot) `
                                      'KMH-Global\tests\KmhGlobal.Tests\LegacyHeartbeatTests.cs') `
                               -Pattern 'ReleasedHeartbeatVector\s*=\s*"([0-9a-f]{64})"' -ErrorAction SilentlyContinue
        if (-not $vec88) {
            Bad '88: the released heartbeat has no pinned byte vector, so reordering it would go unnoticed'
        } elseif ($vec88.Matches[0].Groups[1].Value -ne '17be4e8712cf2915bfd08bf847c68c32690014ba57686d97aa08f775d664b698') {
            Bad '88: the pinned released-heartbeat vector changed - the bytes v1.3.1 signs cannot change'
        } else {
            Good 'the released heartbeat payload is pinned by byte vector, not just by field order'
        }
    }
}

# The other payloads a released server signs must not change without a legacy form. Addon-only, so no Global needed.
$frozen88 = @{
    'RegistrationChallenge' = @('ChallengeId','ServerId','PublicKey')
    'Metadata'              = @('ServerId','IssuedAtUnix','Nonce','Name','Description','Region','Languages',
                                'Tags','DiscordUrl','WebsiteUrl','RulesText','Visibility')
}
$addonCode88 = Get-ChildItem (Join-Path $ServerRoot 'Source') -Recurse -Filter *.cs |
               ForEach-Object { Code $_.FullName } | Out-String
foreach ($domain88 in $frozen88.Keys) {
    $dm88 = [regex]::Match($addonCode88,
        "CanonicalPayload\.For\(\w*SigningDomains\.$domain88\)([\s\S]*?)\.Build\(\)")
    if (-not $dm88.Success) {
        Bad "88: the addon no longer signs a $domain88 payload"
        continue
    }
    $fields88 = @([regex]::Matches($dm88.Groups[1].Value, '\.Add(?:List)?\(\s*([A-Za-z0-9_]+)') |
                  ForEach-Object { $_.Groups[1].Value })
    if (($fields88 -join '|') -ne ($frozen88[$domain88] -join '|')) {
        Bad ("88: the $domain88 payload changed since v1.3.1 - every released server's signature over it " +
             "stops verifying. Got [$($fields88 -join ', ')]")
    } else {
        Good "$domain88 still signs the $($fields88.Count) fields v1.3.1 signs"
    }
}


Section '89. The public product name is the same one in every place a player reads it'
# Several independent files decide the name a player sees; a rename landing in only some makes the mod two things.
$brandProduct = 'KMH: Colony Nexus'
$brandShort   = 'Colony Nexus'
$brandAddon   = 'KMH: Colony Nexus Server Addon'
$brandGlobal  = 'Colony Nexus Global'
$brandLegacy  = 'KMH Patch for RimWorld Together'

$about89 = Join-Path $ClientRoot 'About\About.xml'
if (-not (Test-Path $about89)) {
    Bad '89: About.xml is missing, so nothing states the published mod name'
} else {
    $aboutName89 = ([xml](Get-Content $about89 -Raw)).ModMetaData.name
    if ($aboutName89 -ne $brandProduct) { Bad "89: About.xml publishes '$aboutName89', not '$brandProduct'" }
    else { Good "About.xml publishes $brandProduct" }
}

$cCode89 = Code (Join-Path $clientSrc 'Constants.cs')
$cName89 = [regex]::Match($cCode89, 'DisplayName\s*=\s*"([^"]*)"').Groups[1].Value
if ($cName89 -ne $brandProduct) { Bad "89: the client's Constants.DisplayName is '$cName89', not '$brandProduct'" }
else { Good "the client's About / Welcome / What's New all read $brandProduct" }

# The loader is a separate assembly and cannot reference Constants, so its literal is compared here instead.
$loader89 = Code (Join-Path $clientSrc 'Loader\KMHPatchMod.cs')
$settings89 = [regex]::Match($loader89, 'SettingsCategory\(\)\s*=>\s*"([^"]*)"').Groups[1].Value
if ($settings89 -ne $cName89) {
    Bad "89: Mod Options says '$settings89' while the rest of the client says '$cName89'"
} else { Good "the Mod Options category matches the client's own name" }

$tab89 = Join-Path $ClientRoot '1.6\Defs\MainButtonDefs\KMH.xml'
if (-not (Test-Path $tab89)) { Bad '89: the main tab def is missing' }
else {
    $tabXml89 = [xml](Get-Content $tab89 -Raw)
    if ($tabXml89.Defs.MainButtonDef.label -ne $brandShort) {
        Bad "89: the in-game tab is labelled '$($tabXml89.Defs.MainButtonDef.label)', not '$brandShort'"
    } elseif ($tabXml89.Defs.MainButtonDef.defName -ne 'KMH') {
        Bad "89: the tab defName moved to '$($tabXml89.Defs.MainButtonDef.defName)' - it is a durable key"
    } else { Good "the in-game tab is labelled $brandShort and keeps defName KMH" }
}

$sCode89 = Code (Join-Path $serverSrc 'Constants.cs')
$sName89 = [regex]::Match($sCode89, 'DisplayName\s*=\s*"([^"]*)"').Groups[1].Value
if ($sName89 -ne $brandAddon) { Bad "89: the addon calls itself '$sName89', not '$brandAddon'" }
else { Good "the addon's banners read $brandAddon" }

# RWT admits on the mod NAME a client reports, so the addon warns an owner whose required list holds the old name;
# that warning is useless if it names the wrong string.
$mp89 = Code (Join-Path $serverSrc 'Features\Mods\ModProfile.cs')
$kmhName89 = [regex]::Match($mp89, 'KmhModName\s*=\s*"([^"]*)"').Groups[1].Value
$legacy89  = [regex]::Match($mp89, 'LegacyKmhModName\s*=\s*"([^"]*)"').Groups[1].Value
if ($kmhName89 -ne $aboutName89) {
    Bad "89: ModProfile.KmhModName is '$kmhName89' but About.xml publishes '$aboutName89'"
} elseif ($legacy89 -ne $brandLegacy) {
    Bad "89: ModProfile.LegacyKmhModName is '$legacy89' - v1.3.0 shipped '$brandLegacy' and that cannot change"
} else { Good 'the addon knows both the current and the pre-1.3.1 mod name a client can report' }
if ($mp89 -notmatch 'StaleKmhEntry' -or $mp89 -notmatch 'WarnStaleKmhName') {
    Bad '89: nothing warns an owner whose required list still names the pre-1.3.1 KMH mod'
} else { Good 'an owner requiring the pre-1.3.1 KMH name is told, by name, what to fix' }

# An upgrade makes an UNCHANGED entry wrong, so warnings tied to the profile changing would never fire.
$hashIf89 = $mp89.IndexOf("if (snap.Hash != _state.Hash)")
if ($hashIf89 -lt 0) { Bad '89: the mod profile no longer has a hash-change branch to place the warnings outside of' }
else {
    $open89 = $mp89.IndexOf('{', $hashIf89); $depth89 = 0; $end89 = -1
    for ($i89 = $open89; $i89 -lt $mp89.Length; $i89++) {
        if ($mp89[$i89] -eq '{') { $depth89++ }
        elseif ($mp89[$i89] -eq '}') { $depth89--; if ($depth89 -eq 0) { $end89 = $i89; break } }
    }
    $inside89 = @()
    foreach ($w89 in @('WarnUnmatchableRwt(snap);', 'WarnStaleKmhName(snap);')) {
        $at89 = $mp89.IndexOf($w89)
        if ($at89 -lt 0) { $inside89 += "$w89 is never called" }
        elseif ($end89 -gt 0 -and $at89 -gt $open89 -and $at89 -lt $end89) { $inside89 += $w89 }
    }
    if ($inside89.Count -gt 0) {
        Bad ("89: mod name warning(s) only fire when the profile CHANGES, which is never on the boot that " +
             "makes the name wrong: " + ($inside89 -join ', '))
    } elseif ($mp89 -notmatch 'ShouldSayOnce\(') {
        Bad '89: the name warnings are not latched, so they would print on every profile read'
    } else { Good 'both mod name warnings run on every read, latched to once per name' }
}

$dash89 = Code (Join-Path $globalRoot 'src\KmhGlobal\Api\DashboardPage.cs')
if (-not (Test-Path $globalRoot)) {
    NoGlobal 'the Global dashboard header was not checked'
} elseif ($dash89 -notmatch [regex]::Escape("<header><h1>$brandGlobal</h1>")) {
    Bad "89: the Global dashboard header is not '$brandGlobal'"
} else { Good "the Global dashboard is headed $brandGlobal" }

# Shipped content only: source keeps the old name as a compatibility constant on purpose.
$shipped89 = @()
foreach ($d in @('About', '1.6\Defs', 'Languages')) {
    $p89 = Join-Path $ClientRoot $d
    if (Test-Path $p89) { $shipped89 += Get-ChildItem $p89 -Recurse -File -Include *.xml -ErrorAction SilentlyContinue }
}
if ($shipped89.Count -lt 4) { Bad "89: only $($shipped89.Count) shipped content file(s) found - the scan did not run" }
else {
    $stale89 = @($shipped89 | Where-Object {
        (Get-Content $_.FullName -Raw) -match 'KMH Patch|KMH Server Addon|KMH Global|KMH tab'
    })
    if ($stale89.Count -gt 0) {
        Bad "89: $($stale89.Count) shipped content file(s) still carry a retired name: $(($stale89 | Select-Object -First 3 | ForEach-Object { $_.Name }) -join ', ')"
    } else { Good "$($shipped89.Count) shipped content file(s) carry no retired product name" }
}


Section '90. A payout that closes its row before crediting can be resumed'
# Market, auctions, wants and mail close a row, persist, then credit: safe for duplicates, not for losses. Each pays
# on a durable marker so a replay is a no-op, and each has a boot caller, or the evidence is never read.
$boot90 = Code (Join-Path $serverSrc 'Main.cs')
$mail90 = Code (Join-Path $serverSrc 'Features\Mail\MailStore.cs')

if ($mail90 -notmatch '_pendingPayouts' -or $mail90 -notmatch 'class PendingPayout') {
    Bad '90: mail no longer writes a payout down before it closes an attachment'
} elseif ($mail90 -notmatch 'PendingPayouts\s*\{\s*get;\s*set;') {
    Bad '90: the mail payout record is not persisted, so it dies with the process that wrote it'
} else { Good 'a mail payout is written down in the same file as the attachment it closes' }

# Every recorded payout must have a settler, or the record is written and never acted on.
$rec90 = ([regex]::Matches($mail90, 'RecordPayoutLocked\(')).Count
$set90 = ([regex]::Matches($mail90, 'SettlePayout\(')).Count
if ($rec90 -lt 6) { Bad "90: only $rec90 mail payout site(s) record evidence - the paths that close an attachment are 7" }
elseif ($set90 -lt $rec90 - 1) { Bad "90: $rec90 mail payout(s) are recorded but only $set90 settle site(s) exist" }
else { Good "$rec90 mail payout site(s) record evidence and $($set90 - 1) settle it" }

if ($mail90 -notmatch 'DepositEscrowOnce\(') {
    Bad '90: a mail payout is credited without a marker, so resuming one pays twice'
} else { Good 'a mail payout is credited on its own marker, so a resumed one pays nothing twice' }

if ($boot90 -notmatch 'MailStore\.RecoverPendingPayouts\(\)') {
    Bad '90: boot never resumes an interrupted mail payout'
} elseif ($boot90.IndexOf('MailStore.LoadFromDisk()') -ge $boot90.IndexOf('MailStore.RecoverPendingPayouts()')) {
    Bad '90: mail payout recovery runs before the records are loaded, so it always finds nothing'
} else { Good 'boot resumes interrupted mail payouts, after loading them' }

$marker90 = @{
    'Features\Auctions\AuctionStore.cs'     = 'an auction delivery or payout'
    'Features\WantBoard\WantStore.cs'       = 'a want board payout'
    'Features\Marketplace\MarketplaceStore.cs' = 'a marketplace settlement'
}
foreach ($f90 in $marker90.Keys) {
    $t90 = Code (Join-Path $serverSrc $f90)
    if (-not $t90) { Bad "90: $f90 is missing - the scan did not run"; continue }
    if ($t90 -notmatch 'DepositEscrowOnce\(') { Bad "90: $($marker90[$f90]) no longer pays on a marker" }
    else { Good "$($marker90[$f90]) pays on a marker" }
}
if ($boot90 -notmatch 'MarketplaceStore\.RecoverPendingReserves\(\)') {
    Bad '90: boot never returns an interrupted marketplace reservation'
} else { Good 'boot returns interrupted marketplace reservations' }


# Every value-moving sweep step must run twice in the duplicate-run suite; derived, so a new job cannot skip coverage.
$sweep90 = Code (Join-Path $serverSrc 'Maintenance\ExpirySweeper.cs')
$dup90   = Code (Join-Path $serverSrc 'Maintenance\KmhJobDuplicateRunSelfTest.cs')
$steps90 = @([regex]::Matches($sweep90, 'Step\(\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
# Self-tests register probe jobs on private loops of their own; those never reach the server's scheduler.
foreach ($reg90 in [regex]::Matches((Get-ChildItem $serverSrc -Recurse -Filter *.cs |
                                     Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'SelfTest' } |
                                     ForEach-Object { Code $_.FullName } | Out-String),
                                    'Register\(\s*"([^"]+)"[\s\S]{0,200}?KmhJobKind\.Mutating')) {
    $steps90 += $reg90.Groups[1].Value
}
# Spelled out per step: a loose match finds "snapshot" inside GetSnapshotFor and calls the job covered.
$covered90 = @{
    'marketplace expiry' = 'marketplace expiry run twice'
    'quest expiry'       = 'quest expiry run twice'
    'site production'    = 'site reward cycle run twice'
    'auction settlement' = 'auction settlement run twice'
    'want expiry'        = 'want expiry run twice'
    'mail'               = 'mail expiry run twice'
    'stale deposits'     = 'stale pending deposit is reverted once'
    'world-engine'       = 'world engine run twice'
    'frontier-director'  = 'frontier director run twice'
    'economy reset retry' = 'economy reset retry run twice'
    'season reset retry' = 'season reset retry run twice'
    'owed house fees'    = 'owed house fee run twice'
}
# Moves no value: nothing to pay twice, so running one again costs an owner nothing.
$noValue90 = @{
    'guild invites'         = 'prunes expired invites - no silver, no items'
    'stale publish staging' = 'deletes abandoned upload parts'
    'stale snapshots'       = 'drains an invalidation queue'
    'snapshot requests'     = 'consumes snapshot requests'
    'snapshot pruning'      = 'deletes old snapshot files'
    'status export'         = 'writes a status file for the panel'
    'fragment transfers'    = 'drops abandoned inbound transfers - no silver, no items'
}
if ($steps90.Count -lt 13) { Bad "90: found only $($steps90.Count) sweep step(s) - the scan did not run" }
else {
    $uncovered90 = @(); $unclassified90 = @()
    foreach ($s90 in ($steps90 | Sort-Object -Unique)) {
        if ($noValue90.ContainsKey($s90)) { continue }
        if (-not $covered90.ContainsKey($s90)) { $unclassified90 += $s90; continue }
        if ($dup90 -notmatch [regex]::Escape($covered90[$s90])) { $uncovered90 += $s90 }
    }
    if ($unclassified90.Count -gt 0) {
        Bad ("90: scheduled job(s) nobody has said move value or not: " + ($unclassified90 -join ', '))
    } elseif ($uncovered90.Count -gt 0) {
        Bad ("90: value-moving job(s) with no duplicate-run check: " + ($uncovered90 -join ', '))
    } else {
        Good ("$($steps90.Count) scheduled step(s) - all $($covered90.Count) value-moving ones are run twice " +
              "by the suite, $($noValue90.Count) exempt with a reason")
    }
}

Section '91. Every Federation reason a server or Global stores has words in the client'
# Codes stay in storage and logs; one with no case in FederationText.Reason reaches a player as itself.
$codes91 = New-Object System.Collections.Generic.List[string]
$haveGlobal91 = Test-Path $globalRoot73
$leaveCount91 = 0
if (-not $haveGlobal91) { NoGlobal 'the reasons Global stores were not read' }
else {
    $leave91 = [regex]::Match((Code (Join-Path $globalRoot73 'src\KmhGlobal\Federation\FederationModels.cs')),
                              'class LeaveReason\s*\{([\s\S]*?)\n\}')
    foreach ($m in [regex]::Matches($leave91.Groups[1].Value, 'const string \w+\s*=\s*"([^"]*)"')) {
        if ($m.Groups[1].Value) { $codes91.Add($m.Groups[1].Value) } }
    $leaveCount91 = $codes91.Count
    foreach ($m in [regex]::Matches((Get-Content (Join-Path $globalRoot73 'src\KmhGlobal\Persistence\Migrations\046_federations.sql') -Raw),
                                    "disband_reason = CASE[^']*'([^']+)'")) { $codes91.Add($m.Groups[1].Value) }
    foreach ($f91 in Get-ChildItem (Join-Path $globalRoot73 'src\KmhGlobal') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
        foreach ($m in [regex]::Matches((Code $f91.FullName), 'DemandReproofAsync\([^;]*?"([^"]+)"')) { $codes91.Add($m.Groups[1].Value) } }
}
$srvDetail91 = [regex]::Match((Code (Join-Path $serverSrc 'Features\Global\Guilds\FederatedGuildLink.cs')),
                              'class FederatedLinkDetail\s*\{([\s\S]*?)\n    \}')
$srvCount91 = 0
foreach ($m in [regex]::Matches($srvDetail91.Groups[1].Value, 'const string \w+\s*=\s*"([^"]+)"|=>\s*"([a-z_]+:)"')) {
    $codes91.Add($m.Groups[1].Value + $m.Groups[2].Value); $srvCount91++ }

$reason91 = [regex]::Match((Code (Join-Path $clientSrc 'Features\Federation\FederationText.cs')),
                           'public static string Reason\(([\s\S]*?)public static string LinkDetail\(').Groups[1].Value
$cases91 = @([regex]::Matches($reason91, 'case "([^"]*)":') | ForEach-Object { $_.Groups[1].Value })
$prefixes91 = @([regex]::Matches($reason91, 'Actor\(raw, "([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$unworded91 = @($codes91 | Sort-Object -Unique | Where-Object {
    if ($_.EndsWith(':')) { $prefixes91 -notcontains $_ } else { $cases91 -notcontains $_ } })

$dialog91 = Code (Join-Path $clientSrc 'Features\Federation\Dialog_KMHFederation.cs')
$shown91 = [regex]::Matches($dialog91, '(s\.Link\.Detail|s\.Federation\.DisbandReason)(?!\.Length)').Count
$worded91 = [regex]::Matches($dialog91, 'FederationText\.(Reason|LinkDetail)\((s\.Link\.Detail|s\.Federation\.DisbandReason)\)').Count

if (($haveGlobal91 -and $leaveCount91 -lt 8) -or $srvCount91 -lt 7) {
    Bad "91: read $leaveCount91 Global leave reason(s) and $srvCount91 server detail code(s) - the scan did not run"
} elseif ($unworded91.Count -gt 0) {
    Bad ("91: stored reason(s) with no words in FederationText.Reason: " + ($unworded91 -join ', '))
} elseif ($shown91 -ne $worded91) {
    Bad "91: the federation window draws a stored reason or link detail without FederationText ($worded91 of $shown91 worded)"
} else {
    Good "91: all $(($codes91 | Sort-Object -Unique).Count) stored reason code(s) have client words, and the window shows none raw"
}

# Section 81 reads only code; owners follow SETUP.txt and the generated Config/README.txt, which drift just the same.
Section '92. Owner documentation names settings that exist'
$model92 = (Get-ChildItem -Path $serverSrc -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -match 'Config|Tuning|Settings|Rules' } |
    ForEach-Object { Code $_.FullName }) -join "`n"
$docs92 = [ordered]@{}
foreach ($rel in 'SETUP.txt', 'Templates\kmh-data\README.txt') {
    $p = Join-Path $ServerRoot $rel
    if (Test-Path $p) { $docs92[$rel] = [System.IO.File]::ReadAllText($p) }
}
$regen92 = Join-Path $serverSrc 'Maintenance\KmhConfigRegeneration.cs'
if (Test-Path $regen92) {
    # Only the README writer and its descriptions are owner text; the rest of the file is log lines and code.
    $readme92 = [regex]::Match([System.IO.File]::ReadAllText($regen92), '(?s)void WriteReadme\(\).*').Value
    $docs92['Config/README.txt'] = (([regex]::Matches($readme92, '"((?:[^"\\]|\\.)*)"') |
        ForEach-Object { $_.Groups[1].Value }) -join "`n")
}
$seg92 = '[A-Z][A-Za-z0-9]*(?:\[\d+\])?'
$key92 = "$seg92(?:\.$seg92)*"
$forms92 = @(
    "(?<![\w.$/\\-])($key92)\s*=\s*(?:true|false|-?\d|[A-Za-z""])",         # Key=value
    "(?<![\w.$/\\-])($key92)\s+\((?:default|\d)",                           # Key (default ...)
    "(?<![\w.$/\\-])($key92):\s*(?:true|false)\b",                          # Key: false
    """($key92)""\s*:",                                                      # "Key": in a JSON example
    "(?<![\w.$/\\-])([A-Z][a-z][A-Za-z0-9]*(?:\[\d+\])?(?:\.[A-Z][a-z][A-Za-z0-9]*(?:\[\d+\])?)+)(?![\w(])"   # Dotted.Path
)
# Economy mode names stand left of '=' in "Balanced=1/1" without being keys.
$notKeys92 = @('Balanced', 'Hardcore', 'Localized', 'Standard', 'Custom')
$named92 = 0
$stale92 = @()
foreach ($doc92 in $docs92.Keys) {
    $found92 = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($rx92 in $forms92) { foreach ($m in [regex]::Matches($docs92[$doc92], $rx92)) { [void]$found92.Add($m.Groups[1].Value) } }
    foreach ($k92 in $found92) {
        # Shouted prose ("WORKERS = SPEED") is emphasis, not a key; every KMH setting is PascalCase.
        if ($notKeys92 -contains $k92 -or $k92 -cmatch '^[A-Z0-9_]+$') { continue }
        $named92++
        $missing92 = @(($k92 -replace '\[\d+\]', '') -split '\.' | Where-Object {
            $model92 -notmatch ("(?m)public\s+[A-Za-z0-9_<>\[\],\?]+\s+" + [regex]::Escape($_) + "\s*\{\s*get") })
        if ($missing92.Count -gt 0) { $stale92 += "$doc92 names $k92 (no setting called $($missing92 -join ', '))" }
    }
}
foreach ($s in $stale92) { Bad "92: $s - an owner following it edits a key nothing reads" }
if ($named92 -lt 20) { Bad "92: only $named92 setting name(s) found in owner documentation - the scan did not run" }
elseif ($stale92.Count -eq 0) { Good "92: all $named92 setting name(s) in $($docs92.Keys -join ', ') exist" }

# Owners also read log lines and command replies; only the forms below are checked, not bare names in prose.
Section '93. Owner-facing messages name settings that exist'
$model93 = $model92 + "`n" + ((Get-ChildItem -Path $clientSrc -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -match 'Config|Tuning|Settings|Rules' } |
    ForEach-Object { Code $_.FullName }) -join "`n")
# "World.json WorldQuests.Enabled" is written "World.WorldQuests.Enabled" too; the file name is not a property.
$areas93 = @([regex]::Matches((Code (Join-Path $serverSrc 'Persistence\KmhDataPaths.cs')),
    'Sub\("Config"[^)]*\),\s*"(\w+?)(?:Config)?\.json"') | ForEach-Object { $_.Groups[1].Value })
$seg93 = '[A-Z][A-Za-z0-9]*(?:\[\d+\])?'
$key93 = "$seg93(?:\.$seg93)*"
$forms93 = @(
    "(?<![\w.$/\\-])($key93)\s*=\s*(?:true|false|-?\d|[A-Za-z])",          # Key=value
    "\\""($key93)\\""\s*:",                                                  # \"Key\": in a JSON example
    "(?<![\w.$/\\-])([A-Z][a-z][A-Za-z0-9]*(?:\[\d+\])?(?:\.[A-Z][a-z][A-Za-z0-9]*(?:\[\d+\])?)+)(?![\w(])"   # Dotted.Path
)
# "Disabled = accrues but not withdrawable" explains an access mode; "Restart=" is the systemd unit's policy.
$notKeys93 = @('Disabled', 'Restart')
$named93 = 0
$stale93 = @()
foreach ($f93 in @(Get-ChildItem -Path $serverSrc, $clientSrc -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'SelfTest|Migration' })) {
    # Self-tests and migrations name retired keys on purpose. Char literals go first so '"' cannot open a string.
    $code93 = (Code $f93.FullName) -replace "'(?:[^'\\\r\n]|\\.)'", "''"
    foreach ($lit93 in [regex]::Matches($code93, '(\$?)@?"((?:[^"\\\r\n]|\\.)*)"')) {
        $s93 = $lit93.Groups[2].Value
        if ($lit93.Groups[1].Value -eq '$') { $s93 = [regex]::Replace($s93, '\{[^{}]*\}', ' ') }
        for ($i93 = 0; $i93 -lt $forms93.Count; $i93++) {
            foreach ($m93 in [regex]::Matches($s93, $forms93[$i93])) {
                $k93 = $m93.Groups[1].Value
                if ($notKeys93 -contains $k93 -or $k93 -cmatch '^[A-Z0-9_]+$') { continue }
                $parts93 = @(($k93 -replace '\[\d+\]', '') -split '\.')
                # Features.Sites.Dto is a namespace; the Features.json file has no nested settings.
                if ($parts93[0] -eq 'Features' -and $parts93.Count -gt 2) { continue }
                if ($areas93 -contains $parts93[0]) { $parts93 = @($parts93 | Select-Object -Skip 1) }
                elseif ($i93 -eq 2 -and $model93 -notmatch ("(?m)public\s+[A-Za-z0-9_<>\[\],\?]+\s+" + [regex]::Escape($parts93[0]) + "\s*\{\s*get")) { continue }
                if ($parts93.Count -eq 0) { continue }
                $named93++
                $missing93 = @($parts93 | Where-Object {
                    $model93 -notmatch ("(?m)public\s+[A-Za-z0-9_<>\[\],\?]+\s+" + [regex]::Escape($_) + "\s*\{\s*get") })
                if ($missing93.Count -gt 0) {
                    $stale93 += "$($f93.FullName.Replace($ServerRoot, 'KMH-Server-Addon').Replace($ClientRoot, 'KMH-Patch')) says $k93 (no setting called $($missing93 -join ', '))"
                }
            }
        }
    }
}
if ($areas93.Count -lt 15) { Bad "93: only $($areas93.Count) config file name(s) read from KmhDataPaths.cs - the scan did not run" }
foreach ($s in ($stale93 | Sort-Object -Unique)) { Bad "93: $s - an owner following it edits a key nothing reads" }
if ($named93 -lt 50) { Bad "93: only $named93 setting name(s) found in messages - the scan did not run" }
elseif ($stale93.Count -eq 0) { Good "93: all $named93 setting name(s) in server and client messages exist" }

# A self-test run keeps an acceptance server, which holds a real registration, off the production Global by guarding
# the Global client's one network call. A second HTTP client aimed at Global would walk straight past that guard.
Section '94. Every outbound connection is the guarded Global client or named here with a reason'
$clients94 = [ordered]@{
    'Features\Global\KmhGlobalClient.cs'              = 'the Global client; its one send checks the self-test isolation rule'
    'Features\Media\KmhMediaFetch.cs'                 = 'chat images from players'' HTTPS hosts, private ranges refused; never Global'
    'Features\Discord\Core\DiscordCoordinatorLink.cs' = 'the Discord fleet coordinator, only with Discord on in coordinator mode; not Global'
}
$net94 = 'new\s+HttpClient\b|new\s+SocketsHttpHandler\b|new\s+HttpClientHandler\b|new\s+ClientWebSocket\b|WebRequest\.Create|' +
         'new\s+WebClient\b|new\s+TcpClient\b|new\s+UdpClient\b|new\s+Socket\s*\('
$found94 = @()
foreach ($f94 in Get-ChildItem $serverSrc -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'SelfTest' }) {
    if ((Code $f94.FullName) -match $net94) { $found94 += $f94.FullName.Substring($serverSrc.Length + 1) }
}
$unnamed94 = @($found94 | Where-Object { -not $clients94.Contains($_) })
$gone94 = @($clients94.Keys | Where-Object { $found94 -notcontains $_ })
# The check has to sit on the line that sends, or a new method in the client could send without it.
$gc94 = Code (Join-Path $serverSrc 'Features\Global\KmhGlobalClient.cs')
$sends94 = [regex]::Matches($gc94, '\bHttp\.SendAsync\(').Count
$send94 = [regex]::Match($gc94, 'Task<HttpResponseMessage>\s+Send\(HttpRequestMessage[\s\S]*?\n        \}').Value
$guarded94 = ($send94 -match 'KmhGlobalSafety\.MayContact\(\s*req\.RequestUri') -and ($send94 -match '\bHttp\.SendAsync\(')
if ($unnamed94.Count -gt 0) { Bad "94: outbound connection(s) nobody has said reach Global or not: $($unnamed94 -join ', ')" }
elseif ($gone94.Count -gt 0) { Bad "94: listed file(s) no longer open a connection, so the list is stale: $($gone94 -join ', ')" }
elseif ($sends94 -ne 1 -or -not $guarded94) {
    Bad "94: the Global client sends in $sends94 place(s), and its sending method $(if ($guarded94) { 'is' } else { 'is NOT' }) behind the self-test isolation check"
}
else { Good "94: $($found94.Count) file(s) open outbound connections, each named; the Global client's one send is behind the isolation check" }

# A guild name is reused once it disbands; value bound to the name would pass to the next guild that takes it.
Section '95. Guild economic ownership follows the incarnation, never the name'
$gi95 = Code (Join-Path $serverSrc 'Features\Guilds\GuildIdentity.cs')
$ts95 = Code (Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs')
$ss95 = Code (Join-Path $serverSrc 'Features\Sites\SiteStore.cs')
$gs95 = Code (Join-Path $serverSrc 'Features\Guilds\GuildStore.cs')
$so95 = Code (Join-Path $serverSrc 'Features\Sites\SiteOwnership.cs')
$bad95 = @()
# Read under the treasury lock while GuildStore calls the treasury under its own, so a lock here is an AB-BA deadlock.
if ($gi95 -eq '') { $bad95 += 'GuildIdentity.cs is missing' }
elseif ($gi95 -match '\block\s*\(|Monitor\.|SemaphoreSlim|ReaderWriterLock|Mutex\b|GuildStore\.') { $bad95 += 'GuildIdentity takes a lock or calls GuildStore - the treasury reads it under its own lock' }
# A bare GetOrCreateLocked(<guild>, isGuildOwned: true) anywhere else re-creates a dead guild's vault.
$direct95 = [regex]::Matches($ts95, 'GetOrCreateLocked\(\s*[\w\.]+\s*,\s*isGuildOwned:\s*true\s*\)').Count
$helper95 = [regex]::Matches($ts95, 'IsLive\(\s*guildName\s*,\s*identity\s*\)\)\s*return\s+GetOrCreateLocked\(\s*guildName\s*,\s*isGuildOwned:\s*true\s*\)').Count
if ($direct95 -ne 1 -or $helper95 -ne 1) { $bad95 += "guild vaults are created in $direct95 place(s), not only behind the identity check in LiveGuildVaultLocked" }
foreach ($m in 'DepositGuildSilver','DepositGuildSilverOnce','DepositGuildItem','DepositGuildItemOnce') {
    if ($ts95 -notmatch ("public static bool $m\([^)]*string identity = null\)[\s\S]{0,700}?LiveGuildVaultLocked\(")) { $bad95 += "$m does not credit through LiveGuildVaultLocked with an identity" }
}
# A row with no identity is refunded first; the credit itself must name the incarnation the donation was made to.
if ($ts95 -notmatch 'KindGuildDonate:\s*if \(string\.IsNullOrEmpty\(d\.GuildIdentity\)\)[\s\S]{0,400}?RevertPendingLocked\(v, d\); return;\s*\}\s*TreasurySnapshot gv = LiveGuildVaultLocked\(d\.GuildName, d\.GuildIdentity,') { $bad95 += 'a donation commit can re-create a disbanded guild''s vault' }
# The disband publishes the guild dead (the commit) before its vault is handed back.
$leave95 = [regex]::Match($gs95, 'public static bool Leave\(string username[\s\S]*?\n        \}').Value
$iDrop = $leave95.IndexOf('_incarnation.Remove(gname)'); $iCommit = $leave95.IndexOf('CommitAuthorityLocked(before)'); $iMove = $leave95.IndexOf('MoveGuildVaultToPersonal(')
if ($iDrop -lt 0 -or $iCommit -lt 0 -or $iMove -lt 0 -or -not ($iDrop -lt $iCommit -and $iCommit -lt $iMove)) {
    $bad95 += "Leave does not drop the identity and commit before handing the vault back (drop=$iDrop commit=$iCommit move=$iMove)"
}
if ($gs95 -notmatch 'if \(SaveToDisk\(\)\) \{ _authorityGeneration\+\+; PublishIdentityLocked\(\); return true; \}') { $bad95 += 'a committed authority change does not publish the identity set' }
# Sites: bound where they become guild-owned, gated where they pay or are managed.
$assign95 = [regex]::Matches($ss95, 'OwnerKind\s*=\s*SiteEntry\.OwnerGuildKind[^\r\n]*')
if ($assign95.Count -lt 1) { $bad95 += 'no guild-site assignment found - the scan did not run' }
foreach ($a in $assign95) { if ($a.Value -notmatch '_guildOwner\[\s*tile\s*\]\s*=\s*identity') { $bad95 += "a site becomes guild-owned without a binding: $($a.Value.Trim())" } }
if ($ss95 -notmatch 'if \(SiteOwnership\.IsGuildOwned\(s\) && !GuildBindingIsLive\(s\)\) continue;') { $bad95 += 'the reward cycle no longer skips orphaned guild sites' }
if ($ss95 -notmatch 'GuildIdentity\s*=\s*d\.GuildIdentity') { $bad95 += 'guild site production is owed without the site''s bound identity' }
if ($so95 -notmatch 'SiteStore\.GuildBindingIsLive\(s\);') { $bad95 += 'CanManage no longer requires the bound incarnation' }
if ($bad95.Count) { foreach ($b in $bad95) { Bad "95: $b" } }
else { Good '95: guild vault credits, guild sites and the disband order are bound to the incarnation' }

# Saving then crediting loses the credit on a crash, so quests, sites and roadworks write what they owe in the SAME
# save (KmhOwedPayouts), pay on a marker, and resume at boot and from the sweep.
Section '96. Quest, site and roadworks payouts are written down with what produced them'
$bad96 = @()
$main96 = Code (Join-Path $serverSrc 'Main.cs')
$reset96 = Code (Join-Path $serverSrc 'Features\Economy\KmhEconomyReset.cs')
$stores96 = [ordered]@{
    'QuestStore'     = 'Features\Quests\QuestStore.cs'
    'SiteStore'      = 'Features\Sites\SiteStore.cs'
    'RoadworksStore' = 'Features\Roadworks\RoadworksStore.cs'
}
foreach ($n96 in $stores96.Keys) {
    $c96 = Code (Join-Path $serverSrc $stores96[$n96])
    if ($c96 -notmatch '_owed\s*=\s*new\s+Transactions\.KmhOwedPayouts\(') { $bad96 += "$n96 has no owed-payout ledger"; continue }
    if ($c96 -notmatch '_owed\.SnapshotLocked\(\)' -or $c96 -notmatch '_owed\.LoadLocked\(') { $bad96 += "$n96 does not persist its owed payouts in its own file" }
    if ($c96 -notmatch '_owed\.ClearLocked\(\)') { $bad96 += "$n96 keeps owed payouts across a season wipe" }
    # A value move that bypasses the ledger is the crash window this section exists to close.
    if ($c96 -match 'KmhPayloadEscrow\.Deliver|TreasuryStore\.(WithdrawSilver|DepositSilver|DepositItem|DepositGuild\w*)\(') { $bad96 += "$n96 moves value outside the ledger" }
    $rec96 = ([regex]::Matches($c96, '_owed\.RecordLocked\(|OweLocked\(')).Count
    $set96 = ([regex]::Matches($c96, '_owed\.Settle\(')).Count
    if ($rec96 -lt 2 -or $set96 -lt 2) { $bad96 += "$n96 records $rec96 and settles $set96 payout(s) - the scan did not run" }
    $iLoad = $main96.IndexOf("$n96.LoadFromDisk()"); $iRec = $main96.IndexOf("$n96.RecoverOnBoot()")
    if ($iRec -lt 0) { $bad96 += "boot never resumes $n96 payouts" }
    elseif ($iLoad -lt 0 -or $iLoad -gt $iRec) { $bad96 += "$n96 payouts are resumed before they are loaded" }
    if ($reset96 -notmatch "$n96\.DropOwedFor\(user\)") { $bad96 += "a player's economy reset does not drop what $n96 owes them" }
}
# The quest hand-over is the commit point of a delivery: durable in the treasury file, and what roll-forward reads.
$q96 = Code (Join-Path $serverSrc 'Features\Quests\QuestStore.cs')
$t96 = Code (Join-Path $serverSrc 'Features\Treasury\TreasuryStore.cs')
if ($q96 -notmatch 'HandOverMatchingOnce\(' -or $q96 -notmatch 'TreasuryStore\.KnowsHandOver\(') { $bad96 += 'a quest delivery no longer hands over in one commit, or rolls forward on something other than the durable record' }
if ($t96 -notmatch 'HandOvers\s*\{\s*get;\s*set;' -or $t96 -notmatch '_handOvers\[marker\]\s*=') { $bad96 += 'the quest hand-over record is not persisted with the commit that moves the goods' }
# Charges that buy a site or road change ride a ledger row the change names, so boot refunds or keeps them, never both.
$s96 = Code (Join-Path $serverSrc 'Features\Sites\SiteStore.cs')
$r96 = Code (Join-Path $serverSrc 'Features\Roadworks\RoadworksStore.cs')
if (([regex]::Matches($s96, 'ChargeForSite\(')).Count -lt 4 -or ([regex]::Matches($s96, 'NamePurchaseLocked\(tx\)')).Count -lt 3) { $bad96 += 'a site repair, building or build is charged without a row its change names' }
if ($r96 -notmatch 'TargetRoadStart' -or $r96 -notmatch 'StartTxId\s*=\s*tx\?\.Id') { $bad96 += 'a road project is charged without a row it names' }
# Road work is owed with its cycle and laid once per record.
if ($s96 -notmatch 'Route\s*=\s*RouteRoad' -or $r96 -notmatch '_appliedWork\[marker\]\s*=') { $bad96 += 'a cycle''s road work is not owed with its timers, or is laid without its marker' }
if ($bad96.Count) { foreach ($b in $bad96) { Bad "96: $b" } }
else { Good '96: quest, site and roadworks payouts, charges and road work are written down with what produced them and resumed at boot' }

# The palette and the mark are drawn by six files in three repos; KMH-Planning/brand/tokens.json is the one master.
Section '97. Every copy of the Colony Nexus palette and mark matches the brand tokens'
# KMH_BRAND_TOKENS points the check at another tokens file, which is how it is proven to fail.
$tokPath97 = if ($env:KMH_BRAND_TOKENS) { $env:KMH_BRAND_TOKENS } else { Join-Path (Split-Path -Parent $ServerRoot) 'KMH-Planning\brand\tokens.json' }
if (-not (Test-Path $tokPath97)) {
    if ($env:KMH_BRAND_TOKENS) { Bad "97: KMH_BRAND_TOKENS names $tokPath97, which does not exist" }
    else { NoGlobal 'the brand tokens (KMH-Planning, private like it) were not compared' }
} else {
    $t97 = [System.IO.File]::ReadAllText($tokPath97) | ConvertFrom-Json
    $inv97 = [System.Globalization.CultureInfo]::InvariantCulture
    $bad97 = @(); $n97 = 0; $g97 = 0; $noGlobal97 = $false; $code97 = @{}
    function Tok97($path) {
        $n = $t97
        foreach ($p in $path.Split('.')) { if ($null -eq $n) { return $null }; $n = $n.$p }
        if ($null -ne $n -and $n -isnot [string] -and @($n.PSObject.Properties.Name) -contains 'value') { return $n.value }
        return $n
    }
    function Path97($spec) {
        $root = @{ client = $ClientRoot; server = $ServerRoot; global = $globalRoot }[$spec.Split(':')[0]]
        return Join-Path $root ($spec.Substring($spec.IndexOf(':') + 1).Replace('/', '\'))
    }
    # A token number as a pattern that also takes the trailing zeros a C# literal may carry.
    function Num97($v) {
        $s = ([double]$v).ToString('0.##', $inv97)
        if ($s.Contains('.')) { return [regex]::Escape($s) + '0*' } else { return [regex]::Escape($s) + '(?:\.0*)?' }
    }
    function F97($v) { return (Num97 $v) + 'f' }
    function Norm97($v) { return ("$v" -replace '\s', '').ToLowerInvariant() }
    function Read97($spec) {
        if (-not $code97.ContainsKey($spec)) { $code97[$spec] = Code (Path97 $spec) }
        return $code97[$spec]
    }

    # Every colour a token holds: a drawing file may use these and no others.
    $allowed97 = New-Object 'System.Collections.Generic.HashSet[string]'
    $stack97 = New-Object System.Collections.Stack
    $stack97.Push($t97.color)
    while ($stack97.Count) {
        $node97 = $stack97.Pop()
        if ($node97 -is [string]) { if ($node97 -match '^#[0-9a-fA-F]{6}$') { [void]$allowed97.Add((Norm97 $node97)) } }
        elseif ($node97 -is [System.Management.Automation.PSCustomObject]) { foreach ($pp in $node97.PSObject.Properties) { if ($null -ne $pp.Value) { $stack97.Push($pp.Value) } } }
    }
    $stops97 = @($t97.gradient.world.stops)
    foreach ($st in $stops97) { [void]$allowed97.Add((Norm97 $st.color)) }
    if ($stops97.Count -ne 3 -or (Norm97 $stops97[1].color) -ne (Norm97 (Tok97 'color.brand.primary'))) {
        $bad97 += 'tokens.json: the world gradient must be three stops with the accent in the middle, as every copy draws it'
    }

    $consumers97 = @($t97.consumers.PSObject.Properties)
    if ($consumers97.Count -lt 7) { $bad97 += "tokens.json binds $($consumers97.Count) consumer(s), not the 7 that draw the brand - the scan did not run" }
    foreach ($cn in $consumers97) {
        $c = $cn.Value
        if ($c.file.StartsWith('global:') -and -not (Test-Path $globalRoot)) { $noGlobal97 = $true; continue }
        $leaf = "$(Split-Path -Leaf (Path97 $c.file)) ($($cn.Name))"
        if (-not (Test-Path (Path97 $c.file))) { $bad97 += "$($c.file) is missing"; continue }
        $code = Read97 $c.file
        if ($c.fields) {
            foreach ($f in $c.fields.PSObject.Properties) {
                $want = Tok97 $f.Value
                if ($null -eq $want) { $bad97 += "$leaf $($f.Name) is bound to $($f.Value), which tokens.json does not define"; continue }
                $ms = [regex]::Matches($code, '(?<![\w.])' + [regex]::Escape($f.Name) + '\s*=\s*(?:Hex\(\s*)?(?:0x|"#)([0-9a-fA-F]{6})\b')
                if ($ms.Count -eq 0) { $bad97 += "$leaf no longer declares the colour $($f.Name) - re-bind it in tokens.json"; continue }
                foreach ($m in $ms) {
                    $got = '#' + $m.Groups[1].Value.ToLowerInvariant()
                    if ($got -ne (Norm97 $want)) { $bad97 += "$leaf $($f.Name) = $got, but token $($f.Value) = $want" } else { $n97++ }
                }
            }
            $lits = [regex]::Matches($code, '(?:Hex\(\s*0x|#)([0-9a-fA-F]{6})\b')
            if ($lits.Count -eq 0) { $bad97 += "$leaf has no colour literals at all - the scan did not run" }
            foreach ($m in $lits) {
                $hex = '#' + $m.Groups[1].Value.ToLowerInvariant()
                if (-not $allowed97.Contains($hex)) { $bad97 += "$leaf draws $hex, which is no colour in tokens.json" }
            }
        }
        if ($c.vars) {
            $blk = [regex]::Match($code, [regex]::Escape($c.block) + '\s*=\s*"""(.*?)"""', 'Singleline')
            if (-not $blk.Success) { $bad97 += "$leaf has no $($c.block) block"; continue }
            $decl = @{}
            foreach ($m in [regex]::Matches($blk.Groups[1].Value, '(--[\w-]+)\s*:\s*([^;]+)')) { $decl[$m.Groups[1].Value] = $m.Groups[2].Value.Trim() }
            foreach ($v in $c.vars.PSObject.Properties) {
                $want = Tok97 $v.Value
                if ($null -eq $want) { $bad97 += "$leaf $($v.Name) is bound to $($v.Value), which tokens.json does not define"; continue }
                if ($want -is [int] -or $want -is [long] -or $want -is [double] -or $want -is [decimal]) { $want = "$($want)px" }
                if (-not $decl.ContainsKey($v.Name)) { $bad97 += "$leaf no longer declares $($v.Name)"; continue }
                if ((Norm97 $decl[$v.Name]) -ne (Norm97 $want)) { $bad97 += "$leaf $($v.Name) = $($decl[$v.Name]), but token $($v.Value) = $want" } else { $n97++ }
            }
            foreach ($k in $decl.Keys) {
                if ($decl[$k] -match '^(#|rgba?\()' -and -not $c.vars.PSObject.Properties[$k]) { $bad97 += "$leaf $k = $($decl[$k]) is a colour no token binds" }
            }
        }
    }

    # The mark, its world's light and the moon's orbit, written the way each copy writes them.
    $w97 = $t97.mark.world; $r97 = $t97.mark.ring; $m97 = $t97.mark.moon; $fv97 = $t97.mark.favicon; $web97 = $t97.mark.web; $wg97 = $t97.gradient.world
    $q = '\\?"'
    $fx97 = [math]::Round((0.5 - [double]$wg97.cx) * 2, 4); $fy97 = [math]::Round((0.5 - [double]$wg97.cy) * 2, 4); $fr97 = [math]::Round([double]$wg97.r * 2, 4)
    $hi97 = $stops97[0].color.TrimStart('#'); $lo97 = $stops97[2].color.TrimStart('#')
    $far97 = 'transform=' + $q + 'translate\(' + (Num97 $w97.cx) + ' ' + (Num97 $w97.cy) + '\) rotate\(' + (Num97 $r97.tilt) + '\)' + $q +
             '\s*d=' + $q + 'M' + (Num97 (-$r97.rx)) + ' 0A' + (Num97 $r97.rx) + ' ' + (Num97 $r97.ry) + ' 0 0 1 ' + (Num97 $r97.rx) + ' 0' + $q
    $near97 = $far97.Replace(' 0 0 1 ', ' 0 0 0 ')
    $world97 = 'cx=' + $q + (Num97 $w97.cx) + $q + ' cy=' + $q + (Num97 $w97.cy) + $q + ' r=' + $q + (Num97 $w97.r) + $q
    $moon97 = 'cx=' + $q + (Num97 $m97.cx) + $q + ' cy=' + $q + (Num97 $m97.cy) + $q + ' r=' + $q
    $grad97 = 'id=' + $q + 'mw' + $q + ' cx=' + $q + (Num97 $wg97.cx) + $q + ' cy=' + $q + (Num97 $wg97.cy) + $q + ' r=' + $q + (Num97 $wg97.r) + $q + '>'
    foreach ($st in $stops97) { $grad97 += '<stop offset=' + $q + (Num97 $st.offset) + $q + ' stop-color=' + $q + [regex]::Escape($st.color) + $q + '/>' }
    $geo97 = @()
    foreach ($svg in @('server:Source/Features/Leaderboards/LeaderboardSvg.cs', 'global:src/KmhGlobal/Api/LeaderboardSvg.cs')) {
        $geo97 += ,@($svg, 'far half of the ring', $far97)
        $geo97 += ,@($svg, 'near half of the ring', $near97)
        $geo97 += ,@($svg, 'world', ('<circle ' + $world97))
        $geo97 += ,@($svg, 'moon', ('<circle ' + $moon97 + (Num97 $m97.r) + $q))
        $geo97 += ,@($svg, "ring stroke $($r97.stroke), round caps", ('stroke-width=' + $q + (Num97 $r97.stroke) + $q + ' stroke-linecap=' + $q + 'round' + $q))
        $geo97 += ,@($svg, 'world gradient', $grad97)
    }
    $site97 = 'global:src/KmhGlobal/Api/PublicSite.cs'
    $geo97 += ,@($site97, 'Mark/Favicon far half', $far97)
    $geo97 += ,@($site97, 'Mark/Favicon near half', $near97)
    $geo97 += ,@($site97, 'Mark world', ('class=' + $q + 'm-world' + $q + ' ' + $world97))
    $geo97 += ,@($site97, 'Mark moon', ('class=' + $q + 'm-moon' + $q + ' ' + $moon97 + (Num97 $m97.r) + $q))
    $geo97 += ,@($site97, "Favicon ring $(Tok97 $fv97.ringColor) at $($fv97.ringStroke)", ('stroke=' + $q + (Tok97 $fv97.ringColor) + $q + ' stroke-width=' + $q + (Num97 $fv97.ringStroke) + $q + '/>[\s\S]*stroke=' + $q + (Tok97 $fv97.ringColor) + $q + ' stroke-width=' + $q + (Num97 $fv97.ringStroke) + $q))
    $geo97 += ,@($site97, 'Favicon world', ($world97 + ' fill=' + $q + (Tok97 $fv97.world) + $q))
    $geo97 += ,@($site97, 'Favicon moon', ($moon97 + (Num97 $fv97.moonR) + $q + ' fill=' + $q + (Tok97 $fv97.moonColor) + $q))
    $geo97 += ,@($site97, 'header mark ring style', ('\.m-ring\{[^}]*stroke:' + [regex]::Escape($web97.ringColor) + '[;}][^}]*stroke-opacity:0?' + [regex]::Escape(([double]$web97.ringOpacity).ToString('0.##', $inv97).TrimStart('0')) + '[;}][^}]*stroke-width:' + (Num97 $web97.ringStroke) + '[;}]'))
    $geo97 += ,@($site97, 'header mark world and moon', ('\.m-world\{fill:' + [regex]::Escape($web97.world) + '\}[\s\S]{0,200}\.m-moon\{fill:' + [regex]::Escape($web97.moon) + '\}'))
    $geo97 += ,@($site97, "header mark size $($web97.size) px", ('\.mark\{width:' + $web97.size + 'px;height:' + $web97.size + 'px'))
    # The server draws its own Discord PNG as a verbatim copy of Global's, so both copies are held to the mark.
    foreach ($png97 in @('global:src/KmhGlobal/Api/LeaderboardPng.cs', 'server:Source/Features/Leaderboards/LeaderboardPng.cs')) {
        $geo97 += ,@($png97, 'world centre', ('new PointF\(\s*x\s*\+\s*' + (F97 $w97.cx) + '\s*\*\s*u\s*,\s*y\s*\+\s*' + (F97 $w97.cy) + '\s*\*\s*u\s*\)'))
        $geo97 += ,@($png97, 'ring radii and tilt', ('new SizeF\(\s*' + (F97 $r97.rx) + '\s*\*\s*u\s*,\s*' + (F97 $r97.ry) + '\s*\*\s*u\s*\)\s*,\s*' + (F97 $r97.tilt)))
        $geo97 += ,@($png97, 'world radius', ('\br\s*=\s*' + (F97 $w97.r) + '\s*\*\s*u'))
        $geo97 += ,@($png97, 'ring stroke', ('PenOptions\(\s*Ring\s*,\s*' + (F97 $r97.stroke) + '\s*\*\s*u\s*\)'))
        $geo97 += ,@($png97, 'moon', ('x\s*\+\s*' + (F97 $m97.cx) + '\s*\*\s*u\s*,\s*y\s*\+\s*' + (F97 $m97.cy) + '\s*\*\s*u\s*,\s*' + (F97 $m97.r) + '\s*\*\s*u'))
        $geo97 += ,@($png97, 'world light', ('c\.X\s*-\s*' + (F97 $fx97) + '\s*\*\s*r\s*,\s*c\.Y\s*-\s*' + (F97 $fy97) + '\s*\*\s*r\s*\)\s*,\s*' + (F97 $fr97) + '\s*\*\s*r'))
        $geo97 += ,@($png97, 'world gradient', ('new ColorStop\(\s*' + (Num97 $stops97[0].offset) + 'f?\s*,\s*Hex\("#' + $hi97 + '"\)\)\s*,\s*new ColorStop\(\s*' + (Num97 $stops97[1].offset) + 'f?\s*,\s*Accent\s*\)\s*,\s*new ColorStop\(\s*' + (Num97 $stops97[2].offset) + 'f?\s*,\s*Hex\("#' + $lo97 + '"\)\)'))
    }
    $brand97 = 'client:Source/UI/KmhBrand.cs'
    $geo97 += ,@($brand97, 'world centre', ('WorldC\s*=\s*new Vector2\(\s*' + (F97 $w97.cx) + '\s*,\s*' + (F97 $w97.cy) + '\s*\)'))
    $geo97 += ,@($brand97, 'moon position', ('MoonAt\s*=\s*new Vector2\(\s*' + (F97 $m97.cx) + '\s*,\s*' + (F97 $m97.cy) + '\s*\)'))
    foreach ($pair in @(@('WorldR', $w97.r), @('RingRx', $r97.rx), @('RingRy', $r97.ry), @('RingTilt', $r97.tilt), @('MoonR', $m97.r))) {
        $geo97 += ,@($brand97, "$($pair[0]) $($pair[1])", ('\b' + $pair[0] + '\s*=\s*' + (F97 $pair[1]) + '\s*[,;]'))
    }
    foreach ($set in $t97.mark.strokeBySize.imgui.PSObject.Properties) {
        $geo97 += ,@($brand97, "$($set.Name) px set ring stroke $($set.Value)", ('Build\(\s*' + $set.Name + '\s*,\s*' + (F97 $set.Value) + '\s*\)'))
    }
    $geo97 += ,@($brand97, 'world gradient colours', ('\bhi\s*=\s*Hex\(0x' + $hi97 + '\)\s*,\s*mid\s*=\s*Accent\s*,\s*lo\s*=\s*Hex\(0x' + $lo97 + '\)'))
    $geo97 += ,@($brand97, 'world light', ('WorldC\.x\s*-\s*' + (F97 $fx97) + '\s*\*\s*WorldR\s*,\s*WorldC\.y\s*-\s*' + (F97 $fy97) + '\s*\*\s*WorldR'))
    $geo97 += ,@($brand97, 'world light reach', ('\(\s*' + (F97 $fr97) + '\s*\*\s*WorldR\s*\)'))
    $geo97 += ,@($brand97, 'world gradient middle stop', ('\bt\s*<\s*' + (F97 $stops97[1].offset)))
    $geo97 += ,@('client:Source/UI/KmhLoadingPanel.cs', "moon orbit $($t97.motion.moonOrbit.periodSeconds) s", ('Mathf\.PI\s*\*\s*2f\s*/\s*' + (F97 $t97.motion.moonOrbit.periodSeconds)))
    foreach ($g in $geo97) {
        if ($g[0].StartsWith('global:') -and -not (Test-Path $globalRoot)) { $noGlobal97 = $true; continue }
        if (-not (Test-Path (Path97 $g[0]))) { $bad97 += "$($g[0]) is missing"; continue }
        if ([regex]::IsMatch((Read97 $g[0]), $g[2], 'IgnoreCase')) { $g97++ }
        else { $bad97 += "$(Split-Path -Leaf (Path97 $g[0])): the $($g[1]) is not drawn from the tokens (no match for $($g[2]))" }
    }

    if ($noGlobal97) { NoGlobal 'the website and Global leaderboard images were not compared with the brand tokens' }
    if ($bad97.Count) { foreach ($b in $bad97) { Bad "97: $b" } }
    else { Good "97: $n97 palette values and $g97 mark, light and orbit facts in $($code97.Count) file(s) match $(Split-Path -Leaf $tokPath97)" }
}

# The client names boards by key in the player's language; a board added on the server would otherwise stay English.
Section '98. Every leaderboard the server ranks has a title, description and group in the client'
$cat98 = Join-Path $ServerRoot 'Source/Features/Leaderboards/LeaderboardCatalog.cs'
$txt98 = Join-Path $ClientRoot 'Source/Features/Leaderboards/LeaderboardText.cs'
if (-not (Test-Path $cat98) -or -not (Test-Path $txt98)) { Bad '98: LeaderboardCatalog.cs or LeaderboardText.cs is missing' }
else {
    $strip98 = { param($s) [regex]::Replace([regex]::Replace($s, '/\*[\s\S]*?\*/', ''), '//[^\r\n]*', '') }
    $srv98 = & $strip98 ([IO.File]::ReadAllText($cat98))
    $cli98 = & $strip98 ([IO.File]::ReadAllText($txt98))
    $const98 = @{}
    foreach ($m in [regex]::Matches($srv98, '\b([A-Z]\w*)\s*=\s*"([a-z][a-z0-9-]*)"')) { $const98[$m.Groups[1].Value] = $m.Groups[2].Value }
    $keys98 = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $groups98 = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($m in [regex]::Matches($srv98, '\bP\(\s*"([a-z][a-z0-9-]*)"\s*,\s*"[^"]*"\s*,\s*"([^"]+)"')) { [void]$keys98.Add($m.Groups[1].Value); [void]$groups98.Add($m.Groups[2].Value) }
    foreach ($m in [regex]::Matches($srv98, '\bKey\s*=\s*(\w+)[^}]*?\bGroup\s*=\s*"([^"]+)"')) {
        if ($const98.ContainsKey($m.Groups[1].Value)) { [void]$keys98.Add($const98[$m.Groups[1].Value]) } else { Bad "98: board constant $($m.Groups[1].Value) has no string value" }
        [void]$groups98.Add($m.Groups[2].Value)
    }
    # Ordinal: a PowerShell hashtable would count the key 'economy' and the group 'Economy' as one.
    $cases98 = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([StringComparer]::Ordinal)
    foreach ($m in [regex]::Matches($cli98, '\bcase\s+"([^"]+)"\s*:')) { $c = $m.Groups[1].Value; if ($cases98.ContainsKey($c)) { $cases98[$c]++ } else { $cases98[$c] = 1 } }
    $n98 = { param($c) if ($cases98.ContainsKey($c)) { $cases98[$c] } else { 0 } }
    $bad98 = @()
    if ($keys98.Count -lt 20) { $bad98 += "only $($keys98.Count) board key(s) read from LeaderboardCatalog.cs - the parse no longer matches the catalog" }
    foreach ($k in $keys98) { $n = & $n98 $k; if ($n -ne 2) { $bad98 += "board '$k' has $n case(s) in LeaderboardText.cs; it needs a title and a description" } }
    foreach ($g in $groups98) { $n = & $n98 $g; if ($n -ne 1) { $bad98 += "group '$g' has $n case(s) in LeaderboardText.cs" } }
    foreach ($c in $cases98.Keys) { if (-not $keys98.Contains($c) -and -not $groups98.Contains($c)) { $bad98 += "LeaderboardText.cs names '$c', which the server does not rank" } }
    if ($bad98.Count) { foreach ($b in $bad98) { Bad "98: $b" } }
    else { Good "98: $($keys98.Count) board(s) and $($groups98.Count) group(s) on the server all have client words" }
}

Section '99. The client reconciles an unknown pending deposit again only after the server would revert it'
$srv99 = Code (Join-Path $serverSrc 'Features/Treasury/TreasuryHandler.cs')
$cli99 = Code (Join-Path $clientSrc 'Features/Treasury/GameComponent_KMHDepositLedger.cs')
$grace99 = [regex]::Match($srv99, '\bconst\s+int\s+ReconcileGraceSeconds\s*=\s*(\d+)\s*;')
$again99 = [regex]::Match($cli99, '\bconst\s+double\s+ReconcileAgainSeconds\s*=\s*([\d.]+)\s*;')
if (-not $grace99.Success -or -not $again99.Success) { Bad '99: ReconcileGraceSeconds (server) or ReconcileAgainSeconds (client) not found' }
elseif ([double]$again99.Groups[1].Value -le [double]$grace99.Groups[1].Value) { Bad "99: the client reconciles again after $($again99.Groups[1].Value) s, inside the server's $($grace99.Groups[1].Value) s grace, so a deposit it no longer has stays pending" }
else { Good "99: again after $($again99.Groups[1].Value) s, past the server's $($grace99.Groups[1].Value) s grace" }

Section '100. No Colony Nexus drop pod punches through a roof'
$calls100 = 0; $bad100 = @()
foreach ($f in Get-ChildItem $clientSrc -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\obj\\' }) {
    $src100 = Code $f.FullName
    foreach ($m in [regex]::Matches($src100, 'DropPodUtility\.DropThingsNear\s*\(([^;]*)\)\s*;')) {
        $calls100++
        if ($m.Groups[1].Value -notmatch '\bcanRoofPunch\s*:\s*false\b') { $bad100 += "$($f.Name): DropThingsNear without canRoofPunch: false" }
    }
}
if ($calls100 -eq 0) { Bad '100: no DropPodUtility.DropThingsNear call found - the scan no longer matches the client' }
elseif ($bad100.Count) { foreach ($b in $bad100) { Bad "100: $b (pods aimed at a roofed stockpile break the roof)" } }
else { Good "100: all $calls100 drop pod call(s) forbid roof punching" }

Section '101. A want escrows what the post dialog showed'
$srv101 = Code (Join-Path $serverSrc 'Features/WantBoard/WantStore.cs')
$cli101 = Code (Join-Path $clientSrc 'Features/WantBoard/Dialog_KMHPostWant.cs')
$s101 = [regex]::Match($srv101, 'CostUpTo\(long milli, long units\)\s*=>[^;]*\(milli \* units \+ 999\) / 1000\s*;')
$c101 = [regex]::Match($cli101, 'EscrowFor\(int milli, int qty\)\s*=>[^;]*\(\(long\)milli \* qty \+ 999\) / 1000\s*;')
if (-not $s101.Success -or -not $c101.Success) { Bad '101: the server''s CostUpTo and the client''s EscrowFor no longer both round milli * qty up to whole silver' }
else { Good '101: server and client both escrow milli * qty rounded up to whole silver' }


Section 'RESULT'
if ($fail) { Write-Host '  CONTRACT BROKEN - fix the FAILs above before shipping.' -ForegroundColor Red; exit 1 }
Write-Host '  Contract holds.' -ForegroundColor Green
exit 0
