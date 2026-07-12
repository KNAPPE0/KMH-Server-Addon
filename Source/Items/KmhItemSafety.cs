using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Features.ItemLabels;

namespace KMHServerAddon.Items
{
    // Shared server-side item/economy safety. One place every feature (sites, quests, treasury, marketplace...) asks:
    // is this defName a simple generated resource, may a site/quest generate it, what's its trusted value, and is a
    // client-reported value/skill believable. The server is headless (no RimWorld def DB), so decisions are made from
    // defName heuristics + the client-contributed catalog + config - never from a single client's word.
    internal static class KmhItemSafety
    {
        // Vanilla stackables where def+count is lossless (no identity-changing per-instance state). Compact-storage
        // only, NOT a site allowlist (SiteOutputRules tiers decide that); spacer/high-value defs (ComponentSpacer,
        // ReinforcedBarrel, MedicineUltratech) are deliberately excluded. Owners extend via config.
        private static readonly HashSet<string> SimpleResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Steel","Plasteel","WoodLog","Uranium","Jade","Gold","Silver","Cloth","Synthread","DevilstrandCloth",
            "Hyperweave","Chemfuel","Neutroamine","ComponentIndustrial","MedicineHerbal",
            "MedicineIndustrial","Wool","Leather_Plain","Hay","Kibble","Pemmican","Chocolate","InsectJelly",
            "RawPotatoes","RawRice","RawCorn","RawFungus","RawBerries","RawAgave","RawHops","Milk","PsychoidLeaves",
            "SmokeleafLeaves","Beer","Wort","Meat_Megaspider",
            // blocks + chunks are simple too
            "BlocksSandstone","BlocksGranite","BlocksLimestone","BlocksSlate","BlocksMarble",
        };

        // defName fragments that mark an item as COMPLEX (per-instance state / comps / quality) - never a safe simple
        // output regardless of allowlists. Case-insensitive substring match on the defName.
        private static readonly string[] ComplexMarkers =
        {
            "Gun_","Bow_","MeleeWeapon_","Weapon_","Apparel_","Unfinished","Minified","Corpse_","Bionic","Archotech",
            "Prosthetic","Relic","Genepack","Xenogerm","HumanEmbryo","HumanOvum","Book","Tome","Serum","Persona",
            "Implant","Skull","Egg", // fertilized eggs carry state
            // Explicitly-named blockers (defense-in-depth; these are never a plain stackable site output):
            "Techprint","Techprof","SubpersonaCore","AIPersonaCore","Ammo","Shell_","Neurotrainer","Psytrainer",
            // No bare "Eltex": the raw mineral is a plain stackable; eltex gear is caught by Apparel_/Weapon_/MeleeWeapon_.
            "Gene_","Endogene","GeneRemover","Xenogerm","MechSerum",
            // Broad risk markers from real modded-catalog coverage (kept specific enough not to catch plain resources):
            "Shield","_Belt","Warhead","Missile","Rocket_","Nuclear","Plutonium","FuelRod","Reactor","PowerCore",
            "Permit","NamePlate","Artifact","Psylink","Orbital","Tornado","Targeter",
            // Unsafe stackables surfaced by the all_items catalog (serums are caught by "Serum" above):
            "Subcore","SignalChip","Powerfocus","Nanostructuring","Hemogen","Wastepack","Archite","Transponder",
            "Shard","Bioferrite","LabyrinthMatter","Harbinger","Gauranlen","Seed","MechSerum","VoidNode","Tusk",
        };

        // True when def+count fully represents the item (safe to store/move compactly). Config can extend the set.
        public static bool IsSimpleGeneratedResource(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName)) return false;
            if (LooksComplex(defName)) return false;
            if (SimpleResources.Contains(defName)) return true;
            foreach (string extra in Features.Sites.SitesConfig.Current.ExtraSimpleResourceDefs ?? Array.Empty<string>())
                if (string.Equals(extra, defName, StringComparison.OrdinalIgnoreCase)) return true;
            // Safe resource families (modpack-friendly): meats/leathers/wools/raw crops/stone blocks/meals are always
            // plain stackables and follow these naming conventions. Still gated by the LooksComplex block above.
            foreach (string fam in SafeResourceFamilies)
                if (defName.StartsWith(fam, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static readonly string[] SafeResourceFamilies = { "Meat_", "Leather_", "Wool", "Raw", "Blocks", "Meal" };

        // Backstop for the compact (def+count) deposit path: block complex/unsafe-marked defs a modified client could
        // smuggle in as a plain key; plain stackables (incl. modded) pass - complex items must use the full payload path.
        public static bool IsCompactDepositSafe(string defName, out string reason)
        {
            if (string.IsNullOrWhiteSpace(defName)) { reason = "unknown item"; return false; }
            if (LooksComplex(defName)) { reason = "complex/stateful item - must use the full payload path"; return false; }
            reason = "";
            return true;
        }

        // Whether a site MAY output this def. Until full item-payload support ships, sites are restricted to simple
        // generated resources so they can never mint weapons/apparel/relics/minified/comp-heavy modded items.
        public static bool IsAllowedSiteOutput(string defName, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(defName)) { reason = "no item chosen"; return false; }
            if (LooksComplex(defName)) { reason = $"'{defName}' carries per-item state (quality/HP/comps) and can't be a site output yet"; return false; }
            if (!IsSimpleGeneratedResource(defName))
            {
                reason = $"'{defName}' isn't a recognized simple resource; sites only produce simple resources for now " +
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

        // --- value trust ---

        // Server's trusted per-unit market value (from the client-contributed catalog), or 0 if unknown.
        public static long GetTrustedMarketValue(string defName) => ItemLabelCache.BaseValue(defName);

        // Reconcile a client-reported per-unit value against the trusted catalog. A modified client can under-report to
        // cheapen a site's build cost/cycle; when we have a trusted value we use it and flag large disagreements. When
        // we don't, the reported value is advisory only and clamped to a sane range.
        public static float ValidateClientMarketValue(string defName, float reported, out bool suspicious)
        {
            suspicious = false;
            long trusted = GetTrustedMarketValue(defName);
            if (trusted > 0)
            {
                if (reported < trusted * 0.5f || reported > trusted * 2f) suspicious = true;
                return trusted;   // trusted catalog wins for authoritative economy math
            }
            float clamped = reported < 0 ? 0 : (reported > 100000 ? 100000 : reported);
            return clamped;
        }

        // Client-reported colonist skill is not authoritative (headless server can't verify a pawn's stat). Clamp to
        // the real 0..20 range and treat as advisory; site XP the server owns is the real authority for progression.
        public static int ValidateClientSkill(int reported, out bool advisory)
        {
            advisory = true;   // no server-side pawn stat authority yet - always advisory/display until validated
            return reported < 0 ? 0 : (reported > 20 ? 20 : reported);
        }

        // --- payload identity / safety (operate on metadata only; ScribeXml stays opaque) ---

        // Storage identity. Any state that must NOT merge (clean vs tainted, different HP/quality/stuff, any deep
        // difference) changes the fingerprint. MUST match the client's KmhThingCapture.Fingerprint exactly.
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

        // Two payloads may stack only if RimWorld itself would allow it: same def/stuff/quality, both undamaged (or
        // identical HP), same taint, and neither carries deep per-instance state (a scribe blob = unique instance).
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

        // Fungible stacking: both payloads are client-vouched Mergeable (a fungible food/resource, never a
        // weapon/quality/comp item) and share the same stackable identity - def, stuff, quality, taint. Wear (hit
        // points, rot) is deliberately NOT part of identity here: it's weight-averaged by MergeFungible, so equal
        // food/resource stacks combine into ONE entry instead of splintering one-per-freshness. Blobs are allowed
        // (the representative's blob is kept); the rot the client re-applies on withdraw is the averaged value.
        public static bool CanMergeFungible(KmhThingPayload a, KmhThingPayload b)
        {
            if (a == null || b == null || !a.Mergeable || !b.Mergeable) return false;
            return string.Equals(a.DefName, b.DefName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.StuffDefName ?? "", b.StuffDefName ?? "", StringComparison.OrdinalIgnoreCase)
                && a.Quality == b.Quality
                && a.Tainted == b.Tainted;
        }

        // Stack `incoming` into `target` (same fungible identity): sum the counts and weight-average the wear (hit
        // points + rot) by count, exactly how RimWorld stacks - the combined stack is neither refreshed to fresh nor
        // over-rotted. `target` keeps its own blob as the representative.
        public static void MergeFungible(KmhThingPayload target, KmhThingPayload incoming)
        {
            if (target == null || incoming == null) return;
            long ct = Math.Max(1, target.StackCount);
            long ci = Math.Max(1, incoming.StackCount);
            target.HitPoints        = WeightedAvg(target.HitPoints, ct, incoming.HitPoints, ci);
            target.RotProgressTicks = WeightedAvg(target.RotProgressTicks, ct, incoming.RotProgressTicks, ci);
            target.StackCount       = (int)Math.Min(int.MaxValue, ct + ci);
        }

        // Count-weighted average, treating a negative value as "unknown" (skip it). Returns -1 only when both unknown.
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

        // Fill fingerprint if missing; add warnings for partial/legacy state. Returns false only when unusable.
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

        // Turn an old ItemKey entry (def|stuff|quality + count) into a legacy/partial payload - state we can't prove
        // is left unknown (never fabricated as clean/undamaged/full).
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

        // One-line ledger/audit description of an item's state.
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
