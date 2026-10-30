using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol.Patches
{
    internal static class JoinDiagnostics
    {
        internal sealed class Event
        {
            public long Id; public DateTime Utc; public string Ip = ""; public string User = ""; public string What = ""; public string Detail = "";
        }

        private const int Capacity = 200, LinesPerMinute = 30;
        private static readonly object Gate = new object();
        private static readonly LinkedList<Event> Recent = new LinkedList<Event>();
        private static readonly ConditionalWeakTable<object, string> ModDetail = new ConditionalWeakTable<object, string>();
        private static long _next;
        private static DateTime _windowStart = DateTime.MinValue;
        private static int _windowLines, _suppressed;

        internal const int MaxUser = 64, MaxWhat = 200, MaxDetail = 300, MaxModName = 64;

        internal static void Record(string ip, string user, string what, string detail)
        {
            var e = new Event { Utc = DateTime.UtcNow, Ip = MaskIp(ip), User = Clip(user, MaxUser), What = Clip(what, MaxWhat), Detail = Clip(detail, MaxDetail) };
            bool log;
            lock (Gate)
            {
                e.Id = ++_next;
                Recent.AddLast(e);
                while (Recent.Count > Capacity) Recent.RemoveFirst();
                if (e.Utc - _windowStart > TimeSpan.FromMinutes(1))
                {
                    if (_suppressed > 0) ServerLog.Info($"[join] {_suppressed} more join line(s) were not logged in the last minute; 'kmh joins' lists them.");
                    _windowStart = e.Utc; _windowLines = 0; _suppressed = 0;
                }
                log = _windowLines++ < LinesPerMinute;
                if (!log) _suppressed++;
            }
            if (log) ServerLog.Info("[join] " + Format(e));
        }

        internal static string Format(Event e)
            => $"J{e.Id} {e.Utc:HH:mm:ss}Z {(e.User.Length > 0 ? e.User : "(no name yet)")} from {e.Ip}: {e.What}{(e.Detail.Length > 0 ? " - " + e.Detail : "")}";

        internal static List<Event> Snapshot(int max)
        {
            lock (Gate) return Recent.Reverse().Take(Math.Max(1, max)).Reverse().ToList();
        }

        internal static void ResetForTest() { lock (Gate) { Recent.Clear(); _next = 0; _windowStart = DateTime.MinValue; _windowLines = 0; _suppressed = 0; } }

        internal static void Command(string[] args, Action<string> reply)
        {
            int n = args != null && args.Length > 1 && int.TryParse(args[1], out int v) ? v : 20;
            List<Event> list = Snapshot(n);
            reply($"=== Join attempts: last {list.Count} of the {Capacity} kept since the server started ===");
            if (list.Count == 0) reply("  none refused or adjusted yet. RimWorld Together's own [Connect]/[Log in] lines show successful joins.");
            foreach (Event e in list) reply("  " + Format(e));
        }

        // Player-supplied text reaches the console and 'kmh joins', so it is kept to one bounded line.
        internal static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(Math.Min(s.Length, max + 1));
            foreach (char ch in s) { if (sb.Length > max) break; sb.Append(char.IsControl(ch) ? ' ' : ch); }
            return Util.KmhSafe.Cap(sb.ToString(), max);
        }

        // An IPv4 address keeps its /24 and an IPv6 one its /48: enough to tell two players apart, not to find one.
        internal static string MaskIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return "?";
            string s = ip.Trim();
            System.Net.IPAddress a;
            if (!System.Net.IPAddress.TryParse(s, out a))
            {
                if (!System.Net.IPEndPoint.TryParse(s, out System.Net.IPEndPoint ep)) return "?";
                a = ep.Address;
            }
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            byte[] b = a.GetAddressBytes();
            if (b.Length == 4) return $"{b[0]}.{b[1]}.{b[2]}.x";
            return $"{(b[0] << 8) | b[1]:x}:{(b[2] << 8) | b[3]:x}:{(b[4] << 8) | b[5]:x}::/48";
        }

        // A Steam Workshop file id: what RimWorld Together stores as ModID for a Workshop mod.
        internal static bool IsWorkshopId(string id)
            => !string.IsNullOrEmpty(id) && id.Length >= 6 && id.Length <= 12 && id.All(char.IsDigit);

        internal static List<(int index, string from, string to)> Renames(IReadOnlyList<(string name, string id)> server,
                                                                        IReadOnlyList<(string name, string id)> client)
        {
            var renames = new List<(int, string, string)>();
            if (server == null || client == null) return renames;
            var names = new HashSet<string>(server.Where(s => s.name != null).Select(s => s.name), StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < client.Count; i++)
            {
                (string name, string id) c = client[i];
                if (c.name != null && names.Contains(c.name)) continue;
                if (!IsWorkshopId(c.id?.Trim())) continue;
                var hits = server.Where(s => string.Equals(s.id?.Trim(), c.id.Trim(), StringComparison.Ordinal)).ToList();
                if (hits.Count != 1 || hits[0].name == null || !claimed.Add(c.id.Trim())) continue;
                if (client.Any(o => string.Equals(o.name, hits[0].name, StringComparison.Ordinal))) continue;
                renames.Add((i, c.name, hits[0].name));
            }
            return renames;
        }

        // RimWorld Together's two presence rules, by name: every required mod present, nothing it does not list.
        internal static (List<string> missing, List<string> notAllowed) Mismatch(IReadOnlyList<(string name, bool required)> server,
                                                                                IReadOnlyList<string> client)
        {
            var have = new HashSet<string>((client ?? new string[0]).Where(n => n != null), StringComparer.Ordinal);
            var listed = new HashSet<string>((server ?? new (string, bool)[0]).Where(s => s.name != null).Select(s => s.name), StringComparer.Ordinal);
            List<string> missing = (server ?? new (string, bool)[0]).Where(s => s.required && s.name != null && !have.Contains(s.name)).Select(s => s.name).ToList();
            List<string> notAllowed = (client ?? new string[0]).Where(n => n != null && !listed.Contains(n)).Distinct().ToList();
            return (missing, notAllowed);
        }

        internal static string Describe(List<string> missing, List<string> notAllowed)
        {
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("missing required: " + string.Join(", ", missing.Take(8).Select(n => Clip(n, MaxModName))) + (missing.Count > 8 ? $" (+{missing.Count - 8})" : ""));
            if (notAllowed.Count > 0) parts.Add("not on this server's list: " + string.Join(", ", notAllowed.Take(8).Select(n => Clip(n, MaxModName))) + (notAllowed.Count > 8 ? $" (+{notAllowed.Count - 8})" : ""));
            return parts.Count == 0 ? "" : string.Join("; ", parts);
        }

        internal static void RememberModDetail(object client, string detail)
        {
            if (client == null || string.IsNullOrEmpty(detail)) return;
            ModDetail.AddOrUpdate(client, detail);
        }

        internal static string TakeModDetail(object client)
        {
            if (client == null || !ModDetail.TryGetValue(client, out string d)) return "";
            ModDetail.Remove(client);
            return d;
        }

        // What failed and where, in one bounded line; never the stack.
        internal static string ErrorText(Exception e)
        {
            if (e == null) return "";
            Exception inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            MethodBase at = inner.TargetSite;
            return Clip($"{inner.GetType().Name}: {inner.Message}{(at != null ? $" (in {at.DeclaringType?.Name}.{at.Name})" : "")}", MaxDetail);
        }

        // The socket going away is the network; a file or data error inside a packet handler is RimWorld Together failing.
        internal static bool IsConnectionLoss(Exception e)
            => e is System.Net.Sockets.SocketException || e is ObjectDisposedException || e?.InnerException is System.Net.Sockets.SocketException
               || e?.TargetSite?.DeclaringType?.Namespace == "System.Net.Sockets";

        internal static string ReasonText(string response, int connected, int loggedIn, int max) => response switch
        {
            "Full"       => $"refused: server full ({connected}/{max} connections, {loggedIn} logged in); RimWorld Together sends this before the handshake, so the player usually sees only a timeout",
            "NoWorld"    => "refused: no world yet, and one player is already connected (only the world creator may join first)",
            "Mods"       => "refused: mod list does not match",
            "ModConfigs" => "refused: mod settings do not match",
            "ModOrder"   => "refused: mod load order does not match",
            "Version"    => "refused: RimWorld Together version does not match",
            _            => "refused: " + response,
        };
    }

    [HarmonyPatch]
    internal static class Patch_PM_Mods_ModIdentity
    {
#if RWT_RT
        internal static MethodBase Target() => AccessTools.Method(typeof(PM_Mods), "CheckIfModConflict");
#else
        internal static MethodBase Target() => null;
#endif
        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();

#if RWT_RT
        // Before RimWorld Together's own name check, which runs unchanged: a renamed Workshop mod takes the listed name.
        [HarmonyPrefix]
        private static void Prefix(ServerClient __0, object __1)
        {
            try
            {
                System.Collections.IList mine = Entries(Master.ModConfig), theirs = Entries(__1);
                if (mine == null || theirs == null) return;
                var server = new List<(string name, string id)>();
                foreach (object m in mine) server.Add((Name(m), Str(m, "ModID")));
                var client = new List<(string name, string id)>();
                foreach (object m in theirs) client.Add((Name(m), Str(m, "ModID")));
                string user = __0?.GetData<UserFile>()?.Username ?? "";
                foreach ((int index, string from, string to) in JoinDiagnostics.Renames(server, client))
                {
                    if (!SetName(theirs[index], to)) continue;
                    client[index] = (to, client[index].id);
                    JoinDiagnostics.Record(__0?.IP, user, "renamed mod matched by Workshop id",
                        $"'{JoinDiagnostics.Clip(from, JoinDiagnostics.MaxModName)}' is listed here as '{JoinDiagnostics.Clip(to, JoinDiagnostics.MaxModName)}' (Workshop id {client[index].id}); update Configs/ModConfig.json to the new name");
                }
                var required = new List<(string name, bool required)>();
                foreach (object m in mine) required.Add((Name(m), string.Equals(Prop(m, "Type")?.GetValue(m)?.ToString(), "Required", StringComparison.Ordinal)));
                var (missing, notAllowed) = JoinDiagnostics.Mismatch(required, client.Select(c => c.name).ToList());
                JoinDiagnostics.RememberModDetail(__0, JoinDiagnostics.Describe(missing, notAllowed));
            }
            catch (Exception ex) { ServerLog.Warn($"Join: mod identity check skipped ({ex.GetType().Name}); RimWorld Together's own check still runs."); }
        }

        internal static void PrefixForTest(ServerClient client, object clientConfig) => Prefix(client, clientConfig);

        // By name, as ModProfile reads them: RimWorld Together renamed these members between builds of this generation.
        private static System.Collections.IList Entries(object config) => Prop(config, "ModConfigs")?.GetValue(config) as System.Collections.IList;
        private static PropertyInfo Prop(object o, string name) => o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        private static string Str(object o, string name) => Prop(o, name)?.GetValue(o) as string;
        private static string Name(object m) => Str(m, "ModName") ?? Str(m, "FileName");

        private static bool SetName(object m, string value)
        {
            PropertyInfo p = Prop(m, "ModName") ?? Prop(m, "FileName");
            if (p == null || !p.CanWrite || p.PropertyType != typeof(string)) return false;
            p.SetValue(m, value);
            return true;
        }
#endif
    }

    [HarmonyPatch]
    internal static class Patch_PM_Login_DenyDiagnostics
    {
#if RWT_RT
        internal static MethodBase Target() => AccessTools.Method(typeof(PM_Login), "DenyConnectionWithReason");
#else
        internal static MethodBase Target() => null;
#endif
        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();

#if RWT_RT
        // Observes only: RimWorld Together still sends its refusal and closes the connection exactly as before.
        [HarmonyPrefix]
        private static void Prefix(ServerClient __0, object __1)
        {
            try
            {
                string response = __1?.ToString() ?? "?";
                var all = Network.ServerClients.Keys.ToList();
                int loggedIn = all.Count(c => c?.GetData<UserFile>() != null);
                string text = JoinDiagnostics.ReasonText(response, all.Count, loggedIn, Master.ServerConfig?.MaxPlayers ?? 0);
                string detail = response.StartsWith("Mod", StringComparison.Ordinal) ? JoinDiagnostics.TakeModDetail(__0) : "";
                JoinDiagnostics.Record(__0?.IP, __0?.GetData<UserFile>()?.Username, text, detail);
            }
            catch (Exception ex) { ServerLog.Debug($"Join: refusal not recorded ({ex.GetType().Name})."); }
        }
#endif
    }

    [HarmonyPatch]
    internal static class Patch_PM_WorldObject_KeepConnection
    {
#if RWT_RT
        internal static MethodBase Target() => AccessTools.Method(typeof(PM_WorldObject), "Receive");
#else
        internal static MethodBase Target() => null;
#endif
        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();

#if RWT_RT
        // RWT drops the player on any error recording a map object; the object is lost either way, the session need not be.
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, ServerClient __0)
        {
            if (__exception == null) return null;
            try { JoinDiagnostics.Record(__0?.IP, __0?.GetData<UserFile>()?.Username, "kept connected: RimWorld Together failed on a map object", JoinDiagnostics.ErrorText(__exception)); }
            catch (Exception ex) { ServerLog.Debug($"Join: map object error not recorded ({ex.GetType().Name})."); }
            return null;
        }

        internal static Exception FinalizerForTest(Exception e, ServerClient client) => Finalizer(e, client);
#endif
    }

    [HarmonyPatch]
    internal static class Patch_Listener_DropDiagnostics
    {
#if RWT_RT
        internal static MethodBase Target() => AccessTools.Method(typeof(Listener), "Read");
        private static readonly MethodInfo ClientOf = AccessTools.PropertyGetter(typeof(Listener), "TargetClient");
#else
        internal static MethodBase Target() => null;
#endif
        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();

#if RWT_RT
        // Observes only: RimWorld Together still closes the connection, but the reason reaches this log, not debug output.
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, object __instance)
        {
            if (__exception == null || JoinDiagnostics.IsConnectionLoss(__exception)) return __exception;
            try
            {
                var client = __instance == null ? null : ClientOf?.Invoke(__instance, null) as ServerClient;
                JoinDiagnostics.Record(client?.IP, client?.GetData<UserFile>()?.Username, "dropped: RimWorld Together failed on a packet", JoinDiagnostics.ErrorText(__exception));
            }
            catch (Exception ex) { ServerLog.Debug($"Join: dropped connection not recorded ({ex.GetType().Name})."); }
            return __exception;
        }

        internal static Exception FinalizerForTest(Exception e, object listener) => Finalizer(e, listener);
#endif
    }
}
