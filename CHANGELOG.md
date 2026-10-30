# Changelog

All notable changes to the KMH: Colony Nexus Server Addon. Versions follow the KMH build number shared with the
KMH: Colony Nexus client.

## v1.3.1 — Connected Worlds

**Read this before upgrading.** Two things changed that earlier releases promised would not.

**The protocol is v3, and a v1.2.x or v1.3.0 client can no longer connect.** Both version numbers are named in the
refusal, and on an enforcing server the client is disconnected rather than left waiting. Players update alongside
the server.

**Configuration is regenerated from this build's defaults, not migrated field by field.** Every tunable config is
rewritten so that every server lands on the same reviewed baseline instead of years of part-upgraded files: if you
had tuned something, it is back at its default. `Global.json`, `Staff.json` and the Discord config are identity and
credentials, and are never touched. Before anything is rewritten a pre-migration backup is taken into
`KMH-Data-Backups`, what was regenerated and what was kept is named in the console, and `KMH-Data/Config/README.txt`
explains what each file is for.

### rel-279 (in progress)

- **Cheat containment.** A player used a memory editor to create 396,000 silver and 5,000 ultratech medicine and
  banked them within 27 minutes. A client decides what its colony holds, so the server cannot stop the cheat, but it
  now limits what reaches the economy:
  - **Daily deposit limit** (`Economy.json` → `AntiCheat`, new): an account may deposit at most
    `DailyDepositLimit` (default 200,000) worth of silver and goods in any 24 hours, and `NewPlayerDailyDepositLimit`
    (default 25,000) during its first `NewPlayerHours` (default 10) connected hours this season. A deposit past it is
    refused before anything leaves the colony. Approvals still in flight count, and a deposit sent without one, or
    carrying more than its approval covered, is refused outright. Simulated accounts are not limited. 0 turns a
    limit off.
  - **Freeze** (`kmh freeze <user> [reason]`, `kmh unfreeze`, `kmh frozen`): stops an account's deposits,
    withdrawals, trades, bids, donations, sending mail and quest actions, in game, in guild chat commands and
    from Discord, while chat and reports keep working. A frozen account is off every board, and its listings and
    auctions cannot be bought. A new kind of request is refused for a frozen account until it is known to move no
    value.
  - **Undo** (`kmh freeze <user> undo dry|confirm`): takes back the account's guild donations that are still in
    guild vaults, then runs `wipe-economy` on it, after a backup. Silver it paid other players is not taken back.
  - **Staff alerts**: limit hits, freezes and undos are logged and posted to the Discord admin channel
    (`AntiCheat.AlertStaff`, on by default), at most once per account per half hour.
  - `kmh deposit-limit <user> [extra] [hours]` shows an account's limit and use, and lets staff raise it for an
    honest big haul.
- **RimWorld Together 26.10.20.1, RC15.** RimWorld Together's first full 1.7 release is built for .NET 10, and the
  server now runs on .NET 10 too (the self-contained package carries it; nothing to install). With the .NET 8 host,
  its server stopped at start-up on a missing `Path.Combine` overload. That release accepts only 26.10.20.1 clients,
  which RimWorld Together gives 1.7 players; its 1.6 players still get 26.10.15.1, so one server serves one of them.
  RimWorld Together's later "26.10.20.1 (1)" server, which stops certain world objects from dropping a connection
  while the world is first made, works the same way: it reports the same version, and its clients are unchanged.
- **A map object RimWorld Together cannot record no longer drops the player, RC15.** RimWorld Together closes a
  connection on any error while handling a packet and logs why only at debug verbosity, so a player was dropped
  1.5 s after a site or settlement changed on their map, twice, with nothing in either log. An error recording a map
  object is now logged and the player stays connected; that object is simply not shared.
- **Hunt quests ask for fewer dangerous animals, not more.** The per-animal factor multiplied the goal, so a global
  hunt asked for 1.5x as many thrumbos or bears and 0.75x as many rats. It now divides: with five active players and
  the default settings, 30 thrumbos, 40 boars or 50 rats. `Scaling.DifficultyMultiplier` still raises every goal.
- **Discord library warnings say what failed.** A Discord "Serializer Error" now carries the error it came with
  (one line, clipped), so an event the library cannot read can be identified.
- **Refunds are paid once, even around a failed save or a season reset.** Removing a player's mail returns their
  senders' attachments only once that removal is written down; before, a failed write and a restart let the
  unclaimed-attachment sweep return the same goods a second time. A season reset now drops Want Board refunds still
  owed to the old season: paid at the next boot they landed in the new season, and the restarted want ids made a new
  want's refund count as already paid.
- **Video links answer every player who asked.** A second player pasting the same video in another form (`youtu.be`,
  extra tracking) while it was being fetched never got an answer. A byte range that ends before it starts now gets the
  whole file instead of a negative length.
- **A failed save no longer undoes a Discord link or a site catalog.** A re-link that cannot be written keeps the
  existing link, and the adopted site catalog is written again on the next pass instead of being forgotten.
- **Smaller fixes, rel-279.** Federation actions this server cannot sign are refused here, not sent to Global
  unsigned. Two hello acks for one connection arriving together no longer leave it unmarked. A Discord reload no
  longer stops the world-chat bridge until the next boot. A hand-edited Quests.json or Frontier file with an empty
  list written as `null` no longer breaks the quest board or every Frontier tick. The Discord coordinator refuses
  role changes (no game server asks for them), records a replay nonce only once the request is signed, and caps a
  request body while reading it.
- **Nobody on this server can pass for Colony Nexus or its staff.**
  - An account named like Colony Nexus or the platform is refused at login: "Colony Nexus", "C0l0ny_N3xus",
    "Global", "System", "KMH Staff" and the like, with digits, look-alike letters (I, l and 1 count as one),
    full-width or Cyrillic characters. So is a name spelled like one of this server's staff without being that
    account. The refusal says which.
  - A guild name that reads as Colony Nexus or contains invisible characters is refused when the guild is founded;
    existing guilds still load.
  - A staff badge label that claims Colony Nexus authority ("Global Mod", "Official") falls back to the role's plain
    label.
  - A Discord name relayed into chat that matches one of these, or a staff member's name, is shown with "(Discord)".
  - `give-silver` is for the owner and the console only.
  - A server with no name of its own is "RimWorld Together server" (it was "Colony Nexus Server"), which Colony
    Nexus Global also uses.
  - Leaderboard images say "Reported by the server", as the website does: Colony Nexus does not check the numbers.
  - A Discord or website link in `Listing` that is not https is left off the listing and named by `kmh doctor`;
    Colony Nexus Global now refuses such links, and used to refuse the whole listing update over one.
  - `kmh global recover` on an Official server, or one allowed to relay Global Chat, says that only Colony Nexus
    staff can move its identity (`KMH-SRV-328`), instead of offering a retry that cannot work.
- **A server on RimWorld Together 26.10.20.1 or later is a RimWorld 1.7 server.** That RimWorld Together admits only
  1.7 players, but a new or regenerated `Config/Mods.json` said `RimWorldVersion` 1.6, so Colony Nexus refused every
  player at join and the listing said 1.6. New files now state what the running RimWorld Together admits, and a lower
  setting on 26.10.20.1 is read as 1.7 and named at startup. A higher setting is kept.
- **Upgrading from an early build keeps the listing and the Global registration.** A server whose config schema was
  below 2 (or whose `KMH-Data/.kmh-meta.json` was lost) had its listing description, region, languages, tags and
  links, and its Global registration, pruned as obsolete before the step that moves them ran. The prune now leaves
  them for that step. When the server id and the registration disagree, the console now says to restore
  `.kmh-meta.json` from `KMH-Data-Backups` instead of suggesting a new setup, which would lose the listing.
- **Security fixes from the v1.3.1 release audit.**
  - Mail could duplicate a stored item named twice in different letter case ("Steel" and "sTEEL"). Both now count
    as one stack, so the send is refused unless the vault holds the total.
  - A negative deposit approval could cancel the daily deposit limit's count of real approvals. An approval for
    nothing is refused now.
  - A deposit sent without an approval and refused by the treasury gate is refused outright. It is no longer parked
    in Recovery for an admin to "return".
  - The owner, or the server console, is now needed for:
    - draining the house pool;
    - handing on an orphaned site or vault;
    - `kmh recover drop`;
    - approving another player's bounty;
    - creating or crediting a world quest.

    Each of these hands server-held value to a named player.
  - An admin voiding an auction while a bid lands now refunds that bid.
  - Mail that a frozen account sent cannot be accepted until staff lift the freeze.
  - `kmh snapshot-verify` and `kmh restore-preview-player` only open folders under `KMH-Data/Snapshots`. A network
    path made the host sign in to another machine.
  - A session refused at login is never acknowledged, so nothing it sent alongside the login runs.
  - A connection that sends nothing for the whole login window counts as a failed login.
  - A saved item that uses XML namespaces, or carries a second item, is refused.
- **Discord, from the same audit.**
  - The admin console feed keeps each line on one line, and only Colony Nexus's own Discord lines are left out of it.
  - Relayed chat shows no masked links, unfurls no links and carries no live invites. It is redacted before a batch
    is measured.
  - A bridge reload waits two minutes after the last one.
  - The server's own id in a role list admits nobody.
  - Posts to a channel use the public market view, so guild-only listings stay out of it.
  - A coordinator must be reached over https unless it runs on this machine.
  - The coordinator:
    - strips buttons from routed answers;
    - labels a self-named server with its alias;
    - mutes mentions in every post and edit;
    - drops a server Global no longer lists on every route, not only at heartbeat.
- **Packages.**
  - Every server zip carries `LICENSE` and the full licence texts of the bundled libraries.
  - Self-contained zips carry the .NET runtime's licence and notices.
  - `KMH-Data/Icons` carries the Apache 2.0 text for its Noto Emoji icons.
  - `Tools/get-media-tools` checks yt-dlp and ffmpeg against their publishers' checksum lists before keeping them.
- **ImageSharp 4.1.3.** The image libraries move to ImageSharp 4.1.3, Fonts 3.2.0 and ImageSharp.Drawing 3.1.3, which
  carry the fixes for ImageSharp's October 2026 advisories. Chat images still decode only PNG, JPEG, GIF, WebP and BMP,
  and an embedded colour profile is never converted. The leaderboard image looks as it did. Building a Release from
  source now needs a free Six Labors Community licence key (see `Source/Local.props.example`).
- **Look-alike account names are refused.** Colony Nexus keeps a player's data under a case-insensitive name, while
  RimWorld Together tells accounts apart with the host's own lower-casing. Names that only one of the two calls equal
  ("µ" and "μ", "ς" and "σ", and on a Turkish or Azeri host "I" and "i") could open two accounts on one vault. A new
  account whose name Colony Nexus would store as an existing one is now refused. Two such accounts made before this
  release are both held at login, and the console names them so the owner can remove one.
- **A login RimWorld Together refuses is held too.** A ban, wrong password, server password, version or mod refusal
  by RimWorld Together now stops what the client sent along with its login, as Colony Nexus's own refusals already
  did.
- **RimWorld Together admins and bans are recognised again.** The lookup of RimWorld Together's own account records
  looked for the wrong class, so its admins were never badged and an account banned in RimWorld Together could still
  buy, trade and post through Discord. The records are read once a minute.
- **A forwarded report names no other player.** The copy of a player report sent to Colony Nexus Global replaces every
  other account's name with `[player]`, so a reporter whose name is an ordinary word cannot pull in who else did what.
- **The Discord coordinator runs on .NET 10.** .NET 8 support ends on 10 November 2026. Its installer now checks for
  the .NET 10 runtime itself, not just for a `dotnet` binary.
- **Playtest leftovers stay out of a cloned server.** `tools/Sanitize-KmhClone.ps1` also removes the simulated
  player accounts.
- **Every other connection RimWorld Together drops on an error says why, RC15.** The console and `kmh joins` name
  the player and the error. RimWorld Together still closes the connection, as before. A closed socket is not logged.
- **Want Board prices can be fractional, RC15.** A want can offer what a listing can ask (0.55 silver, from the
  Marketplace's own minimum to its maximum). The escrow is the total rounded up to whole silver; each fill pays the
  rounded-up cost of everything filled so far less what earlier fills paid, so the fills and the refund of a want add
  up to its escrow exactly, and a whole-silver want pays exactly what it did before. A fill priced while another fill
  of the same fractional want landed is returned to the seller to try again. Servers advertise
  `want_decimal_prices`; older clients keep posting whole silver.
- **Security pass, RC7:**
  - Discord's `/kmh console run` no longer counts as the machine's console. `setup owner`, `config-editor` and
    `global` refuse from there, as their messages always said; `restart` still works, as RimWorld Together's own
    `quit` already did.
  - Staff and settings access compare names the way RimWorld Together tells accounts apart. On a Turkish or Azeri
    Windows host, "iris" was treated as the owner "Iris", which RimWorld Together keeps as a separate account.
  - A banned account's linked Discord can no longer trade, buy, post a showcase or WTB, chat into the game, or reach
    extension commands. A banned player is also stopped before RimWorld Together sends the world, whether or not
    Config Enforcement holds the session.
  - The API port refuses compressed frames before a client has authenticated, and caps decompressed bytes per
    connection after. One IPv6 host rotating addresses inside its /64 now shares one connection cap.
  - A repeated request for the same enforcement profile within two minutes is not answered again.
  - The Discord config, which holds the bot token, is made readable by the server's account only when it loads.
  - Relayed game chat cannot start a new line in Discord or ping anyone, and the poster's name is escaped. Showcase
    and WTB taglines, item labels and quest titles and descriptions are escaped, so "[text](link)" cannot post as a
    disguised link.
  - Video requests snap to 144/240/360/480/720/1080 and one player can have at most three fetches waiting; the
    image decoder's frame cap follows the memory budget. Console output sent to Discord hides IPv6 addresses too.
  - A simulated account is matched by its exact name; a real account with a trailing space no longer counts as one.
  - An account whose name contains `[KMH-` (ignoring spaces and invisible characters) is refused, and its chat is
    never relayed: clients treat chat under the system name as the server speaking.
  - Food deposited from stacks that wore differently is stored, not refused. Folding averages the stacks' condition
    and keeps one saved copy; that copy now takes the averaged condition, on deposit and whenever the vault merges
    stacks, where the mismatch refused the deposit after the goods had left the colony and dropped merged rows on
    escrow and guild returns.
- **Other players' words no longer render as markup.** A mail subject in the "new mail" letter, a site worker's pawn
  name and a video's title are shown as text and capped. An item name stored from a player (deposits, wants, sites)
  has to be an item name: letters, digits, `_`, `-` and `.`, at most 128 characters a part.
- **Stores that only ever grew are bounded.** Withdrawals pause while 32 deliveries are still on their way to a
  player; mail and guild invites go only to names that have played on this server; a vault refuses a new kind of item
  past 5,000 before the goods leave the colony; a quest lives at most 30 days, where a huge lifetime used to fail
  after the bounty was taken or never expire.
- **A network ban follows the exact account name.** RimWorld Together keeps "Name " and "Name" apart; a padded twin
  that verified as a banned Global account was recorded under the real name and locked that player out.
- **Guild Admins cannot lift a lower rank's withdraw cap past their own.** An Admin set the member cap to unlimited
  and withdrew through an alt what the Owner's Admin cap withheld. Lowering a cap is unchanged.
- **`/kmh guild` commands typed in chat need a session that completed the Colony Nexus handshake** and is not held by
  Config Enforcement, as the same actions sent by the mod already did.
- **Federation renown for a quest needs a verified poster**, so a second account that never signs in cannot approve
  its owner's quests for credit.
- **A server moved to another directory trusts that directory's key.** The pinned key now records where it came
  from; one pinned from the playtest directory no longer refuses every official ticket after the move.
- **Backups:** a server installed under a folder named Debug, Support, Snapshots or MediaCache backs up again (every
  file was filtered out); signing-key copies in backups keep owner-only access, and the live key is re-tightened
  on load; force-save reports every store whose write failed, so a season reset keeps its journal until it lands.
- **Client debug uploads are taken only when Debug logging is on**, into a folder capped at 256 MB.
- **Owner-link poll ids are never written to the log.**
- **Self-contained bundles carry .NET 8.0.32** (they shipped 8.0.21, ten security releases behind).
- **Discord role sync gives out only roles it made.** A guild named after one of the owner's own roles ("Staff
  (Admin)") was handed that role, with whatever it allowed. Now Colony Nexus remembers the roles it creates and gives
  out or takes away only those; a same-named role it did not make is left alone and named once in the console.
  Roles made by an earlier build are not adopted: delete them and the next sync makes new ones.
- **A renamed Workshop mod no longer locks players out.** RimWorld Together compares mod lists by display name. When
  a player's mod has a name the server does not list but its Workshop id matches exactly one entry, that entry's
  name is used before RimWorld Together's own check runs. Folder ids, ids two entries share and names the player
  already has are left alone; nothing else about the check changes. The console says which entry to rename.
- **`kmh joins`** lists the last 200 refused or adjusted joins with the reason: required mods missing, mods not on
  the list, server full, wrong RimWorld Together version, no world yet. Addresses are shortened to their network,
  and at most 30 join lines a minute reach the console.
- **The directory's "Playing now" counts every signed-in session, simulated playtest players included**, since they
  hold real slots; a session still logging in is not counted. `kmh sim` and the admin channel's status split people
  from simulated players. The heartbeat's fields are unchanged.
- **Discord without embeds.** Nothing KMH posts is an embed any more; each surface is one message edited in place:
  - **Daily leaderboard image.** The server draws the PNG itself (the same bytes Colony Nexus Global draws, so it
    works without Global) and posts one message per board per day (`Live.LeaderboardBoards`, default `auto` and
    `guilds`), edited every `Live.LeaderboardEveryMinutes` (15). When the day turns over (`Live.DayStartsAtUtcHour`,
    default 00:00 UTC, every `Live.LeaderboardRolloverHours`) it gets a final edit and the next one starts. A restart
    carries on with the same day's message. Nothing is posted while nobody is ranked.
  - **Activity logs.** Marketplace listings and sales, sites built and removed, quests completed and guilds founded
    are one line each in a daily message per channel, edited at most every 20 seconds; a full one starts a part 2.
  - **Market board.** One live message in the marketplace channel: the 15 newest public listings and the auctions
    ending soonest (`Live.MarketBoardEveryMinutes`, 10). Players' own showcase and WTB boards are text now.
  - **Admin channel.** A live server status (players and simulated players, uptime, build, persistence, last backup,
    Global link, open reports, Discord) every minute, and plain-text alerts for degraded persistence, a failed
    backup, a Global link lost for ten minutes, a failed start-up and a source erroring over and over - once per
    episode, at most ten an hour (`Events.Status`, `Events.Alerts`, `Events.AlertsPingModerators`). Reports are a
    few plain lines.
  - **Commands** answer in compact text, and `!kmh-leaderboard` and `/kmh leaderboard show` with the image. No post
    or reply can mention anyone. The coordinator wire carries no files, so a coordinated server sends text instead.
- **New dependencies:** SixLabors.Fonts 2.1.3 and SixLabors.ImageSharp.Drawing 2.1.7 (Six Labors Split License), and
  the Inter fonts (SIL OFL 1.1) embedded in the addon. `THIRD-PARTY-NOTICES.txt` now ships in every zip.
- **"KMH" became "Colony Nexus" in everything players, Discord and owners read**: notices, refusals, chat command
  replies, Discord commands and embeds, console output, `kmh help`, the doctor, support bundles and the readme files
  the server writes. The display name "KMH: Colony Nexus Server Addon", the `[KMH-Addon]` log prefix, commands
  (`kmh`, `/kmh`, `!kmh-*`), config keys, folders and error codes are unchanged.
- **Discord uses the Colony Nexus mark.** The bundled `kmh_logo.png` is the website's world, ring and moon. A server
  upgrading in place replaces its `KMH-Data/Icons/kmh_logo.png` on the first boot only when that file is untouched
  stock art KMH has retired (the old KMH icon or the earlier castle placeholder); a logo of your own is never
  touched. A config still holding the old default name "KMH Server" (or the new "Colony Nexus Server") now shows the
  server's own name in every post, including the "is up" announcement and the announcement footer.
- **Leaderboard images in the Colony Nexus look**: the website's colours, its mark and a warm light from the top
  right, without the random lines behind the header. The image hash both sides pin moved with it.
- **Clearer messages.** Building a site says "Built your Farmland site for $500. Send a caravan there and assign a
  colonist to start it." (it named the item's def name), and the vault history says "site build". Global quest
  deliveries and Want Board fills name items the way the game does ("horse meat", not "Meat_Horse").
- **Colony Nexus Global (website).** A leaderboard link shared in Discord or a post previews as that board's image
  instead of the site's general card. "ColonyNexus", "Colony_Nexus" and "nexus" are reserved usernames, as "kmh" was.
  Every page says Colony Nexus where it said KMH (account pages, the operator dashboard, username reasons), and "How
  to join" points at the Colony Nexus card's Browse servers and at Prepare and join.
- **RWT's own server browser lists your address, not your IP.** RWT announces the IP it looks up for itself and has
  no setting to change that. With `PublicHost` set in `KMH-Data/Config/Global.json`, the listing shows that name, and
  RWT's Join dials it. RWT keys a listing by its address, so the first update starts a new entry at 50%
  reputation. The name still resolves to your IP for anyone who looks it up.
- **Discord without embeds.** Recurring posts are one message edited in place and finalized at the UTC day turn:
  - a leaderboard image per board, drawn by the server itself and refreshed every 15 minutes;
  - a live market board and live activity logs;
  - a live server-status message and plain-text alerts in the Admin channel.

  Settings are under `Live` and `Events` in `DiscordConfig.json`, and they are added to an existing file on its first
  boot. The bot needs Attach Files in the leaderboard channel. The server ships two more libraries
  (SixLabors.Fonts, SixLabors.ImageSharp.Drawing) and the Inter font.
- **Fixed:** turning Discord on with `kmh reload discord` never started the leaderboard posts or the marketplace
  showcase until the next restart.
- **Reports go where the player chooses:** this server's staff, the Colony Nexus developers, or both.
  - Staff-only reports never leave the server, and developer-only reports are never shown to staff.
  - `kmh reports list|show|status` adds triage states, and staff reports also post to the Admin channel.
  - Forwarding still needs `ForwardReports` (off by default).
- **`kmh sim`** no longer counts a session that is still logging in as a person.
- **Fixed: an Escort, Defend, Hunt or Build bounty paid out on the claimer's word.** Two minutes after a claim, a
  verify report collected the poster's whole escrow. Now only a silver-only bounty up to
  `Quests.AutoVerifyMaxBountySilver` (new, default 250) pays on the report; a larger bounty, or any bounty with items,
  goes to the poster to approve or reject. A review left unanswered for `ReviewTimeoutHours` reopens the quest unpaid,
  as before. Existing `Quests.json` files get the new setting on the next boot.
- **Fixed: different full-state items shared one vault row.** Two genepacks, two crated buildings, two books or a
  biocoded and a plain weapon of equal quality showed as one row, and taking or listing two from it took both under
  the first one's label. Items now share a row only when their whole saved state is identical, and a take matches by
  that state rather than by the fingerprint the client sent. Identical stacks still show as one row.
- **Fixed: a market boom could be farmed.** Two accounts relisting one item to each other collected the house-pool
  boost every round. A seller now collects at most `Economy.Marketplace.BoomBoostMaxPerSellerPerDay` (new, default
  1000, 0 turns the boost off) per UTC day; the allowance is saved with the house pool, so a restart does not reset
  it, and a sale undone after its boost gives the allowance back.
- **Fixed: site output rules read a label.** Built-in tier keywords and rare/tech markers read the defName only, so
  the language that first reported a label no longer moves an item between tiers. Owner `AllowedDefNames` and
  `BlockedDefNames` still match a label as well, but only a defName entry can unlock a complex item under
  `AllowExplicitComplex`.
- **Fixed: a renamed colonist was remembered as fallen**, and a recruit with a fallen colonist's nickname erased
  their memorial. Colonist records match by the pawn's id when the client sends it (`id` on each roster entry) and
  by name otherwise.
- **Smaller fixes.** `kmh joins` stores at most 300 characters of detail and 64 per mod name, on one line, and
  shortens IPv6 addresses to their /48. Names posted to Discord cannot form a masked link, and backticks in a
  console line cannot end the live console's code block. Silver offered as an item deposit is refused with a pointer
  to the silver deposit, which charges its fee and applies the treasury cap. A marketplace unit price below a whole
  silver is charged rounded up, so a 1.499-silver unit bought singly costs 2, not 1.
- **Resets and wipes, audited.** Every reset path was traced and tested against the cases that lose or duplicate
  value. Fixed:
  - **A reset no longer destroys the other side of an unfinished trade.** A purchase or want fill stuck between its
    writes is finished or undone as a restart would, so the seller is paid or the buyer gets the goods, before the
    reset player's own side is wiped. One that cannot be ended yet keeps the reset open.
  - **A season reset made the rows of a player reset earlier settle into the new season**, because their own
    generation stayed above the moved floor. The wipe now moves every player past their own.
  - **A season reset whose journal was left behind wiped the new season again at boot**, and one whose stores could
    not be written reopened the economy half-wiped. The journal now records the finish, and a failed wipe keeps the
    economy paused until the retry completes it. Auction refunds, the auctions' lead records and world-quest rewards
    owed to the old season now go with it.
  - **A boot no longer pays a sale stranded before a reset** into the vaults the reset emptied.
  - **A reset reported done before its new save id was on disk**, so a restart could wipe the new save's progress a
    second time; `wipe-player` never wrote its forgetting down at all. The reset now stays open until both are saved.
  - **The automatic new-save reset skipped players with an empty vault**, leaving payouts, refunds and recovery
    records owed to the old save to be paid into the new one. It now always runs, takes its backup before it starts
    (a backup holding the open reset would run it again when restored), and owed deliveries are not replayed while
    a reset is unfinished.
  - **`wipe-player` left a guild Owner in charge of a populated guild.** The guild now passes to its senior member.
  - **A poster's reset no longer swallows a delivery** whose goods had already reached them; the claimer is paid.
  - **`kmh maintenance off` could lift a reset's hold** and reopen the economy mid-purge. It now lifts only a
    `kmh maintenance on`.
  - **A restore lifted bans and economy freezes** issued after its backup. The live sanctions are now kept.
  - **A restore closed the transactions it brought back without paying them.** Restored rows match the restored
    vaults, so boot recovery finishes them.
  - **A freeze undo could take back donations made to an earlier guild of the same name**, and marked itself done
    when part of it failed. It now counts only donations to the guild holding the name now, and is marked done only
    once everything landed.
  - **`kmh treasury-reset` changed data without `confirm`** and left escrow and owed payouts behind. One player now
    goes through `wipe-economy`; `all` needs `confirm`, runs behind the barrier and reports a failed save.
  - **`kmh restore`, `kmh season reset` and `kmh treasury-reset all` are owner-only**: an owner in Staff.json, or the
    server console.
  - `wipe-economy` and `wipe-player` no longer print "verify: OK" while a step is still unwritten, and
    `reset-preview` lists leading bids, unfinished transactions, owed deliveries and unpaid payouts.

### rel-278 (in progress)

- **RimWorld 1.7.** The server states which RimWorld version its world is played on: new setting `RimWorldVersion`
  in `Config/Mods.json` (default "1.6", read as major.minor). It is what Colony Nexus Global lists, in place of the
  fixed "1.6" every server reported, and it is sent in the KMH hello. A client that states another version is told
  why and disconnected. A KMH client from before this setting states nothing and is taken as 1.6: a 1.6 server keeps
  it exactly as before, any other server refuses it.
- **Leaderboards: one ranking behind every surface.** The server now builds every leaderboard itself
  (`Features/Leaderboards`), from statistics it already records:
  - 22 categories across economy, trade, contracts, sites, activity, colony and guilds. Each says whether the server
    measured it or the player's game reported it.
  - Ties share a rank (1, 2, 2, 4), and equal values always sort the same way.
  - Nobody is ranked for zero, and banned players are on no board.
  - `kmh leaderboard [<category> [image]]` shows any board in the console and writes it as an image (SVG, wide and
    phone-sized) to `KMH-Data/Leaderboards`.
  - Frontier captures were recorded but never reached the standings, so the season's "Frontier Claims" and every
    frontier column read 0. They now do.
- **Leaderboards on the server's page at Colony Nexus Global** (new `Global.PublishLeaderboards`, off by default):
  - The server signs its standings (the top 100 of each board) and sends them when they change, at most every
    5 minutes.
  - The website shows them under the server: grouped categories, a podium, a searchable and paged table, and the board
    as an image to share. Global draws that image itself from the rows it checked.
  - Leaderboards name players, so `kmh setup privacy` now lists exactly what is sent, and nothing goes unless the
    owner turns this on.
- **Discord shows the same standings as the game and the website.** The bot ranked by its own rules: a banned player
  still topped its boards, players with nothing were listed, and two equal values got different places. Every
  `/kmh leaderboard`, `!kmh-leaderboard`, `!kmh-rank` card and automatic post now reads the shared ranking:
  - Board names are the website's (`trade`, `colony-wealth`, `worker-xp`…), and every sort word the bot took before
    still lands on its board.
  - "wealth" used to add KMH storage to what the colony reports. Storage is its own board (`vault`), so "wealth" is now
    the colony's own figure, and each board's footer says whose word its numbers are.
  - The automatic post opens on the board the website opens on: economy, or the first one anyone is on.
  - When the server's standings are on its public page, each board links to it. The link is whatever Colony Nexus
    Global last said it shows, so a server that is not listed links nowhere. The in-game Leaderboards window gets the
    same page.
- **A banned player is off the game's record tables too.** The player stats, colonist roster and reputation sent to
  players now leave banned players out, as every leaderboard already did. Their numbers stay in the stores and come
  back if the ban is lifted.
- **`kmh config reload <area>` works.** `kmh help` features it, but `kmh config` read "reload" as the name of a
  config area and answered "No config area 'reload'". It now does what `kmh reload <area>` does.
- **A refused treasury change leaves no line in the audit ledger.** The line was queued before the save, so a change
  rolled back because its save failed still appeared in `KMH-Data/ledger` two seconds later. Treasury lines now wait
  for the save that carries their change: a refused change drops its own lines, and a change that stands in memory
  after a failed save is written with the next save that succeeds.
- **The published mod list names package ids exactly, or not at all.** RimWorld Together's config has no package
  ids, so the server matches its mods against the load order. Core never matched (it is `ludeon.rimworld`), an
  expansion could match a Workshop mod ending in the same word, and one id could land on two mods. Core and the
  expansions now get RimWorld's own ids; a Workshop mod gets one only when exactly one order entry fits it and no
  other mod claims it. A mod the config lists twice is published once, as required if either entry says so, with a
  warning naming it.
- **Colony Nexus Global refuses a mod list that contradicts itself** (Global change, staging only until deployed):
  one package id on two different mods, an entry whose role disagrees with the required/optional lists, a mod on
  both lists, or a listed mod with no entry. The same mod listed twice is still accepted.
- **`kmh global visibility` takes effect without a restart.** The listing (name, description, region, languages,
  tags, links, visibility) was sent to Colony Nexus once per boot, so the command's "it reaches Global on the next
  metadata update" never came true until the server restarted; a server set to Unlisted stayed listed. Any change
  to those fields is now sent with the next heartbeat. Found while unlisting the 1.7 test server on staging.
- **RimWorld Together 26.10.4.1: players can connect again.** That release made `Listener.TargetClient` private, and
  the admission filter called it directly, so every connection failed inside RimWorld Together's handshake before
  login. KMH now reads it by reflection whatever its access, and refuses to start admission if it cannot find it.
- **A reload of an older save undoes exactly the deposits that save lost.** Closing the game or a crash makes RimWorld
  Together load an older copy of the colony on the next join. Two things went wrong when that happened:
  - a deposit confirmed less than two minutes before was kept, so its silver existed twice;
  - deposits from another colony of the same player were taken back, because one save counter covered all of them.
  Each colony now carries its own ledger id, and every confirmed deposit records the colony and save it was made
  durable in. A reload undoes that colony's deposits made after the loaded save, however recent, and nothing from
  other colonies. The player is told what was undone. A client or save from before this change keeps the old rule.
  Found by reconnecting on RimWorld Together 26.10.4.1, where a new colony's first session is only uploaded when
  RimWorld Together disconnects cleanly.

### rel-277 (in progress)

- **A player's login is no longer dropped because another connection had not logged in yet.** RimWorld Together
  26.8.31.1 checks for an older session of the player logging in by reading every connection's player name, and a
  connection still at the door has none: the check threw and the player got no reply. A port scanner, or another
  player a moment behind, was enough. KMH runs the same check without that crash on the RimWorld Together
  generations that have it, and falls back to RimWorld Together's own if anything about it changes.
- **A Colony Nexus Global that is down behind its proxy now reads as down.** The proxy's own 502/503/504 page was
  taken as Global's answer, so the server kept saying it was reaching Global and players saw "http.502" instead of
  the offline wording. Global's own error answers are unchanged.

- **Goods in a guild vault can be seen and taken out.** Federation objective rewards and guild site output landed in
  the guild vault as items nobody could see or withdraw. The guild snapshot now carries the vault's goods, and
  `kmh.guild.withdraw_item` takes them out to the member's colony as a delivery the server owes until the game
  confirms it - replayed on the next join if the member is offline. The same rank rule as silver applies (a rank with
  a daily cap of 0 cannot), goods count against a capped rank's daily cap at their value, and the take is bound to the
  guild incarnation that was checked. A crash before the delivery is written gives the goods back to the guild once.
  Goods in a guild vault now also count toward its members' raid wealth share.
- **Quests tell the other party.** The poster is told when their quest is claimed and when a submission or proof
  waits for their review (with the `Quests.ReviewTimeoutHours` auto-release); the claimer is told when it is approved
  and paid, or rejected with the poster's note. Notices are queued for players who are offline.
- **Federation changes are announced.** A removal, a disband, a revoked link, a re-proof request (to the guild's
  Owner and Admins), leadership handed to the guild, a completed objective and the reward it put in the vault are
  each told once to the guild's members, and online members get a fresh Federation snapshot.
- **Guild events are no longer silent.** A kicked player is told who removed them, the other guild's leaders are told
  of an alliance proposal, acceptance, break or hostility, and a new Owner is told of the transfer. A refused
  promote, demote, kick, MOTD, diplomacy or settings change now tells the player why.
- **`kmh help` lists every `kmh global` subcommand** (unlink, rotate, recover, rebind were missing) and `kmh recover
  player`. The self-test now compares each dispatcher's subcommands with its help line, not only the command name.
- **Expired marketplace listings and quests send a notice** to the seller or poster (and to a quest's claimer).
- **A marketplace buyer is told what they bought** and that it is in their treasury; only the seller was told before.
- **`kmh guild inspect <name>`, `kmh guild repair` and `kmh sites inspect <user>` work**, as `kmh validate`, the
  guild integrity notes and the summaries advertised. `kmh inspect guild ...` and `kmh inspect sites ...` hand their
  words on too: they read the subsystem word as the subcommand, so repair, inspect and a player's site detail were
  unreachable.
- **A crash can no longer destroy a fee meant for the house pool.** A deposit's fee was saved out of the vault and
  credited to the pool by a second, later save, so a hard kill between the two kept the deposit and lost the fee.
  Deposit fees (confirmed, reconciled and immediate) and the treasury access fee are now written down as owed in the
  vault's own save, credited to the pool on that marker, and cleared only once the pool's save holds it; boot and the
  minute sweep finish any left over, once. A mail attachment's fee is named on its transaction row and paid before
  the row closes, so recovery pays it if a crash came between. A withdrawal fee the pool refuses is owed rather than
  dropped.
- **A deposit undone because the player loaded an older save gives its fee back to them, not to the pool.** The
  reversal took back only the amount credited to the vault, while the player's older save held the whole deposit
  again, so the house pool kept a fee nobody had paid. The confirmed deposit record now keeps its fee (it was
  stripped with the item data, which also left `fee` and `committed_utc` out of the treasury snapshot), and the
  reversal owes the pool's return in the vault's own save. The pool gives it back once, on its own marker, at once
  or at boot or the minute sweep after a crash. A fee the pool has already paid out is taken back as far as it can be,
  and the shortfall is logged.
- **A guild donation that is save-confirmed pushes the guild snapshot** to the guild's online members, so the Hall
  shows the new vault balance at once instead of on the member's next guild action.
- **The audit ledger no longer lags a value write.** Ledger lines were written on a 2 s timer, so a hard kill right
  after a deposit kept the new balance with no line for it. The treasury (personal and guild vaults) and the
  marketplace house pool now write their pending ledger lines as soon as their own save succeeds, and a house-pool
  credit whose save fails is no longer ledgered. The 2 s flusher stays as a backstop.
- **A player who is signed in but joined through a local or private address is no longer told to sign in.** The
  client reports why its session is unverified, and the Federation snapshot carries reason codes
  (`verified_why_code`, `manage_why_code`, `lead_why_code`) beside the English texts so the client can translate them.

### rel-276 — in development (not released)

- **A guild's silver can no longer move to a different guild that took its name.** A perk purchase or a donation that
  was in progress while its guild disbanded and a new guild formed under the same name used to charge or credit the
  new guild. Both now follow the guild they started with. A perk that was paid for by a guild that then disbanded is
  held for a server admin to settle (`kmh recover`) instead of going to whoever pressed buy. A donation still pending
  from an older build is returned to the donor.
- **A submitted bounty can no longer lock up the poster's silver.** The poster can now reject a submitted bounty, not
  only approve it. A submission nobody reviews goes back to Open after `Quests.ReviewTimeoutHours` (72 by default),
  unpaid. New `kmh quest approve|reject|refund <id>` lets an admin settle one. `kmh cancel quest` now works on quests
  under review.
- **Connected Treasury stays off in v1.3.1.** Setting it on by hand in `Features.json` is ignored, with a warning.
- **Guild Federation says what Global never counted.** Activity queued during a long outage is still sent when Global
  is back, within limits:
  - at most 5000 entries;
  - nothing older than 7 days;
  - the daily cap applies.

  Anything that never counts is now counted by cause and shown in `kmh federation status`, `kmh doctor` and the
  Federation window. One guild's refused reports no longer hold back another's.
- **`kmh doctor` names settings KMH does not recognise.** A misspelt key used to be ignored silently. `Config/README.txt`
  is refreshed when a new build changes it, and a fresh `Enforcement.json` keeps its explanation.
- **Auto-generated global quests vary in length** between `World.WorldQuests.AutoGen.MinMinutes` and `MaxMinutes`,
  most often near `DefaultMinutes`. Before this, every one ran exactly `DefaultMinutes`.
- **A test now guards the image decoder lockdown** (no TIFF, no metadata), so it cannot be undone unnoticed.

### rel-275 — in development (not released)

- **A listing change held for review now follows what you set now.** Changing it again replaces the held version,
  setting it back to what is live withdraws it, and a moderator approving an old version can no longer overwrite a
  newer one. Making a server Private or Unlisted takes effect at once; making it Public waits with the rest of a held
  change. `kmh doctor` now says what to do for every moderation notice, not only when a moderator wrote a message.
- **Boosts count fairly.** The weekly boost limit counts boost time that overlaps or has not run yet, and boosts
  sent at the same moment can no longer go over it.
- **Saves are always a consistent picture.** Every store saves a copy taken in one go, so a save can no longer catch
  a change half made. Player standings (display only) are written at most every 30 seconds instead of on every
  colony report; `kmh save` and shutdown still write them at once. `kmh perf` shows per-file save counts, sizes and
  times.
- **Answers from Colony Nexus Global and the Discord coordinator are size- and time-limited,** so a broken or hostile
  endpoint cannot exhaust the server's memory or hang a request.
- **The Colony Nexus website (Global rel-275):** pages that were not cached load several times faster; favorites
  find a server however far down the list it ranks; the sitemap lists every listed server, not just the first 100;
  and links with made-up parameters can no longer push real pages out of the page cache.
- **The KMH API port refuses an unsafe setup.** If it is bound to a public address with `RequireKmhApiAuth` off, it
  does not start, and `kmh doctor` and `kmh transport` say why. Authentication can no longer be switched off by a
  config reload while the server runs, and `kmh transporttest` no longer interrupts the live listener.
- **One slow answer from Colony Nexus Global no longer stops your server's heartbeat** until a restart, which also
  held back guild federation reports.
- **Reports cannot be doubled.** Two at the same moment can no longer slip past the limits, and a resent report is
  recognised as the same one.
- **Restores are safe even when interrupted.**
  - A crash during a restore can no longer delete your live data.
  - The snapshot archive and logs survive a restore that fails.
  - The backup you restore from cannot be pruned while you restore it, or while a restore waits for the next start.
  - After a file is reported damaged, the next start waits to settle interrupted payouts until you have restored it.
- **Interrupted sales, refunds and payouts finish exactly once.**
  - A marketplace cancel or expiry racing a purchase can no longer duplicate items.
  - A sale cut short by a crash is finished at the next start instead of waiting for an admin.
  - Want refunds and the refunds for a reset seller's auctions are written down before they are paid.
  - The house pool cannot be credited twice for one sale.
  - Expired or deleted mail can no longer pay its sender twice.
- **Deliveries.** After a restore, withdrawals no longer reuse an old delivery id; before, a player could lose them
  without notice. Site output sent to a colony is now re-sent until the colony confirms it, like any withdrawal.
- **New-save resets** (if you use `ResetEconomyOnNewSave`) also clear payouts that were written down but not yet
  paid, and a withdrawal from the old save can no longer arrive in the new one.
- **Discord Buy buttons buy the quantity on the button.** They used the listing number instead, so *Buy 1* on
  listing #57 could buy up to 57.
- **Guild federation:** one guild waiting to re-prove its link no longer holds back every other guild's reports.
- **A guild's sites and silver belong to that guild, not to its name.**
  - When a guild disbands, its sites stop producing and keep what they store; nothing pays into a vault under the old
    name any more, and a new guild that takes the name inherits neither the sites nor any old vault.
  - Sale and want taxes owed to a guild that has gone back to the seller, and a donation confirmed after a disband
    goes back to the donor.
  - **Upgrading:** guild sites from before this build are paused until you confirm who owns them. `kmh orphans` lists
    them; `kmh orphans rebind-legacy confirm` gives each to the guild it names, or use `rebind-site <tile> <guild>`
    one at a time. `kmh orphans release-vault` hands a set-aside vault to a player. Each logs who did it.
- **A crash or a failed save can no longer lose quest, site, roadworks or guild silver and items.**
  - Quest bounties and refunds (cancel, expiry, approval, review) are written down before they are paid and finish
    at the next start if interrupted.
  - A delivery quest moves the goods from claimer to poster in one step; if the server stops before the quest is
    marked complete, it completes once on the next try, sweep or start - the claimer is never charged twice.
  - Site output, guild site taxes, storage collection and colony deliveries are written down with the cycle or
    collection that produced them, and paid exactly once.
  - Repairs, buildings, site builds and road projects either complete or refund, never both and never neither.
  - Road work from a production cycle is laid exactly once, even if the server stops mid-cycle.
  - Roadworks cancel and finish refunds, guild withdrawals and guild perk purchases are likewise never lost.
- **Backups and resets wait for value moves from every source** - player requests, Discord, the sweeper, extensions
  and admin commands - instead of copying or wiping halfway through one.
- **Known: ImageSharp advisories.** The bundled SixLabors.ImageSharp 3.1.12 is named in five security advisories
  (three high). They are fixed only in 4.1.2, which cannot be built without a Six Labors licence key. KMH uses none of
  the affected features: it reads only PNG, JPEG, GIF, WebP and BMP images, skips their embedded metadata, and never
  writes TIFF.

### rel-274 — a safer Colony Nexus Global and a handier website

- **Colony Nexus Global only believes your address when it comes from its own proxy.** A caller that reaches Global
  directly can no longer claim to be someone else, so per-address limits and the address Global records for a server
  that publishes only its port cannot be faked. Deployments also bind Global to the local machine only.
- **The website is quicker to use.**
  - Copy a server's address or ID, share its page, and save servers as favorites; recently viewed servers come back
    on the home page and the server list. Favorites stay in your own browser, and the site works without them.
  - Choose light, dark or your device's theme; the site remembers it.
  - Each server page has a *What you need* box: the server's RimWorld, Colony Nexus and RimWorld Together versions,
    required expansions, how many mods it needs beyond Harmony, RimWorld Together and KMH, its mod rules and whether
    it asks for a password. It never claims you can join; the in-game browser checks your setup.
  - Server pages show the peak over the last 24 hours, the average while online, how much of the week it was online,
    and its usual busiest hours in your own time, once there is enough history to say so.
  - The home page shows who is popular now and groups servers by what they report: official, new-player friendly,
    vanilla-ish, modded, roleplay, PvP and PvE.
  - Links shared in Discord and elsewhere get a proper preview with the server's status, players and region.
- **Tell players more about your server.** A new `Listing` block in `Config/Global.json` holds everything players
  see on Colony Nexus: description, region, languages and tags as before, plus a one-line tagline, play style (PvE,
  PvP or PvPvE), whether new players are welcome, whether you run a whitelist and where to apply, a rules link, a
  Steam Workshop collection, a short announcement, and a maintenance message with when you expect to be back. Every
  field is optional, links must be https, and `kmh doctor` names any field it cannot use.
- **Colony Nexus has moderators now, and they talk to you.** If a listing change is held for review, refused, or the
  listing is hidden or suspended, the console says so in plain words with the moderators' message, and so does
  `kmh doctor` and your page at Colony Nexus. A refused change names the field, never the matched words. Reports
  from players never act on their own; a person decides.
- **Free boosts.** Signed-in players can boost a server they like into the website's *Boosted by players* section for
  a day. It is free, limited to one boost every three days, never your own server, and it does not change *Popular*
  or *Good places to start*.
- **`kmh doctor`** puts every health check in one list, worst first, each with what to do about it, and boot ends
  with a one-line summary.
- **Fixed: mod lists with non-English names.** Colony Nexus Global refused to store a server's mod list when a mod
  name had a character outside plain English (an en dash, an accent, Cyrillic, Chinese, an emoji), so the website
  and the in-game browser showed that server's mods as unknown or out of date. It now stores them as written. The
  same fix covers listing text, reports and moderation notes. Federation names and descriptions in any script now
  fit their character limits, and Global's upgrade keeps existing guild names in Cyrillic, Greek and other scripts
  whole instead of cutting them mid-letter. A server whose mod list Global refuses now says so once in its console.
- **Settings moved, values kept.** Config schema 7 groups three things, moving each value you set:
  - Global's listing fields into `Listing`;
  - Transport's limits and timeouts into `Advanced`;
  - each staff role's badge label and colour into `Staff.json`'s `Badges`.

  What Global assigned this server (its id, key generation and heartbeat sequence) moves from `Global.json` to
  `KMH-Data/Identity/global-state.json`, so editing `Global.json` can never orphan your registration. An empty
  `Policies.json` is no longer kept. `kmh migration-report` lists every move.

  **To go back to rel-273** after this boot, restore the `premigration-config-v6` backup it took in
  `KMH-Data-Backups` (`kmh backups`, then `kmh restore <name>`). An older build does not look for the identity's
  new location.
- The website changes need Colony Nexus Global rel-274; the server works the same against an older Global.

### rel-273 — the Colony Nexus website, finished

- **The public website has its finished look.**
  - The server directory has filter menus for region, language and tag, built from the servers actually listed.
  - Server cards show status, players and how full a server is at a glance.
  - Each server page has a join box, an activity chart you can hover for exact numbers, the mod list grouped into
    expansions, required and optional, and a details panel.
  - It works in light and dark, on phones and on desktops.
- **Every situation has a clear page.** A server under maintenance, restarting, not responding or offline says so in
  plain words. A missing page, an outage or an unexpected error each gets its own page instead of a blank screen.
- **Faster pages:** the site's styling is downloaded once and kept by your browser.
- Nothing changes on your server or in the game. The new look needs Colony Nexus Global rel-273.

### rel-272 — your server on the web, safer image handling

- **Colony Nexus now has a public website.**
  - It has a server directory you can search and filter by region, language and tag, sorted how you like.
  - Every publicly listed server gets its own page: live players, a 7-day activity chart, its description and links,
    the mods it asks for (with Steam Workshop links), and how to join.
  - Private and unlisted servers never appear.
  - Pages are shared between visitors for a short while, so a link posted somewhere busy does not load the
    directory. During a database outage visitors see a short "back shortly" page, never an error dump.
  - Long server names and descriptions wrap on phones.
  - It needs Colony Nexus Global rel-272.
- **`kmh global status` shows your server's page address** only while Colony Nexus Global actually shows the page,
  and uses the id Global knows your server by.
- **Images players post are handled more strictly.**
  - KMH restricts its image library (ImageSharp) to the formats it needs: PNG, JPEG, GIF, WebP and BMP. Unused risky
    formats such as TIFF are disabled, and unneeded metadata parsing is disabled.
  - A converted image no longer carries the original's embedded metadata.
  - ImageSharp 3.1.12 is still named in newly published advisories, which are fixed only in its licensed 4.x line;
    see `THIRD-PARTY-NOTICES.txt`.
- **Very large images are no longer passed on to players.** A PNG, JPEG or GIF link is refused when its header
  declares more than 8192 pixels on a side or about 16.7 megapixels, or when its size cannot be read. A small file
  can declare an enormous picture, and every player's game would have tried to load it. Screenshots, 4K images and
  12-megapixel photos are unaffected.
- **Reports:** a forwarded report now picks out the reporter's own console lines by their whole name, so a player
  called Bob no longer brings along lines about Bobby. A screenshot left half-saved by a crash is now cleaned up.

### rel-271 — player reports get their own table on Colony Nexus Global

- Colony Nexus Global's database step for player reports no longer collides with the older moderation table called
  `reports`. New player reports are stored in `player_reports`. The older moderation data is left untouched; nothing
  is renamed or moved.
- Global now checks that no database step creates a table or index that already exists.
- A player's IP address in a console line is now masked before a forwarded report, or the operator console, reaches
  Colony Nexus Global, as `kmh setup info` promises. The copy kept on your server is unchanged.
- Forwarded reports need Colony Nexus Global rel-271.

### rel-270 — player reports, smoother transfers

- **Players can send bug reports and suggestions from the game.** Each report is kept in `KMH-Data/Reports`, the
  newest 300 for 90 days, with the player's own words, an optional screenshot, the game's diagnostics and this
  server's own: build, health, connection counts and the end of the console. Read them with `kmh reports`,
  `kmh reports show <id>` and `kmh reports delete <id>`. A player can send 3 in 10 minutes and 15 a day, and a
  resend after a lost answer is recognised as the same report.
- **Forwarding to Colony Nexus Global is opt-in.** `kmh reports forward on` (or `Global.ForwardReports=true`)
  sends new reports on as well, signed with this server's key like a heartbeat. The copy Global gets keeps only the
  console's warnings, errors and the reporting player's own lines, and is redacted. Off - the default - nothing
  about a report leaves the server, and `kmh setup info` still promises that no player names are sent. Turn the
  button off with `Reports=false` in `Features.json`; `status.json` now counts reports held and waiting to send.
- **A slow player no longer slows everyone down.** Writes used to happen on the sender's thread, one peer at a
  time: a player whose connection stopped draining held every broadcast behind it for up to 10 seconds. Each
  connection now has its own queue and writer. Small frames waiting together go out in one write, and a peer that
  falls 8 MB behind is dropped and reconnects.
- **A large compressed message no longer drops the connection.** The server packed big snapshots such as the
  marketplace catalog (343 KB into 25 KB) into one frame, and the client refused anything that unpacked past 64
  KB. Now each side says how much it unpacks - this server 2 MB, the rel-270 client 8 MB - and anything bigger
  goes as fragments. An older client that says nothing gets fragments for anything past its frame size.
- **Less garbage per frame sent**: the envelope is written straight to UTF-8 behind its header, 45-75% fewer
  allocations at the same speed. A finished read no longer leaves its idle timer (45 seconds by default) running.
- **`kmh perf`**: frames and bytes in and out, what compression saved, send-queue peak, slow players dropped,
  scheduler jobs, memory and save timings.

### rel-269 — test runs stay off the real Colony Nexus Global

- **A server started in self-test mode (`KMH_SELFTEST=1`) reaches only a local test Global.** That covers the
  offline test runner and local acceptance boots. Before, registration and the heartbeat were held back, but
  other calls could still reach the Global in `Global.json`: Federation, Global Chat, the directory key and
  owner-link polling. Now every Global request is checked where it leaves the server: loopback, `localhost` and
  reserved test names pass, and anything else is refused unless `KMH_ALLOW_PRODUCTION_GLOBAL=1` is set. In
  self-test mode `kmh smoketest` reports how many requests were held back and that none left the process. A
  server not in self-test mode behaves exactly as before.

### rel-268 — server messages name settings that exist

- **No more warning about a Sites.json upgrade that never happened.** Every v1.3.1 boot said `Sites.json` had been
  upgraded to v1.2.0 and asked you to review settings v1.3.1 had already renamed. It looked for a key the regrouped
  file no longer has, so it fired on every start. The warning is gone.
- **Warnings and command replies name the setting to change, as it appears in the file.** In Sites.json that is
  `Output.UseTiers`, `Output.AllowUnsafeLegacy` and `Output.AllowExplicitComplex`. In Economy.json it is
  `Custom.PersonalAccess`, the guild access rules, and `Custom.DepositFeePercent` / `WithdrawFeePercent` in
  validation. In Media.json it is `Video.HelperPath` / `Video.FfmpegPath`. In World.json it is
  `WorldQuests.Enabled`. For Discord it is `Channels.Leaderboard` / `Channels.Marketplace`.
- **`kmh status`, the Discord reload and the smoke test say why Discord is off.** A bridge switched off with
  `Enabled: false` was reported as having no `Bot.Token`, even when one was stored. The two cases are now reported
  separately. The boot log and support bundle name the token's source as `Config/Discord/DiscordConfig.json`,
  not a `Discord.json` that does not exist.
- A contract check now fails the build if a server or client message names a setting no config declares, and a
  self-test pins the defaults the release notes quote: deposit and mail limits, the 32 MB image cap, 1080p video,
  three workers per site, and the 48-hour quest window.
- For contributors: `tools/protocol-contract-check.ps1 -WithoutGlobal` runs on a public checkout. Without the
  private KMH-Global beside the repository the check used to stop with an error. With the switch it now skips the
  cross-repository sections; without it they fail and say why.

### rel-267 — the published notes, checked against the build

Every public update post was read back against this build. What did not match was fixed here, in favour of the
latest thing the posts said.

- **`World.json` is in minutes throughout.** The quest-scaling window was the last setting still in hours; it is
  now `WorldQuests.Scaling.ActivityWindowMinutes` (2880, the same 48 hours). A server already on v1.3.1 has its
  value converted on the next boot - 24 hours becomes 1440 minutes - after the usual verified pre-migration backup.
  Nothing else is regenerated by that step.
- **SETUP.txt names settings that exist.** Seventeen keys it told you to edit had been renamed or regrouped in
  v1.3.1 (`TreasuryAccessFeeSilver` is `Treasury.AccessFeeSilver`, the treasury access modes live under `Custom`,
  the guild hall rules under `GuildHall`, the site tiers under `Output`, the media tool paths under `Video`, the
  world quest settings under `WorldQuests`). The guide now uses the real paths, and a contract check fails the build
  if any owner document names a setting no config declares.
- **SETUP.txt says what the upgrade does**: the first v1.3.1 boot regenerates the tunable configs, and where your
  originals are kept.
- **Debug logs are the player's to share.** The guide said `DebugLogging=true` made every client upload its log;
  the server only asks, and each player's own Never / Ask / Automatically setting decides.
- **Quest deliveries come out of the treasury.** The guide still said the goods left the colony; the server only
  counts goods it holds, and takes only what the objective still needs.
- **`kmh selftest` is described as what it is**: the build's contract suite, run by the offline self-test runner and
  refused on a live server. `kmh help` and the README no longer suggest otherwise.
- **`Config/README.txt` describes Enforcement as settings enforcement** and Mods as required/optional, matching
  what those files control.
- The upgrade banner names the identity commands from rel-266.

### rel-266 — changing a server's key without losing its identity

- **`kmh global rotate`, `kmh global recover` and `kmh global rebind <code>`.** Each keeps the server id.
  - `rotate` replaces a key that still works, for example after a suspected leak. `recover` replaces one you no
    longer have. Both first show what will happen and create nothing until you add `confirm`; you then approve in
    your browser as the server's owner, and the server finishes on its own.
  - `rebind <code>` is for a server with no owner whose id another key holds, or that the operator revoked: the
    Colony Nexus Global operator gives you a one-time code, the server proves its key with it, and picks up the
    change when the operator applies it.
  - The new key is written to disk before Colony Nexus Global is asked, and only installed once Global has
    accepted it; the old key is kept beside it. A restart or an outage in the middle resumes where it stopped, and
    an answer that never arrived is checked rather than asked again. `kmh global status` shows how far it got, and
    `kmh global <command> cancel` abandons it.
  - Federated guilds then need an officer to prove their link again under the new key.
- **The messages for a refused key name a command that fixes it** - `kmh global recover`, or `kmh global rebind`
  for a server with no owner - instead of an "owner panel" that never existed.
- **A federation objective's reward is paid once per guild**, even if the guild leaves and rejoins the federation in
  the same week, or a busy vault has recorded hundreds of other operations since.
- **Federation reasons are stored as codes**, which the client puts into the player's language, and the reason
  Colony Nexus Global gives for ending a link is no longer dropped.

### rel-265 — identity, revisions and shutdown

- **Stopping the launcher stops the server.** The addon starts itself a second time with ReadyToRun off, and that
  second process is the real server. Stopping only the first one - from a process manager, Task Manager, or a
  container's stop signal - left the real server running, holding its ports, its world and its Global heartbeat,
  with nothing left to stop it. The server now watches the process that started it and shuts down when it goes.
  On Linux the launcher also passes a stop signal on and waits for the save to finish.
- **The first start after upgrading from v1.3.0 no longer reports files missing that this release creates**: the
  Federation outbox, the federated guild and connected treasury stores, and the setup state.
- **A heartbeat now says which federation changed.** Colony Nexus Global sends one revision for each federation this
  server is in, and only the ones that moved are read again. Before, it sent only the highest, so a server in two
  federations could miss a change to one of them for up to ten minutes. The single number from an older Global
  still works.
- **A new server's id comes from its own signing key.** If another server learned the id and registered it first,
  this server takes it back when it registers: Colony Nexus Global moves the id to the key it came from, at a new
  signing generation, and ends every federation link made under the other key. Ids issued before v1.3.1 are
  unchanged and stay first come, first served.
- **`kmh global unlink` keeps the server id and signing key**, so `kmh global setup` links the same identity again,
  and the heartbeat sequence and generation carry on. It used to move the key aside and promise a new identity,
  which Colony Nexus Global then refused, because the id was still registered to the old key.
- **A private identity Colony Nexus Global no longer knows registers itself again** - after the Global database was
  restored from a backup, for example - with the same id and key.
- **A revoked registration says it is final.** The console and `kmh global status` no longer suggest unlinking and
  setting up again: a revoked id cannot be registered again, and the Colony Nexus Global operator is the one to ask.
  Federation closes on the server with that reason (`global.revoked`), and Colony Nexus Global ends the server's
  federation memberships, so a revoked leader no longer holds its federation.

### rel-264 — Guild Federation, and no value between servers

- **Guild Federation is on by default.** Guilds on different servers form one federation: identity, members,
  leadership, renown, weekly objectives, achievements and history are kept by Colony Nexus Global. This server
  signs each request, re-checks the player's guild rank at the moment they act, and keeps a local cache and a
  queue of verified activity, so an outage delays reports and never touches local guilds.
- **A server nobody set Global up on gets a private Colony Nexus Global identity** for Federation. It is never
  listed in the browser and sends no listing details and no mod list: a heartbeat, and Federation requests for
  guilds that federate. `kmh setup info` lists every field; `kmh global disable` or `GuildFederation: false` in
  `Config/Features.json` stops it. `kmh global setup` later claims it with the same id, key and federations.
- **The Connected Treasury is off.** `Connected` in `Features.json` is split into `GuildFederation` (on) and
  `ConnectedTreasury` (off), and the upgrade turns the treasury off whatever the old setting said. New personal
  and guild deposits and withdrawals are refused; operations started under an earlier build still finish or
  refund on their own.
- **A server whose owner switched Global off stays off**: the upgrade keeps the new private identity off too.
- **New `Config/Federation.json`**: turn local bonuses or objective rewards off, lower the bonus ceilings (3% at
  most), scale rewards from 0 to 2×, or stop reporting activity.
- **New commands:** `kmh federation status` and `kmh federation sync`; `kmh setup` has a *Guild Federation* line.
- **Federation authority follows guild ranks.** Admin or Owner founds, joins, invites, withdraws invites, leaves and
  proves the link again; the Owner of the leading guild removes members, hands over leadership, edits the profile
  and disbands.
- **`kmh selftest` only runs in the offline self-test runner.** It seeds and clears real stores; on a live server it
  would have overwritten players' guilds and treasury operations. `kmh smoketest` is the live health check.
- **Guild names can no longer contain `<` or `>`**, which other players' clients would have drawn as markup.
- Wire kinds `kmh.guild.connected.link`, `invite`, `invite_revoke`, `reprove` and `revoke` are replaced by
  `kmh.federation.*`; the guild treasury kinds remain, behind `ConnectedTreasury`.

### rel-221 — Global Chat, network bans and the operator console

- **Global Chat.** One chat channel shared by every connected server, carried by KMH Global itself rather than
  routed through RWT or any one game server, so it keeps working when a server restarts. Your account owns what you
  say: standing, rate limits and blocks follow the account, not the server you happen to be on.
- **A Discord channel can be bridged to Global Chat**, and a relayed line is charged to the server owner's account -
  that is what stops a bridge being an unaccountable megaphone. Bridging is off until an owner turns it on, and the
  directory can allow or deny one server explicitly.
- **Network bans.** A ban can cover global chat alone or the whole network, can expire, and is carried to official
  servers on the heartbeat they already receive - so it keeps working even if Global is unreachable when someone
  tries to join. A ban can be placed on a Steam account that has never signed in; it binds the moment they do.
- **Join tickets.** Global signs a short-lived, server-scoped ticket proving who a player is, and a game server
  verifies it **offline** against Global's published key. That is what makes a network ban enforceable at the door
  without every server having to ask Global at join time.
- **The server console, readable from the directory - by invitation only.** A directory operator can open a session
  against a server marked official and watch its console live. Two switches have to agree: the directory opens the
  session, and the **server owner sets `Global.AllowOperatorConsole`** (off by default). Official is a flag the
  directory sets on its own side, so without the owner's switch it would be a way into someone else's machine. The
  owner is told in their own console whenever a session starts and when it ends, and the session lapses on its own.
  It is read-only: nothing here runs a command on a server.
- **KMH traffic is compressed.** Frame bodies are JSON, which deflates hard - measured on a real site catalog,
  374 KB on the wire became 17 KB, and a payload that needed nine fragments and a reassembly round trip now fits a
  single frame. Negotiated per connection, so an older client is unaffected.
- **Settings changed in game now actually apply.** Several areas saved the file and then never reloaded it, so a
  change looked accepted and did nothing until the next restart. Areas that genuinely cannot be applied live say so
  instead, and there is now a `kmh server restart` for them that works whichever way the server was started.
- **Server settings are editable in game**, owner-only, and gated on a console-issued time-limited grant rather than
  on an Owners entry alone - being an owner in a file is not the same as being at the machine.
- **Configuration is regenerated, not migrated.** Every tunable config is rewritten from this build's reviewed
  defaults, and what was regenerated and what was kept is named in the console and written to
  `KMH-Data/Config/README.txt`. Identity and credentials - `Global.json`, `Staff.json`, the Discord token - are never
  touched. Your previous files are kept in a pre-migration backup.
- **Settings are grouped by the question they answer.** `World.json` went from 36 top-level settings to 4
  (`Events`, `WorldQuests`), `Sites.json` from 31 to 9 (`Limits`, `Costs`, `Output`, `Workers`, `Archetypes`),
  `Media.json` from 29 to 6 (`Images`, `Fetch`, `Cache`, `Video`, `Watch`) and `Chat.json` from 34 to 15. Every
  duration in `World.json` is now minutes; the auto-generated quest block used to carry hours beside a
  "DefaultMinutes" that meant something else entirely.
- A server name or tag written by another owner can no longer become markup in the browser. RimWorld's labels parse
  rich text, and the browser was drawing directory text raw.

#### The config migration is now a transaction

An external review of the v1.3.1 source found that the regeneration above - which deliberately overwrites every
tunable config - would run even when the things that make it reversible had failed. Each of these now stops the
migration instead of continuing past it, and a stopped migration leaves your configs exactly as they were:

- **A pre-migration backup that cannot be taken, or that does not verify against its own checksums, refuses the
  migration.** It used to log a warning and rewrite every config anyway, so a full disk or a locked file could cost
  an owner their entire configuration with no copy to restore.
- **The rollback point is pinned while the migration runs.** Retention prunes on every backup creation, so with a
  low `BackupRetention` the act of taking the pre-migration copy could delete the backup the crash journal named.
- **The crash journal names that pre-migration backup**, not the ordinary boot backup - which did not exist at all
  when `BackupOnBoot` was off, leaving an interrupted migration with nothing to roll back to.
- **A journal that cannot be written to disk refuses the migration**, rather than proceeding without the barrier
  that exists to survive a crash.
- **A migration step that fails or throws is a failure**, and the boot no longer commits the journal or clears it.
  The journal is left behind on purpose: it is what makes the next start roll back.
- **Every regenerated config is written, read back and re-parsed.** A file that did not survive that fails the whole
  migration, instead of leaving a mixed set of old and new layouts stamped as current.
- **The new schema is only recorded once it is on disk.** A stamp that lived only in memory made the next boot
  migrate again - resetting the settings the owner had just put back.

#### Also from that review

- **The upgrade banner said the opposite of what v1.3.1 does.** It told owners the protocol stayed at v2 and that
  their configs were migrated in place with their values kept. Both stopped being true in this release, and it is
  the text an owner reads while deciding how to upgrade. A check now fails the build's test suite if the banner
  claims either while the protocol is past v2 or the regeneration step is live.
- **The operator console is redacted before it leaves the machine.** It streamed raw `ServerLog` lines, so anything
  secret that was ever logged - the one-time owner link code included - was remotely readable. Lines and the status
  payload now pass through the same redaction the support bundle uses, and the link code is registered as a secret
  before it is printed.
- **Plain `http://` now reaches a directory only on this machine.** A custom directory address on another host got
  the player's bearer token in clear; loopback still works for local development.
- **The Global Chat socket no longer accepts a token in the query string.** Query-string credentials end up in proxy
  access logs and browser history; every KMH client already sends the header.
- **The server's signing key is ACL-restricted on Windows**, not merely marked Normal. Inheritance is broken and
  only the service identity is granted, which is what the Unix side already did.
- **A release package is now checked for the release it claims.** Every v1.3.1 build shares `AssemblyVersion
  1.3.1.0`, so the version check could not tell a stale rel-218 binary from a current one. The build now fails if
  the source's build tag and `InformationalVersion` disagree, and both packagers verify the tag in the built binary.
- **Global Chat no longer labels a message with a server the sender only claimed.** Nothing proved which server an
  ordinary post came from, so anyone could put an official server's name on their own message. A server name now
  appears only on messages that server itself signed for, through the Discord relay route.
- **Global's own signing key can be encrypted at rest.** It signs the join tickets game servers verify offline, and
  it lived in the database in plaintext - so a `pg_dump` was enough to forge one. Set a deployment secret and it is
  wrapped in place on the next boot, keeping the same key and the same fingerprint, so nothing pinned breaks.
- **Player credentials expire on two clocks**: 30 days idle, and 180 days from issue however often they are used.
  A token that was issued and never used also expires now - the idle check compared against a timestamp that is
  null until first use, so it never fired at all.
- **A plaintext directory signing key no longer signs anything.** Global refuses to issue join tickets, and
  `/health/ready` returns 503, until `Global__DirectoryKeyWrapKey` is deployed - there is a development override,
  but it has to be set on purpose. Nothing infers safety from a hostname or an environment name.
- **A first boot with no wrap secret creates no key at all**, rather than writing one in the clear that every
  backup taken before anyone noticed would still hold.
- **The operator console sends a purpose-built view**, not the owner's own status export: no data folder, no
  backup list, no player leaderboard, and the log file is named rather than located.
- **Two redaction holes closed.** `privateKey=` and `"client_secret":` both went out in clear, because the
  free-text rule listed fewer names than the JSON rule and would not match a name with its own prefix. Both are
  now derived from one list, so they cannot drift apart again. This also covers the support bundle.
- **A backup no longer destroys a file of yours called `manifest.txt`.** The backup wrote its own checksum
  manifest to `manifest.txt` at the root of the backup - the same name a file in `KMH-Data` can have - so that
  file was overwritten in every backup ever taken, and a restore skipped it as metadata. The backup's own
  manifest is now `.kmh-backup-manifest.txt`; older backups are still read and restored from the old name. Found
  by the new migration guard on a live server: the affected backups could never verify, so the config migration
  correctly refused to run rather than rewriting configs it had no restore point for.

### Unreleased — Discord status, and servers that say what they are

- **Your Discord status says which KMH server you are on**, how full it is and whether it is official, instead of
  RimWorld Together's "Playing Multiplayer" and a timer. On by default and one switch away in KMH settings, because
  it does tell anyone who can see your status where you are playing.
- **Friends can join from it** — but only for a server that is publicly listed **and** has no password. A join is
  resolved through the joining player's own KMH server list, never an address carried in the invitation.
- **Servers report whether RWT requires a join password.** It rides the signed heartbeat, so the directory and the
  browser can say a server is locked rather than offering a join RWT then refuses at the door. A directory that
  never reported it reads as locked, not open.

### Unreleased — server listings say something

- **What an owner writes about their server now reaches players.** The description, Discord link and website have
  been collected, signed, published and stored since v1.3.1 — and were never handed back by the listing, so the
  browser could only ever show a name, a region and some tags. They are carried on the card now and drawn in the
  server's detail panel. Nothing new needs configuring: servers that already filled these in just start showing.
- Links are shown in full with a **Copy** button rather than opened. The address is typed by another server's
  owner, and a listing should not be able to send your browser somewhere you did not choose.
- A listing loaded from the on-disk cache is made inert like a freshly fetched one. The cache deserializes
  straight into the card type, so it was a second way another owner's markup could reach a label.

### rel-220 — Steam identity

- **Link your Steam account to KMH Global.** It is what gives a player one identity across servers, and it is what
  a network ban and a join ticket are keyed on. Linking is done in a browser, from a code the game gives you.
- **Players have a directory-wide username**, chosen once and shown wherever Global speaks for you.
- A Discord coordinator for operating a fleet of servers is built and tested but is not shipped on: it needs its own
  bot token, and the one in the repository's history must be treated as exposed and rotated.

### rel-219 — KMH runs natively on Linux

- **No Windows machine is needed to host a KMH server.** The full server suite passes on a Debian guest, identical
  to Windows: no P/Invoke, no Registry, no System.Drawing. RWT's Linux and Windows server bundles carry
  byte-identical managed assemblies, so mod-list admission hashes the same file either way.

### rel-218 - current-RWT server browser

- **RWT 26.8.31.1 heartbeats now reach KMH Global at all.** `GlobalStatusProbe` called
  `CommonValues.ExecutableVersion` directly, and 26.8.31.1 turned that property into a field, so the call failed
  when the method was compiled - before its own try block. Every heartbeat from a current-RWT server was dropped and
  the server never appeared in the browser. It is reached by reflection now, as the client already did, and contract
  57 covers both sides.
- **The server publishes its mod profile itself.** It is derived from RWT's own `Master.ModConfig` - the list RWT
  actually refuses a join over - so nothing has to be published by hand. Both config shapes are read
  (`AllowAllMods`/`ModName` and the older `BypassMods`/`FileName`), and the browser is told AllowAll, NotSet or
  Enforced rather than being left with nothing to say. It is sent only when its revision moves.
- `Global.json PublicHost` is the address players connect to, paired with RWT's own port. Blank publishes no address
  at all: a guessed one would send every player in the browser somewhere else.
- `prepare-acceptance-server.ps1` copies KMH's own dependencies, and its verify step reads KMH's `deps.json`. A slim
  RWT download carries twelve files and no Harmony, so the previous five-file copy produced a folder that passed the
  readiness check and then failed at boot.

### rel-217 — v1.3.1 stabilization candidate

- **Wire protocol v3.** A v2 client is refused with both version numbers named and, on an enforcing server, is
  disconnected rather than left waiting. An older client that never acks at all now ends the pre-world hold in
  30 seconds instead of five minutes - the handshake wait and the human-consent wait are separate clocks.
- Site workers are keyed per colonist, so a player's second colonist fills the next slot instead of overwriting the
  first, and capacity upgrades are reachable. Share splits stay per account, so this cannot mint output.
  `MaxWorkersPerPlayerPerSite` (3) bounds what one client may claim. Existing sites migrate on load.
- Discord embed icons are embedded in the addon and restored at boot when missing: an in-place upgrade no longer
  silently loses them. The setup script now checks for the files, not just the folder.
- `Chat.json AllowImagePreviews` defaults to **true** for new servers. Existing configs keep whatever they have.
- `Media.json SourceHostAllowList` ships the same host list Chat does; an explicitly emptied list still means any
  https host, with every address guard unchanged.
- `VideoHelperPath` / `FfmpegPath`: a configured path that is a folder, missing or unreadable now says which before
  falling back to discovery, instead of being ignored in silence.
- `Discord.json` is generated from the type rather than a hand-written literal that had fallen three fields behind.
- The boot backfill is driven off the one canonical config list, so Chat, Media and Staff stop being the configs
  that were validated but never topped up.
- Treasury access fee (`EconomyConfig.TreasuryAccessFeeSilver`, default 0 = off): a flat charge to begin a treasury
  session, never a per-refresh charge, taken from the vault being opened and waived while that vault is empty.
- KMH Global: `registration_state` is now real. An owner can revoke a registration, after which the key stops
  authenticating and the listing leaves the browser. `kmh global unlink confirm` is the local half - this
  installation forgets its identity; `kmh global disable` stays a pause. `kmh global status` reports what Global
  believes, not only what this server holds.

### rel-216 — manual acceptance candidate

- Hold native RWT PostLogin until current connection/profile admission verifies, then release it exactly once.
- Preserve RWT mod presence checks; suppress settings/order checks only while KMH Enforcement is enabled. Unknown targets fail readiness.
- Omit an absent optional owner display name from the Global verification message. Manual RimWorld acceptance remains pending.

### Added

- **KMH Global.** `kmh global setup` lists this server in the in-game browser. Off until you run it, and
  `kmh global disable` takes you back out. Your account approves the link in a browser; the server proves itself
  with an ES256 key it generates in `KMH-Data/Identity` and never sends. Also `kmh global status`, `defer`, and
  `visibility <Public|Unlisted|Private>`. Console only — linking a machine to an account is not an in-game admin's
  decision. Back up `KMH-Data/Identity` with your saves.
- **Your listing is published from `Config/Global.json`.** Region, languages, tags and description are signed with
  the same key the heartbeat uses and sent after the first accepted heartbeat.
- **An in-game server browser**, with the servers you can actually join listed first and the reason given when you
  cannot - a mod mismatch names the mods, rather than leaving the row greyed with no explanation. Official,
  Favourites and Recent tabs, and Join actually joins.
- **The mod list a server enforces is published by the server itself**, derived from RWT's own configuration - the
  list RWT genuinely refuses a join over - so an owner never maintains it by hand.
- **Global Chat, Steam account linking, directory-wide usernames, network bans and join tickets.** See rel-220 and
  rel-221 above.
- **A treasury access fee** (`TreasuryAccessFeeSilver`, default 0 = off): a flat charge to open a vault, never a
  per-refresh charge, taken from the vault being opened and waived while it is empty.

### Changed

- Settings are fewer, grouped by the question they answer, and **regenerated rather than migrated** - see the note at
  the top of this release. The console and the migration report name every file that was rewritten and every file
  that was kept.

### Fixed

- **A client that disappears mid-push no longer floods the console.** Enforcement profile and media pushes stop at
  the first refused chunk instead of writing one failed line per chunk to a socket that is already gone.
- **A missing runtime dependency is named, not guessed.** KMH uses RWT's own Newtonsoft.Json, which on .NET 8 needs
  `System.Security.Permissions.dll` from the RWT download. Without it every save failed and KMH reported
  "KMH-Data is not writable". The boot check now names the assembly the runtime actually asked for and where it
  comes from.

## v1.3.0

Requires .NET 8. Verified against the nine most recent RimWorld Together releases — 26.8.31.1, 26.8.16.1 (1),
26.8.16.1, 26.8.9.1, 26.7.25.1, 26.6.23.1, 26.6.9.1, 26.6.8.1 and 26.5.24.1 — with the launcher picking the right
payload for the server it finds. 26.5.24.1 is the oldest supported release: RWT moved a core network type below
that point, so 26.4.18.1 and older are a different generation, and on those KMH refuses to start and says so rather
than running half-patched. Protocol stays at v2, so a v1.2.x client still connects; new features negotiate through
the capability manifest.

Upgrading is in-place: existing `KMH-Data` and `Config` files are migrated on first boot, with a written report and
an automatic backup beforehand. No configuration changes are required.

### Fixed

- **A brand-new server no longer reports itself as damaged.** The first boot scanned KMH-Data before it had written
  it, so a fresh install announced "23 required missing" and then listed every file it was about to create. A new
  server now says it is new and names what it has; the missing-file warnings are kept for a server that really has
  lost something.
- **Three state files are created at first boot like every other.** The outbound delivery queue, the guild
  contribution ledger and the transaction ledger were only written once something used them, so a healthy new server
  reported them missing on every boot and every `kmh verify` until the first delivery, contribution or transaction.
  They are now materialized empty alongside the other twenty, and both ledgers also join the shutdown flush.

- **A picture Discord attaches late is converted like any other.** Discord resolves a link's preview by editing the
  message afterwards, and KMH attached that preview without running it through the media resolver — so an animated
  WebP shared as a link reached players as Discord's flattened still, while the identical picture posted as an
  attachment animated. Both paths now take the same decision.
- **Discord is no longer asked to re-encode pictures it already serves.** Every proxied image was asked for as PNG.
  Measured on a real link, that turned a 212 KB JPEG into 1.1 MB of PNG for an identical picture, and flattened an
  animated WebP into a still. A format the game can already read is now taken as served, and a wrapped animated
  source is asked for as GIF, which keeps every frame.

- **The server no longer sends players at a KMH port it is not listening on.** The hello quoted the *configured*
  transport port and said the transport was on whenever the setting was on — neither asked the listener. If the bind
  had failed, every client dutifully dialled a dead socket, timed out, and settled onto the chat path while the
  server itself looked healthy. Both values now come from the socket that actually bound, so a server that could not
  open its port tells clients that plainly instead of sending them somewhere empty. A port of 0 is reported only when
  nothing is listening.
- **Two deposit caps that were never really caps are now applied to existing servers.** A server carried over from
  v1.2.x kept `MaxSilverDepositPerTx` at 100,000,000 and `MaxItemDepositQtyPerTx` at 100,000 — numbers high enough
  that nothing was ever refused. Both move to the shipped 1,000,000 and 5,000 on the next boot, and, as with every
  defaults upgrade, only a value still sitting at the old stock number is touched: anything you set yourself is kept
  and named in the boot notice. Fresh installs already generated the new figures.
- **`kmh status` reports the port clients are actually given.** It printed the configured value, which is the one
  number that is useless when those two have diverged, and it now also spells out the exact host and port a joining
  client is told to dial.
- **Listing a one-of-a-kind item can no longer lose it.** Posting an item with its exact condition — a damaged
  weapon, a piece of named gear — briefly belonged to nobody: the goods left the vault before the record naming them
  was written, so a crash in that instant lost them. The vault now holds them itself until the record lands, and
  anything caught in between is returned at the next boot. Two further ways the same paths could hand goods back
  twice were found and closed at the same time.
- **A Discord account can no longer be linked to two players.** Linking to a second account is refused and names the
  one that already holds it. A data file that already contains a duplicate resolves to nobody, and says so, instead
  of picking whichever entry happened to load first. A link the disk refused is no longer reported as successful.
- **Backups are one moment in time, and restores are verifiable.** A backup taken while trading continued could catch
  the treasury at one instant and the auction house at another. Value moves are now paused and every store flushed
  before anything is copied, and each backup carries a checksum per file plus the build, protocol and schema that
  wrote it. Restoring verifies all of that first and refuses a backup that does not match, always takes a safety copy
  of the current data before starting, assembles the restored files to one side and swaps them in at the end — so an
  interrupted restore leaves the data you had, never a mix of both. Diagnostic logs and support bundles survive a
  rollback, and a restore advances the data generation so actions created before it are refused rather than applied.
- **`kmh recover refund` no longer prices an item from a number a client supplied.** It was the one path where a
  client-reported value became silver that actually moved. It now requires an owner-set value and refuses with an
  explanation otherwise, pointing at `kmh catalog`.
- **A client can tell "sent" from "applied" for its item catalog.** The client latched success on a successful send,
  so a catalog refused during a maintenance window left it believing in one the server never held. The server now
  reports which catalog it actually adopted, and a push that never landed is retried.
- **World-map markers no longer wear the wrong artwork.** A marker whose icon failed to resolve once — which happens
  if it is asked for before the game has finished loading — kept the fallback for the whole session with nothing in
  the log. Lookups are now retried and a genuinely missing file is named. A path-casing check was also fixed: it was
  case-insensitive, so a wrong-case texture path passed on Windows and would have failed on a Linux server.
- **Shipped configs keep their own instructions.** `DiscordConfig.json` and `Enforcement.json` ship with a `_readme`
  explaining how to fill them in, and the first boot of a brand-new install deleted it as an unknown setting before
  anyone saw it. A fresh install also no longer performs a migration of itself.
- **Notification queues report a failed write.** Notices put back after a failed hand-off are held in memory only if
  the disk refuses them; that is now said out loud rather than counted as delivered.

- **Auction payouts can no longer destroy a winner's silver.** A malformed auction row with no seller was settled as a
  sale: the item went to the winner and their escrowed bid was credited to nobody and discarded. Such a row is now
  refused - the bid goes back to the bidder, the item is held for an operator, and bids on it are rejected outright.
  Any auction silver that cannot be credited is held in recovery instead of dropped, the same way items already were.
- **A corrupt data file can no longer be overwritten.** When a corrupt file could not be moved aside, the server
  started from defaults and the next save destroyed the only copy. Saving to that file is now refused until it is
  moved by hand, and resumes on its own once it is gone.
- **A pending deposit that can never resolve is reported.** Reconcile only acts on pending rows, so one resting in any
  other state held its value out of the spendable balance forever with nothing to show for it - not even the integrity
  check, since the file is not corrupt. Those rows are now named at boot.
- **A large treasury opens again.** Past a certain size a vault's contents no longer fit in one network message, and
  the message was dropped without a word - the screen sat on "Loading treasury…" forever while every other panel
  refreshed normally. Messages that outgrow one frame are now split and reassembled automatically, so no KMH feature
  fails merely because a player accumulated a lot. A panel that gets no answer now says so instead of showing a
  green "live" badge over an empty screen.
- **A big vault reads as a list of items, not a list of stacks.** Storing the same thing repeatedly used to add a
  row every time, because a modded item's saved state made each stack look unique - hundreds of `Bone x75` lines for
  one kind of item. Identical-looking stacks now show as one row with the total, while the server keeps every stack
  exactly as it was, and withdrawing draws across them. Anything genuinely different - damaged, tainted, another
  material or quality - still gets its own row.
- **Reclaims scale with your population.** A Reclaim used to ask for the same 150 steel and pay the same 500 silver
  whether one player was on or forty. It now scales with both how many are online and how many have ever joined —
  15% per active player beyond the first, 5% per known player, capped at five times. The *same* multiplier is applied
  to the cost and to the reward, so a busy server is a bigger contest rather than a better or worse deal, and the
  operation is still fully backed by the house pool before it is ever advertised. Both weights are yours to set in
  `Frontier.json`; zero them for the old flat behaviour.
- **You can turn the terminal down without losing anything.** `Maintenance.json` gains `ConsoleLogLevel` — `all`
  (default), `warn`, `error` or `quiet`. It decides only what the console is shown; `KMH-Data/Debug` still receives
  every line, so a quiet window and a complete diagnostic file are no longer a trade-off.
- **One port. KMH never opens a second one.** Video links are served over the KMH transport port, and the setting
  that could put the relay on its own listener is gone rather than defaulted off — a dedicated port was one more
  thing to know about and forward, and when nobody had, video failed as a connection timeout with nothing to
  suggest the port was the reason. An existing `Media.json` keeps its old `VideoServerPort` line; it is ignored,
  and disappears the next time the file is rewritten.
- **Debug logs stay a diagnostic, not a disk filler.** Each session writes its own timestamped file under
  `KMH-Patch/Logs`, rotated at 10 MB, ten kept, cleaned up after two weeks. Packet tracing is now a separate switch
  from ordinary debug logging, and the 15-second heartbeat is summarised once an hour instead of logged every time.
- **Saving no longer sends the same confirmation three times.** Several hooks notice one save, and each used to
  resend the whole deposit history. One save now sends one confirmation, carrying only what that save made durable.
- **Withdrawing part of a stack works.** A stack of 75 could only be taken all at once, because "can this stack be
  divided" and "can two separate stacks be combined" were answered by the same flag. They are now separate: units
  come off a stack the game already holds together, while combining two independently stored stacks still requires
  their hidden state to match.
- **Modded items keep their identity.** Stored items are no longer merged on the basis that their item type looks
  stackable - a renamed, owned or otherwise customised modded item could lose that state to a neighbouring stack.
  Two stored stacks are only combined when the game itself vouched for that specific stack.
- **Roadworks builds road again.** A road project took its silver into escrow and then never progressed - nothing
  ever turned a Roadworks site's work into construction. A Roadworks site with an active project now spends its work
  cycle building instead of producing goods, from the crew that turned up: worker count, Construction skill and the
  site's condition. Output tier and production buildings deliberately do not make road go up faster.
- **Standings statistics that were always zero.** Sites and worker XP were shown in Standings, ranked in Discord and
  fed into the economy score while nothing ever wrote them. Worker XP now belongs to the worker who earned it rather
  than to whoever owns the site, "Sites" means what you control right now, and a separate count records what you
  actually built this season. A captured outpost is a Frontier record, not a construction one. The economy score is worked out
  from those numbers as it is read, instead of from a stored total that site work and sites built never updated - a
  player whose contribution was site work scored nothing, and Discord ranked everyone on that.
- **Guild item donations are recorded.** Donating silver built a contribution history and donating items built
  nothing, so the two were never comparable in `kmh contributions`, player snapshots or Discord.

- **Wants no longer lose their requirements.** A want's match constraints — minimum quality, required material, and
  whether used, tainted or damaged items are accepted — were dropped on the way to every client. The board showed no
  requirements, and a want that accepted used gear could not be fulfilled from the UI at all.
- **Guild halls reach clients again.** A guild's hall was dropped from every snapshot, so the world marker never
  appeared, the hall window always read "no hall", and the client's hall-radius check treated that as "unrestricted".
- **Site snapshots carry every field.** Outposts reached clients with a blank template and state, so nothing read as
  an outpost and nothing could be claimed. Site condition, archetype, buildings and stored items were dropped too.
- **A derelict outpost no longer asks for admin review.** An outpost produces nothing until it is claimed, which the
  startup output check read as a blocked output and paused, warning on every boot.
- **Guild Hall treasury modes actually enforce again.** Because the hall never reached clients, they always reported
  "at the hall", so `GuildHallRequired` enforced nothing and `CaravanNearGuildHall` enforced only that a caravan
  existed. **If your server uses either mode, it has not been restricting access — it will now.** Servers on the
  default `Remote` mode are unaffected.
- **"Caravan near Guild Hall" now means the caravan.** It accepted a caravan anywhere in the world as long as some
  colony sat near the hall. This also applies to `RequireCaravanNearGuildHallForContribution`. Clients older than
  this release still use the previous, looser answer rather than being locked out.
- **A claim you earned stays yours for the whole window.** The winner is recorded on the location the moment it
  becomes claimable, instead of being worked out from a completed operation that leaves the board half an hour into
  a sixty-minute window - which used to make a location unclaimable while the timer still showed time left.
- **Frontier survives an unclean shutdown.** A server killed part-way through resolving an operation now finishes it
  on the next start rather than refusing to act again for the life of the save. On boot the director also checks its
  own bookkeeping against the sites and quests that actually exist, repairs what a crash left behind, and says what
  it corrected - a healthy start stays silent.
- **An unclaimed outpost no longer strands the world director.** A restored location nobody claimed in time stayed
  claimable forever — refusing every claim while still holding its tile and counting against the outpost cap. Once
  enough of those accumulated the director stopped placing anything, permanently. They now go dormant and release
  the slot, and can be offered again by a later operation.
- **Global quest snapshots no longer share live records.** The world snapshot handed out the quest objects themselves
  and serialized them after releasing its lock, so a delivery landing mid-send could corrupt the outgoing snapshot.
- **Value is no longer confirmed before KMH-Data has actually saved it.** A withdrawal debited the balance in memory,
  told the client to materialise the silver, and moved on — even when the write to disk had failed. On restart the
  balance came back and the silver had been minted; the mirror case lost a deposit the player had already handed
  over. Deposits, withdrawals, listings, bids, quest bounties, site rewards and recovery payouts are now refused
  rather than confirmed when the server cannot write them down, and the change is undone rather than left half
  applied. The freeze after repeated failures remains as a safety net on top, but no longer decides correctness.
- **A held recovery record can no longer be paid out twice.** Triage marked a record resolved only in memory, so a
  crash or a failed write between the payout and the record left it looking untouched and payable again. The claim
  is now written down before the value moves, and anything interrupted mid-delivery is reported at the next boot
  for an operator to confirm rather than silently re-issued.
- **KMH API connections no longer outlive the session that owns them.** A connection whose RWT session had ended
  kept answering heartbeats and looked healthy while every request it carried was dropped. The API transport is now
  bound to the exact authenticated session, closes with it, and its credentials are invalidated with it — so a
  stale connection cannot be mistaken for a live one or carry across a reconnect.
- **A request that may already have been received can no longer be applied twice.** When a network write failed part
  way, KMH re-sent the same request over the chat transport, which could submit one withdrawal, bid or purchase
  twice. Requests that move value now name the action they are, and the server applies each one once however many
  copies reach it — so an unlucky socket error is retried and delivered rather than dropped with nothing to show.
- **One click is one purchase, however slow the server is.** Buying, posting a listing or auction, posting or
  filling a want, posting a quest, sending mail with an attachment, adding a site building, starting a road, buying
  a guild perk, withdrawing, donating and delivering to a global quest all carried the risk that a repeated or
  replayed message did the whole thing again — a second escrow, a second charge, a second delivery. Each is now
  applied once per action; a genuinely new one a moment later still goes through, and a refusal (not enough silver,
  say) can be retried immediately rather than being mistaken for a repeat. Cancelling, bidding and the like were
  already safe to repeat and are unchanged.
- **Large requests no longer fail when KMH falls back to RWT chat.** The chat path sent oversized messages whole and
  the server dropped them. It now splits them the same way the direct API transport does.
- **The chat-fallback setting is enforced, not only advertised.** With `AllowChatTransportFallback` off, KMH features
  no longer tunnel through the chat log in either direction, and a modified client cannot ignore the setting. The
  handshake itself still uses chat, because that is how the API is discovered at all.
- **A global quest delivery now comes out of your KMH treasury.** The server used to take the client's word for what
  had been handed over, which meant a modified client could claim quest progress it had not earned — and, against a
  quest that had already ended, be "refunded" items it never owned. Deposit the goods first and the delivery is paid
  from what the server can see you have; anything the objective has no room for stays in your vault.
- **A site can no longer be priced from a value only the client reported.** For an item the server had no trusted
  market value for, the build cost, cycle time and output were computed from whatever the client claimed — so an
  under-reported value bought a cheap, fast, productive site. Such a build is now refused with an explanation, and an
  operator can set the value with `kmh catalog`.
- **Reported pawn skill no longer raises what a site actually produces.** A headless server cannot verify a
  colonist's skill, and taking the higher of the reported skill and the earned level handed a claimed skill 20 the
  output of a fully trained worker on their first cycle. Production and road progress now count only XP the server
  awarded. The pawn's skill is still shown.
- **Two quest posts can no longer both take the last slot.** The open-quest limit was checked before the bounty was
  escrowed and never re-checked when the quest was inserted, so simultaneous posts could both get through. The loser
  now has its bounty returned in full.
- **An extension cannot move value while maintenance is on.** `kmh maintenance on` paused requests from players but
  not from loaded extensions, which reached the same stores directly. Operator commands are deliberately unaffected.

### Changed

- **An RWT release older than KMH supports now says so.** On 26.4.18.1 and older, KMH detected the file set as
  26.5.x, started patching, and stopped on a Harmony `TypeLoadException` — safe, because nothing operates after a
  failed start-up, but impossible to act on. It now checks that the type it patches actually exists in that
  release's `TCPNetwork.dll` and refuses before touching anything, naming 26.5.24.1 as the oldest it runs on.
- **An older picture no longer expires back into a still.** The media cache holds two different things: the converted
  bytes, and the note saying which URL an image came from. Its two-hour expiry dropped both — so a gif nobody had
  opened for a couple of hours lost the only record of where it came from, and the next person to open it got
  Discord's flattened preview instead of the animation. The expiry now releases bytes and keeps the note.
- **One picture, many players, one fetch.** When several people loaded the same image at once the server fetched and
  converted it once per person — the same URL pulled repeatedly from the source host and transcoded repeatedly here.
  Everyone after the first now waits on the work already running.
- **Animations are converted at the size chat actually draws.** A GIF writes every frame in full, so a 500-pixel
  animation costs several times the bytes and one oversized texture per frame on every client that shows it in a
  200-pixel row. Animations taller than `Media.json` `AnimationMaxHeight` (320 by default) are scaled to it, with
  every frame and its timing kept. Measured on a real 498x498 source: 5.4 MB became 2.5 MB, all 42 frames intact,
  and an animation already under the height is passed through byte-for-byte. Set it to 0 to convert at source size.

- **A snapshot sent to every player is prepared once, not once per player.** Each recipient of a broadcast was making
  the server serialize, UTF-8 encode and frame the same payload again from scratch. Those bytes are identical for
  everyone, so they are now built once and written to each connection. The saving scales with your player count and
  with payload size — the site catalog on a vanilla game is around 120 KB, and it was being encoded once per client.
- **The world road list is built once per broadcast.** Every player's roadworks snapshot rebuilt the whole planet's
  road network, inside the store's lock, even though the roads are the same for all of them. The list is now shared
  across one broadcast and thrown away the moment anything about the network changes.

- **Hunt and build global quests no longer pay a reward by default.** Progress on those objectives is a number the
  client reports about itself — a headless server cannot see a kill or a finished structure — so a modified client
  could report the goal and take the pot. They still run, complete and announce, and they are no longer generated
  automatically. Set `World.json` `PayRewardsForSelfReportedObjectives` to `true` on a server where you trust every
  client build. Deliver objectives are unaffected: they are paid out of the treasury.
- **Generated global-quest rewards scale with banked silver, not reported colony wealth.** `QuestRewardWealthPercent`
  previously applied to the colony wealth players report about themselves, which one modified client could inflate
  to pin every generated reward at your configured maximum. It now applies to the silver held across KMH treasuries.
  That is a much smaller number, so if you tuned this setting, re-tune it — the house-pool, item-value and
  per-target terms and `QuestMinReward` still apply.
- **A media refresh may only name a reference the server minted.** A client asking to re-acquire an expired Discord
  link is answered only when a message still in history carries that exact reference, in a channel that player can
  read. A crafted reference previously reached Discord with the server's own bot credentials.
- **Sites: `UnknownOutputFamily` now does what it says.** The field was generated, documented and validated, and then
  read by nothing - an owner setting it saw no effect. It now supplies the family for a def the classifier cannot
  place. The default is still empty, which leaves such items Unclassified and buildable by no archetype.
- **Every owner config is seeded at boot and topped up on upgrade.** `Frontier.json` was only written when the world
  director first ran, and `Mail.json` and `Frontier.json` never gained fields added by a later version. A contract
  check now fails the build if a config is generated without also being seeded, topped up and inspectable.
- **Chat history saved before the media resolver existed gets its resolver id back on load.** Media a client cannot
  decode - an animated WebP - was falling back to a still after every restart.
- **New-server defaults.** A fresh install now generates `EconomyMode=Balanced` with `MaxSilverDepositPerTx`
  1,000,000 and `MaxItemDepositQtyPerTx` 5,000 (a 100,000,000-silver cap is not a cap), `MailConfig.MaxAttachSilver`
  500,000, and Frontier Operations enabled. An existing config keeps every value it already has.
- **Two package layouts, and the zip name tells you which.** The plain
  `KMHServerAddon-v1.3.0-<platform>.zip` is still one `KMHServerAddon.exe` with everything inside it, and still needs
  .NET 8 on the host. The `-selfcontained` one carries .NET 8 instead, which makes it a folder of files where
  `KMHServerAddon.exe` is a small launcher that will not start on its own — extract the whole thing and keep it
  together. Renaming the exe into a panel's fixed `GameServer.exe` slot still works in both.

### Added

- **A self-contained build for every architecture.** The `-selfcontained` zip carries .NET 8 inside it, and it now
  exists for all six: win-x64, win-x86, win-arm64, linux-x64, linux-arm and linux-arm64. Before, only the two x64
  builds had one, so a panel pinned to an older runtime - or a box with no internet to fetch one - was stuck on
  every other architecture. Nothing to install, nothing to download on the server.
- **The video tools come with a script that fetches them.** In-game video needs `yt-dlp` and `ffmpeg`, which belong
  to other people under other licences and so are not bundled. The scripts that download them now ship in the
  server's `Tools` folder, and separately as `KMH-MediaTools-v1.3.0.zip` if that is all you want. They put both
  binaries where the server already looks, so there is no config to edit and nothing to move. Tell one which
  platform you are fetching for and a server with no internet stops being a problem - run it elsewhere, copy the
  folder over. Skip all of it and the only thing you lose is video.
- **`kmh support-bundle`.** Writes one shareable zip - configs, status, the latest migration report and the tail of
  the system log - with bot tokens, API tokens, webhooks, passwords and signed-url signatures masked. No binaries,
  backups, saves, media cache or chat history. Reporting a problem no longer means sending a whole server directory.
- **The Discord bot token can live outside the config.** Set `KMH_DISCORD_BOT_TOKEN` and leave `Bot.Token` blank; the
  variable wins, and the config file is never rewritten with the secret. `kmh diag` names the source, never the value.
- **Backups no longer copy the media cache.** Converted chat media is regenerable and is wiped at every boot, but it
  was being copied into every backup - up to `CacheMaxMegabytes` (256 MB by default) per copy, on a path the
  save-reset anti-cheat runs on player action, retained twelve deep. Support bundles are excluded for the same
  reason: a bundle is a redacted copy of what the backup already holds. Old bundles are pruned to the newest five.
- **Two commands were dispatchable but invisible.** `kmh support-bundle` and `kmh roadworks` never appeared in
  `kmh help`, so the only way to find them was to already know they existed. Both are listed now, and a check fails
  the build if the dispatch table and the help table ever disagree again.
- **`kmh config` covers every config, and says what is actually in force.** Chat, media, staff, mail and Frontier were
  missing from it. For economy it now also prints the resolved policy, because the granular fee, cooldown, cap and
  raid fields are read only under `EconomyMode=Custom` - beside a preset they describe a policy that is not applied.
- **Frontier records in Standings.** Outposts you hold now, and the claims you have won - the second is history, so
  losing a location never erases it. Standings totals are cumulative on this server: `kmh season roll` archives the
  finished season into the archive and the all-time records without clearing them, and only the destructive
  `kmh season reset confirm` starts player totals over.
- **Guild invites expire.** A pending invite is good for 14 days and a guild holds at most 25 at once, so an invite
  list stops growing forever and a year-old invite stops letting someone walk in. Existing invites get a full window
  rather than being cleared on upgrade.
- **`kmh roadworks`.** Inspect road projects, see how much silver is held in escrow, and cancel a stuck project on a
  player's behalf - it returns their unbuilt escrow exactly as their own cancel would and keeps the road already laid.
- **Global quests size to the right crowd.** `GlobalQuestUseOnlinePlayersOnly` now genuinely chooses between players
  online right now and everyone active within `GlobalQuestActivityWindowHours` (default 48), never sizing below the
  players actually present.

- **Frontier Works.** Sites now have an archetype — Farmland, Quarry, Woodland, Ranch, Roadworks or Custom — which
  names the site and decides which colonist skill its work uses. Picking the archetype that suits what you produce
  earns a small specialization bonus to output; Custom takes none, which is the price of producing anything at all.
  Existing sites read as Custom and keep producing exactly what they produced. Site condition and placed buildings
  feed production; a damaged site is worse off, not worthless.
- **Site buildings and storage.** Sites have a small slot budget by tier (2/3/4). A production building raises
  output, housing raises the worker cap, and storage lets a site hold its output for collection instead of
  delivering every cycle. Held goods are the owner's off-map wealth, so they count toward raid scaling and do not
  survive an economy reset; only the owner can store at their own site, and overflow always falls through to the
  treasury rather than being lost. Demolishing refunds nothing, which keeps a building a sink and never stored value.
- **Site condition.** A damaged site produces less but is never worthless — output flattens to a floor rather than
  scaling to zero, and only a fallen site stops entirely. Nothing lowers condition on its own: there is no decay and
  no upkeep chore. Damage comes from `kmh site-condition damage <tile>` or an extension, and owners repair from the
  site's building window, priced on the damage actually undone.
- **Roadworks.** A Construction-focused archetype that builds persistent roads across the world: Trail, then Road,
  then Highway, each unlocked by the site's tier. Routes are priced per new segment, so crossing an existing road
  costs nothing. Silver is held while a project runs and returned for whatever is still unbuilt if it is cancelled;
  finished road stays. Reserved silver counts toward raid scaling and is destroyed by an economy reset, so a road
  project cannot be used to hide wealth. Owners can turn the whole system off with `Sites.json` `AllowRoadworks`.
- **Frontier Operations.** The world now creates its own work. A server-side director opens objectives on the map —
  the first is Reclaim, restoring a derelict ruin — and hands the restored location to whoever contributed most,
  earliest; the winner chooses at claim time whether it becomes theirs or their guild's. Operations are funded
  entirely from the House Pool: the advertised reward is capped to what the pool can actually back, and the operation
  simply does not open if that falls below the configured minimum. Nothing is ever minted to cover a shortfall.
  Locations are placed by asking connected clients for candidate tiles; matching answers from independent clients are
  stronger corroboration, and an owner who wants that guarantee sets `AllowSingleProposer=false` — by default a small
  server may act on one valid proposal. Either way the server decides, and proposing costs and pays nothing. Sites can
  now be held by a player, a guild, nobody, or a hostile faction, without inventing a player account to stand in.
  A captured location arrives as infrastructure rather than a finished site: its new owner picks what it produces
  once, through the ordinary site output rules and with no build cost, after which it behaves as a normal site.
  A location nobody claims goes dormant and is reused by a later operation instead of holding its tile for good.
  Configure in `Frontier.json`; Frontier is enabled by default and held in check by its spawn budget, cooldowns and
  active-location limits rather than by an off switch. Server owners get `kmh frontier` to inspect the
  director, list outposts, place one directly, run a step immediately, or claim on a player's behalf — the last is
  refused unless that player actually earned it, so it resolves disputes without handing anything out.
- **`kmh worldquest deliver <id> <user> <qty>`.** Credit a delivery lost to a disconnect. It runs the ordinary
  completion path, so the reward and any operation consequence apply exactly as a real delivery's would.
- **A cooperative objective takes only what it still needs.** Delivering 150 into a goal that wanted one more no
  longer reads as 299 of 150: one is credited and the other 149 go straight back to your treasury. It matters most
  on a Frontier reclaim, where the largest contribution decides who may claim the location.
- **A guild site's production belongs to the guild.** Its owner share goes to the site's own storage or the guild
  vault — never through whichever member happens to open the menu — and emptying that storage puts the goods in the
  vault too. Workers still receive their own shares personally. Production is guild income, so it does not count as
  a member donation or inflate anyone's contribution standing.
- **Guild-owned sites are managed by the guild.** Any current member of the controlling guild can assign workers,
  build, repair, collect storage and set up a captured location — including its Roadworks projects. Leaving the guild
  removes that access at once. A road project stays personal to whoever starts it: they pay for it, they can cancel
  it, and their unbuilt escrow returns to them.
- **Presence and playtime.** Communications shows who is online and who is not, with how long ago each offline player
  was last here. The server measures time connected itself and, separately, time actually played — a paused game
  nobody has touched for two minutes stops counting, while watching a raid play out does not. Both appear on a new
  **Activity Records** standings board alongside a focus share and last seen, and on each player's card. Discord gets
  `/kmh leaderboard active` reading the same server-side figures.
- **Finding people is easier.** The roster keeps whoever was here most recently rather than whoever comes first
  alphabetically, so a busy server no longer hides half its players. Typing `@` completes names from that roster,
  online first; being named reaches you even on a channel you have set to silent, and someone you have blocked is
  never suggested.
- **Staff badges and server colours.** Owners, admins, moderators and developers are marked in chat and on boards
  from the server's own role list, never from anything a client claims. Name, message and Discord-relay colours are
  configurable per server, and text always carries the same meaning as the colour for anyone who cannot separate them.
- **KMH chat.** Server-wide, per-guild and direct-message channels, with history on reconnect, per-user rate limits
  and a server-side log of every message.
- **Player mail.** Players can send each other letters carrying silver, items or full-condition gear. Attachments are
  escrowed server-side, can be recalled while unread, and are returned automatically if never claimed.
- **Moderation.** Per-player block lists that filter both live messages and history, plus staff message removal.
- **Discord chat bridge.** Mirrors KMH chat to a Discord channel in either direction, independently configurable.
  Linked accounts post under their verified in-game name; relayed messages can never masquerade as native chat.
- **Off-map wealth counts toward raids.** Value parked in treasuries, guild vaults, listings, auctions, wants, quest
  bounties and mail attachments now feeds threat scaling on v1.3.0+ clients. Server-controlled. A guild site's wealth
  is split across its members by share rather than charged to one of them.
- **Guild property outlives a member's save.** Resetting your own save clears what is yours; the guild's vault and its
  sites belong to the guild and stay, so one member starting over cannot empty a shared treasury.
- **Extension SDK.** Typed APIs, events, veto hooks (including marketplace visibility), per-extension storage, and an
  SDK contract version so an incompatible extension is skipped with a clear message instead of failing oddly.
  Two worked examples ship in `Templates/`.
- **Config migration and recovery.** Config files migrate atomically with a journal and startup rollback, are
  load-tested at boot, and are restored from backup when damaged instead of silently reverting to defaults.
- **Transaction ledger.** An append-only record of value movement, with crash reconciliation at boot.
- **Guild contribution ledger** and single-owner integrity checks.
- **RWT preflight.** KMH validates the RWT server's own config files before handing off, restores a damaged one from
  its last good copy, and translates two known-fatal RWT startup crashes into a sentence naming the file.

### Fixed

- **Deposit duplication via save rollback.** A deposit that was confirmed and then undone by loading an older save is
  now reversed on the server, so goods cannot exist in a colony and a vault at once. Reversal only runs on positive
  evidence, never drives a balance negative, and reports anything already spent.
- **Want-board fills could charge for undelivered goods.** An item stack carrying saved condition cannot be split, so
  asking for fewer units than the stack holds delivered nothing while still billing the buyer. Fills are now sized by
  what can actually be handed over.
- **Silver and item counts could overflow.** Every balance and quantity accumulation now saturates instead of wrapping
  to a negative number.
- **Site build cost could be gamed.** A large enough requested amount overflowed the cost calculation and priced the
  site at the floor.
- **Guild daily withdraw caps reset on every restart.** The cap now reads its persisted record, so a daily limit is
  daily rather than per-uptime.
- **Guild perks did not apply to existing sites**, and a site kept a former guild's perks after the owner left. Site
  worker caps, XP bonuses, access and quota are now derived from current membership.
- **Quest bounties and mail attachments survived economy resets**, sheltering value from a wipe. All three reset paths
  now share one definition of what a reset destroys.
- **Failed saves reported success.** A store that cannot be written now logs loudly and freezes value-moving requests
  until it recovers, rather than accepting deposits that exist only in memory.
- **Boot backups were pruned while data was damaged**, spending retention slots on copies of broken data at exactly
  the moment the intact ones mattered.
- Marketplace, auction, quest and site refunds now route through the recovery queue, so a delivery that cannot land is
  held for admin triage instead of being dropped.
- Guild vault deposit caps could be overshot by concurrent donations.
- Chat and mail text can no longer forge Discord lines or inject formatting into other players' UI.
- Block lists are bounded and rate-limited, so they cannot be grown until the disk fills.
- `kmh smoketest` no longer leaves an empty diagnostic vault behind on every run; existing ones are removed at boot.

### Changed

- Snapshots are built once per viewing guild and reused, rather than rebuilt per recipient.
- `kmh audit-player`, the per-player snapshot record and the reset preview now agree with each other about what a
  player holds, and the off-map wealth figure states what it actually counts.
- `status.json` reports mail, chat, unclaimed mail escrow and parked recovery value.
- The offline notice queue is no longer called "mail" anywhere; Player Mail is a separate feature with its own name.

### Notes for server owners

- Back up `KMH-Data` before upgrading. KMH also takes its own backup on first boot.
- Update every player's KMH-Patch alongside the server. The protocol is still v2, so a v1.2.x client is not locked
  out - it connects, KMH turns on, and it quietly shows none of the new features. Nobody will report that as broken.
- `kmh verify` and `kmh smoketest` both run more checks than in v1.2.x; both are safe on a live server.
- Discord chat mirroring is off until you set a channel in `Config/Discord/DiscordConfig.json`.
