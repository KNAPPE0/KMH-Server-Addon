using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.AdminCommands
{
    internal static class KmhConfigInspect
    {
        public readonly struct FieldDiff
        {
            public readonly string Name;
            public readonly string Value;
            public readonly string Default;
            public readonly bool   Changed;
            public FieldDiff(string name, string value, string def, bool changed) { Name = name; Value = value; Default = def; Changed = changed; }
        }

        // Compared as JSON so scalars, arrays and nested objects all work without per-type handling.
        public static List<FieldDiff> Diff(object live, object def)
        {
            var outp = new List<FieldDiff>();
            if (live == null || def == null) return outp;
            foreach (PropertyInfo p in live.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;   // derived, not real config
                string lv, dv;
                try { lv = JsonConvert.SerializeObject(p.GetValue(live)); dv = JsonConvert.SerializeObject(p.GetValue(def)); }
                catch { continue; }
                bool changed = lv != dv;
                if (IsSensitive(p.Name))   // never print a bot token / secret; show only whether it's set
                    outp.Add(new FieldDiff(p.Name, changed ? "***set***" : "(unset)", "(unset)", changed));
                else
                    outp.Add(new FieldDiff(p.Name, Short(lv), Short(dv), changed));
            }
            return outp;
        }

        private static bool IsSensitive(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("token") || n.Contains("secret") || n.Contains("password") || n.Contains("webhook") || n.Contains("apikey");
        }

        private static string Short(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            if (json[0] == '"' && json.Length >= 2 && json[json.Length - 1] == '"') return json.Substring(1, json.Length - 2);
            if (json[0] == '[') { try { int n = JArray.Parse(json).Count; return $"[{n} item{(n == 1 ? "" : "s")}]"; } catch { return "[list]"; } }
            if (json[0] == '{') return "{…}";   // never dump a nested object's contents - it can hold secrets (e.g. Discord Bot.Token)
            return json.Length > 40 ? json.Substring(0, 39) + "…" : json;
        }

        // Deferred because a live getter can hit disk, which must not happen while this table is being built.
        private static readonly (string Area, Func<object> Live, Func<object> Def)[] Areas =
        {
            ("economy",     () => Features.Economy.EconomyConfig.Current,        () => new Features.Economy.EconomyConfig()),
            ("world",       () => Features.World.WorldConfig.Current,            () => new Features.World.WorldConfig()),
            ("sites",       () => Features.Sites.SitesConfig.Current,            () => new Features.Sites.SitesConfig()),
            ("discord",     () => Features.Discord.DiscordConfig.LoadOrDefault(),() => new Features.Discord.DiscordConfig()),
            ("transport",   () => Features.Transport.TransportConfig.Current,    () => new Features.Transport.TransportConfig()),
            ("features",    () => Features.FeaturesConfig.Current,               () => new Features.FeaturesConfig()),
            ("reputation",  () => Features.Reputation.ReputationConfig.Current,  () => new Features.Reputation.ReputationConfig()),
            ("quests",      () => Features.Quests.QuestsConfig.Current,          () => new Features.Quests.QuestsConfig()),
            ("enforcement", () => Features.Enforcement.EnforcementConfig.Current,() => new Features.Enforcement.EnforcementConfig()),
            ("maintenance", () => Maintenance.MaintenanceConfig.Current,         () => new Maintenance.MaintenanceConfig()),
            ("chat",        () => Features.Chat.ChatConfig.Current,              () => new Features.Chat.ChatConfig()),
            ("media",       () => Features.Media.MediaConfig.Current,            () => new Features.Media.MediaConfig()),
            ("staff",       () => Features.Identity.StaffConfig.Current,         () => new Features.Identity.StaffConfig()),
            ("mail",        () => Features.Mail.MailConfig.Current,              () => new Features.Mail.MailConfig()),
            ("frontier",    () => Features.Frontier.FrontierConfig.Current,      () => new Features.Frontier.FrontierConfig()),
        };

        public static void Run(string[] args, Action<string> reply)
        {
            string area = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : null;
            bool all = args != null && Array.Exists(args, a => string.Equals(a, "all", StringComparison.OrdinalIgnoreCase));

            if (area == null || area == "all")
            {
                reply("=== KMH config (fields you've changed from default) ===");
                reply("'kmh config <area>' shows one area's changes; add 'all' to see every field.");
                foreach ((string a, Func<object> live, Func<object> def) in Areas)
                {
                    List<FieldDiff> d = Diff(SafeGet(live), def());
                    int changed = 0; foreach (FieldDiff f in d) if (f.Changed) changed++;
                    reply($"  {a,-12} {changed,2} changed / {d.Count}");
                }
                return;
            }

            foreach ((string a, Func<object> live, Func<object> def) in Areas)
                if (a == area)
                {
                    List<FieldDiff> d = Diff(SafeGet(live), def());
                    int changed = 0; foreach (FieldDiff f in d) if (f.Changed) changed++;
                    reply($"=== {a} ({changed} of {d.Count} changed from default){(all ? " - all fields" : "")} ===");
                    foreach (FieldDiff f in d)
                    {
                        if (!all && !f.Changed) continue;
                        reply(f.Changed ? $"  * {f.Name} = {f.Value}   (default {f.Default})" : $"    {f.Name} = {f.Value}");
                    }
                    if (!all && changed == 0) reply("  (all defaults - nothing changed)");
                    ModeNote(a, reply);
                    if (!all) reply($"  'kmh config {a} all' shows all {d.Count} fields.");
                    return;
                }

            var names = new List<string>();
            foreach ((string a, Func<object> _, Func<object> __) in Areas) names.Add(a);
            reply($"No config area '{area}'. Areas: {string.Join(", ", names)}.");
        }

        // The fee and cooldown fields are read only under EconomyMode=Custom, so under a preset they are not in force.
        private static void ModeNote(string area, Action<string> reply)
        {
            if (area != "economy") return;
            try
            {
                Features.Economy.EconomyPolicy p = Features.Economy.EconomyConfig.Current.ResolvePolicy();
                reply($"  in force: mode {p.Mode} - {p.DepositFeePct:0.##}%/{p.WithdrawFeePct:0.##}% fees, "
                    + $"{p.DepositCooldownSec}s/{p.WithdrawCooldownSec}s cooldowns, personal {p.PersonalAccess}, guild {p.GuildAccess}");
                if (!string.Equals(p.Mode, "Custom", StringComparison.OrdinalIgnoreCase))
                    reply("  the fee/cooldown/cap/raid fields above apply only to EconomyMode=Custom.");
            }
            catch { }
        }

        private static object SafeGet(Func<object> f) { try { return f(); } catch { return null; } }
    }
}
