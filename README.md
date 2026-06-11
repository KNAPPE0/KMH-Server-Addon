# KMH Server Addon

A drop-in addition for **RimWorld Together** servers that gives your players a
complete economy - marketplace, treasury, quests, guilds, reputation,
production sites, leaderboards, and an optional Discord bridge. Two files in a
folder is the whole install.

It pairs with the **[KMH Patch](../KMH-Patch)** client mod. Players without the
patch can still join and play normally - KMH features simply appear for those
who have it.

Works with both current RimWorld Together server versions (26.5.24.1 and
26.6.9.1+); the addon detects which one it's sitting next to and adapts
automatically. When you upgrade your server, KMH just follows.

## What your players get

- **Marketplace** - escrowed player-to-player trading with search and
  category filters. You control the economy: house tax (accrues to a server
  pool you can award), price floors and ceilings, per-player listing caps,
  listing lifetimes. Guilds can layer their own sale tax into the guild
  vault. Oversell-safe under concurrent buyers.
- **Treasury** - a personal vault per player plus shared guild vaults, with
  full transaction history and crash-safe persistence.
- **Quests** - seven kinds (deliver, bounty, escort, defend, hunt, build,
  custom) with server-side escrow so bounties always pay, proof-and-review
  with image links, per-poster caps, automatic expiry.
- **Reputation** - quest behaviour feeds a trust score with named tiers shown
  as badges everywhere; scoring weights and cutoffs are yours to tune.
- **Guilds** - ranks, invites, buyable perks paid from the guild vault,
  per-rank daily withdraw limits, MOTDs, and alliance/hostility diplomacy
  that gates guild-only visibility.
- **Sites** - players build production sites on world tiles, staff them
  together, earn worker XP, and route output where they choose.
- **Leaderboards** - a live Discord leaderboard that updates in place
  (player + guild boards in one post, daily archive rollover), in-game boards
  with sorting and filters, and per-player stat cards (`!kmh-rank`,
  `/kmh leaderboard rank`, or click a row in-game).
- **Discord bridge** *(optional)* - two-way chat, account linking, market
  trading by friendly item names, showcase/WTB boards that edit in place,
  join/leave announces, slash commands, branded embeds with bundled icons.
  Each piece self-disables when its channel isn't configured.
- **Config enforcement** *(optional)* - publish your mod configs as the
  server profile from in-game; clients verify, back up, apply, and lock Mod
  Options. Per-mod safe list, gameplay-only mode, admin bypass.
- **Admin tools** - the `kmh` command family in console and `/kmh` in chat:
  status, grant silver, drain the house pool, live config reloads,
  enforcement management.
- **Extensions** - drop community DLLs into `kmh-extensions/`; the SDK gives
  authors a stable API plus storage, events, and command hooks. See
  [EXTENSIONS.md](EXTENSIONS.md).

Every knob - taxes, caps, quest rules, reputation weights, site costs - lives
in `KMH-Data/Config/*.json`, generated with sane defaults on first boot.

## Install

Drop `KMHServerAddon.exe` next to the official `GameServer.exe` and run the
addon (not `GameServer.exe`). Requires the **.NET 8 runtime** on the server.
Full steps in [SETUP.txt](SETUP.txt).

## License

See [LICENSE](LICENSE).
