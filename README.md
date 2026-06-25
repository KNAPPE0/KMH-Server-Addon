# KMH Server Addon

A drop-in addon that gives your **RimWorld Together** server a full player-driven
economy: marketplace, auctions, a want board, personal and guild treasuries, a
seven-kind quest board, guilds with diplomacy, reputation, production sites, a
server-wide event & global-quest engine, a Server Standings hub, and an optional
Discord bridge. Everything is server-authoritative and crash-safe. One file next
to your server is the whole install.

It pairs with the **[KMH Patch](../KMH-Patch)** client mod. Players without the
patch still connect and play normally; the KMH features simply light up for the
ones who have it.

## RWT version

Recommended **RWT 26.6.9.1** — KMH v1.1.0 is built and tested around it. It also
runs on **RWT 26.5.24.1** (the older RWT generation); the addon auto-detects
which one it's sitting next to and binds the matching API automatically.

**RWT 26.6.23.1 and newer is experimental / compatibility-in-progress — don't
update to it yet.** If Steam auto-updated RimWorld Together past 26.6.9.1, players
downgrade in-game: `Mod Options → RimWorld Together → Change Version → 26.6.9.1`.

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
- **Offline mail** - auction, marketplace, and quest outcomes that fire while a
  player is away are queued and delivered as letters on their next login.

## What you control as the owner

- **One folder of config.** Every knob - taxes, caps, quest rules, reputation
  weights, site costs, event tuning - lives in `KMH-Data/Config/*.json`,
  generated with sane defaults on first boot.
- **Discord bridge** *(optional)* - two-way chat, account linking, trading by
  friendly item names, showcase/WTB boards that edit in place, join/leave
  announces, slash commands, a live console feed, and branded embeds with
  bundled icons. Player IPs and host file paths are scrubbed at the boundary,
  and each piece self-disables when its channel isn't configured.
- **Config enforcement** *(optional)* - publish your mod configs as the server
  profile from in-game; clients verify, back up, apply, and lock their Mod
  Options to match, with a per-mod safe list, a gameplay-only mode, and admin
  bypass.
- **KMH API transport** *(optional, experimental, off by default)* - a
  KMH-owned port so clients talk to the addon directly instead of riding RWT
  chat. It keeps KMH traffic off the chat stream and on its own readable
  channel; connections authenticate with a one-time token issued over the
  verified RWT session, and KMH falls back to chat if the port is unreachable.
  Turn it on in `Config/Transport.json` and check it with `kmh transport`.
- **Admin tools** - the `kmh` command family in console and `/kmh` in chat:
  `status`, `diag`, `transport`, grant silver, drain the house pool, live config
  reloads, enforcement management, fire events / global quests, and season rolls.
- **Reliability & recovery** - automatic backups of `KMH-Data` on boot and
  before any data-format migration, a startup integrity scan, a guard that
  refuses to corrupt data across versions, an append-only economy ledger, and
  recovery commands: `verify`, `backup`/`backups`, `save`, `inspect`, `cancel`
  (refund a stuck auction/want/quest), `ledger`, `rebuild-standings`, and
  `smoketest`.
- **Extensions** - drop community DLLs into `kmh-extensions/`; the SDK gives
  authors a stable API plus storage, events, and command hooks. See
  [EXTENSIONS.md](EXTENSIONS.md).

## Install & update

Drop `KMHServerAddon.exe` next to the official `GameServer.exe` and run the
addon (not `GameServer.exe`). It needs the **.NET 8 runtime** on the server and
loads RWT's own DLLs from that folder at runtime — it never bundles RimWorld
Together. Full steps in [SETUP.txt](SETUP.txt).

To **update**, replace `KMHServerAddon.exe` with the new build and restart.
**Back up your server first** (or at least the `KMH-Data` folder) — KMH also
takes an automatic `KMH-Data` backup on boot. All KMH state lives under
`KMH-Data/` (owner config in `KMH-Data/Config/`), kept separate from RWT's own
files, so it carries over across updates with no wipe.

## License

See [LICENSE](LICENSE).
