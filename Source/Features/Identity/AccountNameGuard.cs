using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Identity
{
    // Player data is keyed case-insensitively; RWT tells accounts apart with the host's ToLower(). Names only one calls
    // equal ("µ"/"μ", "ς"/"σ", "I"/"i" on a Turkish host) would be two RWT accounts on one vault.
    internal static class AccountNameGuard
    {
        private static readonly TimeSpan LoginMaxAge = TimeSpan.FromMinutes(1);

        internal static bool Install(Harmony harmony)
        {
            MethodInfo exists = RwtCompat.UserStoreMethod("CheckIfUserExists", 2);
            if (exists == null || exists.ReturnType != typeof(bool) || exists.GetParameters()[0].ParameterType != typeof(ServerClient))
            {
                ServerLog.Warn("Accounts: RWT's account check was not found - a look-alike of an existing account name is not refused.");
                return false;
            }
            harmony.Patch(exists, postfix: new HarmonyMethod(typeof(AccountNameGuard), nameof(AfterExistsCheck)));
            return true;
        }

        // "Exists" sends RWT down its sign-in path, which needs that account's exact name and password, so it is refused.
        private static void AfterExistsCheck(object[] __args, ref bool __result)
        {
            if (__result) return;
            try
            {
                string name = LoginName(__args?.Length > 1 ? __args[1] : null);
                string clash = ClashWith(name, StaffRegistry.Accounts(TimeSpan.Zero)?.Keys);
                if (clash == null) return;
                ServerLog.Warn($"Accounts: refused a new account '{name}' - Colony Nexus would keep it and '{clash}' as one player.");
                __result = true;
            }
            catch (Exception ex) { ServerLog.Warn($"Accounts: the look-alike check failed ({ex.GetType().Name})."); }
        }

        internal static bool AfterExistsCheckForTest(object login, bool rwtSaid)
        {
            bool result = rwtSaid;
            AfterExistsCheck(new object[] { null, login }, ref result);
            return result;
        }

        // RWT 26.6 named it _username; RWT 26.7 and later, Username.
        private static string LoginName(object login)
        {
            if (login == null) return null;
            Type t = login.GetType();
            foreach (string member in new[] { "Username", "_username" })
            {
                PropertyInfo p = t.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(string)) return p.GetValue(login) as string;
                FieldInfo f = t.GetField(member, BindingFlags.Public | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(string)) return f.GetValue(login) as string;
            }
            return null;
        }

        // The other account this name would share player data with: equal under either fold the stores use.
        internal static string ClashWith(string name, IEnumerable<string> accounts)
        {
            if (string.IsNullOrEmpty(name) || accounts == null) return null;
            string folded = name.ToLowerInvariant();
            foreach (string other in accounts)
                if (other != null && !string.Equals(other, name, StringComparison.Ordinal)
                    && (string.Equals(other, name, StringComparison.OrdinalIgnoreCase) || other.ToLowerInvariant() == folded))
                    return other;
            return null;
        }

        // Pairs made before this guard existed: neither is picked as the owner, both wait for the server owner.
        internal static bool RefuseIfShared(ServerClient client)
        {
            if (client == null) return false;
            try
            {
                string username = client.GetData<UserFile>()?.Username ?? "";
                string other = ClashWith(username, StaffRegistry.Accounts(LoginMaxAge)?.Keys);
                if (other == null) return false;
                ServerLog.Warn($"Accounts: held '{username}' at login - Colony Nexus keeps it and '{other}' as one player. "
                             + "Remove one of the two from RimWorld Together's Users folder to let the other play.");
                client.Listener?.MarkForDisconnect();
                return true;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Accounts: the shared-account check failed, admitting ({ex.GetType().Name}).");
                return false;
            }
        }
    }
}
