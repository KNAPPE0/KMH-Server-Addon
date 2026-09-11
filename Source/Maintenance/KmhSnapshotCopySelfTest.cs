using System.Collections.Generic;

namespace KMHServerAddon.Maintenance
{
    // One suite for every store, because a hand-listed copy drops whatever was added since it was written.
    internal static class KmhSnapshotCopySelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            r.AddRange(WantChecks());
            r.AddRange(AuctionChecks());
            r.AddRange(GuildChecks());
            r.AddRange(WorldQuestChecks());
            r.AddRange(MarketplaceChecks());
            r.AddRange(TreasuryChecks());
            r.AddRange(PersistedComparerChecks());
            r.AddRange(SerializerMutationCheck());
            return r;
        }

        // JSON deserialization builds a fresh dictionary, so a case-insensitive key comes back case-sensitive.
        private static List<(string, bool, string)> PersistedComparerChecks()
        {
            var r = new List<(string, bool, string)>();
            var q = new Features.World.Dto.ServerQuestDto { Id = 1 };
            q.Contributors["Ada"] = 5;
            q.ContributorFirstUtc["Ada"] = 7;

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(q);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Features.World.Dto.ServerQuestDto>(json);

            r.Add(("Persistence: a reloaded quest still matches contributors case-insensitively",
                   back != null && back.Contributors.ContainsKey("ADA") && back.ContributorFirstUtc.ContainsKey("ada"),
                   "a comparer lost on load makes every name-keyed lookup miss after a restart"));
            return r;
        }

        // A snapshot on its way to a client must be a copy, since the live object is still being mutated.
        private static List<(string, bool, string)> SerializerMutationCheck()
        {
            var r = new List<(string, bool, string)>();
            var q = new Features.World.Dto.ServerQuestDto { Id = 1 };
            for (int i = 0; i < 64; i++) q.Contributors["u" + i] = i;

            bool threw = false;
            try
            {
                var writer = new Newtonsoft.Json.JsonSerializer();
                using (var sw = new System.IO.StringWriter())
                using (var jw = new Newtonsoft.Json.JsonTextWriter(sw))
                {
                    // Mutate mid-write: the serializer is part-way through the dictionary when the key is added.
                    jw.WriteStartObject();
                    jw.WritePropertyName("contributors");
                    var e = q.Contributors.GetEnumerator();
                    e.MoveNext();
                    q.Contributors["late"] = 99;
                    e.MoveNext();      // enumerating after a structural change is what the serializer does
                    writer.Serialize(jw, q.Contributors);
                    jw.WriteEndObject();
                }
            }
            catch (System.InvalidOperationException) { threw = true; }

            r.Add(("Persistence: enumerating a collection after it changes still throws",
                   threw,
                   "the retry in JsonFileStore.Save exists for exactly this; if it stops throwing, that retry is dead code"));
            return r;
        }

        private static List<(string, bool, string)> MarketplaceChecks()
        {
            var l = new Features.Marketplace.Dto.MarketplaceListing
            {
                Id = 9, SellerUsername = "Ada", SellerTreasuryKey = "ada", ItemDefName = "Steel",
                RemainingQty = 5, OriginalQty = 10, UnitPriceSilver = 3, UnitPriceMilli = 3500,
                ListedUtcTicks = 111, ExpiresUtcTicks = 222, IsAutoListing = true,
                QualityIndex = 4, StuffDefName = "Plasteel", Visibility = "guild_only",
                StateFingerprint = "fp", StateNote = "note",
                EscrowPayloads = new List<Items.KmhThingPayload> { new Items.KmhThingPayload() },
            };
            var copy = Features.Marketplace.MarketplaceStore.CopyForTest(l);
            var r = KmhCopyCoverage.Check("Marketplace", l, copy, "EscrowPayloads");
            r.Add(("Marketplace: escrow payloads never reach the wire",
                   copy.EscrowPayloads == null, "stripped deliberately, not dropped by omission"));
            return r;
        }

            // Treasury's copy is deliberately narrower, so server-side bookkeeping is named rather than leaked.
        private static List<(string, bool, string)> TreasuryChecks()
        {
            var r = new List<(string, bool, string)>();
            var serverOnly = new[] { "RecentCommittedTxns", "CommittedDeposits", "LastSaveGeneration", "PendingTakes" };
            var missing = new List<string>();
            foreach (string name in serverOnly)
                if (typeof(Features.Treasury.Dto.TreasurySnapshot).GetProperty(name) == null) missing.Add(name);
            r.Add(("Treasury: the server-only fields still exist to be withheld",
                   missing.Count == 0,
                   missing.Count == 0 ? "" : $"renamed or gone: {string.Join(", ", missing)}"));

            var snap = Features.Treasury.TreasuryStore.GetSnapshotFor("kmh-copy-coverage-probe");
            r.Add(("Treasury: a snapshot withholds server-only bookkeeping",
                   snap != null && (snap.RecentCommittedTxns == null || snap.RecentCommittedTxns.Count == 0)
                   && (snap.CommittedDeposits == null || snap.CommittedDeposits.Count == 0)
                   && (snap.PendingTakes == null || snap.PendingTakes.Count == 0)
                   && snap.LastSaveGeneration == 0,
                   "reading a treasury must also not create one"));
            return r;
        }

        private static List<(string, bool, string)> WorldQuestChecks()
        {
            var q = new Features.World.Dto.ServerQuestDto
            {
                Id = 5, Kind = "competitive", Objective = "hunt", Title = "T", Description = "D",
                TargetDefName = "Muffalo", GoalQty = 40, ProgressQty = 12, RewardPool = 500,
                ReservedFromPool = 500, State = "active", Winner = "Ada", EndsUtcTicks = 9,
                OperationType = "reclaim", OperationSource = "world_director", TargetSiteTile = 42,
                Consequence = "outpost_claimable", WindowEndsUtcTicks = 77,
            };
            q.Contributors["Ada"] = 12;
            q.ContributorFirstUtc["Ada"] = 3;

            var copy = Features.World.WorldStore.CopyQuest(q);
            var r = KmhCopyCoverage.Check("WorldQuests", q, copy);
            // Every delivery mutates these, so a shared reference would let one land in an unrelated snapshot.
            r.Add(("WorldQuests: a snapshot quest owns its contributor dictionaries",
                   !ReferenceEquals(q.Contributors, copy.Contributors)
                   && !ReferenceEquals(q.ContributorFirstUtc, copy.ContributorFirstUtc)
                   && copy.Contributors["Ada"] == 12 && copy.ContributorFirstUtc["Ada"] == 3,
                   "serialization runs after the store lock is released"));
            return r;
        }

        private static List<(string, bool, string)> WantChecks()
        {
            var w = new Features.WantBoard.Dto.WantDto
            {
                Id = 7, BuyerUsername = "Ada", BuyerTreasuryKey = "ada", ItemDefName = "Steel",
                QtyWanted = 50, QtyFilled = 10, UnitPriceSilver = 3, EscrowRemaining = 120,
                ListedUtcTicks = 111, EndsUtcTicks = 222, Visibility = "guild_only",
                MinQuality = 4, RequiredStuff = "Plasteel",
                AllowComplex = true, AllowTainted = true, AllowDamaged = true,
            };
            var r = KmhCopyCoverage.Check("Wants", w, Features.WantBoard.WantStore.CopyForTest(w));
            // These five were the fields actually lost, which made a gear want match anything.
            var c = Features.WantBoard.WantStore.CopyForTest(w);
            r.Add(("Wants: a copied want keeps the buyer's match constraints",
                   c.MinQuality == 4 && c.RequiredStuff == "Plasteel"
                   && c.AllowComplex && c.AllowTainted && c.AllowDamaged,
                   "the client gates its whole gear-offer path on allow_complex"));

            // Taint, damage and gear state exist only on a payload, so they must force the payload path.
            r.Add(("Wants: a plain want needs no payload fulfil",
                   !Features.WantBoard.WantStore.NeedsPayloadFulfil(false, false, false), ""));
            r.Add(("Wants: opting into tainted or damaged forces the payload path",
                   Features.WantBoard.WantStore.NeedsPayloadFulfil(false, true, false)
                   && Features.WantBoard.WantStore.NeedsPayloadFulfil(false, false, true), ""));
            r.Add(("Wants: an explicit gear want forces the payload path",
                   Features.WantBoard.WantStore.NeedsPayloadFulfil(true, false, false), ""));

            r.Add(("Wants: the compact matcher honours material and quality",
                   Util.ItemKey.Matches("MeleeWeapon_LongSword|Steel|4", "MeleeWeapon_LongSword", "Steel", 3)
                   && !Util.ItemKey.Matches("MeleeWeapon_LongSword|Steel|4", "MeleeWeapon_LongSword", "Plasteel", 0)
                   && !Util.ItemKey.Matches("MeleeWeapon_LongSword|Steel|4", "MeleeWeapon_LongSword", "", 5)
                   && Util.ItemKey.Matches("MeleeWeapon_LongSword|Steel|4", "MeleeWeapon_LongSword", "", 0)
                   && Util.ItemKey.Matches("Steel", "Steel", "", 0), ""));
            return r;
        }

        private static List<(string, bool, string)> AuctionChecks()
        {
            var a = new Features.Auctions.Dto.AuctionDto
            {
                Id = 3, SellerUsername = "Bo", SellerTreasuryKey = "bo", ItemDefName = "Rifle",
                StuffDefName = "Steel", QualityIndex = 5, Qty = 2, StartingBid = 100, MinIncrement = 5,
                BuyoutSilver = 900, CurrentBid = 150, HighBidder = "Ada", BidCount = 3,
                ListedUtcTicks = 111, EndsUtcTicks = 222, Visibility = "public",
                StateFingerprint = "fp", StateNote = "note",
                EscrowPayloads = new List<Items.KmhThingPayload> { new Items.KmhThingPayload() },
            };
            var copy = Features.Auctions.AuctionStore.CopyForTest(a);
            // Escrow is server-side item state and is stripped on purpose, so it is excluded rather than expected.
            var r = KmhCopyCoverage.Check("Auctions", a, copy, "EscrowPayloads");
            r.Add(("Auctions: escrow payloads never reach the wire",
                   copy.EscrowPayloads == null, "stripped deliberately, not dropped by omission"));
            return r;
        }

        private static List<(string, bool, string)> GuildChecks()
        {
            var g = new Features.Guilds.Dto.GuildSnapshot
            {
                Name = "Wardens", Motd = "hold the line", OpenJoin = true,
                Hall = new Features.Guilds.Dto.GuildHallDto
                { HasHall = true, Tile = 4242, Leader = "Ada", RadiusTiles = 10, CreatedUtcTicks = 9 },
            };
            g.Members.Add(new Features.Guilds.Dto.GuildMemberDto { Username = "Ada", Rank = "owner", SilverContributed = 50 });
            g.PendingInvites.Add("Bo");
            g.Relationships["Other"] = "allied";
            g.Perks.WorkerXpBonusLevel = 3;
            g.Settings.MarketplaceSaleTaxPercent = 25;

            var copy = Features.Guilds.GuildStore.CopyForTest(g);
            // Silver and the treasury flag are read live from the treasury, never carried over from the guild record.
            var r = KmhCopyCoverage.Check("Guilds", g, copy,
                "GuildSilver", "PendingDonationsSilver", "GuildTreasuryEnabled");
            r.Add(("Guilds: a copied guild keeps its hall",
                   copy.Hall != null && copy.Hall.HasHall && copy.Hall.Tile == 4242 && copy.Hall.RadiusTiles == 10,
                   "no hall on the wire reads as 'no hall -> unrestricted' on the client"));
            r.Add(("Guilds: a copied guild keeps its perks and settings",
                   copy.Perks.WorkerXpBonusLevel == 3 && copy.Settings.MarketplaceSaleTaxPercent == 25, ""));
            return r;
        }
    }
}
