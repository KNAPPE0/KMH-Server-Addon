kmh-data/ - KMH addon persistent state + config
================================================

Everything KMH-specific is rooted here. RWT's own state stays in its
usual folders (Assets / Backups / Configs / Logs / Temp) next to this
one - KMH never touches them.

State is split into one folder per system, with all editable config in
config/. The addon creates these on first run:

  config/        owner-editable settings (the only files you hand-edit)
    economy.json     market tax, price floors, listing caps, lifetimes
    sites.json       custom-site cost/reward tuning
    reputation.json  quest-trust scoring weights + tier cutoffs
    quests.json      quest posting caps (per-user, lengths, bounty items)
    discord.json     OPTIONAL Discord bridge config (see below)

  treasury/      personal + guild vault balances
  marketplace/   open listings + lifetime trade stats
  quests/        open / claimed / submitted quests
  guilds/        membership, ranks, perks, alliances, MOTD, settings
  reputation/    per-player quest-trust scores
  sites/         custom production sites + workers
  players/       roster + lifetime stats per player
  accounts/      in-game username <-> Discord identity map
  catalog/       defName -> friendly label cache (filled by clients)
  discord/       bridge runtime state (showcase / WTB / leaderboard)


To activate the Discord bridge
------------------------------

1. Open  config/discord.json  (generated on first boot).
2. Paste your bot token (from the Discord Developer Portal) into the
   "DiscordBotToken" field. Set channel ids as desired (0 = off).
3. Save, restart KMHServerAddon.exe.

Without that file the bridge is silently disabled - every other KMH
feature runs normally.


Notes
-----

Every file is JSON. Hand-editing config/ is expected; the state folders
are written by the addon - edit them only with the server stopped so
writes don't race. The load path tolerates missing files and fields
(defaults apply), so deleting a state file just resets that system.

Back this whole folder up alongside RWT's own Backups/ folder - a
complete server backup needs both.
