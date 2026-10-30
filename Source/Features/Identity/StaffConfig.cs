using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Identity
{
    // Admins are not listed here by default: RWT already knows its own, and a second list would drift from it.
    internal sealed class StaffConfig
    {
        // Every other config carries one; without it a future breaking change here has no migration hook.
        public int SchemaVersion { get; set; } = 1;

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

        public StaffBadges Badges { get; set; } = new StaffBadges();

        // Where each key of an older Staff.json lives now.
        internal static readonly IReadOnlyList<(string From, string To)> Moves = BuildMoves();

        private static IReadOnlyList<(string From, string To)> BuildMoves()
        {
            var moves = new List<(string, string)>();
            foreach (string role in new[] { "Owner", "Developer", "Admin", "Moderator", "Op" })
            {
                moves.Add(("Label" + role, "Badges." + role + ".Label"));
                moves.Add(("Color" + role, "Badges." + role + ".Color"));
            }
            return moves;
        }

        private static StaffConfig _current;
        public static StaffConfig Current => _current ?? (_current = LoadOrDefault());

        public static StaffConfig LoadOrDefault()
        {
            StaffConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.StaffConfigFile, Moves, out StaffConfig loaded) && loaded != null
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
            Badges ??= new StaffBadges();
            foreach (string role in StaffRoles.All)
            {
                StaffBadge b = Badges.For(role);
                if (b == null) continue;
                b.Label = LabelOr(b.Label, role);
                b.Color = ColorOr(b.Color, role);
            }
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
            // This server's roles, never Colony Nexus's: such a label falls back to the plain one.
            if (Moderation.KmhBrandWords.ClaimsAuthority(t)) return "";
            return t.Length > 12 ? t.Substring(0, 12) : t;
        }

        public string LabelFor(string role)
        {
            StaffBadge b = (Badges ?? new StaffBadges()).For(StaffRoles.Normalize(role));
            return b == null ? "" : LabelOr(b.Label, role);
        }

        public string ColorFor(string role)
        {
            StaffBadge b = (Badges ?? new StaffBadges()).For(StaffRoles.Normalize(role));
            if (b == null) return "";
            return string.IsNullOrWhiteSpace(b.Color) ? StaffRoles.DefaultColor(role) : b.Color.Trim().TrimStart('#');
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

        // Create only; new fields arrive through the shared boot backfill.
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.StaffConfigFile))
                JsonFileStore.Save(KmhDataPaths.StaffConfigFile, new StaffConfig());
        }
    }

    internal sealed class StaffBadge
    {
        public string Label { get; set; } = "";
        public string Color { get; set; } = "";
    }

    internal sealed class StaffBadges
    {
        public StaffBadge Owner     { get; set; } = Default(StaffRoles.Owner);
        public StaffBadge Developer { get; set; } = Default(StaffRoles.Developer);
        public StaffBadge Admin     { get; set; } = Default(StaffRoles.Admin);
        public StaffBadge Moderator { get; set; } = Default(StaffRoles.Moderator);
        public StaffBadge Op        { get; set; } = Default(StaffRoles.Op);

        private static StaffBadge Default(string role)
            => new StaffBadge { Label = StaffRoles.DefaultLabel(role), Color = StaffRoles.DefaultColor(role) };

        public StaffBadge For(string role)
        {
            switch (role)
            {
                case StaffRoles.Owner:     return Owner ??= Default(StaffRoles.Owner);
                case StaffRoles.Developer: return Developer ??= Default(StaffRoles.Developer);
                case StaffRoles.Admin:     return Admin ??= Default(StaffRoles.Admin);
                case StaffRoles.Moderator: return Moderator ??= Default(StaffRoles.Moderator);
                case StaffRoles.Op:        return Op ??= Default(StaffRoles.Op);
                default:                   return null;
            }
        }
    }
}
