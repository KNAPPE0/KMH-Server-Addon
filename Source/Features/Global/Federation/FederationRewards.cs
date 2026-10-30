using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global.Guilds;

namespace KMHServerAddon.Features.Global.Federation
{
    // Rewards are minted locally into the guild's vault; nothing comes from or leaves for another server.
    internal static class FederationRewards
    {
        internal struct Bundle
        {
            public string DefName;
            public int    Count;
            // Null for base RimWorld.
            public string Expansion;
            public Bundle(string def, int count, string expansion = null) { DefName = def; Count = count; Expansion = expansion; }
        }

        internal static readonly Bundle[] Vanilla =
        {
            new Bundle("MedicineIndustrial", 5),
            new Bundle("ComponentIndustrial", 4),
            new Bundle("MealSurvivalPack", 12),
            new Bundle("Steel", 80),
            new Bundle("Plasteel", 12),
            new Bundle("Gold", 10),
        };

        internal static readonly Bundle[] Expansions =
        {
            new Bundle("HemogenPack", 6, "Biotech"),
            new Bundle("Bioferrite", 25, "Anomaly"),
        };

        // Self-test seams; nothing else assigns them.
        internal static Func<string, bool> ExpansionRequiredForTest;
        internal static Func<string, bool> DefKnownForTest;

        // Only a required expansion is on every player; a vault item a member's game cannot load is worse than none.
        internal static bool ExpansionRequired(string expansion)
        {
            if (ExpansionRequiredForTest != null) return ExpansionRequiredForTest(expansion);
            if (string.IsNullOrEmpty(expansion)) return true;
            try
            {
                foreach (Mods.ModEntry e in Mods.ModProfile.Current.Entries)
                {
                    if (e == null || e.Role != Mods.ModRole.Required) continue;
                    if (string.Equals(e.Id, expansion, StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(e.PackageId, "ludeon.rimworld." + expansion.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // The item catalog only holds defs a connected client actually has.
        internal static bool DefKnown(string def)
        {
            if (DefKnownForTest != null) return DefKnownForTest(def);
            try { return ItemLabels.ItemLabelCache.BaseValue(def) > 0; } catch (Exception) { return false; }
        }

        // The same objective always picks the same cache, so a retry never rolls again.
        internal static Bundle Choose(string objectiveId)
        {
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("kmh.federation.reward|" + (objectiveId ?? "")));
            int pick = h[0] | (h[1] << 8);

            // Base items skip DefKnown on purpose: they exist in every game.
            var pool = new List<Bundle>(Vanilla);
            foreach (Bundle b in Expansions)
                if (ExpansionRequired(b.Expansion) && DefKnown(b.DefName)) pool.Add(b);

            Bundle chosen = pool[pick % pool.Count];
            return chosen;
        }

        internal static int Scaled(int count)
        {
            double scale = FederationConfig.Current.RewardScale;
            if (scale <= 0) return 0;
            return Math.Max(1, (int)Math.Round(count * scale));
        }

        internal static string Describe(string objectiveId)
        {
            Bundle b = Choose(objectiveId);
            return $"{Scaled(b.Count)}x {ItemLabels.ItemLabelCache.LabelFor(b.DefName) ?? b.DefName}";
        }

        // A value move: a reset or backup waits for it, and while one runs it is refused and retried next beat.
        public static int GrantPending(FederatedGuildLink link)
        {
            using (Maintenance.KmhValueOps.Scope op = Maintenance.KmhValueOps.Begin(Maintenance.KmhIngress.Scheduler, out _))
                return op == null ? 0 : GrantPendingAdmitted(link);
        }

        private static int GrantPendingAdmitted(FederatedGuildLink link)
        {
            if (link?.View?.Objectives == null || !FederationConfig.Current.ObjectiveRewards) return 0;
            if (link.State != FederatedLinkState.Active || !link.FederationActive) return 0;
            if (!Features.Guilds.GuildStore.GuildExists(link.LocalGuild)) return 0;

            int granted = 0;
            foreach (FederationObjective o in link.View.Objectives)
            {
                if (o == null || o.State != "Completed" || !o.RewardEligible || string.IsNullOrEmpty(o.Id)) continue;
                if (FederatedGuildStore.HasRewarded(link.LinkId, o.Id)) continue;

                Bundle b = Choose(o.Id);
                int count = Scaled(b.Count);
                string marker = Features.Treasury.TreasuryStore.OneTimeRewardPrefix + o.Id;
                // The vault marker, committed with the items, is the record: the link's list is lost on leave/rejoin
                // and may miss a crash between the two. The marker cap is safe: views only carry this week's few objectives.
                if (Features.Treasury.TreasuryStore.HasGuildMarker(link.LocalGuild, marker))
                {
                    FederatedGuildStore.MarkRewarded(link.LinkId, o.Id);
                    continue;
                }
                // Incarnation-bound so a re-created same-name guild never collects the old link's reward.
                if (count > 0 && !Features.Treasury.TreasuryStore.DepositGuildItemOnce(link.LocalGuild, b.DefName, count, marker,
                                                                                      "federation objective reward", identity: link.Incarnation))
                {
                    ServerLog.Verbose($"Federation: the reward for objective {o.Id} could not reach '{link.LocalGuild}' yet.");
                    continue;
                }
                FederatedGuildStore.MarkRewarded(link.LinkId, o.Id);
                granted++;
                ServerLog.Info($"Federation: '{link.LocalGuild}' received {count}x {b.DefName} for a completed federation objective.");
            }
            return granted;
        }
    }
}
