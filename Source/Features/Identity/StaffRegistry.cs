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

        // Deliberately not RoleFor: that answers "what badge do they show", which an owner can switch off without
        // ceasing to be the owner. Authority must not be readable from a display setting.
        public static bool IsOwner(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            StaffConfig cfg = StaffConfig.Current;
            if (cfg == null) return false;
            return Contains(cfg.Owners, username);
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

        // Only the config side is trimmed: RWT keeps "Name " and "Name" as two accounts, so trimming the authenticated
        // name would hand the owner's authority to whoever registered the padded one.
        internal static bool Contains(List<string> list, string username)
        {
            if (list == null || string.IsNullOrWhiteSpace(username)) return false;
            foreach (string s in list)
                if (!string.IsNullOrWhiteSpace(s) && SameAccount(s.Trim(), username))
                    return true;
            return false;
        }

        // RWT's own account rule (UserManager compares Username.ToLower()); OrdinalIgnoreCase would merge "Iris" and
        // "iris", which a Turkish or Azeri host keeps as two accounts.
        internal static bool SameAccount(string a, string b)
            => a != null && b != null && string.Equals(a.ToLower(), b.ToLower(), StringComparison.Ordinal);

        internal static readonly IEqualityComparer<string> AccountComparer = new AccountNameComparer();

        private sealed class AccountNameComparer : IEqualityComparer<string>
        {
            public bool Equals(string a, string b) => SameAccount(a, b);
            public int GetHashCode(string s) => s == null ? 0 : StringComparer.Ordinal.GetHashCode(s.ToLower());
        }

        internal readonly struct RwtAccount
        {
            public readonly bool Admin, Banned;
            public RwtAccount(bool admin, bool banned) { Admin = admin; Banned = banned; }
        }

        // RWT reads every account file to answer one name and badges ask per player, so one read a minute serves them all.
        private static readonly TimeSpan AccountsMaxAge = TimeSpan.FromMinutes(1);
        private static readonly object AccountsLock = new object();
        private static Dictionary<string, RwtAccount> _accounts;
        private static long _accountsAtTicks;
        private static Dictionary<string, RwtAccount> _accountsForTest;
        private static bool _warned;

        internal static bool IsRwtAdmin(string username) => Account(username)?.Admin == true;

        internal static bool IsRwtBanned(string username) => Account(username)?.Banned == true;

        private static RwtAccount? Account(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            Dictionary<string, RwtAccount> all = Accounts(AccountsMaxAge);
            return all != null && all.TryGetValue(username, out RwtAccount a) ? a : null;
        }

        // Every RWT account by its exact name, as RWT signs one in; null when this RWT exposes no account list.
        internal static Dictionary<string, RwtAccount> Accounts(TimeSpan maxAge)
        {
            if (_accountsForTest != null) return _accountsForTest;
            lock (AccountsLock)
            {
                long now = DateTime.UtcNow.Ticks;
                if (_accounts != null && now - _accountsAtTicks < maxAge.Ticks) return _accounts;
                try
                {
                    MethodInfo all = RwtCompat.UserStoreMethod("GetAllUserFiles", 0);
                    if (all == null)
                    {
                        if (!_warned) { _warned = true; ServerLog.Info("Staff: this RWT build exposes no account list - only the names listed in Config/Staff.json are badged."); }
                        return null;
                    }
                    var map = new Dictionary<string, RwtAccount>(StringComparer.Ordinal);
                    if (all.Invoke(null, null) is System.Collections.IEnumerable files)
                        foreach (object o in files)
                            if (o is UserFile uf && !string.IsNullOrEmpty(uf.Username)) map[uf.Username] = new RwtAccount(uf.IsAdmin, uf.IsBanned);
                    _accounts = map;
                    _accountsAtTicks = now;
                    return map;
                }
                catch (Exception ex)
                {
                    if (!_warned) { _warned = true; ServerLog.Warn($"Staff: RWT account list failed ({ex.GetBaseException().Message}) - RWT admins will not be badged."); }
                    return _accounts;
                }
            }
        }

        internal static void AccountsForTest(Dictionary<string, RwtAccount> accounts) => _accountsForTest = accounts;

        // The table is cached, which would otherwise let a suite's first call decide the whole process.
        internal static void ResetLookupForTest() { lock (AccountsLock) { _accounts = null; _accountsAtTicks = 0; _warned = false; } }
    }
}
