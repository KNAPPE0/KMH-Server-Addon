using System;
using System.Collections.Generic;
using KMH.Sdk.Server.Apis;
using KMH.Sdk.Server.Records;

namespace KMHServerAddon.Extensibility
{
    // Facade implementations for each SDK API. Thin pass-through to the internal stores, converting internal DTOs
    // to SDK records.
    //
    // Why facades instead of exposing the stores directly? The stores carry wire/persistence concerns (Newtonsoft
    // attributes, snake_case property names, default values dictated by the on-disk format). A stable SDK contract
    // insulates extensions from changes to those.

    internal sealed class TreasuryApiImpl : ITreasuryApi
    {
        public long GetSilver(string username) =>
            Features.Treasury.TreasuryStore.GetSnapshotFor(username)?.SilverBalance ?? 0;

        public IReadOnlyDictionary<string, int> GetItems(string username)
        {
            Features.Treasury.Dto.TreasurySnapshot snap =
                Features.Treasury.TreasuryStore.GetSnapshotFor(username);
            return snap?.Items != null
                ? new Dictionary<string, int>(snap.Items, StringComparer.OrdinalIgnoreCase)
                : (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();
        }

        public IReadOnlyList<TreasuryTransactionRecord> GetRecentTransactions(string username)
        {
            Features.Treasury.Dto.TreasurySnapshot snap =
                Features.Treasury.TreasuryStore.GetSnapshotFor(username);
            List<TreasuryTransactionRecord> result = new List<TreasuryTransactionRecord>();
            if (snap?.RecentTransactions != null)
            {
                foreach (Features.Treasury.Dto.TreasuryTransaction tx in snap.RecentTransactions)
                {
                    result.Add(new TreasuryTransactionRecord
                    {
                        UtcTicks    = tx.UtcTicks,
                        Username    = tx.Username ?? "",
                        Kind        = tx.Kind     ?? "",
                        Amount      = tx.Amount,
                        ItemDefName = tx.ItemDefName ?? "",
                        Note        = tx.Note ?? "",
                    });
                }
            }
            return result;
        }

        public bool DepositSilver (string u, int amount, string note = "") => Features.Treasury.TreasuryStore.DepositSilver (u, amount, note);
        public bool WithdrawSilver(string u, int amount, string note = "") => Features.Treasury.TreasuryStore.WithdrawSilver(u, amount, note);
        public bool DepositItem   (string u, string def, int qty, string note = "") => Features.Treasury.TreasuryStore.DepositItem  (u, def, qty, note);
        public bool WithdrawItem  (string u, string def, int qty, string note = "") => Features.Treasury.TreasuryStore.WithdrawItem (u, def, qty, note);
    }

    internal sealed class MarketplaceApiImpl : IMarketplaceApi
    {
        public IReadOnlyList<MarketplaceListingRecord> GetOpenListings(string callerUsername)
        {
            Features.Marketplace.Dto.MarketplaceSnapshot snap =
                Features.Marketplace.MarketplaceStore.BuildSnapshot(callerUsername ?? "");
            List<MarketplaceListingRecord> result = new List<MarketplaceListingRecord>();
            if (snap?.Listings != null)
            {
                foreach (Features.Marketplace.Dto.MarketplaceListing l in snap.Listings)
                {
                    result.Add(new MarketplaceListingRecord
                    {
                        Id                = l.Id,
                        SellerUsername    = l.SellerUsername    ?? "",
                        SellerTreasuryKey = l.SellerTreasuryKey ?? "",
                        ItemDefName       = l.ItemDefName       ?? "",
                        RemainingQty      = l.RemainingQty,
                        OriginalQty       = l.OriginalQty,
                        UnitPriceSilver   = l.UnitPriceSilver,
                        ListedUtcTicks    = l.ListedUtcTicks,
                        ExpiresUtcTicks   = l.ExpiresUtcTicks,
                        Visibility        = l.Visibility ?? "public",
                    });
                }
            }
            return result;
        }

        public long Post(string seller, string def, int qty, int price, string visibility = "public", int expiresHours = 0)
            => Features.Marketplace.MarketplaceStore.Post(seller, def, qty, price, visibility, expiresHours);

        public bool Cancel(string caller, long listingId)
            => Features.Marketplace.MarketplaceStore.Cancel(caller, listingId);

        public bool Buy(string buyer, long listingId, int qty, out string sellerUsername)
            => Features.Marketplace.MarketplaceStore.Buy(buyer, listingId, qty, out sellerUsername);
    }

    internal sealed class QuestApiImpl : IQuestApi
    {
        public IReadOnlyList<QuestRecord> GetVisibleQuests(string callerUsername)
        {
            Features.Quests.Dto.QuestSnapshot snap =
                Features.Quests.QuestStore.BuildSnapshot(callerUsername ?? "");
            List<QuestRecord> result = new List<QuestRecord>();
            if (snap?.Quests != null)
            {
                foreach (Features.Quests.Dto.QuestEntry q in snap.Quests)
                {
                    result.Add(new QuestRecord
                    {
                        Id                = q.Id,
                        Kind              = q.Kind              ?? "",
                        State             = q.State             ?? "",
                        Visibility        = q.Visibility        ?? "public",
                        PosterUsername    = q.PosterUsername    ?? "",
                        PosterTreasuryKey = q.PosterTreasuryKey ?? "",
                        ClaimedByUsername = q.ClaimedByUsername ?? "",
                        Title             = q.Title             ?? "",
                        Description       = q.Description       ?? "",
                        BountySilver      = q.BountySilver,
                        TargetItemDefName = q.TargetItemDefName ?? "",
                        TargetItemQty     = q.TargetItemQty,
                        PostedUtcTicks    = q.PostedUtcTicks,
                        ExpiresUtcTicks   = q.ExpiresUtcTicks,
                    });
                }
            }
            return result;
        }

        public long PostDeliverItem(string poster, string title, string desc, int bounty,
                                    string targetDef, int targetQty, string visibility = "public", int expiresHours = 0)
            => Features.Quests.QuestStore.Post(poster, Features.Quests.Dto.QuestEntry.KindDeliverItem, visibility,
                title, desc, bounty, targetDef, targetQty, expiresHours);

        public long PostBounty(string poster, string title, string desc, int bounty,
                               string visibility = "public", int expiresHours = 0)
            => Features.Quests.QuestStore.Post(poster, Features.Quests.Dto.QuestEntry.KindBounty, visibility,
                title, desc, bounty, targetItemDefName: "", targetItemQty: 0, expiresInHours: expiresHours);

        public bool Claim   (string user, long id) => Features.Quests.QuestStore.Claim   (user, id);
        public bool Submit  (string user, long id) => Features.Quests.QuestStore.Submit  (user, id, out _);
        public bool Approve (string user, long id) => Features.Quests.QuestStore.Approve (user, id, out _);
        public bool Cancel  (string user, long id) => Features.Quests.QuestStore.Cancel  (user, id);
    }

    internal sealed class GuildApiImpl : IGuildApi
    {
        public IReadOnlyList<GuildSummaryRecord> GetAll()
        {
            List<Features.Guilds.GuildStore.GuildSummary> rows = Features.Guilds.GuildStore.ComputeLeaderboard();
            List<GuildSummaryRecord> result = new List<GuildSummaryRecord>(rows.Count);
            foreach (Features.Guilds.GuildStore.GuildSummary g in rows)
            {
                result.Add(new GuildSummaryRecord
                {
                    Name           = g.Name,
                    MemberCount    = g.MemberCount,
                    TreasurySilver = g.TreasurySilver,
                });
            }
            return result;
        }

        public string CurrentGuildOf(string username) => Features.Guilds.GuildStore.CurrentGuildOf(username) ?? "";
        public bool   AreAllied(string a, string b)   => Features.Guilds.GuildStore.AreAllied(a, b);

        // -- system-level mutations --
        public bool CreateGuild(string name)                              => Features.Guilds.GuildStore.CreateGuild(name);
        public bool AddMember(string username, string guildName, string rank = "member")
                                                                          => Features.Guilds.GuildStore.AddMember(username, guildName, rank);
        public bool SetMotd(string guildName, string motd)                => Features.Guilds.GuildStore.SetMotdByName(guildName, motd);

        // -- guild treasury --
        public long GetGuildSilver(string guildName)                      => Features.Treasury.TreasuryStore.GetGuildSilver(guildName);
        public bool DepositGuildSilver(string guildName, int amount, string contributor, string note = "")
                                                                          => Features.Treasury.TreasuryStore.DepositGuildSilver(guildName, amount, contributor, note);
        public bool WithdrawGuildSilver(string guildName, int amount, string actor, string note = "")
                                                                          => Features.Treasury.TreasuryStore.WithdrawGuildSilver(guildName, amount, actor, note);
    }

    internal sealed class LinkedAccountsApiImpl : ILinkedAccountsApi
    {
        public bool   IsLinked(string username) => Features.LinkedAccounts.LinkedAccountsStore.IsLinked(username);
        public string DiscordDisplayFor(string username)
            => Features.LinkedAccounts.LinkedAccountsStore.TryGetLink(username, out string display) ? display : null;
        public ulong  DiscordIdFor(string username) => Features.LinkedAccounts.LinkedAccountsStore.DiscordIdFor(username);
        public string UsernameByDiscordId(ulong discordId)
            => Features.LinkedAccounts.LinkedAccountsStore.FindUsernameByDiscordId(discordId);
    }

    internal sealed class PlayerStatsApiImpl : IPlayerStatsApi
    {
        public IReadOnlyList<PlayerStatRecord> GetAll()
        {
            Features.PlayerStats.Dto.PlayerStatsSnapshot snap = Features.PlayerStats.PlayerStatsStore.BuildSnapshot();
            List<PlayerStatRecord> result = new List<PlayerStatRecord>();
            if (snap?.Entries != null)
            {
                foreach (Features.PlayerStats.Dto.PlayerLeaderboardEntry e in snap.Entries)
                {
                    result.Add(new PlayerStatRecord
                    {
                        Username          = e.Username ?? "",
                        FirstSeenUtcTicks = e.FirstSeenUtcTicks,
                        SilverDonated     = e.SilverDonated,
                        SalesEarned       = e.SalesEarned,
                        MarketplaceSales  = e.MarketplaceSales,
                        QuestsCompleted   = e.QuestsCompleted,
                        QuestsPosted      = e.QuestsPosted,
                        SitesBuilt        = e.SitesBuilt,
                        WorkerXp          = e.WorkerXp,
                        EconomyScore      = e.EconomyScore,
                    });
                }
            }
            return result;
        }

        public void EnsurePlayer       (string u)               => Features.PlayerStats.PlayerStatsStore.EnsurePlayer(u);
        public void AddSilverDonated   (string u, long delta)   => Features.PlayerStats.PlayerStatsStore.AddSilverDonated(u, delta);
        public void AddSalesEarned     (string u, long delta)   => Features.PlayerStats.PlayerStatsStore.AddSalesEarned(u, delta);
        public void BumpQuestsCompleted(string u)               => Features.PlayerStats.PlayerStatsStore.BumpQuestsCompleted(u);
        public void BumpQuestsPosted   (string u)               => Features.PlayerStats.PlayerStatsStore.BumpQuestsPosted(u);
    }

    internal sealed class ReputationApiImpl : IReputationApi
    {
        public int    ScoreOf(string username) => Features.Reputation.ReputationStore.Get(username).score;
        public string TierOf(string username)  => Features.Reputation.ReputationStore.Get(username).tier;

        public IReadOnlyList<ReputationRecord> GetAll()
        {
            List<ReputationRecord> result = new List<ReputationRecord>();
            foreach (Features.Reputation.Dto.ReputationEntryDto e in Features.Reputation.ReputationStore.BuildSnapshot().Entries)
                result.Add(new ReputationRecord { Username = e.Username, Score = e.Score, Tier = e.Tier });
            return result;
        }
    }

    internal sealed class SitesApiImpl : ISitesApi
    {
        private static SiteRecord ToRecord(Features.Sites.Dto.SiteEntry s) => new SiteRecord
        {
            Tile = s.Tile, OwnerUsername = s.OwnerUsername, OwnerGuild = s.OwnerGuild,
            ItemDefName = s.ItemDefName, BaseAmountPerCycle = s.BaseAmountPerCycle, AccessMode = s.AccessMode,
            Workers = new List<string>(s.Workers), MaxWorkers = s.MaxWorkers,
            ProductionMultiplier = s.ProductionMultiplier, EffectiveCycleMinutes = s.EffectiveCycleMinutes,
            TotalSilverGenerated = s.TotalSilverGenerated,
        };

        public IReadOnlyList<SiteRecord> GetAll()
        {
            List<SiteRecord> result = new List<SiteRecord>();
            foreach (Features.Sites.Dto.SiteEntry s in Features.Sites.SiteStore.AllForApi()) result.Add(ToRecord(s));
            return result;
        }

        public SiteRecord GetByTile(int tile)
        {
            Features.Sites.Dto.SiteEntry s = Features.Sites.SiteStore.GetForApi(tile);
            return s == null ? null : ToRecord(s);
        }

        public IReadOnlyList<SiteRecord> GetByOwner(string username)
        {
            List<SiteRecord> result = new List<SiteRecord>();
            foreach (Features.Sites.Dto.SiteEntry s in Features.Sites.SiteStore.AllForApi())
                if (string.Equals(s.OwnerUsername, username, StringComparison.OrdinalIgnoreCase))
                    result.Add(ToRecord(s));
            return result;
        }
    }

    internal sealed class ItemLabelsApiImpl : IItemLabelsApi
    {
        public string LabelFor(string defName) => Features.ItemLabels.ItemLabelCache.LabelFor(defName);
        public bool   HasLabel(string defName) => !string.IsNullOrEmpty(defName) && Features.ItemLabels.ItemLabelCache.LabelFor(defName) != defName;
        public int    Count                    => Features.ItemLabels.ItemLabelCache.Count;
        public string ResolveDefNameByQuery(string query, out List<string> candidates)
            => Features.ItemLabels.ItemLabelCache.ResolveDefNameByQuery(query, out candidates);
    }

    internal sealed class AuctionApiImpl : IAuctionApi
    {
        private static AuctionRecord ToRecord(Features.Auctions.Dto.AuctionDto a) => new AuctionRecord
        {
            Id = a.Id, SellerUsername = a.SellerUsername, SellerTreasuryKey = a.SellerTreasuryKey,
            ItemDefName = a.ItemDefName, StuffDefName = a.StuffDefName, QualityIndex = a.QualityIndex, Qty = a.Qty,
            StartingBid = a.StartingBid, MinIncrement = a.MinIncrement, BuyoutSilver = a.BuyoutSilver,
            CurrentBid = a.CurrentBid, HighBidder = a.HighBidder, BidCount = a.BidCount,
            ListedUtcTicks = a.ListedUtcTicks, EndsUtcTicks = a.EndsUtcTicks, Visibility = a.Visibility,
        };

        public IReadOnlyList<AuctionRecord> GetOpenAuctions(string callerUsername)
        {
            List<AuctionRecord> result = new List<AuctionRecord>();
            foreach (Features.Auctions.Dto.AuctionDto a in Features.Auctions.AuctionStore.BuildSnapshot(callerUsername).Auctions)
                result.Add(ToRecord(a));
            return result;
        }

        public long Post(string sellerUsername, string itemDefName, string stuffDefName, int quality, int qty,
                         long startingBid, long minIncrement, long buyoutSilver, int durationHours, string visibility = "public")
            => Features.Auctions.AuctionStore.Post(sellerUsername, itemDefName, stuffDefName, quality, qty,
                                                   startingBid, minIncrement, buyoutSilver, durationHours, visibility).id;

        public bool Bid(string bidderUsername, long auctionId, long amount, out string reason)
        {
            Features.Auctions.AuctionStore.BidResult r = Features.Auctions.AuctionStore.Bid(bidderUsername, auctionId, amount);
            reason = r.Reason;
            return r.Ok;
        }

        public bool Cancel(string sellerUsername, long auctionId, out string reason)
        {
            (bool ok, string why) = Features.Auctions.AuctionStore.Cancel(sellerUsername, auctionId);
            reason = why;
            return ok;
        }
    }

    internal sealed class WorldApiImpl : IWorldApi
    {
        private const string Actor = "extension";

        private static WorldEventRecord ToRecord(Features.World.Dto.WorldEventDto e) => new WorldEventRecord
        {
            Id = e.Id, Type = e.Type, Title = e.Title, Description = e.Description, Magnitude = e.Magnitude,
            Target = e.Target, StartedUtcTicks = e.StartedUtcTicks, EndsUtcTicks = e.EndsUtcTicks,
        };

        private static ServerQuestRecord ToRecord(Features.World.Dto.ServerQuestDto q) => new ServerQuestRecord
        {
            Id = q.Id, Kind = q.Kind, Objective = q.Objective, Title = q.Title, Description = q.Description,
            TargetDefName = q.TargetDefName, GoalQty = q.GoalQty, ProgressQty = q.ProgressQty,
            RewardPool = q.RewardPool, State = q.State, Winner = q.Winner, EndsUtcTicks = q.EndsUtcTicks,
            Contributors = new Dictionary<string, int>(q.Contributors, StringComparer.OrdinalIgnoreCase),
        };

        public IReadOnlyList<WorldEventRecord> GetActiveEvents()
        {
            List<WorldEventRecord> result = new List<WorldEventRecord>();
            foreach (Features.World.Dto.WorldEventDto e in Features.World.WorldStore.ActiveEvents()) result.Add(ToRecord(e));
            return result;
        }

        public IReadOnlyList<ServerQuestRecord> GetServerQuests()
        {
            List<ServerQuestRecord> result = new List<ServerQuestRecord>();
            foreach (Features.World.Dto.ServerQuestDto q in Features.World.WorldStore.BuildSnapshot().ServerQuests) result.Add(ToRecord(q));
            return result;
        }

        public bool FireEvent(string type, double magnitude, string target, int durationMinutes, out string reason)
        {
            (bool ok, string why) = Features.World.WorldEngine.FireEvent(type, magnitude, target, durationMinutes, Actor);
            reason = why;
            return ok;
        }

        public bool EndEvent(string type, out string reason)
        {
            (bool ok, string why) = Features.World.WorldEngine.EndEvent(type);
            reason = why;
            return ok;
        }

        public bool CreateQuest(string kind, string objective, string targetDefName, int goalQty, long reward,
                                int durationMinutes, string title, string description, out string reason)
        {
            (bool ok, string why) = Features.World.WorldEngine.CreateWorldQuest(kind, objective, targetDefName, goalQty,
                                                                               reward, durationMinutes, title, description, Actor);
            reason = why;
            return ok;
        }

        public bool EndQuest(long questId, out string reason)
        {
            (bool ok, string why) = Features.World.WorldEngine.EndWorldQuest(questId, Actor);
            reason = why;
            return ok;
        }

        public bool   IsTaxHoliday()                            => Features.World.WorldStore.IsTaxHoliday();
        public double MarketPayoutMultiplierFor(string itemDef) => Features.World.WorldStore.MarketPayoutMultiplierFor(itemDef);
    }
}
