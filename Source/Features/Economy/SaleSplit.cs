using System;

namespace KMHServerAddon.Features.Economy
{
    // The one authoritative sale breakdown shared by the marketplace, auctions and the want board.
    // Invariant: BuyerCharge + PoolBoost == SellerPayout + ServerTax + GuildTax + PoolReturn - a sale distributes
    // exactly what the buyer paid. World-event payout boosts are funded from the house pool (never minted) and
    // crashes return the seller's shortfall to the pool (never vanish).
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

        // Call only at commit points (buyer silver already secured) - a boom debits the house pool here.
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
                // In-demand items get a tax rebate, gluts a surcharge - buyer cost unchanged, only the split moves.
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
                    if (boost > 0 && Marketplace.MarketplaceStore.TryDebitHousePool(boost))
                    { s.PoolBoost = boost; sellerShare += boost; }
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

        // Move the tax legs (house pool + guild vault). The caller pays the seller and logs the sale.
        public void Settle(string seller, string note)
        {
            if (!Balances)
                Diagnostics.ServerLog.Warn($"SaleSplit imbalance ({note}): charge={BuyerCharge} payout={SellerPayout} " +
                                           $"tax={ServerTax} guild={GuildTax} boost={PoolBoost} return={PoolReturn}");
            long toPool = ServerTax + PoolReturn;
            if (toPool > 0) Marketplace.MarketplaceStore.CreditHousePool(toPool, note);
            if (GuildTax > 0 && !string.IsNullOrEmpty(GuildName))
                Treasury.TreasuryStore.DepositGuildSilver(GuildName, (int)Math.Min(int.MaxValue, GuildTax), seller,
                    note: note + " - guild sale tax");
        }
    }
}
