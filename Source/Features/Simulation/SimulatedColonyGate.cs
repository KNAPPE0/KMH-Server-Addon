using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Simulation
{
    // A simulated player never answers, so RWT sees it offline for gifts, trades, aid and events and refunds them itself.
    internal static class SimulatedColonyGate
    {
        private static readonly string[] Generations = { "RTServer", "GameServer" };
        private static readonly ConcurrentDictionary<string, DateTime> _logged = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Swapped by the self-test; RWT's own lookups otherwise.
        internal static Func<int, string> OwnerAt = RwtOwnerAt;
        internal static Action<object> SendUnavailable = RwtSendUnavailable;

        internal static Type RwtType(string relative)
        {
            foreach (string g in Generations)
            {
                Type t = Type.GetType($"{g}.{relative}, {g}", throwOnError: false) ?? AccessTools.TypeByName($"{g}.{relative}");
                if (t != null) return t;
            }
            return null;
        }

        private static Type PacketManager(string name) => RwtType("PacketManagers." + name) ?? RwtType("PacketManager." + name);

        internal static MethodInfo ConnectedTarget()
        {
            MethodInfo m = RwtType("Managers.UserManagerH")?.GetMethod("CheckIfUserIsConnected",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            return m != null && m.ReturnType == typeof(bool) ? m : null;
        }

        internal static MethodInfo SessionTarget()
        {
            Type pm = PacketManager("PM_Synchronous");
            MethodInfo m = pm == null ? null : AccessTools.Method(pm, "TryStartSynchronousSession");
            ParameterInfo[] ps = m?.GetParameters();
            return m != null && m.IsStatic && ps.Length == 2 && !ps[0].ParameterType.IsValueType && !ps[1].ParameterType.IsValueType ? m : null;
        }

        internal static void Install(Harmony harmony)
        {
            Apply(harmony, ConnectedTarget(), nameof(OfflineIfSimulated), "UserManagerH.CheckIfUserIsConnected",
                  "gifts, trades, aid and events sent to a simulated player while it is online are lost, not returned");
            Apply(harmony, SessionTarget(), nameof(UnavailableIfSimulated), "PM_Synchronous.TryStartSynchronousSession",
                  "an online visit or raid on a simulated player waits for an answer that never comes");
        }

        private static void Apply(Harmony harmony, MethodInfo target, string patch, string what, string without)
        {
            try
            {
                if (target == null)
                {
                    // Only a server running simulated players needs this, so the rest are not warned.
                    string line = $"Simulated players: this RWT build has no {what} - {without}.";
                    if (SimulatedAccounts.All().Count > 0) ServerLog.Warn(line); else ServerLog.Diag(line);
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(SimulatedColonyGate), patch));
            }
            catch (Exception ex) { ServerLog.Warn($"Simulated players: patching {what} threw ({ex.Message}) - {without}."); }
        }

        // Not "Prefix": applied by hand, and PatchAll would take that name as a patch.
        internal static bool OfflineIfSimulated(string __0, ref bool __result)
        {
            try
            {
                if (!SimulatedAccounts.IsSimulated(__0)) return true;
                __result = false;
                Note(__0, "offline", $"Simulated players: RWT asked whether {__0} is online and was told no, so RWT returns or refunds the gift, trade, aid or event.");
                return false;
            }
            catch { return true; }
        }

        internal static bool UnavailableIfSimulated(object __0, object __1)
        {
            try
            {
                string owner = OwnerAt(TileOf(__1));
                if (!SimulatedAccounts.IsSimulated(owner)) return true;
                SendUnavailable(__0);
                Note(owner, "session", $"Simulated players: an online visit or raid on {owner}'s colony was answered \"unavailable\".");
                return false;
            }
            catch (Exception ex)
            {
                Note("", "session-error", $"Simulated players: the visit/raid check fell back to RWT's own ({ex.GetType().Name}).");
                return true;
            }
        }

        private static void Note(string who, string kind, string line)
        {
            DateTime now = DateTime.UtcNow;
            string key = kind + ":" + who;
            if (_logged.TryGetValue(key, out DateTime last) && now - last < TimeSpan.FromHours(1)) return;
            _logged[key] = now;
            ServerLog.Info(line);
        }

        internal static int TileOf(object packet)
        {
            object v = packet?.GetType().GetProperty("ToTile", BindingFlags.Public | BindingFlags.Instance)?.GetValue(packet);
            return v is int t ? t : -1;
        }

        private static string RwtOwnerAt(int tile)
        {
            if (tile < 0) return null;
            MethodInfo get = PacketManager("PM_Settlements")?.GetMethod("GetSettlementFileFromTile", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            object file = get?.Invoke(null, new object[] { tile });
            return file?.GetType().GetProperty("Username")?.GetValue(file) as string;
        }

        private static void RwtSendUnavailable(object client)
        {
            MethodInfo send = RwtType("Managers.ResponseShortcutManager")?.GetMethod("SendUserUnavailablePacket", BindingFlags.Public | BindingFlags.Static);
            if (send == null) throw new MissingMethodException("ResponseShortcutManager.SendUserUnavailablePacket");
            send.Invoke(null, new[] { client });
        }
    }
}
