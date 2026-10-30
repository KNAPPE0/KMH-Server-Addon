using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol.Patches
{
    // RWT's RemoveOldClientSessions (since 26.8.31.1) reads every connection's player name and throws on one not yet
    // logged in, dropping the joining player without a reply. Same rule here, skipping connections with no player.
    [HarmonyPatch]
    internal static class Patch_PM_Login_OldSessions
    {
#if RWT_RT
        internal static MethodBase Target() => AccessTools.Method(typeof(PM_Login), "RemoveOldClientSessions");
#else
        internal static MethodBase Target() => null;
#endif

        private static bool Prepare() => Target() != null;

        private static MethodBase TargetMethod() => Target();

        // Pure: the other connections already logged in under this exact name (RWT compares ordinally).
        internal static List<T> OlderSessions<T>(T client, string name, IEnumerable<T> connected, Func<T, string> nameOf) where T : class
        {
            var older = new List<T>();
            if (string.IsNullOrEmpty(name) || connected == null) return older;
            foreach (T c in connected)
            {
                if (c == null || ReferenceEquals(c, client)) continue;
                string other = nameOf(c);
                if (other != null && string.Equals(other, name, StringComparison.Ordinal)) older.Add(c);
            }
            return older;
        }

#if RWT_RT
        [HarmonyPrefix]
        private static bool Prefix(ServerClient client)
        {
            try
            {
                string name = client?.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(name)) return true;
                var snapshot = new List<ServerClient>(Network.ServerClients.Keys);
                // true, as RWT does: the old session's game is told it was disconnected.
                foreach (ServerClient old in OlderSessions(client, name, snapshot, c => c.GetData<UserFile>()?.Username))
                    old.Listener?.MarkForDisconnect(true);
                return false;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Login: old-session check fell back to RimWorld Together's own ({ex.GetType().Name}).");
                return true;
            }
        }
#endif
    }
}
