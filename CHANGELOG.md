# Changelog

All notable changes to the KMH Server Addon. Versions follow the KMH build number shared with the KMH Patch client.

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

- **One player dropping mid-download no longer floods the server log.** If a client's connection was reset while the
  server was sending it the enforced config profile, the failed write left that connection registered as healthy — so
  every remaining chunk was written to the same dead socket, each failure logged twice, and the push ran to the last
  chunk regardless. A single disconnect could fill a log file, and with the chat fallback enabled the leftover chunks
  were queued as chat. A write that fails now closes that connection straight away, and a profile push that loses its
  recipient stops where it is; the client re-requests it on its next connection.

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
