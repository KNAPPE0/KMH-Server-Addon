using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KMHServerAddon.Features.Simulation;
using KMHServerAddon.SubProtocol.Patches;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhRwtLoginGuardSelfTest
    {
        private sealed class Conn { public string Name; public Conn(string n) { Name = n; } }
        private sealed class SyncPacket { public int ToTile { get; set; } }

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Login: a connection that has not logged in yet is passed over, and only the same player's older session is closed", () =>
            {
                var me = new Conn("alice");
                var scanner = new Conn(null);
                var older = new Conn("alice");
                var other = new Conn("bob");
                var differentCase = new Conn("Alice");
                List<Conn> hit = Patch_PM_Login_OldSessions.OlderSessions(me, "alice",
                    new[] { scanner, me, older, null, other, differentCase }, c => c.Name);
                bool ok = hit.Count == 1 && ReferenceEquals(hit[0], older);
                return (ok, ok ? "older session only" : $"{hit.Count} closed");
            });

            Check("Login: a player with no name yet closes nothing", () =>
            {
                List<Conn> hit = Patch_PM_Login_OldSessions.OlderSessions(new Conn(null), null, new[] { new Conn("alice") }, c => c.Name);
                return (hit.Count == 0, $"{hit.Count} closed");
            });

            Check("Join: a renamed Workshop mod takes the name this server lists, matched by its Workshop id", () =>
            {
                var server = new List<(string, string)> { ("RimWorld Together", "3005289691"), ("Harmony", "2009463077"), ("KMH: Colony Nexus", "KMHPatch") };
                var client = new List<(string, string)> { ("Harmony", "2009463077"), ("RimWorld Together 1.7", "3005289691"), ("KMH: Colony Nexus", "KMHPatch") };
                var renames = JoinDiagnostics.Renames(server, client);
                bool ok = renames.Count == 1 && renames[0].index == 1 && renames[0].to == "RimWorld Together" && renames[0].from == "RimWorld Together 1.7";
                return (ok, string.Join(", ", renames.Select(x => $"{x.index}:{x.from}->{x.to}")));
            });

            Check("Join: no rename for a listed name, a folder id, an id two entries share, or a name the client already has", () =>
            {
                var server = new List<(string, string)> { ("Power Tab 2", "3528086894"), ("Local Mod", "LocalFolder"), ("Twin A", "1111111111"), ("Twin B", "1111111111"), ("Taken", "2222222222") };
                int listed = JoinDiagnostics.Renames(server, new List<(string, string)> { ("Power Tab 2", "3528086894") }).Count;
                int folder = JoinDiagnostics.Renames(server, new List<(string, string)> { ("Local Mod Renamed", "LocalFolder") }).Count;
                int shared = JoinDiagnostics.Renames(server, new List<(string, string)> { ("Twin C", "1111111111") }).Count;
                int taken = JoinDiagnostics.Renames(server, new List<(string, string)> { ("Taken v2", "2222222222"), ("Taken", "9999999999") }).Count;
                int renamed = JoinDiagnostics.Renames(server, new List<(string, string)> { ("Power Tab 2 - 1.6 Bug Fixed Version", "3528086894") }).Count;
                bool ok = listed == 0 && folder == 0 && shared == 0 && taken == 0 && renamed == 1;
                return (ok, $"listed {listed}, folder {folder}, shared id {shared}, name taken {taken}, renamed {renamed}");
            });

            Check("Join: a refused mod list names what is missing and what is not allowed, by name as RimWorld Together compares", () =>
            {
                var (missing, notAllowed) = JoinDiagnostics.Mismatch(
                    new List<(string, bool)> { ("Core", true), ("KMH: Colony Nexus", true), ("Royalty", false) },
                    new List<string> { "Core", "Royalty", "Fancy Mod" });
                bool ok = missing.SequenceEqual(new[] { "KMH: Colony Nexus" }) && notAllowed.SequenceEqual(new[] { "Fancy Mod" });
                return (ok, JoinDiagnostics.Describe(missing, notAllowed));
            });

            // An IPv6 address cut at its last group still names one household's /64, and a mapped IPv4 loses everything.
            Check("Join: an address in the log keeps the network, not the player - IPv4 to /24, IPv6 to /48", () =>
            {
                string v4 = JoinDiagnostics.MaskIp("203.0.113.45"), v6 = JoinDiagnostics.MaskIp("2001:db8:abcd:12:34:56:78:9a");
                string mapped = JoinDiagnostics.MaskIp("::ffff:203.0.113.45"), withPort = JoinDiagnostics.MaskIp("203.0.113.45:5555");
                string v6Port = JoinDiagnostics.MaskIp("[2001:db8:abcd:12::7]:5555");
                bool ok = v4 == "203.0.113.x" && v6 == "2001:db8:abcd::/48" && mapped == "203.0.113.x" && withPort == "203.0.113.x"
                       && v6Port == "2001:db8:abcd::/48" && JoinDiagnostics.MaskIp(null) == "?" && JoinDiagnostics.MaskIp("not-an-address") == "?";
                return (ok, $"{v4} {v6} mapped={mapped} port={withPort} v6port={v6Port}");
            });

            Check("Join: a stored join line is bounded - one line, 64-char name, 300-char detail, 64 chars per mod name", () =>
            {
                string hugeMod = new string('M', 500) + "\n[join] J1 forged line";
                string described = JoinDiagnostics.Describe(new List<string> { hugeMod, "Core" }, new List<string>());
                JoinDiagnostics.Record("203.0.113.9", "bad\r\n[join] forged " + new string('u', 200), "refused: " + new string('w', 400), described + new string('d', 2000));
                JoinDiagnostics.Event e = JoinDiagnostics.Snapshot(1)[0];
                bool oneLine = !(e.User + e.What + e.Detail + described).Contains('\n') && !(e.User + e.Detail).Contains('\r');
                bool ok = oneLine && e.User.Length <= JoinDiagnostics.MaxUser && e.What.Length <= JoinDiagnostics.MaxWhat
                       && e.Detail.Length <= JoinDiagnostics.MaxDetail && described.Length < 120 && described.Contains("Core");
                return (ok, $"user={e.User.Length} what={e.What.Length} detail={e.Detail.Length} described={described.Length} oneLine={oneLine}");
            });

#if RWT_RT
            Check("Login: the guarded RimWorld Together method exists in this generation", () =>
                (Patch_PM_Login_OldSessions.Target() != null, Patch_PM_Login_OldSessions.Target()?.Name ?? "MISSING - the guard would be skipped"));
            Check("Join: the mod check and the refusal RimWorld Together sends exist in this generation", () =>
                (Patch_PM_Mods_ModIdentity.Target() != null && Patch_PM_Login_DenyDiagnostics.Target() != null,
                 $"{Patch_PM_Mods_ModIdentity.Target()?.Name ?? "MISSING"}, {Patch_PM_Login_DenyDiagnostics.Target()?.Name ?? "MISSING"}"));

            // Built from this build's own types, as a join delivers them: the prefix reaches them only by member name.
            Check("Join: a Workshop mod renamed in its 1.7 update keeps its listed name on this build's own mod list types; a local mod is left alone", () =>
            {
                System.Reflection.PropertyInfo cfgProp = typeof(Master).GetProperty("ModConfig");
                Type cfgType = cfgProp?.PropertyType;
                System.Reflection.PropertyInfo listProp = cfgType?.GetProperty("ModConfigs");
                Type dataType = listProp?.PropertyType.GetGenericArguments().FirstOrDefault();
                System.Reflection.PropertyInfo nameProp = dataType?.GetProperty("ModName") ?? dataType?.GetProperty("FileName");
                if (nameProp == null || dataType.GetProperty("ModID") == null) return (false, "no ModConfig.ModConfigs[].ModName/ModID on this build");
                object Entry(string name, string id, string type)
                {
                    object d = Activator.CreateInstance(dataType);
                    nameProp.SetValue(d, name);
                    dataType.GetProperty("ModID").SetValue(d, id);
                    System.Reflection.PropertyInfo tp = dataType.GetProperty("Type");
                    if (tp != null && type != null) tp.SetValue(d, Enum.Parse(tp.PropertyType, type));
                    return d;
                }
                object Config(params object[] entries)
                {
                    object c = Activator.CreateInstance(cfgType);
                    var list = (System.Collections.IList)Activator.CreateInstance(listProp.PropertyType);
                    foreach (object e in entries) list.Add(e);
                    listProp.SetValue(c, list);
                    return c;
                }
                object had = cfgProp.GetValue(null);
                try
                {
                    cfgProp.SetValue(null, Config(Entry("Power Tab", "3528086894", "Required"), Entry("Local Thing", "LocalFolder", "Required")));
                    object client = Config(Entry("Power Tab (1.7)", "3528086894", null), Entry("Local Thing 1.7", "LocalFolder", null));
                    Patch_PM_Mods_ModIdentity.PrefixForTest(null, client);
                    var sent = (System.Collections.IList)listProp.GetValue(client);
                    string workshop = (string)nameProp.GetValue(sent[0]), local = (string)nameProp.GetValue(sent[1]);
                    bool ok = workshop == "Power Tab" && local == "Local Thing 1.7";
                    return (ok, $"workshop mod reported as '{workshop}', local mod as '{local}'");
                }
                finally { cfgProp.SetValue(null, had); }
            });

            Check("Connections: RimWorld Together's map object handler and packet reader exist in this generation", () =>
                (Patch_PM_WorldObject_KeepConnection.Target() != null && Patch_Listener_DropDiagnostics.Target() != null,
                 $"{Patch_PM_WorldObject_KeepConnection.Target()?.Name ?? "MISSING"}, {Patch_Listener_DropDiagnostics.Target()?.Name ?? "MISSING"}"));

            // Unpatched, a WorldObject packet error drops the player with nothing in either log saying why.
            Check("Connections: a map object RimWorld Together cannot record keeps the player; any other packet error is logged and still ends the connection; a lost socket is not logged", () =>
            {
                var boom = new InvalidOperationException("tile taken");
                Exception kept = Patch_PM_WorldObject_KeepConnection.FinalizerForTest(boom, null);
                Exception clean = Patch_PM_WorldObject_KeepConnection.FinalizerForTest(null, null);
                var locked = new IOException("The process cannot access the file because it is being used by another process.");
                Exception dropped = Patch_Listener_DropDiagnostics.FinalizerForTest(locked, null);
                var gone = new IOException("Unable to read data from the transport connection.", new System.Net.Sockets.SocketException(10054));
                Exception lost = Patch_Listener_DropDiagnostics.FinalizerForTest(gone, null);
                List<JoinDiagnostics.Event> seen = JoinDiagnostics.Snapshot(2);
                bool ok = kept == null && clean == null && ReferenceEquals(dropped, locked) && ReferenceEquals(lost, gone)
                       && seen.Count == 2 && seen[0].What.StartsWith("kept connected", StringComparison.Ordinal) && seen[0].Detail.Contains("tile taken")
                       && seen[1].What.StartsWith("dropped", StringComparison.Ordinal) && seen[1].Detail.Contains("being used by another process");
                return (ok, $"map object suppressed={kept == null}, packet error rethrown={ReferenceEquals(dropped, locked)}, socket loss rethrown={ReferenceEquals(lost, gone)}; "
                          + string.Join(" | ", seen.Select(e => e.What + ": " + e.Detail)));
            });
#else
            Check("Login: the guard is skipped on generations it was not written for", () =>
                (Patch_PM_Login_OldSessions.Target() == null, ""));
#endif

            Check("Simulated players: RWT is told a simulated player is offline, and asking about a person runs RWT's own check", () =>
            {
                using (SimulatedAccounts.UseForTest(new SimulatedAccountsFile { Accounts = new List<string> { "SimA" } }))
                {
                    bool sim = true, person = true, nobody = true;
                    bool simRunsRwt = SimulatedColonyGate.OfflineIfSimulated("sima", ref sim);
                    bool personRunsRwt = SimulatedColonyGate.OfflineIfSimulated("bob", ref person);
                    bool nobodyRunsRwt = SimulatedColonyGate.OfflineIfSimulated(null, ref nobody);
                    bool ok = !simRunsRwt && !sim && personRunsRwt && person && nobodyRunsRwt;
                    return (ok, $"sim: runs RWT {simRunsRwt}, online {sim}; person: runs RWT {personRunsRwt}; no name: runs RWT {nobodyRunsRwt}");
                }
            });

            Check("Simulated players: an online visit or raid on a simulated colony is answered unavailable, any other goes to RWT", () =>
            {
                Func<int, string> owner = SimulatedColonyGate.OwnerAt;
                Action<object> send = SimulatedColonyGate.SendUnavailable;
                var sent = new List<object>();
                try
                {
                    using (SimulatedAccounts.UseForTest(new SimulatedAccountsFile { Accounts = new List<string> { "SimA" } }))
                    {
                        SimulatedColonyGate.OwnerAt = t => t == 7 ? "SimA" : t == 8 ? "bob" : null;
                        SimulatedColonyGate.SendUnavailable = c => sent.Add(c);
                        object asker = new object();
                        bool simRunsRwt = SimulatedColonyGate.UnavailableIfSimulated(asker, new SyncPacket { ToTile = 7 });
                        bool personRunsRwt = SimulatedColonyGate.UnavailableIfSimulated(asker, new SyncPacket { ToTile = 8 });
                        bool emptyRunsRwt = SimulatedColonyGate.UnavailableIfSimulated(asker, new SyncPacket { ToTile = 9 });
                        bool ok = !simRunsRwt && personRunsRwt && emptyRunsRwt && sent.Count == 1 && ReferenceEquals(sent[0], asker);
                        return (ok, $"sim: runs RWT {simRunsRwt}; person: {personRunsRwt}; empty tile: {emptyRunsRwt}; unavailable sent {sent.Count}");
                    }
                }
                finally { SimulatedColonyGate.OwnerAt = owner; SimulatedColonyGate.SendUnavailable = send; }
            });

            Check("Simulated players: the directory's player count includes simulated sessions, not one still logging in", () =>
            {
                using (SimulatedAccounts.UseForTest(new SimulatedAccountsFile { Accounts = new List<string> { "SimA" } }))
                {
                    int n = Features.Global.GlobalStatusProbe.Players(new[] { "SimA", "bob", "?" });
                    return (n == 2, $"SimA, bob and a session still logging in count as {n}");
                }
            });

            Check("Simulated players: colonies are removed only with nobody online; a session still logging in and a simulated one are nobody", () =>
            {
                using (SimulatedAccounts.UseForTest(new SimulatedAccountsFile { Accounts = new List<string> { "SimA" } }))
                {
                    string nobody = SimulatedColonies.RemovalRefusal(new[] { "SimA", "?" }, new[] { "sima" });
                    string rwtPerson = SimulatedColonies.RemovalRefusal(new[] { "SimA", "bob" }, new string[0]);
                    string linkedPerson = SimulatedColonies.RemovalRefusal(new[] { "SimA" }, new[] { "ann" });
                    bool ok = nobody == null && rwtPerson != null && linkedPerson != null;
                    return (ok, $"nobody: {nobody ?? "allowed"} | RWT person: {rwtPerson ?? "allowed"} | linked person: {linkedPerson ?? "allowed"}");
                }
            });

            Check("Simulated players: only a registered account's settlements are listed, with their uploaded maps", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "kmh-simcol-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    string settlements = Path.Combine(root, "Settlements"), maps = Path.Combine(root, "Maps");
                    Directory.CreateDirectory(settlements); Directory.CreateDirectory(maps);
                    File.WriteAllText(Path.Combine(settlements, "5.json"), "{\"Tile\":5,\"IconID\":0,\"IconColor\":0,\"Username\":\"SimA\"}");
                    File.WriteAllText(Path.Combine(settlements, "6.json"), "{\"Tile\":6,\"IconID\":0,\"IconColor\":0,\"Username\":\"bob\"}");
                    File.WriteAllText(Path.Combine(settlements, "9.json"), "{\"Tile\":9,\"IconID\":0,\"IconColor\":0,\"Username\":\"sima\"}");
                    File.WriteAllText(Path.Combine(maps, "5.json"), "x");
                    File.WriteAllText(Path.Combine(maps, "6.json"), "x");
                    using (SimulatedAccounts.UseForTest(new SimulatedAccountsFile { Accounts = new List<string> { "SimA" } }))
                    {
                        List<SimulatedColonies.Colony> found = SimulatedColonies.Find(settlements, maps, SimulatedAccounts.IsSimulated);
                        string got = string.Join(" ", found.Select(c => $"{c.Tile}:{c.Username}:{(c.HasMap ? "map" : "nomap")}"));
                        return (got == "5:SimA:map 9:sima:nomap", got);
                    }
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            });

#if RWT_RT
            Check("Simulated players: the RWT methods the gate and the removal use exist in this generation", () =>
            {
                bool removal = SimulatedColonyGate.RwtType("PacketManagers.PM_Settlements")?.GetMethod("RemoveSettlement")?.GetParameters().Length == 2;
                bool ok = SimulatedColonyGate.ConnectedTarget() != null && SimulatedColonyGate.SessionTarget() != null && removal;
                return (ok, $"connected check {SimulatedColonyGate.ConnectedTarget()?.Name ?? "MISSING"}, session {SimulatedColonyGate.SessionTarget()?.Name ?? "MISSING"}, removal {removal}");
            });
#endif
            return r;
        }
    }
}
