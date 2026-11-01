using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Mods
{
    internal enum ModPolicy
    {
        // RWT has not loaded its mod config yet, so no listing may claim any of the others.
        Unknown,
        AllowAll,
        // RWT enforces nothing until a world exists and a list has been published; that is a real state, not a gap.
        NotSet,
        Enforced,
    }

    // Derived from the Master.ModConfig RWT refuses joins over, so a listing never advertises an unapplied rule.
    internal sealed class ModProfileSnapshot
    {
        public ModPolicy Policy = ModPolicy.Unknown;
        public bool EnforceSettings;
        public bool EnforceOrder;
        // RWT admits on these alone: every Required mod present, every mod run listed; a ban is not a third role.
        public List<string> Required = new List<string>();
        public List<string> Optional = new List<string>();
        // From RWT's FL_ModOrder (ModsConfig.xml verbatim); empty when none is published or its hash fails, never partial.
        public List<string> Order = new List<string>();
        public string OrderHash = "";
        public string Hash = "";
        public long Revision;

        // The same mods with player-actionable identity; the lists above stay because older clients read them.
        public List<ModEntry> Entries = new List<ModEntry>();

        public int Count => Required.Count + Optional.Count;

        public string PolicyName
        {
            get
            {
                switch (Policy)
                {
                    case ModPolicy.AllowAll: return "AllowAll";
                    case ModPolicy.NotSet:   return "NotSet";
                    case ModPolicy.Enforced: return "Enforced";
                    default:                 return "Unknown";
                }
            }
        }
    }

    internal sealed class ModProfileState
    {
        public int    SchemaVersion { get; set; } = 1;
        public long   Revision      { get; set; }
        public string Hash          { get; set; } = "";
    }

    internal static class ModProfile
    {
        private static readonly object _lock = new object();
        private static ModProfileSnapshot _current = new ModProfileSnapshot();
        private static ModProfileState _state = new ModProfileState();
        private static bool _stateLoaded;

        public static ModProfileSnapshot Current { get { lock (_lock) return _current; } }

        public static void LoadFromDisk()
        {
            lock (_lock)
            {
                _state = JsonFileStore.TryLoad(KmhDataPaths.ModProfileFile, out ModProfileState loaded) && loaded != null
                    ? loaded : new ModProfileState();
                _stateLoaded = true;
            }
            Refresh();
        }

        // Cheap enough to call on every heartbeat: RWT keeps the whole mod config in memory.
        public static ModProfileSnapshot Refresh()
        {
            ModProfileSnapshot snap = Read();
            lock (_lock)
            {
                if (!_stateLoaded) { _current = snap; return snap; }

                // Unknown never advances the revision: a server that has not read its config yet has not changed it.
                if (snap.Policy == ModPolicy.Unknown) { snap.Revision = _state.Revision; _current = snap; return snap; }

                if (snap.Hash != _state.Hash)
                {
                    _state.Revision++;
                    _state.Hash = snap.Hash;
                    try { JsonFileStore.Save(KmhDataPaths.ModProfileFile, _state); }
                    catch (Exception ex) { ServerLog.Warn($"Mod profile: saving the revision failed: {ex.Message}"); }
                    ServerLog.Info($"Mod profile: revision {_state.Revision} - {snap.PolicyName}, " +
                                   $"{snap.Required.Count} required, {snap.Optional.Count} optional.");
                }
                // Outside the hash check: a KMH upgrade can make an unchanged entry wrong; latched per name to warn once.
                WarnUnmatchableRwt(snap);
                WarnStaleKmhName(snap);
                snap.Revision = _state.Revision;
                _current = snap;
                return snap;
            }
        }

        // Global keeps the highest revision it was sent, so a counter a reinstall reset has to climb past it.
        internal static long ClimbPast(long listed)
        {
            lock (_lock)
            {
                if (!_stateLoaded || listed < _state.Revision) return _state.Revision;
                _state.Revision = listed + 1;
                try { JsonFileStore.Save(KmhDataPaths.ModProfileFile, _state); }
                catch (Exception ex) { ServerLog.Warn($"Mod profile: saving the revision failed: {ex.Message}"); }
                _current.Revision = _state.Revision;
                return _state.Revision;
            }
        }

        private static readonly HashSet<string> _warnedNames = new HashSet<string>(StringComparer.Ordinal);

        internal static bool ShouldSayOnce(string what)
        {
            lock (_warnedNames) return _warnedNames.Add(what);
        }

        internal static void ForgetNameWarningsForTest()
        {
            lock (_warnedNames) _warnedNames.Clear();
        }

        // RWT admits on reported mod names and always reports exactly "RimWorld Together", so a required folder name
        // like "RimWorld Together - 26.6.9.1 Snapshot" silently refuses every player.
        internal const string RwtModName = "RimWorld Together";

        internal static string UnmatchableRwtEntry(ModProfileSnapshot snap)
        {
            if (snap == null || snap.Policy != ModPolicy.Enforced) return null;
            foreach (string name in snap.Required)
            {
                if (name == null) continue;
                string n = name.Trim();
                // Not a return: a stamped entry refuses everybody even beside the right name, which sorts first.
                if (string.Equals(n, RwtModName, StringComparison.Ordinal)) continue;
                if (n.StartsWith(RwtModName, StringComparison.OrdinalIgnoreCase)) return n;
            }
            return null;
        }

        // RWT admits on the reported mod name, so a required list still naming the legacy KMH name refuses every
        // updated player, and KMH can only read that list, never edit it.
        internal const string KmhModName       = "KMH: Colony Nexus";
        internal const string LegacyKmhModName = "KMH Patch for RimWorld Together";

        internal static string StaleKmhEntry(ModProfileSnapshot snap)
        {
            if (snap == null || snap.Policy != ModPolicy.Enforced) return null;
            foreach (string name in snap.Required)
                if (name != null && string.Equals(name.Trim(), LegacyKmhModName, StringComparison.OrdinalIgnoreCase))
                    return name.Trim();
            return null;
        }

        private static void WarnStaleKmhName(ModProfileSnapshot snap)
        {
            string stale = StaleKmhEntry(snap);
            if (stale == null || !ShouldSayOnce("kmh:" + stale)) return;
            ServerLog.Warn($"Mod profile: this server requires a mod named '{stale}', which is what the Colony Nexus mod was called "
                         + $"before v1.3.1. Updated players report '{KmhModName}' instead and are refused with "
                         + $"\"{stale} not allowed\". Rename that entry in Configs/ModConfig.json.");
        }

        private static void WarnUnmatchableRwt(ModProfileSnapshot snap)
        {
            string bad = UnmatchableRwtEntry(snap);
            if (bad == null || !ShouldSayOnce("rwt:" + bad)) return;
            ServerLog.Warn($"Mod profile: this server requires a mod named '{bad}', but RimWorld Together reports "
                         + $"itself as '{RwtModName}' on every install. No player can satisfy that entry - they are "
                         + $"refused with \"{bad} not allowed\". Fix the name in Configs/ModConfig.json.");
        }

        private static ModProfileSnapshot Read()
        {
            ModProfileSnapshot snap = new ModProfileSnapshot();
            try
            {
                object cfg = Master.ModConfig;
                if (cfg == null) return snap;

                bool? allowAll = Bool(cfg, "AllowAllMods") ?? Bool(cfg, "BypassMods");
                if (allowAll == null) return snap;

                snap.EnforceSettings = Bool(cfg, "EnforceSettings") ?? false;

                // Absent on older RWT, null on 26.8.x until a world publishes one: nothing to enforce, not an empty order.
                object order = Member(cfg, "ModOrder");
                if (order != null)
                {
                    RwtModOrder.Parsed parsed = RwtModOrder.Read(Str(order, "DataString"), Str(order, "Hash"));
                    RwtModOrder.WarnIfUntrusted(parsed);
                    snap.Order = parsed.Order;
                    snap.OrderHash = parsed.Hash;
                }
                snap.EnforceOrder = snap.Order.Count > 0;

                ModPolicyConfig policy = ModPolicyConfig.Current;

                foreach (object entry in Entries(cfg))
                {
                    string name = Str(entry, "ModName") ?? Str(entry, "FileName");
                    if (string.IsNullOrEmpty(name)) continue;
                    string type = Member(entry, "Type")?.ToString() ?? "";

                    string live = string.Equals(type, "Optional", StringComparison.OrdinalIgnoreCase)
                                ? ModRole.Optional : ModRole.Required;

                    // Workshop file id or expansion name: the difference between "press Subscribe" and "you must own this".
                    string id = (Str(entry, "ModID") ?? "").Trim();
                    ModEntry me = new ModEntry { Name = name, Id = id, Kind = ModEntry.KindOf(id) };
                    string role = Project(entry, policy, me, live);

                    if (role == ModRole.Optional) snap.Optional.Add(name);
                    else snap.Required.Add(name);

                    snap.Entries.Add(me);
                }

                WarnUnmatchedPolicy(policy, snap);
                MergeRepeatedNames(snap);

                MatchPackageIds(snap);

                snap.Required.Sort(StringComparer.Ordinal);
                snap.Optional.Sort(StringComparer.Ordinal);
                // Mirrors RWT's own gate: with no world, or with nothing published, it refuses no one yet.
                snap.Policy = allowAll.Value ? ModPolicy.AllowAll
                            : snap.Count == 0 || !WorldExists() ? ModPolicy.NotSet
                            : ModPolicy.Enforced;
                snap.Hash = HashOf(snap);
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Mod profile: reading RWT's mod config failed: {ex.Message}");
                return new ModProfileSnapshot();
            }
            return snap;
        }

        // RWT entries carry no packageId, so the order is the only source; an ambiguous or shared match stays blank
        // and the client falls back to the name rather than acting on a guess.
        internal static void MatchPackageIds(ModProfileSnapshot snap)
        {
            if (snap.Order.Count == 0) return;
            var claims = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry e in snap.Entries)
            {
                string official = OfficialPackageId(e.Id) ?? OfficialPackageId(e.Name);
                string p = official != null ? snap.Order.Find(o => string.Equals(o, official, StringComparison.OrdinalIgnoreCase))
                                            : TailMatch(snap.Order, e.Name);
                if (string.IsNullOrEmpty(p)) continue;
                if (!claims.TryGetValue(p, out List<ModEntry> by)) claims[p] = by = new List<ModEntry>();
                by.Add(e);
            }
            foreach (KeyValuePair<string, List<ModEntry>> kv in claims)
                if (kv.Value.Count == 1) kv.Value[0].PackageId = kv.Key;
        }

        // RWT can list a mod twice, even in both roles; it is published once, required if either says so (the safe reading).
        internal static void MergeRepeatedNames(ModProfileSnapshot snap)
        {
            var first = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
            var kept = new List<ModEntry>();
            foreach (ModEntry e in snap.Entries)
            {
                if (!first.TryGetValue(e.Name, out ModEntry was)) { first[e.Name] = e; kept.Add(e); continue; }
                if (e.Role == was.Role) continue;
                was.Role = ModRole.Required;
                if (ShouldSayOnce("both:" + e.Name))
                    ServerLog.Warn($"Mod profile: Configs/ModConfig.json lists '{e.Name}' as both required and optional. "
                                 + "Colony Nexus shows it as required; remove one of the two entries.");
            }
            if (kept.Count == snap.Entries.Count) return;
            snap.Entries = kept;
            snap.Required = kept.FindAll(e => e.Role == ModRole.Required).ConvertAll(e => e.Name);
            snap.Optional = kept.FindAll(e => e.Role == ModRole.Optional).ConvertAll(e => e.Name);
        }

        internal static string OfficialPackageId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (string.Equals(id.Trim(), "Core", StringComparison.OrdinalIgnoreCase)) return "ludeon.rimworld";
            return ModEntry.IsExpansion(id) ? "ludeon.rimworld." + id.Trim().ToLowerInvariant() : null;
        }

        private static string TailMatch(List<string> order, string name)
        {
            string want = (name ?? "").Replace(" ", ""), found = null;
            if (want.Length == 0) return null;
            foreach (string p in order)
            {
                if (string.IsNullOrEmpty(p) || p.StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(p.Substring(p.LastIndexOf('.') + 1), want, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) return null;
                found = p;
            }
            return found;
        }

        // Covers the policy flags as well as the names: turning settings enforcement on changes what joining requires.
        public static string HashOf(ModProfileSnapshot snap)
        {
            StringBuilder sb = new StringBuilder();
            // Entries are hashed too, or a fixed Workshop id leaves the revision unmoved and clients offering the old item.
            sb.Append("kmh.modprofile.v4\n");
            sb.Append(snap.Policy == ModPolicy.AllowAll ? "allow\n" : "enforce\n");
            sb.Append(snap.EnforceSettings ? "settings\n" : "-\n");
            sb.Append(snap.EnforceOrder ? "order\n" : "-\n");
            Append(sb, "R", snap.Required);
            Append(sb, "O", snap.Optional);
            Append(sb, "L", snap.Order);
            sb.Append(snap.OrderHash).Append("\n");
            sb.Append("E:").Append(snap.Entries.Count).Append('\n');
            foreach (ModEntry e in snap.Entries)
            {
                Field(sb, e.Name); Field(sb, e.Role); Field(sb, e.Kind);
                Field(sb, e.Id); Field(sb, e.PackageId); Field(sb, e.Author);
                sb.Append('\n');
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                StringBuilder hex = new StringBuilder(h.Length * 2);
                foreach (byte b in h) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        private static void Append(StringBuilder sb, string tag, List<string> names)
        {
            sb.Append(tag).Append(':').Append(names.Count).Append('\n');
            // Length-prefixed, or a mod called "a\nb" could forge a different split of the same bytes.
            foreach (string n in names) sb.Append(n.Length).Append(':').Append(n).Append('\n');
        }

        private static void Field(StringBuilder sb, string value)
        {
            string v = value ?? "";
            sb.Append(v.Length).Append(':').Append(v).Append('|');
        }

        // Reflected: the member moved between RWT generations, and a direct reference would not compile for all three.
        private static bool WorldExists()
        {
            try
            {
                PropertyInfo p = typeof(Master).GetProperty("WorldValues", BindingFlags.Public | BindingFlags.Static);
                if (p != null) return p.GetValue(null) != null;
                FieldInfo f = typeof(Master).GetField("WorldValues", BindingFlags.Public | BindingFlags.Static);
                // No such member at all means this build cannot tell, and refusing to claim a profile is the safe half.
                return f == null || f.GetValue(null) != null;
            }
            catch { return true; }
        }

        private static IEnumerable<object> Entries(object cfg)
        {
            object list = Member(cfg, "ModConfigs");
            if (!(list is IEnumerable seq)) yield break;
            foreach (object o in seq) if (o != null) yield return o;
        }

        private static object Member(object target, string name)
        {
            if (target == null) return null;
            Type t = target.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(target);
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(target);
        }

        // A role is published only once written back into the list RWT checks, or the browser says "ready" while the
        // socket refuses; returns the role now true of both.
        internal static string Project(object entry, ModPolicyConfig policy, ModEntry me, string live)
        {
            string baseRole = BaselineRole(me.Name, live);
            me.Role = baseRole;

            string wanted = policy.RoleFor(me, baseRole);
            if (wanted != live && !TrySetRole(entry, wanted)) wanted = live;
            Projected(me.Name, baseRole, wanted);
            me.Role = wanted;
            return wanted;
        }

        // What RWT asked for before KMH projected onto it; without this, turning a setting off would read KMH's own
        // earlier write as RWT's wish and keep it for ever.
        private sealed class Baseline
        {
            public string Original;
            public string Written;
        }

        private static readonly Dictionary<string, Baseline> _baseline =
            new Dictionary<string, Baseline>(StringComparer.OrdinalIgnoreCase);

        private static string BaselineRole(string name, string live)
        {
            lock (_baseline)
            {
                // A live role KMH did not write means the owner re-synced, so it becomes the new baseline.
                if (_baseline.TryGetValue(name, out Baseline b) && b.Written == live) return b.Original;
                return live;
            }
        }

        private static void Projected(string name, string original, string written)
        {
            lock (_baseline) _baseline[name] = new Baseline { Original = original, Written = written };
        }

        internal static void ForgetBaselineForTest()
        {
            lock (_baseline) _baseline.Clear();
        }

        private static readonly HashSet<string> _warnedRoles = new HashSet<string>(StringComparer.Ordinal);

        // Reflected: RWT's ModType lives in the server assembly and differs by flavour; a build lacking the value keeps
        // RWT's role and says so, rather than KMH publishing a rule RWT will not apply.
        internal static bool TrySetRole(object entry, string role)
        {
            if (entry == null) return false;
            string member = role == ModRole.Optional ? "Optional" : "Required";
            try
            {
                Type t = entry.GetType();
                PropertyInfo p = t.GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo f = p == null ? t.GetField("Type", BindingFlags.Public | BindingFlags.Instance) : null;
                Type enumType = p?.PropertyType ?? f?.FieldType;
                if (enumType == null || !enumType.IsEnum) return false;

                // RWT's required member is not named Required on every flavour, so fall back to the enum's default value.
                object value;
                if (Enum.IsDefined(enumType, member)) value = Enum.Parse(enumType, member);
                else if (member == "Required") value = Enum.ToObject(enumType, 0);
                else return WarnRole(member);

                if (p != null && p.CanWrite) { p.SetValue(entry, value); return true; }
                if (f != null) { f.SetValue(entry, value); return true; }
                return false;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Mod profile: could not set a mod's role to {member}: {ex.Message}");
                return false;
            }
        }

        private static bool WarnRole(string member)
        {
            lock (_warnedRoles)
            {
                if (!_warnedRoles.Add(member)) return false;
            }
            ServerLog.Warn($"Mod profile: this RimWorld Together build has no '{member}' mod role, so the Colony Nexus mod "
                         + $"policy cannot change one. The server's own list is used unchanged.");
            return false;
        }

        private static void WarnUnmatchedPolicy(ModPolicyConfig policy, ModProfileSnapshot snap)
        {
            List<string> unknown = policy.UnmatchedNames(snap);
            if (unknown.Count == 0) return;
            ServerLog.Warn($"Mod profile: Config/Mods.json names {string.Join(", ", unknown.ToArray())}, which this "
                         + "server's mod list does not contain. Colony Nexus cannot add a mod to the list RimWorld Together "
                         + "checks, so these are ignored - add them in Configs/ModConfig.json first.");
        }

        private static bool? Bool(object target, string name) => Member(target, name) as bool?;

        private static string Str(object target, string name) => Member(target, name) as string;

        internal static void ApplyForTest(ModProfileSnapshot snap, long revision)
        {
            lock (_lock)
            {
                snap.Revision = revision;
                _current = snap;
                _state = new ModProfileState { Revision = revision, Hash = snap.Hash };
                _stateLoaded = true;
            }
        }
    }
}
