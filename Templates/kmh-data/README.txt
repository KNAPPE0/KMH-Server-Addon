KMH-Data/ - KMH addon persistent state + config
================================================

Everything KMH-specific is rooted here. RWT's own state stays in its
usual folders (Assets / Backups / Configs / Logs / Temp) next to this
one - KMH never touches them.

State is split into one folder per system, with all editable config in
Config/. The addon creates the rest on first run:

  Config/        owner-editable settings (the only files you hand-edit)
    Economy.json       market tax, price floors, listing caps, lifetimes
    Sites.json         custom-site cost/reward tuning
    Reputation.json    quest-trust scoring weights + tier cutoffs
    Quests.json        quest posting caps (per-user, lengths, bounty items)
    Enforcement.json   config-enforcement state + safe-mods list
    World.json         global event / quest scheduler tuning
    Maintenance.json   automatic-backup + boot integrity-scan settings
    Discord/DiscordConfig.json   OPTIONAL Discord bridge config (see below)

  Icons/         Discord embed icons (ships pre-filled; swap in your own)
  Treasury/      personal + guild vault balances
  Marketplace/   open listings + lifetime trade stats
  Quests/        open / claimed / submitted quests
  Guilds/        membership, ranks, perks, alliances, MOTD, settings
  Reputation/    per-player quest-trust scores
  Sites/         custom production sites + workers
  World/         global events + server-owned quests
  Auctions/      open auctions + bids
  Notifications/ queued letters for offline players
  WantBoard/     want-to-buy orders + escrowed silver
  Seasons/       season archive + all-time server records
  Ledger/        append-only economy audit log (daily JSONL files)
  Players/       roster + lifetime stats per player
  Accounts/      in-game username <-> Discord identity map
  Catalog/       defName -> friendly label cache (filled by clients)
  Discord/       bridge runtime state (showcase / WTB / leaderboard)
  extensions/    per-extension storage (one subfolder each)


To activate the Discord bridge
------------------------------

1. Open  Config/Discord/DiscordConfig.json  (generated on first boot).
2. Paste your bot token (from the Discord Developer Portal) into the
   Bot > Token field. Set channel ids as desired (blank = off).
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
