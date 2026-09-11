using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Features.ItemLabels;

namespace KMHServerAddon.Items
{
    // The server is headless with no RimWorld def DB, so item facts come from heuristics and the contributed catalog, never one client's word.
    internal static class KmhItemSafety
    {
        // Compact-storage safety only, not a site allowlist - SiteOutputRules tiers decide that.
        private static readonly HashSet<string> SimpleResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Steel","Plasteel","WoodLog","Uranium","Jade","Gold","Silver","Cloth","Synthread","DevilstrandCloth",
            "Hyperweave","Chemfuel","Neutroamine","ComponentIndustrial","MedicineHerbal",
            "MedicineIndustrial","Wool","Leather_Plain","Hay","Kibble","Pemmican","Chocolate","InsectJelly",
            "RawPotatoes","RawRice","RawCorn","RawFungus","RawBerries","RawAgave","RawHops","Milk","PsychoidLeaves",
            "SmokeleafLeaves","Beer","Wort","Meat_Megaspider",
            "BlocksSandstone","BlocksGranite","BlocksLimestone","BlocksSlate","BlocksMarble",
        };

        // Case-insensitive substring match: anything matching carries per-item state and is never a safe simple output.
        private static readonly string[] ComplexMarkers =
        {
            "Gun_","Bow_","MeleeWeapon_","Weapon_","Apparel_","Unfinished","Minified","Corpse_","Bionic","Archotech",
            "Prosthetic","Relic","Genepack","Xenogerm","HumanEmbryo","HumanOvum","Book","Tome","Serum","Persona",
            "Implant","Skull","Egg", // fertilized eggs carry state
            "Techprint","Techprof","SubpersonaCore","AIPersonaCore","Ammo","Shell_","Neurotrainer","Psytrainer",
            // No bare "Eltex": the raw mineral is a plain stackable; eltex gear is caught by Apparel_/Weapon_/MeleeWeapon_.
            "Gene_","Endogene","GeneRemover","Xenogerm","MechSerum",
            "Shield","_Belt","Warhead","Missile","Rocket_","Nuclear","Plutonium","FuelRod","Reactor","PowerCore",
            "Permit","NamePlate","Artifact","Psylink","Orbital","Tornado","Targeter",
            "Subcore","SignalChip","Powerfocus","Nanostructuring","Hemogen","Wastepack","Archite","Transponder",
            "Shard","Bioferrite","LabyrinthMatter","Harbinger","Gauranlen","Seed","MechSerum","VoidNode","Tusk",
        };

        // True when def+count fully represents the item, so it can be stored and moved compactly.
        public static bool IsSimpleGeneratedResource(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName)) return false;
            if (LooksComplex(defName)) return false;
            if (SimpleResources.Contains(defName)) return true;
            foreach (string extra in Features.Sites.SitesConfig.Current.ExtraSimpleResourceDefs ?? Array.Empty<string>())
                if (string.Equals(extra, defName, StringComparison.OrdinalIgnoreCase)) return true;
            // Naming-convention families so modded resources pass without a config entry; still gated by LooksComplex above.
            foreach (string fam in SafeResourceFamilies)
                if (defName.StartsWith(fam, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static readonly string[] SafeResourceFamilies = { "Meat_", "Leather_", "Wool", "Raw", "Blocks", "Meal" };

        // Backstop for the compact def+count path: a modified client must not smuggle a stateful item in as a plain key.
        public static bool IsCompactDepositSafe(string defName, out string reason)
        {
            if (string.IsNullOrWhiteSpace(defName)) { reason = "unknown item"; return false; }
            if (LooksComplex(defName)) { reason = "complex/stateful item - must use the full payload path"; return false; }
            reason = "";
            return true;
        }

        // Sites are restricted to simple resources so they can never mint weapons, apparel, relics or comp-heavy modded items.
        public static bool IsAllowedSiteOutput(string defName, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(defName)) { reason = "no item chosen"; return false; }
            if (LooksComplex(defName)) { reason = $"'{defName}' carries per-item state (quality/HP/comps), so it can't be a site output"; return false; }
            if (!IsSimpleGeneratedResource(defName))
            {
                reason = $"'{defName}' isn't a recognized simple resource; sites produce simple resources only " +
                         "(add it to Sites.json ExtraSimpleResourceDefs if it's genuinely a plain stackable)";
                return false;
            }
            return true;
        }

        private static bool LooksComplex(string defName)
        {
            foreach (string m in ComplexMarkers)
                if (defName.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static long GetTrustedMarketValue(string defName) => ItemLabelCache.BaseValue(defName);

        // Never the client's figure - it sets site build cost, so an under-report buys a cheap fast site. Mutators must refuse on 0.
        public static float TrustedMarketValueOrZero(string defName, float reported, out bool suspicious)
        {
            long trusted = GetTrustedMarketValue(defName);
            suspicious = trusted > 0 && (reported < trusted * 0.5f || reported > trusted * 2f);
            return trusted > 0 ? trusted : 0f;
        }

        // Advisory only - shown, never raising real output; server-owned site XP drives production.
        public static int ValidateClientSkill(int reported, out bool advisory)
        {
            advisory = true;
            return reported < 0 ? 0 : (reported > 20 ? 20 : reported);
        }

        // Wire contract: must match the client's KmhThingCapture.Fingerprint exactly.
        public static string GetStateFingerprint(KmhThingPayload p)
        {
            if (p == null) return "";
            string basis = string.Join("|", new[]
            {
                p.DefName ?? "", p.StuffDefName ?? "", p.Quality.ToString(),
                p.HitPoints.ToString(), p.MaxHitPoints.ToString(), p.Tainted ? "1" : "0",
                Hash(p.ScribeXml),
            });
            return Hash(basis).Substring(0, 16);
        }

        // Everything the fingerprint uses except the blob, so byte-different copies of one item are shown as a single row.
        public static string DisplayKey(KmhThingPayload p)
        {
            if (p == null) return "";
            return string.Join("|", new[]
            {
                p.DefName ?? "", p.StuffDefName ?? "", p.Quality.ToString(),
                p.HitPoints.ToString(), p.MaxHitPoints.ToString(), p.Tainted ? "1" : "0",
                // Bucketed rather than hashed: two stateful stacks group together, but never with a plain one.
                string.IsNullOrEmpty(p.ScribeXml) ? "plain" : "stateful",
            });
        }

        // Mirrors RimWorld's own stacking rule; a scribe blob marks a unique instance that can never stack.
        public static bool CanSafelyMerge(KmhThingPayload a, KmhThingPayload b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.ScribeXml) || !string.IsNullOrEmpty(b.ScribeXml)) return false;
            return string.Equals(a.DefName, b.DefName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.StuffDefName ?? "", b.StuffDefName ?? "", StringComparison.OrdinalIgnoreCase)
                && a.Quality == b.Quality
                && a.Tainted == b.Tainted
                && a.HitPoints == b.HitPoints
                && a.MaxHitPoints == b.MaxHitPoints;
        }

        // Wear is deliberately not part of identity, or equal food stacks would splinter into one entry per freshness.
        public static bool CanMergeFungible(KmhThingPayload a, KmhThingPayload b)
        {
            if (a == null || b == null || !a.Mergeable || !b.Mergeable) return false;
            return string.Equals(a.DefName, b.DefName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.StuffDefName ?? "", b.StuffDefName ?? "", StringComparison.OrdinalIgnoreCase)
                && a.Quality == b.Quality
                && a.Tainted == b.Tainted;
        }

        // Wear is weight-averaged by count, so a merged stack is neither refreshed to fresh nor over-rotted.
        public static void MergeFungible(KmhThingPayload target, KmhThingPayload incoming)
        {
            if (target == null || incoming == null) return;
            long ct = Math.Max(1, target.StackCount);
            long ci = Math.Max(1, incoming.StackCount);
            target.HitPoints        = WeightedAvg(target.HitPoints, ct, incoming.HitPoints, ci);
            target.RotProgressTicks = WeightedAvg(target.RotProgressTicks, ct, incoming.RotProgressTicks, ci);
            target.StackCount       = (int)Math.Min(int.MaxValue, ct + ci);
        }

        // A negative value means unknown and is skipped, so -1 comes back only when both are unknown.
        private static long WeightedAvg(long a, long ca, long b, long cb)
        {
            if (a < 0 && b < 0) return -1;
            if (a < 0) return b;
            if (b < 0) return a;
            long denom = ca + cb;
            return denom <= 0 ? a : (long)Math.Round((a * (double)ca + b * (double)cb) / denom);
        }

        private static int WeightedAvg(int a, long ca, int b, long cb)
            => (int)WeightedAvg((long)a, ca, (long)b, cb);

        public static bool ValidatePayload(KmhThingPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.DefName)) return false;
            if (p.StackCount < 1) p.StackCount = 1;
            if (string.IsNullOrEmpty(p.Fingerprint)) p.Fingerprint = GetStateFingerprint(p);
            p.Warnings = p.Warnings ?? new List<string>();
            if (p.Legacy || string.Equals(p.Fidelity, KmhThingPayload.FidelityLegacy, StringComparison.OrdinalIgnoreCase))
            {
                p.Legacy = true;
                if (!p.Warnings.Contains(LegacyWarning)) p.Warnings.Add(LegacyWarning);
            }
            return true;
        }

        // State an old ItemKey entry cannot prove is left unknown, never fabricated as clean or undamaged.
        public static KmhThingPayload MarkLegacyPartial(string defName, string stuffDefName, int quality, int count, string label)
        {
            KmhThingPayload p = new KmhThingPayload
            {
                DefName = defName ?? "", StuffDefName = stuffDefName ?? "", Quality = quality,
                StackCount = Math.Max(1, count), DisplayLabel = string.IsNullOrEmpty(label) ? (defName ?? "item") : label,
                HitPoints = -1, MaxHitPoints = -1, Tainted = false,
                Fidelity = KmhThingPayload.FidelityLegacy, Legacy = true,
                MarketValue = GetTrustedMarketValue(defName),
            };
            p.Warnings.Add(LegacyWarning);
            p.Fingerprint = GetStateFingerprint(p);
            return p;
        }

        public const string LegacyWarning =
            "legacy item: pre-payload data - exact damage/taint/comp state is unknown and can't be proven on restore";

        public static string DescribeStateForLedger(KmhThingPayload p)
        {
            if (p == null) return "?";
            List<string> bits = new List<string>();
            if (!string.IsNullOrEmpty(p.StuffDefName)) bits.Add(p.StuffDefName);
            if (p.Quality > 0) bits.Add("q" + p.Quality);
            if (p.HitPoints >= 0 && p.MaxHitPoints > 0 && p.HitPoints < p.MaxHitPoints) bits.Add($"{p.HitPoints}/{p.MaxHitPoints}hp");
            if (p.Tainted) bits.Add("tainted");
            if (p.Legacy) bits.Add("legacy");
            else if (string.Equals(p.Fidelity, KmhThingPayload.FidelityMetadata, StringComparison.OrdinalIgnoreCase)) bits.Add("partial");
            string state = bits.Count > 0 ? " (" + string.Join(", ", bits) + ")" : "";
            return $"{p.StackCount}x {(string.IsNullOrEmpty(p.DisplayLabel) ? p.DefName : p.DisplayLabel)}{state}";
        }

        private static string Hash(string s)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
