using System;
using System.Collections.Generic;
using System.Reflection;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Identity
{
    // Stamped onto outbound snapshots rather than stored, so no import or hand-edited save can smuggle a role in.
    internal static class StaffRegistry
    {
        public static string RoleFor(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return StaffRoles.None;
            StaffConfig cfg = StaffConfig.Current;
            if (cfg == null || !cfg.ShowStaffBadges) return StaffRoles.None;
            return Resolve(username, cfg, cfg.TreatRwtAdminsAsStaff && IsRwtAdmin(username));
        }

        // Highest role wins, so listing someone as Owner is not outranked by their also being a live admin.
        internal static string Resolve(string username, StaffConfig cfg, bool rwtAdmin)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(username)) return StaffRoles.None;

            string role = StaffRoles.None;
            if (Contains(cfg.Ops,        username)) role = StaffRoles.Higher(role, StaffRoles.Op);
            if (Contains(cfg.Moderators, username)) role = StaffRoles.Higher(role, StaffRoles.Moderator);
            if (Contains(cfg.Admins,     username)) role = StaffRoles.Higher(role, StaffRoles.Admin);
            if (rwtAdmin)                           role = StaffRoles.Higher(role, StaffRoles.Admin);
            if (Contains(cfg.Developers, username)) role = StaffRoles.Higher(role, StaffRoles.Developer);
            if (Contains(cfg.Owners,     username)) role = StaffRoles.Higher(role, StaffRoles.Owner);
            return role;
        }

        internal static bool Contains(List<string> list, string username)
        {
            if (list == null || string.IsNullOrWhiteSpace(username)) return false;
            foreach (string s in list)
                if (!string.IsNullOrWhiteSpace(s) && string.Equals(s.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Reflection because the user store is named differently across RWT generations, and a miss degrades to "not admin".
        private static MethodInfo _lookup;
        private static bool _lookupResolved;
        private static bool _warned;

        internal static bool IsRwtAdmin(string username)
        {
            try
            {
                UserFile uf = FindUserFile(username);
                return uf != null && uf.IsAdmin;
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; ServerLog.Warn($"Staff: RWT admin lookup failed ({ex.Message}) - RWT admins will not be badged."); }
                return false;
            }
        }

        private static UserFile FindUserFile(string username)
        {
            if (!_lookupResolved)
            {
                _lookupResolved = true;
                foreach (string typeName in new[] { "UserManager", "Managers.UserManager" })
                {
                    Type t = Type.GetType(typeName) ?? FindType(typeName);
                    if (t == null) continue;
                    _lookup = t.GetMethod("GetUserFileFromName", BindingFlags.Public | BindingFlags.Static, null,
                                          new[] { typeof(string) }, null);
                    if (_lookup != null) break;
                }
                if (_lookup == null && !_warned)
                {
                    _warned = true;
                    ServerLog.Info("Staff: this RWT build exposes no user lookup - only the names listed in Config/Staff.json are badged.");
                }
            }
            return _lookup?.Invoke(null, new object[] { username }) as UserFile;
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(name, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        // The reflection probe caches its miss, which would otherwise let a suite's first call decide the whole process.
        internal static void ResetLookupForTest() { _lookup = null; _lookupResolved = false; _warned = false; }
    }
}
