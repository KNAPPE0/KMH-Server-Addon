# KMH Server Addon - Extensions SDK

A stable public API for writing server-side extensions that plug into
KMH without modifying its source. Extensions get a clean facade over
treasury, marketplace, quests, guilds, linked accounts, player stats,
item labels, lifecycle events, custom wire kinds, and per-extension
persistent storage - and **never** see RWT types directly, so RWT
release upgrades don't break your code.


## Why this exists

If you're maintaining a downstream patch (custom launcher, server
tooling, in-house marketplace UI, Discord integration that wraps KMH
data, etc.), you've probably hit the cycle:
1. New RWT release drops
2. RWT internals shift (renamed types, restructured packets,
   `UserFile` moves to `GetData<UserFile>()`, etc.)
3. Every downstream mod has to chase the change

The SDK absorbs that for you. When KMH ships a new release that
rebases against a new RWT version, the SDK contract stays. Your
extension keeps working - usually with no recompile, definitely
with no source changes.

You also get isolation: a misbehaving extension can't take KMH down,
and extensions can't reach into each other's state.


## At a glance

```csharp
public sealed class MyExtension : IKmhServerExtension
{
    public string Name    => "My Extension";
    public string Version => "1.0.0";

    public void Register(IKmhServerHost host)
    {
        // Subscribe to events
        host.Events.MarketplaceBuy += e =>
            host.Log.Info($"{e.BuyerUsername} bought x{e.QtyBought} {e.ItemDefName}");

        // Read live state
        long bal = host.Treasury.GetSilver("alice");

        // Mutate via stable API
        host.Treasury.DepositSilver("alice", 50, note: "first-login bonus");

        // Register your own wire kinds
        host.RegisterHandler("myname.auction.bid", (client, env) =>
        {
            int amount = env.GetInt("amount");
            host.Log.Info($"{client.Username} bid {amount}");
        });

        // Persist per-extension state
        var storage = host.StorageFor(Name);
        storage.Save("state.json", new { lastSeen = DateTime.UtcNow });
    }

    public void Shutdown() { }
}
```


## Architecture

```
        ┌──────────────────────────────┐
        │   Your extension DLL         │
        │   (referencing only          │
        │    KMH.Sdk.Server.dll)       │
        └──────────────┬───────────────┘
                       │ stable interfaces
                       ▼
        ┌──────────────────────────────┐
        │   KMH.Sdk.Server             │
        │   • IKmhServerExtension      │
        │   • IKmhServerHost           │
        │   • ITreasuryApi, IQuestApi, │
        │     IMarketplaceApi, ...     │
        │   • Records, Events          │
        └──────────────┬───────────────┘
                       │ implementations
                       ▼
        ┌──────────────────────────────┐
        │   KMHServerAddon (internal)  │
        │   • TreasuryStore            │
        │   • MarketplaceStore         │
        │   • Harmony patches          │
        │   • RWT type plumbing        │
        └──────────────┬───────────────┘
                       │ RWT-side API
                       ▼
        ┌──────────────────────────────┐
        │   RimWorld Together server   │
        └──────────────────────────────┘
```

Your extension only ever touches the top two boxes. Everything below
is implementation detail that can change between KMH releases.


## Quick start

### 1. Set up the project

Easiest path - copy the example template:

```
cp -r KMH-Server-Addon/Templates/ServerExtension/ ./my-extension
cd my-extension
```

The example `WelcomeBonus.csproj` references `KMH.Sdk.Server.dll` at
`..\..\Deploy\KMH.Sdk.Server.dll`. Adjust the HintPath to wherever
your local copy of the SDK DLL lives.

```xml
<Reference Include="KMH.Sdk.Server">
  <HintPath>path\to\Deploy\KMH.Sdk.Server.dll</HintPath>
  <Private>False</Private>
</Reference>
```

`Private=False` so your output doesn't redistribute the SDK - the
addon already ships it.

### 2. Implement the entry point

```csharp
using KMH.Sdk.Server;
using KMH.Sdk.Server.Apis;

public sealed class MyExtension : IKmhServerExtension
{
    public string Name    => "My Extension";
    public string Version => "1.0.0";

    public void Register(IKmhServerHost host)
    {
        // wire your handlers / event subscriptions / state load
    }

    public void Shutdown()
    {
        // optional: flush buffers, close async clients, etc.
    }
}
```

### 3. Build + deploy

```
dotnet build -c Debug
cp bin/Debug/net8.0/MyExtension.dll <server-folder>/kmh-extensions/
```

Restart `KMHServerAddon.exe`. You'll see in the log:

```
[KMH-Addon] Extensions: loaded 'My Extension' v1.0.0 (from MyExtension.dll)
```


## SDK reference

### `IKmhServerExtension`

Implement this. The loader requires:
- A parameterless public constructor
- `Name` (display string, used in logs + `/kmh-server status`)
- `Version` (semver string, surfaced in admin commands)
- `Register(IKmhServerHost)` - called once at startup
- `Shutdown()` - called on graceful host exit (best-effort)

### `IKmhServerHost`

The stable surface you get in `Register`. Holds these members:

- **Identity**: `KmhVersion`, `SdkVersion`, `KmhHarmonyId`
- **Facades**: `Treasury`, `Marketplace`, `Quests`, `Guilds`,
  `LinkedAccounts`, `PlayerStats`, `ItemLabels`, `Reputation`, `Sites`
- **Logging**: `Log` - prefixed with your extension name
- **Events**: `Events` - see lifecycle subscriptions below
- **Storage**: `StorageFor(name)` - per-extension scope, returns
  `IExtensionStorage`
- **Protocol**: `RegisterHandler(kind, handler)`, `Send`,
  `SendToUsername`, `Broadcast`
- **Discord**: `RegisterDiscordCommand(command, handler)` - add your
  own `!command` to the KMH Discord bot

> **Reserved namespace.** The `kmh.` wire-kind prefix and the `kmh-`
> Discord-command prefix belong to KMH core. `RegisterHandler` /
> `RegisterDiscordCommand` **refuse** (and log) any registration using
> them, and they also refuse a kind/command already claimed by core or
> another extension. First registration wins; everything outside the
> reserved prefixes is yours to claim. Namespace under your extension
> name so two extensions don't collide.

### Facade APIs (`KMH.Sdk.Server.Apis.*`)

| Interface | Surface |
|-----------|---------|
| `ITreasuryApi` | `GetSilver`, `GetItems`, `GetRecentTransactions`, `DepositSilver`, `WithdrawSilver`, `DepositItem`, `WithdrawItem` |
| `IMarketplaceApi` | `GetOpenListings`, `Post`, `Cancel`, `Buy` |
| `IQuestApi` | `GetVisibleQuests`, `PostDeliverItem`, `PostBounty`, `Claim`, `Submit`, `Approve`, `Cancel` |
| `IGuildApi` | read: `GetAll`, `CurrentGuildOf`, `AreAllied` • mutate (system-level, not rank-gated): `CreateGuild`, `AddMember`, `SetMotd` • guild vault: `GetGuildSilver`, `DepositGuildSilver`, `WithdrawGuildSilver` |
| `ILinkedAccountsApi` | `IsLinked`, `DiscordDisplayFor`, `DiscordIdFor`, `UsernameByDiscordId` |
| `IPlayerStatsApi` | `GetAll`, `EnsurePlayer`, `AddSilverDonated`, `AddSalesEarned`, `BumpQuestsCompleted`, `BumpQuestsPosted` |
| `IItemLabelsApi` | `LabelFor`, `HasLabel`, `Count`, `ResolveDefNameByQuery` |
| `IReputationApi` | read-only: `ScoreOf`, `TierOf`, `GetAll` (quest reputation - moves only on quest activity) |
| `ISitesApi` | read-only: `GetAll`, `GetByTile`, `GetByOwner` (custom production sites) |

All read methods return immutable record DTOs from
`KMH.Sdk.Server.Records.*`. All mutations return `bool` (false on
invalid input - silver/items aren't moved on partial failure) and
auto-push fresh snapshots to affected clients.

### Events (`IKmhEvents`)

Subscribe with `host.Events.Xyz += handler`.

```csharp
event Action<PlayerJoinedEvent>      PlayerJoined;
event Action<PlayerLeftEvent>        PlayerLeft;
event Action<PlayerLinkedEvent>      PlayerLinked;
event Action<PlayerUnlinkedEvent>    PlayerUnlinked;
event Action<MarketplacePostEvent>   MarketplacePost;
event Action<MarketplaceBuyEvent>    MarketplaceBuy;
event Action<MarketplaceCancelEvent> MarketplaceCancel;
event Action<QuestPostedEvent>       QuestPosted;
event Action<QuestClaimedEvent>      QuestClaimed;
event Action<QuestSubmittedEvent>    QuestSubmitted;
event Action<QuestApprovedEvent>     QuestApproved;
event Action<QuestCancelledEvent>    QuestCancelled;
event Action<TreasuryChangedEvent>   TreasuryChanged;
event Action<GuildChangedEvent>      GuildChanged;   // created / membership / perk / settings / motd / treasury
event Action<ReputationChangedEvent> ReputationChanged; // a player's quest-reputation score moved
event Action<SiteChangedEvent>       SiteChanged;    // built / worker_joined / worker_left / removed
```

Each payload is an immutable `sealed class` with `init`-only
properties. Handlers fire synchronously on the action's originating
thread; queue to your own background worker for long-running work.

**Subscriber-throw protection:** if your handler throws, the
exception is logged but dispatch continues to other subscribers. One
bad handler can't break others.

### Custom wire kinds

Register your own kinds for client↔server traffic. Namespace them so
they don't collide with KMH's `kmh.*` kinds or other extensions:

```csharp
host.RegisterHandler("myname.auction.bid", (client, env) =>
{
    int amount = env.GetInt("amount");
    long lot   = env.GetInt("lot_id");
    // ...
});

// Send a response
host.Send(client, "myname.auction.confirmed", new { lot_id = lot, accepted = true });

// Or broadcast to everyone
host.Broadcast("myname.auction.next_round", new { round_number = 5 });
```

The wire envelope is opaque - extensions see `IKmhEnvelope` with
typed accessors (`GetInt`, `GetString`, `GetBool`, `DataAs<T>`) so
your code doesn't depend on Newtonsoft.Json directly.

### Discord commands

Add your own command to the KMH Discord bot. Core commands are tried
first, so you can never shadow a built-in; the `kmh-` prefix is
reserved (see the namespace note above).

```csharp
host.RegisterDiscordCommand("auction-bid", ctx =>
{
    // ctx.LinkedUsername is the in-game account this Discord user linked,
    // or "" if they haven't linked - authorize account-scoped actions on it.
    if (string.IsNullOrEmpty(ctx.LinkedUsername))
    {
        ctx.Reply("Link your account first with `!kmh-link`.");
        return;
    }
    if (ctx.Args.Count < 2)
    {
        ctx.Reply("Usage: `!auction-bid <lot> <amount>`");
        return;
    }
    ctx.Reply($"{ctx.LinkedUsername} bid {ctx.Args[1]} on lot {ctx.Args[0]}.");
});
```

`IKmhDiscordCommand` gives you `Command`, `Args`, `RawArgs`,
`AuthorDisplay`, `AuthorId` (snowflake), `ChannelId`, `LinkedUsername`,
and a fire-and-forget `Reply`. The handler runs on the Discord
gateway thread - keep it quick or hand heavy work to your own thread.
On a server with no Discord configured the registration is inert (it
logs once) and your extension still loads fine.

### Per-extension storage (`IExtensionStorage`)

```csharp
var storage = host.StorageFor(Name);

storage.Save("state.json", new { foo = 42 });

if (storage.TryLoad("state.json", out MyState state)) {
    // ...
}

foreach (string filename in storage.ListFiles()) {
    // ...
}

storage.Delete("old.json");
```

Files land at `kmh-data/extensions/<extension-name>/`. The folder is
auto-created on first save. Filenames are sanitised; extension names
can be any string.


## Version compatibility

The SDK uses semantic versioning **independently** from KMH internals.

| Compatibility | Guarantee |
|---------------|-----------|
| KMH.Sdk.Server **1.x** | All `public` members stable. Adding members is fine. Renaming / removing / changing signatures requires a 2.x bump. |
| KMHServerAddon internals | Can roll independently. Extension built against SDK 1.3 keeps working with addon 1.20 as long as both are still on SDK 1.x. |
| RWT releases | When RWT introduces breaking API drift (e.g. namespace moves, signature changes), KMH absorbs it inside the addon. **Extensions don't see RWT types directly**, so RWT upgrades don't ripple into your code. |

You can guard against future SDK bumps by checking `host.SdkVersion`
in `Register`:

```csharp
public void Register(IKmhServerHost host)
{
    if (!host.SdkVersion.StartsWith("1."))
    {
        host.Log.Warn("This extension requires SDK 1.x; refusing to load");
        throw new NotSupportedException("SDK version mismatch");
    }
    // ...
}
```


## License - extensions are not modifications

KMH's LICENSE restricts modification of KMH source itself. Extensions
that *consume* the SDK are using it as a library, not modifying it -
same way any RimWorld mod isn't a "modification" of RimWorld. You're
welcome to:

- Write and distribute extensions under any license you choose
- Sell extensions
- Keep extensions closed-source
- Bundle multiple extensions together

You may **not**:

- Modify, fork, or redistribute KMH source itself without
  permission from KNAPPE0 / KMH (see LICENSE)
- Bundle KMH binaries inside your extension distribution - point
  users at the upstream KMH release instead

If you're not sure whether your use case is an extension or a
modification, ping KNAPPE0 and ask. Better to confirm than guess.


## Extension folder

The addon scans `<server-folder>/kmh-extensions/*.dll` recursively.
Both layouts work:

```
kmh-extensions/                              kmh-extensions/
├── WelcomeBonus.dll                         ├── WelcomeBonus/
└── AuctionHouse.dll                         │   ├── WelcomeBonus.dll
                                             │   └── README.txt
                                             └── AuctionHouse/
                                                 ├── AuctionHouse.dll
                                                 └── third-party-dep.dll
```

The first time the addon starts it creates an empty
`kmh-extensions/` folder with a `README.txt` so admins see where
extensions go.


## Troubleshooting

| Symptom | Cause / Fix |
|---------|-------------|
| "Could not load 'MyExt.dll': ..." | Missing transitive dependency. Drop the dep DLL next to yours. |
| "type load failure" with `LoaderExceptions` listed | Same - the listed types are missing assemblies. |
| Extension loads but events never fire | Confirm you subscribed in `Register`, not in the ctor (the ctor runs before `host` exists). |
| `host.Treasury.DepositSilver` returns false | Check the addon log - usually invalid input (empty username, non-positive amount). |
| Multiple instances of your extension load | Don't put more than one `IKmhServerExtension`-implementing class in your assembly unless you want them all loaded. |


## Roadmap

Today the SDK covers the server side. The client-side mirror (an SDK
for KMH-Patch extensions that load as RimWorld mods depending on
`KMHPatch.dll`) is on the roadmap and will follow the same
contract-stability rules.

Suggestions, missing surfaces, awkward APIs - open an issue or ping
KNAPPE0.
