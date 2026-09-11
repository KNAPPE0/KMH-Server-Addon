using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Identity
{
    // Admins are not listed here by default: RWT already knows its own, and a second list would drift from it.
    internal sealed class StaffConfig
    {
        // Off also stops the roster leaving the server, for an owner who wants staff unidentifiable in game.
        public bool ShowStaffBadges { get; set; } = true;

        // Matched against KMH account names, never Discord display names, which anyone can rename into a badge.
        public List<string> Owners     { get; set; } = new List<string>();
        public List<string> Developers { get; set; } = new List<string>();
        public List<string> Admins     { get; set; } = new List<string>();
        public List<string> Moderators { get; set; } = new List<string>();
        public List<string> Ops        { get; set; } = new List<string>();

        // On by default, so the people already running the server need not edit a second list to be recognised.
        public bool TreatRwtAdminsAsStaff { get; set; } = true;

        // Plain text only, because RimWorld's font draws a Discord custom-emoji id as its literal digits.
        public string LabelOwner     { get; set; } = StaffRoles.DefaultLabel(StaffRoles.Owner);
        public string LabelDeveloper { get; set; } = StaffRoles.DefaultLabel(StaffRoles.Developer);
        public string LabelAdmin     { get; set; } = StaffRoles.DefaultLabel(StaffRoles.Admin);
        public string LabelModerator { get; set; } = StaffRoles.DefaultLabel(StaffRoles.Moderator);
        public string LabelOp        { get; set; } = StaffRoles.DefaultLabel(StaffRoles.Op);

        // The label always shows alongside, so a badge never depends on colour alone to be readable.
        public string ColorOwner     { get; set; } = StaffRoles.DefaultColor(StaffRoles.Owner);
        public string ColorDeveloper { get; set; } = StaffRoles.DefaultColor(StaffRoles.Developer);
        public string ColorAdmin     { get; set; } = StaffRoles.DefaultColor(StaffRoles.Admin);
        public string ColorModerator { get; set; } = StaffRoles.DefaultColor(StaffRoles.Moderator);
        public string ColorOp        { get; set; } = StaffRoles.DefaultColor(StaffRoles.Op);

        private static StaffConfig _current;
        public static StaffConfig Current => _current ?? (_current = LoadOrDefault());

        public static StaffConfig LoadOrDefault()
        {
            StaffConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.StaffConfigFile, out StaffConfig loaded) && loaded != null
                            ? loaded : new StaffConfig();
            cfg.Normalize();
            return cfg;
        }

        public static void Reload() { _current = null; }

        // Cleaned once here, so no reader has to re-derive what counts as a name in a hand-edited list.
        public void Normalize()
        {
            Owners     = Clean(Owners);
            Developers = Clean(Developers);
            Admins     = Clean(Admins);
            Moderators = Clean(Moderators);
            Ops        = Clean(Ops);

            // Backfilled rather than clamped, so an older file says what is actually in use; a set label is untouched.
            LabelOwner     = LabelOr(LabelOwner,     StaffRoles.Owner);
            LabelDeveloper = LabelOr(LabelDeveloper, StaffRoles.Developer);
            LabelAdmin     = LabelOr(LabelAdmin,     StaffRoles.Admin);
            LabelModerator = LabelOr(LabelModerator, StaffRoles.Moderator);
            LabelOp        = LabelOr(LabelOp,        StaffRoles.Op);

            ColorOwner     = ColorOr(ColorOwner,     StaffRoles.Owner);
            ColorDeveloper = ColorOr(ColorDeveloper, StaffRoles.Developer);
            ColorAdmin     = ColorOr(ColorAdmin,     StaffRoles.Admin);
            ColorModerator = ColorOr(ColorModerator, StaffRoles.Moderator);
            ColorOp        = ColorOr(ColorOp,        StaffRoles.Op);
        }

        private static string LabelOr(string set, string role)
        {
            string clamped = ClampLabel(set);
            return string.IsNullOrEmpty(clamped) ? StaffRoles.DefaultLabel(role) : clamped;
        }

        private static string ColorOr(string set, string role)
        {
            string t = (set ?? "").Trim().TrimStart('#');
            return t.Length == 0 ? StaffRoles.DefaultColor(role) : t;
        }

        internal static List<string> Clean(List<string> src)
        {
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var outList = new List<string>();
            if (src == null) return outList;
            foreach (string s in src)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                string t = s.Trim();
                if (seen.Add(t)) outList.Add(t);
            }
            return outList;
        }

        // Markup is refused rather than stripped, because stripping leaves debris that reads as deliberate.
        internal static string ClampLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            string t = label.Trim();
            if (t.IndexOf('<') >= 0 || t.IndexOf('>') >= 0) return "";
            return t.Length > 12 ? t.Substring(0, 12) : t;
        }

        public string LabelFor(string role)
        {
            string set;
            switch (StaffRoles.Normalize(role))
            {
                case StaffRoles.Owner:     set = LabelOwner;     break;
                case StaffRoles.Developer: set = LabelDeveloper; break;
                case StaffRoles.Admin:     set = LabelAdmin;     break;
                case StaffRoles.Moderator: set = LabelModerator; break;
                case StaffRoles.Op:        set = LabelOp;        break;
                default: return "";
            }
            string clamped = ClampLabel(set);
            return string.IsNullOrEmpty(clamped) ? StaffRoles.DefaultLabel(role) : clamped;
        }

        public string ColorFor(string role)
        {
            string set;
            switch (StaffRoles.Normalize(role))
            {
                case StaffRoles.Owner:     set = ColorOwner;     break;
                case StaffRoles.Developer: set = ColorDeveloper; break;
                case StaffRoles.Admin:     set = ColorAdmin;     break;
                case StaffRoles.Moderator: set = ColorModerator; break;
                case StaffRoles.Op:        set = ColorOp;        break;
                default: return "";
            }
            return string.IsNullOrWhiteSpace(set) ? StaffRoles.DefaultColor(role) : set.Trim().TrimStart('#');
        }

        // One packed field instead of ten keys, and an older client simply ignores the one it does not know.
        public string BadgeWire()
        {
            if (!ShowStaffBadges) return "";
            var sb = new System.Text.StringBuilder();
            foreach (string role in StaffRoles.All)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(role).Append(':').Append(LabelFor(role)).Append(':').Append(ColorFor(role));
            }
            return sb.ToString();
        }

        // Sent with the hello so a historic message badges correctly even when its author is offline.
        public string RoleWire()
        {
            if (!ShowStaffBadges) return "";

            var seen = new SortedDictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            void Add(List<string> names)
            {
                foreach (string n in names ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    string u = n.Trim();
                    // Resolved rather than assigned, so a name in several lists cannot depend on walk order.
                    seen[u] = StaffRegistry.Resolve(u, this, rwtAdmin: false);
                }
            }
            Add(Owners); Add(Developers); Add(Admins); Add(Moderators); Add(Ops);

            var sb = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, string> kv in seen)
            {
                if (string.IsNullOrEmpty(kv.Value) || kv.Value == StaffRoles.None) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key).Append(':').Append(kv.Value);
            }
            return sb.ToString();
        }

        // Topped up on later boots, because an owner can only tune a setting they can actually see in the file.
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.StaffConfigFile))
            {
                JsonFileStore.Save(KmhDataPaths.StaffConfigFile, new StaffConfig());
                return;
            }

            try
            {
                string existing = System.IO.File.ReadAllText(KmhDataPaths.StaffConfigFile);
                StaffConfig cfg = LoadOrDefault();
                if (JsonFileStore.ToJson(cfg).Trim() != existing.Trim())
                    JsonFileStore.Save(KmhDataPaths.StaffConfigFile, cfg);
            }
            catch { }
        }
    }
}
