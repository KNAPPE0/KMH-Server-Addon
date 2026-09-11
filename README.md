# KMH Server Addon

A drop-in addon that gives your **RimWorld Together** server a full player-driven
economy: marketplace, auctions, a want board, personal and guild treasuries, a
seven-kind quest board, guilds with diplomacy, reputation, production sites, a
server-wide event & global-quest engine, a Server Standings hub, player mail and
chat channels, and an optional Discord bridge. Everything is server-authoritative
and crash-safe. Extract it next to your server and that is the whole install.

It pairs with the **[KMH Patch](../KMH-Patch)** client mod. Players without the
patch still connect and play normally; the KMH features simply light up for the
ones who have it.

## RWT version

KMH v1.3.0 is verified against the nine most recent RWT releases, from
**26.5.24.1** up to **26.8.31.1**. The addon auto-detects which one it is sitting
next to and loads the matching build — it identifies the RWT server by what the
executable actually contains, so a renamed or panel-mangled server file is still
found.

| RWT release | Status |
|---|---|
| 26.8.31.1 · 26.8.16.1 (1) · 26.8.16.1 · 26.8.9.1 · 26.7.25.1 | supported |
| 26.6.23.1 · 26.6.9.1 · 26.6.8.1 | supported |
| 26.5.24.1 | supported — oldest supported release |
| 26.4.18.1 and older | **not supported** |

RWT moved a core network type between 26.4.18.1 and 26.5.24.1, so releases below
26.5.24.1 are a different generation. On those, KMH refuses to start rather than
running half-patched: it reports that it is not operational and leaves your RWT
server running normally.

**On RWT 26.6.23.1 and newer, RWT chat is not a reliable KMH carrier — keep the
KMH API transport enabled (the default since v1.2.0); it's the recommended/required
path there.**

Update the **KMH Server Addon** and the **KMH Patch** together so both sides match.

## What your players get

- **Marketplace** - escrowed player-to-player trading with search and category
  filters, oversell-safe under concurrent buyers. You set the rules: house tax
  (accrues to a server pool you can award out), price floors and ceilings,
  per-player listing caps, listing lifetimes, and optional demand-based pricing.
  Guilds can layer their own sale tax into the guild vault.
- **Auctions & want board** - timed bidding with buyout, an anti-snipe
  extension, and escrowed bids that refund the instant they're outbid; plus a
  want-to-buy board where buyers escrow silver and any seller can fulfil
  (partial fills allowed) from their treasury. Both settle through the server
  ledger.
- **Living World** - a server event & quest engine: tax holidays, market booms
  and crashes, resource shortages, double worker XP, and house stipends, plus
  co-op (reward split by contribution) and competitive (winner-take-all) global
  quests. Rewards scale to the live economy, are reserved from the house pool,
  and refund automatically if a quest expires. Owner-triggered or auto-rolled.
- **Treasuries** - a personal vault per player and shared guild vaults, with
  full transaction history and crash-safe persistence. Players deposit silver
  and stored colony goods straight from stockpiles and shelves - no caravan
  required.
- **Quests** - seven kinds (deliver, bounty, escort, defend, hunt, build,
  custom) with server-side escrow so bounties always pay, a proof-and-review
  flow with image links, per-poster caps, and automatic expiry.
- **Reputation** - quest behaviour feeds a trust score with named tiers shown
  as badges across every board; the weights and cutoffs are yours to tune.
- **Guilds** - ranks, invites, buyable perks paid from the guild vault,
  per-rank daily withdraw limits, MOTDs, and alliance/hostility diplomacy that
  gates guild-only visibility.
- **Sites** - players build production sites on world tiles, staff them
  together for worker XP, and route the output where they choose.
- **Server Standings** - a full in-game progression hub: player, guild,
  member-contribution, colony, colonist, trade, contract, battle, site, and
  reputation boards, plus player and colonist profiles and a season archive
  that folds each season's leaders into all-time records.
- **Player mail** - self-hosted player-to-player messages that reach someone
  whether they are online or not; recipients are picked from the standings
  roster, never typed. Mail can carry silver, stacked items, and full-state gear:
  the goods escrow out of the sender's treasury and settle exactly once, either
  claimed by the recipient or returned to the sender. Anything left unopened is
  returned automatically, and a sender can recall their own unopened attachment
  at any time, so gifts can never strand.
- **KMH chat** - server-wide, guild, and private channels in one Communications
  window, with unread counts and a glow on the KMH tab so a message is never
  missed. Recent history survives a reconnect. Players can block someone or mute
  notifications per channel type; staff can remove messages.
- **Offline notifications** - auction, marketplace, and quest outcomes that fire
  while a player is away are queued and delivered as letters on their next login.
  (Separate from Player mail above, which is player-written.)

## What you control as the owner

- **One folder of config.** Every knob - taxes, caps, quest rules, reputation
  weights, site costs, event tuning, chat and mail limits - lives in
  `KMH-Data/Config/*.json`, generated with sane defaults on first boot.
- **Communication controls.** `Config/Chat.json` and `Config/Mail.json` set
  message length, rate limits, history and retention, inbox size, attachment
  caps, and how long an unopened attachment waits before it goes home. Mail can
  also charge an optional attachment fee (off by default) that flows to the house
  pool. Either system can be switched off entirely from `Config/Features.json`.
- **Discord bridge** *(optional)* - two-way chat, account linking, trading by
  friendly item names, showcase/WTB boards that edit in place, join/leave
  announces, slash commands, a live console feed, and branded embeds with
  bundled icons. KMH chat can be bridged to its own Discord channel with each
  direction toggled independently; linked accounts post under the player's
  in-game name, and relayed lines are always tagged as coming from Discord.
  Player IPs and host file paths are scrubbed at the boundary, and each piece
  self-disables when its channel isn't configured.
- **Config enforcement** *(optional)* - publish your mod configs as the server
  profile from in-game; clients verify, back up, apply, and lock their Mod
  Options to match, with a per-mod safe list, a gameplay-only mode, and admin
  bypass.
- **KMH API transport** *(on by default — the recommended path for newer RWT
  versions)* - a KMH-owned port (default `5099`) so clients talk to the addon
  directly instead of riding RWT chat. Public-ready and locked down by default:
  authentication (one-time token over the verified RWT session), connection
  caps, per-IP caps, failed-auth throttling, idle/auth timeouts, and frame-size
  limits are all required out of the box — a public bind never means an
  unsecured bind. Forward TCP `5099` for remote players; KMH falls back to chat
  if the port is unreachable. Tune in `Config/Transport.json`; verify with
  `kmh transport-test`.
- **Admin tools** - the `kmh` command family in console and `/kmh` in chat:
  `status`, `diag`, `transport`, grant silver, drain the house pool, live config
  reloads, enforcement management, fire events / global quests, and season rolls.
- **Reliability & recovery** - automatic backups of `KMH-Data` on boot and
  before any data-format migration, a startup integrity scan, a guard that
  refuses to corrupt data across versions, an append-only economy ledger, and
  recovery commands: `verify`, `backup`/`backups`, `save`, `inspect`, `cancel`
  (refund a stuck auction/want/quest), `ledger`, `rebuild-standings`, and
  `smoketest` (live health). `kmh selftest` runs the build's contract suite -
  a developer/support tool, not a health metric; see `kmh help advanced`.
- **Extensions** - drop community DLLs into `kmh-extensions/`; the SDK gives
  authors a stable API plus storage, events, and command hooks. See
  [EXTENSIONS.md](EXTENSIONS.md).

## Install & update

Extract the zip next to the official RWT server executable (`GameServer.exe`, or
`RTServer.exe` on newer builds) and run `KMHServerAddon.exe`, not the RWT server.
Keep the whole folder together: the zip also carries `KMH-Data` with the icons
and notes, and the `-selfcontained` build puts the .NET runtime beside the
executable.

It needs the **.NET 8 runtime** on the server, and it loads RWT's own DLLs from
that folder at runtime — RimWorld Together is never bundled. On Linux the file
is `KMHServerAddon` with no extension, and a zip cannot carry the execute bit,
so `chmod +x KMHServerAddon` once after extracting. Full steps in
[SETUP.txt](SETUP.txt).

On a host without .NET 8 — or one with no internet to fetch a runtime — use the
**`-selfcontained`** zip for your platform. It carries .NET 8 inside it, so
nothing needs installing or downloading on the server. Every architecture has
one: `win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm`, `linux-arm64`.
Managed panels (Bisect, Pterodactyl, …) that run a fixed `GameServer` launcher
are covered in the *Managed / panel hosts* section of [SETUP.txt](SETUP.txt).

**In-game video** stays off until you add `yt-dlp` and `ffmpeg`. They belong to
other people under other licences, so KMH fetches them rather than shipping
them: run `Tools\get-media-tools.ps1` (or the `.sh`) from inside the extracted
server and both land where KMH looks. The same scripts are published on their
own as **`KMH-MediaTools-v1.3.0.zip`**. Either one runs on any machine with
internet, so a server without any is fine — fetch elsewhere, copy the `Tools`
folder over. Details in [SETUP.txt](SETUP.txt).

To **update**, replace the extracted KMH files with the new build and restart.
**Back up your server first** (or at least the `KMH-Data` folder) — KMH also
takes an automatic `KMH-Data` backup on boot. All KMH state lives under
`KMH-Data/` (owner config in `KMH-Data/Config/`), kept separate from RWT's own
files, so it carries over across updates with no wipe.

## License

See [LICENSE](LICENSE).
