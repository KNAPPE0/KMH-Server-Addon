using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Features.ItemLabels;

namespace KMHServerAddon.Items
{
    // Headless, with no RimWorld def DB: item facts come from heuristics and the contributed catalog, never one client.
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
            foreach (string extra in Features.Sites.SitesConfig.Current.Output.ExtraSimpleResourceDefs ?? Array.Empty<string>())
                if (string.Equals(extra, defName, StringComparison.OrdinalIgnoreCase)) return true;
            // Naming-convention families so modded resources pass without a config entry; still gated by LooksComplex above.
            foreach (string fam in SafeResourceFamilies)
                if (defName.StartsWith(fam, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static readonly string[] SafeResourceFamilies = { "Meat_", "Leather_", "Wool", "Raw", "Blocks", "Meal" };

        // As an item, silver would skip the silver deposit's fee and the treasury's silver cap.
        public static bool IsSilverDef(string defName)
            => string.Equals((defName ?? "").Split('|')[0].Trim(), "Silver", StringComparison.OrdinalIgnoreCase);

        public const string SilverAsItemRefusal =
            "Silver goes into the treasury as silver, not as an item - use the silver deposit (it applies the deposit fee and your treasury cap).";

        // Backstop for the compact def+count path: a modified client must not smuggle a stateful item in as a plain key.
        public static bool IsCompactDepositSafe(string defName, out string reason)
        {
            if (string.IsNullOrWhiteSpace(defName)) { reason = "unknown item"; return false; }
            if (LooksComplex(defName)) { reason = "complex/stateful item - must use the full payload path"; return false; }
            reason = "";
            return true;
        }

        // Simple resources only, so a site can never mint weapons, apparel, relics or comp-heavy modded items.
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

        // Never the client's figure: it sets site build cost, so an under-report buys a cheap site. Mutators must refuse 0.
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

        // Everything the fingerprint uses, recomputed rather than read from the stored one: one row is one exact state.
        public static string DisplayKey(KmhThingPayload p)
        {
            if (p == null) return "";
            return string.Join("|", new[]
            {
                p.DefName ?? "", p.StuffDefName ?? "", p.Quality.ToString(),
                p.HitPoints.ToString(), p.MaxHitPoints.ToString(), p.Tainted ? "1" : "0",
                // The whole blob (genes, crate contents, biocodes live there) minus thing ids, or identical swords split.
                string.IsNullOrEmpty(p.ScribeXml) ? "plain" : BlobHashes.GetValue(p.ScribeXml, x => Hash(WithoutThingIds(x))),
            });
        }

        private static readonly System.Text.RegularExpressions.Regex ThingIdElement =
            new System.Text.RegularExpressions.Regex(@"<id>[^<]*</id>", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        internal static string WithoutThingIds(string xml) => string.IsNullOrEmpty(xml) ? "" : ThingIdElement.Replace(xml, "<id/>");

        // Per blob instance, since strings never change and every snapshot re-keys every stack in the vault.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, string> BlobHashes
            = new System.Runtime.CompilerServices.ConditionalWeakTable<string, string>();

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
            SyncBlobHealth(target);
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

        // The blob is what the recipient loads, so its def, material, quality and condition must match what was judged.
        internal static bool BlobMatches(KmhThingPayload p, out string why)
        {
            why = null;
            if (p == null || string.IsNullOrEmpty(p.ScribeXml)) return true;
            System.Xml.Linq.XElement t;
            try { if (!PlainShape(LoadBlob(p.ScribeXml), out t, out why)) return false; }
            catch (Exception) { why = "its saved state is not readable"; return false; }
            // No item node means no deep state to load, the same as an empty blob: the client rebuilds from metadata.
            if (t == null) return true;

            string def = (string)t.Element("def");
            if (!string.Equals(def, p.DefName, StringComparison.Ordinal)) { why = $"its saved state is a {def ?? "(nothing)"}, not a {p.DefName}"; return false; }
            string stuff = (string)t.Element("stuff");
            if (stuff != null && !string.Equals(stuff, p.StuffDefName ?? "", StringComparison.Ordinal)) { why = "its saved material differs"; return false; }
            string quality = (string)t.Element("quality");
            if (quality != null && QualityIndex(quality) != p.Quality) { why = "its saved quality differs"; return false; }
            if (int.TryParse((string)t.Element("health"), out int hp) && p.HitPoints >= 0 && hp != p.HitPoints
                && !(p.Mergeable && SyncBlobHealth(p)))
            { why = "its saved condition differs"; return false; }
            return true;
        }

        // RimWorld finds <t> by its bare name, so a namespaced twin would load while the plain one was judged.
        internal static bool PlainShape(System.Xml.Linq.XDocument doc, out System.Xml.Linq.XElement t, out string why)
        {
            t = null; why = null;
            System.Xml.Linq.XElement root = doc?.Root;
            if (root == null) { why = "its saved state is empty"; return false; }
            foreach (System.Xml.Linq.XElement e in root.DescendantsAndSelf())
            {
                if (e.Name.Namespace != System.Xml.Linq.XNamespace.None) { why = "its saved state uses an XML namespace"; return false; }
                foreach (System.Xml.Linq.XAttribute a in e.Attributes())
                    if (a.IsNamespaceDeclaration || a.Name.Namespace != System.Xml.Linq.XNamespace.None)
                    { why = "its saved state uses an XML namespace"; return false; }
            }
            int items = 0;
            foreach (System.Xml.Linq.XElement c in root.Elements("t")) { items++; t = c; }
            if (items > 1) { t = null; why = "its saved state holds more than one item"; return false; }
            return true;
        }

        // Folding keeps the first blob but averages wear, so the blob takes the average; refusing it would destroy goods.
        internal static bool SyncBlobHealth(KmhThingPayload p)
        {
            if (p == null || p.HitPoints < 0 || string.IsNullOrEmpty(p.ScribeXml)) return false;
            try
            {
                System.Xml.Linq.XDocument doc = LoadBlob(p.ScribeXml);
                System.Xml.Linq.XElement health = doc.Root?.Element("t")?.Element("health");
                if (health == null) return false;
                string want = p.HitPoints.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (health.Value == want) return true;
                health.Value = want;
                // The stored fingerprint is left alone: withdrawals find the row by it, and a merge never changed it.
                p.ScribeXml = doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static System.Xml.Linq.XDocument LoadBlob(string xml)
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null,
                IgnoreComments = true, MaxCharactersInDocument = 4L * 1024 * 1024,
            };
            using (var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml), settings))
                return System.Xml.Linq.XDocument.Load(reader);
        }

        // RimWorld's QualityCategory names, in order, as the payload's 1..7.
        private static readonly string[] QualityNames = { "Awful", "Poor", "Normal", "Good", "Excellent", "Masterwork", "Legendary" };

        private static int QualityIndex(string name)
        {
            for (int i = 0; i < QualityNames.Length; i++)
                if (string.Equals(QualityNames[i], (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return i + 1;
            return -1;
        }

        public static bool ValidatePayload(KmhThingPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.DefName)) return false;
            if (!BlobMatches(p, out _)) return false;
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
