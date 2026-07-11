using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.AdminCommands
{
    // `kmh validate [configs|items|treasury|sites|recovery|all]` - read-only pre-flight so an owner can vet configs and
    // item pools before players use them. Never mutates or deletes; only reports.
    internal static class KmhValidateCommands
    {
        public static void Run(string[] args, Action<string> reply)
        {
            string what = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "all";
            bool all = what == "all";

            if (all || what == "configs")  Configs(reply);
            if (all || what == "items" || what == "sites") Catalog(reply);
            if (all || what == "sites")    SiteWorkers(reply);
            if (all || what == "treasury") Treasury(reply);
            if (all || what == "recovery") Recovery(reply);
            if (all || what == "colonists" || what == "standings") Colonists(reply);
            if (all || what == "guilds") Guilds(reply);
            if (!all && what != "configs" && what != "items" && what != "sites" && what != "treasury" && what != "recovery" && what != "colonists" && what != "standings" && what != "guilds")
                reply("Usage: kmh validate [configs|items|treasury|sites|recovery|colonists|guilds|all]");
        }

        private static void Configs(Action<string> reply)
        {
            reply("=== Config validation ===");
            foreach ((string name, string path) in ConfigFiles())
            {
                if (!File.Exists(path)) { reply($"  {name}: (not generated yet - will be created with safe defaults on boot)"); continue; }
                try
                {
                    JObject o = JObject.Parse(File.ReadAllText(path));
                    string ver = o["SchemaVersion"]?.ToString();
                    reply($"  {name}: OK{(string.IsNullOrEmpty(ver) ? "" : $" (SchemaVersion {ver})")}");
                }
                catch (Exception ex) { reply($"  {name}: [WARN] parse failed - {ex.Message} (boot loads safe defaults instead)"); }
            }
            Features.Economy.EconomyConfig ec = Features.Economy.EconomyConfig.Current;
            if (string.Equals(ec.EconomyMode ?? "Standard", "Standard", StringComparison.OrdinalIgnoreCase))
                reply("  [REVIEW] EconomyMode=Standard - remote off-map wealth; Balanced/Localized/Hardcore are safer for public servers.");
            bool restrictive = !string.Equals(ec.PersonalTreasuryAccessMode, "Remote", StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(ec.GuildTreasuryAccessMode, "Remote", StringComparison.OrdinalIgnoreCase);
            if (restrictive)
                reply($"  [INFO] Treasury access personal={ec.PersonalTreasuryAccessMode}, guild={ec.GuildTreasuryAccessMode}. Passive income (site rewards, marketplace/auction/want payouts) still accrues into the vault; players withdraw once they meet the access rule (Disabled = accrues but not withdrawable until re-enabled).");
        }

        private static void Catalog(Action<string> reply)
        {
            List<(string defName, string label, long value)> cat = Features.ItemLabels.ItemLabelCache.AllForCatalog();
            int total = cat.Count, allowed = 0, blocked = 0, unknown = 0;
            int[] tier = new int[5];   // allowed-by-tier, index 1..4
            foreach ((string defName, string label, long value) in cat)
            {
                Features.Sites.SiteOutputClass c = Features.Sites.SiteOutputRules.Classify(defName, label, value);
                if (c.IsUnknownOrSuspicious) unknown++;
                if (c.IsAllowed) { allowed++; if (c.TierNumber >= 1 && c.TierNumber <= 4) tier[c.TierNumber]++; } else blocked++;
            }
            reply($"Item catalog: {total} known def(s) - site-allowed {allowed} (T1 {tier[1]}, T2 {tier[2]}, T3 {tier[3]}, T4 {tier[4]}), blocked {blocked}, unknown/suspicious {unknown}" +
                (total < 12 ? " (SPARSE - clients haven't pushed labels yet)" : "") + ". Full tiering: kmh audit.");
        }

        private static void SiteWorkers(Action<string> reply)
        {
            List<Features.Sites.Dto.SiteEntry> sites = Features.Sites.SiteStore.AllForApi();
            int legacy = 0, missingPawn = 0, active = 0;
            var pawnToSites = new Dictionary<int, List<int>>();
            foreach (Features.Sites.Dto.SiteEntry s in sites)
            {
                if (s?.WorkerProgress == null) continue;
                foreach (var kv in s.WorkerProgress)
                {
                    Features.Sites.Dto.WorkerProgressDto wp = kv.Value;
                    if (wp == null) continue;
                    if (wp.Legacy) { legacy++; continue; }
                    if (wp.PawnLoadId <= 0) { missingPawn++; continue; }
                    active++;
                    if (!pawnToSites.TryGetValue(wp.PawnLoadId, out List<int> tiles)) { tiles = new List<int>(); pawnToSites[wp.PawnLoadId] = tiles; }
                    tiles.Add(s.Tile);
                }
            }
            reply($"Site workers: {active} real pawn(s), {legacy} legacy account-worker(s) DISABLED, {missingPawn} with missing pawn data.");
            foreach (var kv in pawnToSites)
                if (kv.Value.Count > 1)
                    reply($"  [REVIEW] pawn loadId {kv.Key} is assigned to {kv.Value.Count} sites (tiles {string.Join(",", kv.Value)}) - a pawn can only work one.");
            if (legacy > 0) reply("  Legacy workers were kept (not deleted); reassign a real pawn via caravan to resume production.");
        }

        private static void Treasury(Action<string> reply)
        {
            (int full, int partial, int legacy) = Features.Treasury.TreasuryStore.PayloadHealth();
            reply($"Treasury payloads: {full} full-fidelity, {partial} partial/metadata, {legacy} legacy (pre-payload; exact state unproven).");
        }

        // Guild health: vault/perks/hall are always valid data; this flags membership/orphan problems across all guilds.
        private static void Guilds(Action<string> reply)
        {
            System.Collections.Generic.List<(string Name, int MemberCount)> gs = Features.Guilds.GuildStore.ListGuilds();
            reply($"=== Guild validation === {gs.Count} guild(s)");
            int problems = 0;
            foreach ((string name, int _) in gs)
                if (Features.Guilds.GuildStore.InspectGuild(name, out System.Collections.Generic.List<string> lines))
                    foreach (string l in lines)
                        if (l.Contains("[PROBLEM]")) { reply($"  {name}:{l.Substring(l.IndexOf("[PROBLEM]"))}"); problems++; }
            reply(problems == 0
                ? "  all guilds consistent (vault/perks/hall/members OK). Detail: 'kmh guild inspect <name>'."
                : $"  {problems} problem(s) - fix membership with 'kmh guild repair'; detail via 'kmh guild inspect <name>'.");
        }

        private static void Recovery(Action<string> reply)
        {
            int held = Features.Recovery.RecoveryStore.HeldCount;
            reply($"Recovery queue: {held} held item/silver record(s)" + (held > 0 ? " - review with 'kmh recover list'." : "."));
        }

        // Review-only skill sanity pass over the standings rosters. Skills are display-only vanity data - flagged
        // colonists are never auto-punished or deleted; legit maxing exists (neurotrainers, books, long play, genes).
        private static void Colonists(Action<string> reply)
        {
            List<Features.PlayerStats.Dto.ColonistEntry> all =
                Features.PlayerStats.PlayerStatsStore.BuildColonistRoster()?.Colonists;
            reply("=== Colonist records validation ===");
            if (all == null || all.Count == 0) { reply("  No colonist rosters reported yet."); return; }
            int flagged = 0;
            foreach (Features.PlayerStats.Dto.ColonistEntry c in all)
            {
                if (c == null) continue;
                int[] sk = { c.SkShooting, c.SkMelee, c.SkMedicine, c.SkCrafting, c.SkConstruction };
                int over20 = 0, at20 = 0;
                foreach (int s in sk) { if (s > 20) over20++; if (s >= 20) at20++; }
                string reason = over20 > 0                 ? $"{over20} tracked skill(s) above the vanilla cap of 20"
                              : (at20 >= 4 && c.Days < 60) ? $"{at20}/5 tracked skills maxed after only {c.Days} day(s)"
                              : null;
                if (reason == null) continue;
                flagged++;
                reply($"  [REVIEW] {c.Owner}/{c.Colony}: '{c.Name}' - {reason}.");
            }
            reply(flagged == 0
                ? $"  {all.Count} colonist(s) across all colonies look normal."
                : $"  {flagged} of {all.Count} colonist(s) flagged for review only (display-only data, never auto-punished or deleted).");
        }

        private static IEnumerable<(string, string)> ConfigFiles()
        {
            yield return ("Economy",     KmhDataPaths.EconomyConfigFile);
            yield return ("Sites",       KmhDataPaths.SitesConfigFile);
            yield return ("World",       KmhDataPaths.WorldConfigFile);
            yield return ("Quests",      KmhDataPaths.QuestsConfigFile);
            yield return ("Enforcement", KmhDataPaths.EnforcementConfigFile);
            yield return ("Reputation",  KmhDataPaths.ReputationConfigFile);
            yield return ("Transport",   KmhDataPaths.TransportConfigFile);
            yield return ("Maintenance", KmhDataPaths.MaintenanceConfigFile);
            yield return ("Features",    KmhDataPaths.FeaturesConfigFile);
            yield return ("Discord",     KmhDataPaths.DiscordConfigFile);
        }
    }
}
