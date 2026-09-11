using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using KMHServerAddon.Features.Identity;

namespace KMHServerAddon.Maintenance
{
    // A badge is only worth showing if it cannot be claimed.
    internal static class KmhStaffIdentitySelfTest
    {
        private static string ReadSource(string relative)
        {
            try
            {
                // Assembly.Location is empty inside a single-file bundle, which is how the addon ships.
                string dir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/'));
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Source", relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    candidate = Path.Combine(dir, relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            // "Who outranks whom" is answered once, so every surface agrees.
            r.Add(("roles: the ladder is strictly ordered owner > dev > admin > mod > op",
                   StaffRoles.Priority(StaffRoles.Owner)     > StaffRoles.Priority(StaffRoles.Developer)
                   && StaffRoles.Priority(StaffRoles.Developer) > StaffRoles.Priority(StaffRoles.Admin)
                   && StaffRoles.Priority(StaffRoles.Admin)     > StaffRoles.Priority(StaffRoles.Moderator)
                   && StaffRoles.Priority(StaffRoles.Moderator) > StaffRoles.Priority(StaffRoles.Op)
                   && StaffRoles.Priority(StaffRoles.Op)        > StaffRoles.Priority(StaffRoles.None), ""));
            r.Add(("roles: an unknown role outranks nothing",
                   StaffRoles.Priority("archmage") == 0 && StaffRoles.Normalize("archmage") == StaffRoles.None, ""));
            r.Add(("roles: casing and aliases normalise to one value",
                   StaffRoles.Normalize("  ADMIN ") == StaffRoles.Admin
                   && StaffRoles.Normalize("Mod") == StaffRoles.Moderator
                   && StaffRoles.Normalize("dev") == StaffRoles.Developer, ""));
            r.Add(("roles: every listed role has a default label and colour",
                   AllHaveDefaults(), ""));

            // RWT's answer is passed in, so the rule is testable without a live server.
            var cfg = new StaffConfig
            {
                Owners     = new List<string> { "Knappe" },
                Developers = new List<string> { "devperson" },
                Moderators = new List<string> { "modperson" },
                Ops        = new List<string> { "opperson" },
            };
            cfg.Normalize();

            r.Add(("staff: a listed owner resolves to owner",
                   StaffRegistry.Resolve("Knappe", cfg, false) == StaffRoles.Owner, ""));
            r.Add(("staff: matching is case-insensitive",
                   StaffRegistry.Resolve("KNAPPE", cfg, false) == StaffRoles.Owner
                   && StaffRegistry.Resolve("modperson", cfg, false) == StaffRoles.Moderator, ""));
            r.Add(("staff: an unlisted non-admin is not staff",
                   StaffRegistry.Resolve("randomer", cfg, false) == StaffRoles.None, ""));
            r.Add(("staff: a live RWT admin is badged without being listed",
                   StaffRegistry.Resolve("randomer", cfg, true) == StaffRoles.Admin, ""));

            // An owner who is also a live admin must stay an owner, which is why Higher() exists.
            r.Add(("staff: being an admin never demotes a higher listed role",
                   StaffRegistry.Resolve("Knappe", cfg, true) == StaffRoles.Owner
                   && StaffRegistry.Resolve("devperson", cfg, true) == StaffRoles.Developer, ""));
            r.Add(("staff: an admin outranks a moderator listing on the same name",
                   StaffRegistry.Resolve("modperson", cfg, true) == StaffRoles.Admin, ""));

            r.Add(("staff: an empty or missing name is never staff",
                   StaffRegistry.Resolve("", cfg, true) == StaffRoles.None
                   && StaffRegistry.Resolve(null, cfg, true) == StaffRoles.None
                   && StaffRegistry.Resolve("   ", cfg, true) == StaffRoles.None, ""));

            // The owner's master switch has to mean it - not "badges hidden but the roster still ships".
            var off = new StaffConfig { ShowStaffBadges = false, Owners = new List<string> { "Knappe" } };
            off.Normalize();
            r.Add(("staff: badges off means no role leaves the server and no wire payload",
                   off.BadgeWire() == "", ""));

            // These lists are hand-edited, so blanks and duplicates are the normal case.
            var messy = StaffConfig.Clean(new List<string> { " Knappe ", "knappe", "", null, "   ", "Other" });
            r.Add(("staff config: blanks are dropped and duplicates collapse case-insensitively",
                   messy.Count == 2 && messy[0] == "Knappe" && messy[1] == "Other", string.Join(",", messy)));

            // A badge is built into rich text on every chat line, so markup is refused rather than stripped.
            r.Add(("staff config: a label carrying markup falls back to the default",
                   StaffConfig.ClampLabel("<b>Boss</b>") == "" && StaffConfig.ClampLabel("<color=red>") == "", ""));
            r.Add(("staff config: an over-long label is cut, not accepted whole",
                   StaffConfig.ClampLabel("ABCDEFGHIJKLMNOP").Length == 12, ""));
            var blank = new StaffConfig();
            r.Add(("staff config: a blank label falls back to KMH's own",
                   blank.LabelFor(StaffRoles.Moderator) == "Mod" && blank.LabelFor(StaffRoles.Owner) == "Owner", ""));
            r.Add(("staff config: an owner's own label is used as set",
                   new StaffConfig { LabelModerator = "Warden" }.LabelFor(StaffRoles.Moderator) == "Warden", ""));
            r.Add(("staff config: a non-role has no label and no colour",
                   blank.LabelFor("archmage") == "" && blank.ColorFor("archmage") == "", ""));

            string wire = new StaffConfig().BadgeWire();
            r.Add(("staff wire: every role is published with a label and a colour",
                   Regex.Matches(wire, @"(^|;)[a-z]+:[^:;]+:[0-9A-Fa-f]{6}").Count == StaffRoles.All.Length,
                   wire));
            r.Add(("staff wire: no role label can carry markup into a chat line",
                   wire.IndexOf('<') < 0 && wire.IndexOf('>') < 0, ""));

            string store = ReadSource(Path.Combine("Features", "PlayerStats", "PlayerStatsStore.cs"));
            if (store == null)
            {
                r.Add(("staff: store source not available (shipped build) - stamping checks skipped", true, ""));
            }
            else
            {
                r.Add(("staff: the role is derived per snapshot from the registry",
                       Regex.IsMatch(store, @"e\.StaffRole\s*=\s*Identity\.StaffRegistry\.RoleFor\("), ""));
                // Never copied out of the stored row, or a hand-edited PlayerStats.json could grant a badge.
                r.Add(("staff: the role is never copied from the persisted player record",
                       !Regex.IsMatch(store, @"StaffRole\s*=\s*e\.StaffRole"), ""));
            }

            string handler = ReadSource(Path.Combine("Features", "PlayerStats", "PlayerStatsHandler.cs"));
            if (handler != null)
                r.Add(("staff: no inbound client report is allowed to set a role",
                       !Regex.IsMatch(handler, @"StaffRole\s*="), ""));

            return r;
        }

        private static bool AllHaveDefaults()
        {
            foreach (string role in StaffRoles.All)
                if (string.IsNullOrEmpty(StaffRoles.DefaultLabel(role)) || string.IsNullOrEmpty(StaffRoles.DefaultColor(role)))
                    return false;
            return true;
        }
    }
}
