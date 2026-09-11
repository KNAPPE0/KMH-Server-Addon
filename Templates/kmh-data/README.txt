KMH-Data/ - KMH addon persistent state + config
================================================

Everything KMH-specific is rooted here. RWT's own state stays in its
usual folders (Assets / Backups / Configs / Logs / Temp) next to this
one - KMH never touches them.

State is split into one folder per system, with all editable config in
Config/. The addon creates the rest on first run:

  Config/        owner-editable settings (the only files you hand-edit)
    Economy.json       market tax, price floors, listing caps, lifetimes
    Sites.json         site cost/reward tuning, archetypes, output tiers, roadworks
    Frontier.json      Frontier Operations director - budget, cooldowns, caps
    Reputation.json    quest-trust scoring weights + tier cutoffs
    Quests.json        quest posting caps (per-user, lengths, bounty items)
    Enforcement.json   config-enforcement state + safe-mods list
    World.json         global event / quest scheduler tuning
    Maintenance.json   automatic-backup + boot integrity-scan settings
    Transport.json     KMH API transport (port, auth, client debug uplink)
    Features.json      turn whole systems on/off
    Chat.json          KMH chat retention, rate limits, image/video policy
    Mail.json          player mail limits + optional attachment fee
    Media.json         chat media resolver + cache limits
    Staff.json         owner/admin/moderator lists, badges, colours
    Policies.json      resolved policy snapshot (written by KMH, not hand-edited)
    Discord/DiscordConfig.json   OPTIONAL Discord bridge config (see below)

  'kmh config' prints every area and what you have changed from default;
  'kmh config <area> all' prints every field. That is easier than reading JSON.

  Icons/         Discord embed icons (ships pre-filled; swap in your own)
  Treasury/      personal + guild vault balances
  Marketplace/   open listings + lifetime trade stats
  Quests/        open / claimed / submitted quests
  Guilds/        membership, ranks, perks, alliances, MOTD, contributions
  Reputation/    per-player quest-trust scores
  Sites/         sites, workers, buildings, condition, output catalog
  Roads/         roadworks projects + the roads already laid
  Frontier/      Frontier Operations director state + outposts
  World/         global events + server-owned quests
  Auctions/      open auctions + bids
  WantBoard/     want-to-buy orders + escrowed silver
  Mail/          player mail + escrowed attachments
  Chat/          KMH chat history
  ChatModeration/ per-player block lists
  Notifications/ queued letters for offline players
  Delivery/      outbound goods waiting on a client to confirm
  Recovery/      value held for an operator when it could not be credited
  Transactions/  append-only transaction ledger + crash reconciliation
  Ledger/        append-only economy audit log (daily JSONL files)
  Seasons/       season archive + all-time server records
  Players/       roster, lifetime stats, save ids, economy-reset state
  Accounts/      in-game username <-> Discord identity map
  Catalog/       defName -> friendly label + weather def cache (filled by clients)
  Discord/       bridge runtime state (showcase / WTB / leaderboard)
  Enforcement/   applied config-enforcement profiles per player
  History/       recorded state changes ('kmh history'), kept 3 months
  Migrations/    migration journal + the last upgrade report
  RwtConfigBackup/ last-good copies of RWT's own configs (preflight self-heal)
  MediaCache/    converted chat media - regenerable, wiped every boot
  Snapshots/     operator snapshots ('kmh snapshot-*')
  Debug/         diagnostic logs, including client debug uplink
  extensions/    per-extension storage (one subfolder each)


To activate the Discord bridge
------------------------------

1. Open  Config/Discord/DiscordConfig.json  (generated on first boot).
2. Paste your bot token (from the Discord Developer Portal) into the
   Bot > Token field. Set channel ids as desired (blank = off).
   To keep the token out of the file entirely, leave Bot > Token blank and set
   the KMH_DISCORD_BOT_TOKEN environment variable instead - it wins, and the
   config is never rewritten with the secret.
3. Save, restart KMHServerAddon.exe.

Without that file the bridge is silently disabled - every other KMH
feature runs normally. The embeds attach the images in Icons/; drop your
own PNGs there (see Icons/README.txt for the names) or keep the bundled set.


Notes
-----

Every file is JSON. Hand-editing Config/ is expected; the state folders
are written by the addon - edit them only with the server stopped so
writes don't race. The load path tolerates missing files and fields
(defaults apply), so deleting a state file just resets that system.

Back this whole folder up alongside RWT's own Backups/ folder - a
complete server backup needs both. KMH also auto-snapshots this folder
into a sibling KMH-Data-Backups/ on every boot (tune in Config/Maintenance.json).
