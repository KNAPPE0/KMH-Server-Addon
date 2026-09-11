using System;

namespace KMHServerAddon.Features.Economy
{
    // BuyerCharge + PoolBoost == SellerPayout + ServerTax + GuildTax + PoolReturn, so a sale never mints silver.
    public sealed class SaleSplit
    {
        public long   BuyerCharge;       // what the buyer actually paid
        public long   ServerTax;         // house tax -> house pool
        public long   GuildTax;          // seller-guild sale tax -> guild vault (0 when guildless/0%)
        public long   SellerPayout;      // what the seller receives
        public long   PoolBoost;         // house-pool silver added to the payout (market boom), bounded by the pool
        public long   PoolReturn;        // seller-share silver returned to the pool (market crash)
        public string GuildName = "";

        public bool Balances =>
            BuyerCharge + PoolBoost == SellerPayout + ServerTax + GuildTax + PoolReturn
            && ServerTax >= 0 && GuildTax >= 0 && SellerPayout >= 0 && PoolBoost >= 0 && PoolReturn >= 0;

        // Plans the split without moving anything; the boom boost is only taken by CommitBoost, which a caller can unwind.
        public static SaleSplit Compute(string seller, string itemDefName, long buyerCharge,
                                        bool demandDrift, bool worldPayoutEvents)
        {
            SaleSplit s = new SaleSplit { BuyerCharge = Math.Max(0, buyerCharge) };
            if (s.BuyerCharge <= 0) return s;

            EconomyConfig cfg = EconomyConfig.Current;
            int pct = Math.Max(0, cfg.MarketplaceTaxPercent
                                  - Guilds.GuildStore.GetMarketplaceTaxReductionPoints(Guilds.GuildStore.CurrentGuildOf(seller)));
            if (World.WorldStore.IsTaxHoliday()) pct = 0;
            else if (demandDrift && cfg.DynamicDemandPricingEnabled)
            {
                // Only the seller/house split moves; the buyer's cost is already fixed.
                long demand = WantBoard.WantStore.OpenDemandQty(itemDefName);
                long supply = Marketplace.MarketplaceStore.OpenSupplyQty(itemDefName);
                long denom  = demand + supply;
                if (denom > 0)
                {
                    double f = (demand - supply) / (double)denom; // +1 = pure demand .. -1 = pure glut
                    pct = (int)Math.Min(90, Math.Max(0, pct - Math.Round(f * cfg.DemandTaxSwingPercent)));
                }
            }
            s.ServerTax = Math.Min(s.BuyerCharge, Math.Max(0, (long)Math.Round(s.BuyerCharge * (pct / 100.0))));
            long sellerShare = s.BuyerCharge - s.ServerTax;

            if (worldPayoutEvents && sellerShare > 0)
            {
                double mult = World.WorldStore.MarketPayoutMultiplierFor(itemDefName);
                if (mult > 1.0)
                {
                    long boost = (long)Math.Round(sellerShare * mult) - sellerShare;
                    boost = Math.Min(boost, Marketplace.MarketplaceStore.HousePoolBalance());
                    if (boost > 0) { s.PoolBoost = boost; sellerShare += boost; }
                }
                else if (mult < 1.0)
                {
                    long keep    = Math.Max(0, (long)Math.Round(sellerShare * mult));
                    s.PoolReturn = sellerShare - keep;
                    sellerShare  = keep;
                }
            }

            int gpct = Guilds.GuildStore.GetGuildSaleTaxPercent(seller, out string guildName);
            if (gpct > 0 && sellerShare > 0)
            {
                s.GuildTax  = (long)Math.Floor(sellerShare * (gpct / 100.0));
                s.GuildName = guildName ?? "";
            }
            s.SellerPayout = sellerShare - s.GuildTax;
            return s;
        }

        // A pool that cannot fund the boost rebalances the split down rather than paying a seller silver nobody has.
        public bool CommitBoost(string note)
        {
            if (PoolBoost <= 0) return true;
            if (Marketplace.MarketplaceStore.TryDebitHousePool(PoolBoost, note)) return true;
            SellerPayout = Math.Max(0, SellerPayout - PoolBoost);
            PoolBoost = 0;
            return false;
        }

        // Both legs are silver already taken out of the buyer's charge, so one that did not land must stay owed somewhere.
        public struct Unsettled
        {
            public long ToHousePool;
            public long ToGuild;
            public string GuildName;
            public bool Any => ToHousePool > 0 || ToGuild > 0;
        }

        // Moves only the tax legs - paying the seller stays with the caller; settlementKey makes both replay-safe.
        public Unsettled Settle(string seller, string note, string settlementKey)
        {
            if (!Balances)
                Diagnostics.ServerLog.Warn($"SaleSplit imbalance ({note}): charge={BuyerCharge} payout={SellerPayout} " +
                                           $"tax={ServerTax} guild={GuildTax} boost={PoolBoost} return={PoolReturn}");
            Unsettled left = new Unsettled { GuildName = GuildName };
            long toPool = ServerTax + PoolReturn;
            string house = string.IsNullOrEmpty(settlementKey) ? null : settlementKey + ":house";
            string guild = string.IsNullOrEmpty(settlementKey) ? null : settlementKey + ":guild";
            if (toPool > 0 && !Marketplace.MarketplaceStore.TryCreditHousePoolOnce(house, toPool, note))
                left.ToHousePool = toPool;
            if (GuildTax > 0 && !string.IsNullOrEmpty(GuildName)
                && !Treasury.TreasuryStore.DepositGuildSilverOnce(GuildName, guild,
                                                                  (int)Math.Min(int.MaxValue, GuildTax), seller,
                                                                  note: note + " - guild sale tax"))
                left.ToGuild = GuildTax;
            HoldUnsettled(left, seller, note);
            return left;
        }

        // Parked rather than dropped: the tax was already deducted from the buyer, so vanishing shrinks the economy on every failed write.
        private static void HoldUnsettled(Unsettled left, string seller, string note)
        {
            if (left.ToHousePool > 0)
                Recovery.RecoveryStore.HoldSilver(seller, left.ToHousePool, note, "house tax could not be credited");
            if (left.ToGuild > 0)
                Recovery.RecoveryStore.HoldSilver(string.IsNullOrEmpty(left.GuildName) ? seller : left.GuildName,
                    left.ToGuild, note, "guild sale tax could not be credited");
        }
    }
}
