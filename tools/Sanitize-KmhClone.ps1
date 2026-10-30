# Strips what a copied KMH server must not inherit, so a clone can never act as the server it came from.
# A kept Discord token logs into the real bot; a kept Global identity heartbeats as the same public server.
# Usage: .\tools\Sanitize-KmhClone.ps1 -Path <clone folder, never the original> [-KeepPlayerData] [-WhatIf]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    # Keeps treasuries, guilds, sites and saves. Identity and secrets are cleared either way.
    [switch]$KeepPlayerData,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Path)) { throw "not found: $Path" }
$root = (Resolve-Path $Path).Path
$data = Join-Path $root "KMH-Data"
if (-not (Test-Path $data)) { throw "no KMH-Data here - is this a KMH server folder? $root" }

$cleared = New-Object System.Collections.Generic.List[string]
function Drop($item, $why) {
    if (-not (Test-Path $item)) { return }
    if ($WhatIf) { Write-Host "  would clear $why" -ForegroundColor Yellow; return }
    Remove-Item $item -Recurse -Force
    $cleared.Add($why)
}

Write-Host "[sanitize] $root"

Drop (Join-Path $data "Identity")       "Global signing identity"
Drop (Join-Path $data ".kmh-meta.json") "ServerId"

$globalCfg = Join-Path $data "Config\Global.json"
if (Test-Path $globalCfg) {
    $g = Get-Content $globalCfg -Raw | ConvertFrom-Json
    $g.Enabled = $false                 # a clone never lists itself until its owner says so
    $g.RegisteredServerId = ""
    $g.LastSequence = 0
    $g.LastMetadataRevision = 0
    $g.SigningGeneration = 1
    $g.PublicHost = ""
    if (-not $WhatIf) { $g | ConvertTo-Json -Depth 12 | Set-Content $globalCfg -Encoding utf8 }
    $cleared.Add("Global registration + public host (listing disabled)")
}

$discordCfg = Join-Path $data "Config\Discord\DiscordConfig.json"
if (Test-Path $discordCfg) {
    $d = Get-Content $discordCfg -Raw | ConvertFrom-Json
    if ($d.PSObject.Properties.Name -contains 'Bot' -and $d.Bot) {
        foreach ($f in @('Token','ClientSecret','WebhookUrl')) {
            if ($d.Bot.PSObject.Properties.Name -contains $f) { $d.Bot.$f = "" }
        }
    }
    foreach ($f in @('Token','BotToken','WebhookUrl')) {
        if ($d.PSObject.Properties.Name -contains $f) { $d.$f = "" }
    }
    if (-not $WhatIf) { $d | ConvertTo-Json -Depth 12 | Set-Content $discordCfg -Encoding utf8 }
    $cleared.Add("Discord bot token / webhook")
}

foreach ($d in @("Debug", "Snapshots", "MediaCache", "History", "Ledger", "Migrations")) {
    Drop (Join-Path $data $d) "$d"
}
# The parent's simulated players: a clone would otherwise run (and rank) another server's test accounts.
Drop (Join-Path $data "Simulation") "simulated player accounts"

# Global sent this ban list to the parent; a clone is never sent a replacement and would enforce it for ever.
Drop (Join-Path $data "Moderation\NetworkBans.json") "network ban list"
Drop (Join-Path $root "Logs")             "logs"
Drop (Join-Path $root "KMH-Data-Backups") "KMH-Data backups"

if (-not $KeepPlayerData) {
    foreach ($d in @("Treasury","Marketplace","Auctions","Quests","Guilds","Sites","Reputation","World",
                     "Players","Mail","Chat","ChatModeration","Notifications","Delivery","Transactions",
                     "Recovery","Seasons","Roadworks","Frontier","Accounts","Catalog")) {
        Drop (Join-Path $data $d) "$d state"
    }
    Drop (Join-Path $root "Assets")  "RWT world and saves"
    Drop (Join-Path $root "Backups") "RWT backups"
}

Write-Host ""
if ($cleared.Count -eq 0) { Write-Host "  nothing to clear - already sanitized" -ForegroundColor Green }
else { foreach ($c in $cleared) { Write-Host "  cleared: $c" -ForegroundColor Green } }
Write-Host ""
Write-Host "This clone now has no identity of its own. Run 'kmh global setup' in it to register it separately."
