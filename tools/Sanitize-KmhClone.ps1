# Strips what a copied KMH server must not inherit, so a clone can never act as the server it came from.
# A kept Discord token logs into the real bot; a kept Global identity heartbeats as the same public server.
# Usage: .\tools\Sanitize-KmhClone.ps1 -Path <clone folder, never the original> -Force [-KeepPlayerData] [-WhatIf]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    # Keeps treasuries, guilds, sites and saves. Identity and secrets are cleared either way.
    [switch]$KeepPlayerData,
    [switch]$WhatIf,
    # Confirms the folder is a copy: run on the original it would cut that server off from Global and Discord.
    [switch]$Force
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Path)) { throw "not found: $Path" }
$root = (Resolve-Path $Path).Path.TrimEnd('\', '/')
$data = Join-Path $root "KMH-Data"
if (-not (Test-Path $data)) { throw "no KMH-Data here - is this a KMH server folder? $root" }
if (-not $Force -and -not $WhatIf) { throw "refusing without -Force: this clears the identity and Discord token of $root. Only ever run it on a copy." }
$live = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
if ($live.Count -gt 0) { throw "a server is running from $root ($($live[0].ProcessName)) - stop it first; never sanitize a live server" }

$done = New-Object System.Collections.Generic.List[string]
function Add-Cleared($why) { $done.Add($(if ($WhatIf) { "would clear: $why" } else { "cleared: $why" })) }
function Remove-Inherited($item, $why) {
    if (-not (Test-Path $item)) { return }
    if (-not $WhatIf) { Remove-Item $item -Recurse -Force }
    Add-Cleared $why
}
function Set-IfPresent($obj, $name, $value) {
    $p = $obj.PSObject.Properties[$name]
    if ($p -and $p.Value -ne $value) { $p.Value = $value; return $true }
    return $false
}

Write-Host "[sanitize] $root"

# Secrets first: a later step that fails must never leave a clone able to log into the real bot.
$discordCfg = Join-Path $data "Config\Discord\DiscordConfig.json"
if (Test-Path $discordCfg) {
    $d = Get-Content $discordCfg -Raw | ConvertFrom-Json
    # KMH_DISCORD_BOT_TOKEN outranks the file, so blanking the token alone could still log in.
    $changed = Set-IfPresent $d 'Enabled' $false
    if ($d.PSObject.Properties['Bot'] -and $d.Bot) {
        foreach ($f in @('Token', 'ClientSecret', 'WebhookUrl')) { if (Set-IfPresent $d.Bot $f "") { $changed = $true } }
    }
    foreach ($f in @('Token', 'BotToken', 'WebhookUrl')) { if (Set-IfPresent $d $f "") { $changed = $true } }
    if ($changed) {
        if (-not $WhatIf) { $d | ConvertTo-Json -Depth 12 | Set-Content $discordCfg -Encoding utf8 }
        Add-Cleared "Discord bot token / webhook (Discord disabled)"
    }
}

$globalCfg = Join-Path $data "Config\Global.json"
if (Test-Path $globalCfg) {
    $g = Get-Content $globalCfg -Raw | ConvertFrom-Json
    $changed = $false
    # A clone never lists itself until its owner says so; the registration fields only exist in pre-1.3 files.
    foreach ($kv in @(@('Enabled', $false), @('PublicHost', ""), @('RegisteredServerId', ""), @('LastSequence', 0),
                      @('LastMetadataRevision', 0), @('SigningGeneration', 1))) {
        if (Set-IfPresent $g $kv[0] $kv[1]) { $changed = $true }
    }
    if ($changed) {
        if (-not $WhatIf) { $g | ConvertTo-Json -Depth 12 | Set-Content $globalCfg -Encoding utf8 }
        Add-Cleared "Global listing + public host (listing disabled)"
    }
}

Remove-Inherited (Join-Path $data "Identity")       "Global signing identity and registration"
Remove-Inherited (Join-Path $data ".kmh-meta.json") "ServerId"

foreach ($d in @("Debug", "Snapshots", "MediaCache", "History", "Ledger", "Migrations")) {
    Remove-Inherited (Join-Path $data $d) "$d"
}
# The parent's simulated players: a clone would otherwise run (and rank) another server's test accounts.
Remove-Inherited (Join-Path $data "Simulation") "simulated player accounts"

# Global sent this ban list to the parent; a clone is never sent a replacement and would enforce it for ever.
Remove-Inherited (Join-Path $data "Moderation\NetworkBans.json") "network ban list"
Remove-Inherited (Join-Path $root "Logs")             "logs"
Remove-Inherited (Join-Path $root "KMH-Data-Backups") "KMH-Data backups"

if (-not $KeepPlayerData) {
    foreach ($d in @("Treasury","Marketplace","Auctions","Quests","Guilds","Sites","Reputation","World",
                     "Players","Mail","Chat","ChatModeration","Notifications","Delivery","Transactions",
                     "Recovery","Seasons","Roadworks","Frontier","Accounts","Catalog")) {
        Remove-Inherited (Join-Path $data $d) "$d state"
    }
    Remove-Inherited (Join-Path $root "Assets")  "RWT world and saves"
    Remove-Inherited (Join-Path $root "Backups") "RWT backups"
}

Write-Host ""
if ($done.Count -eq 0) { Write-Host "  nothing to clear - already sanitized" -ForegroundColor Green }
else { foreach ($c in $done) { Write-Host "  $c" -ForegroundColor $(if ($WhatIf) { "Yellow" } else { "Green" }) } }
Write-Host ""
if ($WhatIf) { Write-Host "Dry run: nothing was changed." }
else { Write-Host "This clone now has no identity of its own. Run 'kmh global setup' in it to register it separately." }
