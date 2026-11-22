using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

            Check("Connections: a connection that never logged in is passed over by the relay to all but the sender, the lookup by name and RWT's old-session check, rather than dropping whoever asked", () =>
            {
                var h = new HarmonyLib.Harmony("kmh.selftest.unlogged-peers");
                ServerClient Fake(string name)
                {
                    var c = new ServerClient(null, null, createListener: false);
                    if (name != null) c.GetData<UserFile>(new UserFile { Username = name });
                    return c;
                }
                ServerClient sender = Fake("selftest_relay_a"), peer = Fake("selftest_relay_b"), scanner = Fake(null);
                try
                {
                    // Boot's PatchAll takes only annotated classes; then RWT's own methods, which unpatched throw on the scanner.
                    bool atBoot = HarmonyLib.GeneralExtensions.HasHarmonyAttribute(typeof(Patch_ServerNetwork_SkipUnloggedPeers));
                    h.CreateClassProcessor(typeof(Patch_ServerNetwork_SkipUnloggedPeers)).Patch();
                    foreach (ServerClient c in new[] { sender, peer, scanner }) Network.ServerClients.TryAdd(c, -1);
                    MethodBase list = Patch_ServerNetwork_SkipUnloggedPeers.Target();
                    var relayed = (ServerClient[])list.Invoke(null, new object[] { sender });
                    var everyone = (ServerClient[])list.Invoke(null, new object[] { null });
                    MethodBase byName = HarmonyLib.AccessTools.Method(list.DeclaringType, "GetConnectedClientFromUsername");
                    object found = byName.Invoke(null, new object[] { "selftest_relay_b" }), offline = byName.Invoke(null, new object[] { "selftest_relay_offline" });
#if RWT_RT
                    HarmonyLib.AccessTools.Method(typeof(PM_Login), "RemoveOldClientSessions").Invoke(null, new object[] { sender });
#endif
                    bool ok = atBoot && relayed.Contains(peer) && !relayed.Contains(sender) && !relayed.Contains(scanner)
                           && everyone.Contains(sender) && everyone.Contains(peer) && !everyone.Contains(scanner)
                           && ReferenceEquals(found, peer) && offline == null;
                    return (ok, $"applied at boot={atBoot}; relayed to {relayed.Length} (unlogged={relayed.Contains(scanner)}); listed {everyone.Length} (unlogged={everyone.Contains(scanner)}); "
                              + $"by name: peer={ReferenceEquals(found, peer)}, offline={offline == null}");
                }
                catch (TargetInvocationException e) { return (false, $"RWT threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"); }
                finally
                {
                    foreach (ServerClient c in new[] { sender, peer, scanner }) Network.ServerClients.TryRemove(c, out _);
                    h.UnpatchAll(h.Id);
                }
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

            Check("Connections: RimWorld Together's settlement removal exists in this generation", () =>
                (Patch_PM_Settlements_StaleRemoval.Target() != null, Patch_PM_Settlements_StaleRemoval.Target()?.Name ?? "MISSING - the guard would be skipped"));

            // Unpatched, RWT calls the removal illegal and drops the player; a connection with no listener throws there instead.
            Check("Connections: removing a settlement this server no longer has, or another player's, keeps the player and removes nothing; removing your own still goes to RimWorld Together", () =>
            {
                var h = new HarmonyLib.Harmony("kmh.selftest.stale-settlement");
                var client = new ServerClient(null, null, createListener: false);
                client.GetData<UserFile>(new UserFile { Username = "selftest_settle" });
                const int gone = 987654321, held = 987654322, own = 987654323;
                string file = Path.Combine(Master.SettlementsPath, held + ".json"), ownFile = Path.Combine(Master.SettlementsPath, own + ".json");
                object Packet(int tile)
                {
                    var p = new PKT_PlayerSettlement();
                    PropertyInfo prop = typeof(PKT_PlayerSettlement).GetProperty("File");
                    object f = prop.GetValue(p) ?? Activator.CreateInstance(prop.PropertyType);
                    f.GetType().GetProperty("Tile").SetValue(f, tile);
                    prop.SetValue(p, f);
                    return p;
                }
                try
                {
                    bool atBoot = HarmonyLib.GeneralExtensions.HasHarmonyAttribute(typeof(Patch_PM_Settlements_StaleRemoval));
                    h.CreateClassProcessor(typeof(Patch_PM_Settlements_StaleRemoval)).Patch();
                    Directory.CreateDirectory(Master.SettlementsPath);
                    File.WriteAllText(file, "{\"Tile\":" + held + ",\"IconID\":0,\"IconColor\":0,\"Username\":\"selftest_settle_other\"}");
                    File.WriteAllText(ownFile, "{\"Tile\":" + own + ",\"IconID\":0,\"IconColor\":0,\"Username\":\"selftest_settle\"}");
                    Patch_PM_Settlements_StaleRemoval.Target().Invoke(null, new object[] { client, Packet(gone) });
                    Patch_PM_Settlements_StaleRemoval.Target().Invoke(null, new object[] { client, Packet(held) });
                    List<JoinDiagnostics.Event> seen = JoinDiagnostics.Snapshot(2);
                    bool kept = File.Exists(file);
                    bool toRwt = Patch_PM_Settlements_StaleRemoval.PrefixForTest(client, Packet(own));
                    bool ok = atBoot && seen.Count == 2 && seen[0].What == "kept connected: removed a settlement this server no longer has" && seen[0].Detail == "tile " + gone
                           && seen[1].What == "kept connected: did not remove another player's settlement" && seen[1].Detail == "tile " + held && kept && toRwt;
                    return (ok, $"applied at boot={atBoot}; {string.Join(" | ", seen.Select(e => e.What + " (" + e.Detail + ")"))}; other's file kept={kept}; own tile goes to RWT={toRwt}");
                }
                catch (TargetInvocationException e) { return (false, $"RWT threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"); }
                finally { try { File.Delete(file); File.Delete(ownFile); } catch { } h.UnpatchAll(h.Id); }
            });

            Check("Connections: RimWorld Together's settlement add exists in this generation", () =>
                (Patch_PM_Settlements_OwnReAdd.Target() != null, Patch_PM_Settlements_OwnReAdd.Target()?.Name ?? "MISSING - the guard would be skipped"));

            // Unpatched, RWT calls the add illegal and drops the player; a connection with no listener throws there instead.
            Check("Connections: adding a settlement on a tile the server has, the player's own or another's, keeps the player and changes nothing; a free tile still goes to RimWorld Together", () =>
            {
                var h = new HarmonyLib.Harmony("kmh.selftest.own-settlement");
                var client = new ServerClient(null, null, createListener: false);
                client.GetData<UserFile>(new UserFile { Username = "selftest_settle" });
                const int own = 987654331, other = 987654332, free = 987654333;
                string ownFile = Path.Combine(Master.SettlementsPath, own + ".json"), otherFile = Path.Combine(Master.SettlementsPath, other + ".json");
                object Packet(int tile)
                {
                    var p = new PKT_PlayerSettlement();
                    PropertyInfo prop = typeof(PKT_PlayerSettlement).GetProperty("File");
                    object f = prop.GetValue(p) ?? Activator.CreateInstance(prop.PropertyType);
                    f.GetType().GetProperty("Tile").SetValue(f, tile);
                    prop.SetValue(p, f);
                    return p;
                }
                try
                {
                    bool atBoot = HarmonyLib.GeneralExtensions.HasHarmonyAttribute(typeof(Patch_PM_Settlements_OwnReAdd));
                    h.CreateClassProcessor(typeof(Patch_PM_Settlements_OwnReAdd)).Patch();
                    Directory.CreateDirectory(Master.SettlementsPath);
                    File.WriteAllText(ownFile, "{\"Tile\":" + own + ",\"IconID\":0,\"IconColor\":0,\"Username\":\"selftest_settle\"}");
                    File.WriteAllText(otherFile, "{\"Tile\":" + other + ",\"IconID\":0,\"IconColor\":0,\"Username\":\"selftest_settle_other\"}");
                    Patch_PM_Settlements_OwnReAdd.Target().Invoke(null, new object[] { client, Packet(own) });
                    Patch_PM_Settlements_OwnReAdd.Target().Invoke(null, new object[] { client, Packet(other) });
                    List<JoinDiagnostics.Event> seen = JoinDiagnostics.Snapshot(2);
                    bool otherKept = File.ReadAllText(otherFile).Contains("\"selftest_settle_other\"");
                    bool freeToRwt = Patch_PM_Settlements_OwnReAdd.PrefixForTest(client, Packet(free));
                    bool ok = atBoot && seen.Count == 2 && seen[0].What == "kept connected: re-added its own settlement" && seen[0].Detail == "tile " + own
                           && seen[1].What == "kept connected: did not add a settlement on another player's tile" && seen[1].Detail == "tile " + other && otherKept && freeToRwt;
                    return (ok, $"applied at boot={atBoot}; {string.Join(" | ", seen.Select(e => e.What + " (" + e.Detail + ")"))}; other's owner kept={otherKept}; a free tile goes to RWT={freeToRwt}");
                }
                catch (TargetInvocationException e) { return (false, $"RWT threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"); }
                finally { try { File.Delete(ownFile); File.Delete(otherFile); } catch { } h.UnpatchAll(h.Id); }
            });

            Check("Connections: RimWorld Together's map upload exists in this generation", () =>
                (Patch_PM_Map_OthersTile.Target() != null, Patch_PM_Map_OthersTile.Target()?.Name ?? "MISSING - the guard would be skipped"));

            // Unpatched, 26.11.2.1 calls the upload illegal and drops the player; older builds store it over the owner's map.
            Check("Connections: uploading a map for another player's tile keeps the player and stores nothing; the player's own tile and a free tile still go to RimWorld Together", () =>
            {
                var h = new HarmonyLib.Harmony("kmh.selftest.others-map");
                var client = new ServerClient(null, null, createListener: false);
                client.GetData<UserFile>(new UserFile { Username = "selftest_settle" });
                const int own = 987654341, other = 987654342, free = 987654343;
                string ownFile = Path.Combine(Master.SettlementsPath, own + ".json"), otherFile = Path.Combine(Master.SettlementsPath, other + ".json");
                string otherMap = Path.Combine(Master.MapsPath, other + ".json");
                object Packet(int tile)
                {
                    var p = new PKT_Map();
                    HarmonyLib.Traverse.Create(p).Property("Tile").SetValue(tile);
                    HarmonyLib.Traverse.Create(p).Property("Bytes").SetValue(new byte[] { 1 });
                    return p;
                }
                try
                {
                    bool atBoot = HarmonyLib.GeneralExtensions.HasHarmonyAttribute(typeof(Patch_PM_Map_OthersTile));
                    h.CreateClassProcessor(typeof(Patch_PM_Map_OthersTile)).Patch();
                    Directory.CreateDirectory(Master.SettlementsPath); Directory.CreateDirectory(Master.MapsPath);
                    File.WriteAllText(ownFile, "{\"Tile\":" + own + ",\"IconID\":0,\"IconColor\":0,\"Username\":\"selftest_settle\"}");
                    File.WriteAllText(otherFile, "{\"Tile\":" + other + ",\"IconID\":0,\"IconColor\":0,\"Username\":\"selftest_settle_other\"}");
                    Patch_PM_Map_OthersTile.Target().Invoke(null, new object[] { client, Packet(other) });
                    JoinDiagnostics.Event e = JoinDiagnostics.Snapshot(1)[0];
                    bool stored = File.Exists(otherMap);
                    bool ownToRwt = Patch_PM_Map_OthersTile.PrefixForTest(client, Packet(own)), freeToRwt = Patch_PM_Map_OthersTile.PrefixForTest(client, Packet(free));
                    bool ok = atBoot && e.What == "kept connected: did not store a map for another player's tile" && e.Detail == "tile " + other && !stored && ownToRwt && freeToRwt;
                    return (ok, $"applied at boot={atBoot}; {e.What} ({e.Detail}); map stored={stored}; own tile goes to RWT={ownToRwt}, a free tile={freeToRwt}");
                }
                catch (TargetInvocationException e) { return (false, $"RWT threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"); }
                finally { try { File.Delete(ownFile); File.Delete(otherFile); File.Delete(otherMap); } catch { } h.UnpatchAll(h.Id); }
            });
#else
            Check("Login: the guard is skipped on generations it was not written for", () =>
                (Patch_PM_Login_OldSessions.Target() == null, ""));
            Check("Connections: the settlement removal guard is skipped on generations it was not written for", () =>
                (Patch_PM_Settlements_StaleRemoval.Target() == null, ""));
            Check("Connections: the own-settlement guard is skipped on generations it was not written for", () =>
                (Patch_PM_Settlements_OwnReAdd.Target() == null, ""));
            Check("Connections: the map upload guard is skipped on generations it was not written for", () =>
                (Patch_PM_Map_OthersTile.Target() == null, ""));
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

            string HashFiles(Action<string, string, string, string> body)
            {
                string root = Path.Combine(Path.GetTempPath(), "kmh-rwthash-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string users = Path.Combine(root, "Users"), saves = Path.Combine(root, "Saves"), guilds = Path.Combine(root, "Guilds");
                try
                {
                    foreach (string d in new[] { users, saves, guilds }) Directory.CreateDirectory(d);
                    File.WriteAllText(Path.Combine(users, "HA.json"), "{\"Username\":\"alice\",\"Hash\":\"HA\"}");
                    File.WriteAllText(Path.Combine(users, "bob.json"), "{\"Username\":\"bob\",\"Hash\":\"HB\"}");
                    File.WriteAllText(Path.Combine(users, "HC.json"), "{\"Username\":\"carol\",\"Hash\":\"HC\"}");
                    File.WriteAllText(Path.Combine(saves, "alice.json"), "A");
                    File.WriteAllText(Path.Combine(saves, "bob.json"), "B");
                    File.WriteAllText(Path.Combine(guilds, "Smiths.json"), "{\"Name\":\"Smiths\",\"IconID\":4,\"IconColor\":2,\"GuildMembers\":[{\"Username\":\"alice\",\"Rank\":2}]}");
                    body(users, saves, guilds, Path.Combine(root, "park"));
                    return string.Join(" ", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                        .Select(f => f.Substring(root.Length + 1).Replace('\\', '/') + "=" + File.ReadAllText(f).Length).OrderBy(s => s, StringComparer.Ordinal));
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }
            Func<string, string> fakeHash = s => "G" + s.ToUpperInvariant();

            Check("RWT files: on an RWT that finds saves by hash, each returning player's save is renamed to the account's hash name and an old RWT guild gets its hash, keeping its members", () =>
            {
                RwtHashKeyMigration.Outcome o = null;
                string guild = null;
                string files = HashFiles((u, s, g, park) =>
                {
                    o = RwtHashKeyMigration.Run(u, s, g, park, fakeHash);
                    string p = Path.Combine(g, "GSMITHS.json");
                    guild = File.Exists(p) ? File.ReadAllText(p) : "";
                    bool moved = File.ReadAllText(Path.Combine(s, "HA.json")) == "A" && File.ReadAllText(Path.Combine(s, "HB.json")) == "B";
                    if (!moved) o.Failures.Add("save contents");
                });
                bool ok = o.Saves == 2 && o.Guilds == 1 && o.Parked == 0 && o.Failures.Count == 0
                       && guild.Contains("\"Hash\": \"GSMITHS\"") && guild.Contains("alice") && guild.Contains("\"IconID\": 4")
                       && !files.Contains("Saves/alice.json") && !files.Contains("Saves/bob.json") && !files.Contains("Guilds/Smiths.json");
                return (ok, $"saves {o.Saves}, guilds {o.Guilds}, parked {o.Parked}, failures {string.Join("; ", o.Failures)} | {files}");
            });

            Check("RWT files: a second run changes nothing, and a save left under both names keeps the account one and moves the older one aside rather than letting RWT delete it", () =>
            {
                RwtHashKeyMigration.Outcome again = null, both = null;
                string kept = null, parked = null;
                HashFiles((u, s, g, park) =>
                {
                    RwtHashKeyMigration.Run(u, s, g, park, fakeHash);
                    again = RwtHashKeyMigration.Run(u, s, g, park, fakeHash);
                    File.WriteAllText(Path.Combine(s, "alice.json"), "older");
                    both = RwtHashKeyMigration.Run(u, s, g, park, fakeHash);
                    kept = File.ReadAllText(Path.Combine(s, "HA.json"));
                    string p = Path.Combine(park, "Saves", "alice.json");
                    parked = File.Exists(p) ? File.ReadAllText(p) : "missing";
                    if (File.Exists(Path.Combine(s, "alice.json"))) parked += " (still in Saves)";
                });
                bool ok = again.Saves + again.Guilds + again.Parked == 0 && both.Parked == 1 && both.Saves == 0 && kept == "A" && parked == "older";
                return (ok, $"second run {again.Saves}/{again.Guilds}/{again.Parked}; both names: parked {both.Parked}, kept '{kept}', parked copy '{parked}'");
            });

            Check("RWT files: a save goes only to the account whose name matches its file exactly; two accounts with one name, or an account with no hash, stop the boot and leave the save alone", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "kmh-rwthash-own-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string users = Path.Combine(root, "Users"), saves = Path.Combine(root, "Saves");
                try
                {
                    Directory.CreateDirectory(users); Directory.CreateDirectory(saves);
                    File.WriteAllText(Path.Combine(users, "a-dana.json"), "{\"Username\":\"dana\",\"Hash\":\"HL\"}");
                    File.WriteAllText(Path.Combine(users, "b-Dana.json"), "{\"Username\":\"Dana\",\"Hash\":\"HD\"}");
                    File.WriteAllText(Path.Combine(users, "c1.json"), "{\"Username\":\"Eve\",\"Hash\":\"HE1\"}");
                    File.WriteAllText(Path.Combine(users, "c2.json"), "{\"Username\":\"Eve\",\"Hash\":\"HE2\"}");
                    File.WriteAllText(Path.Combine(users, "d.json"), "{\"Username\":\"Finn\",\"Hash\":\"\"}");
                    File.WriteAllText(Path.Combine(saves, "Dana.json"), "D");
                    File.WriteAllText(Path.Combine(saves, "Eve.json"), "E");
                    File.WriteAllText(Path.Combine(saves, "Finn.json"), "F");
                    RwtHashKeyMigration.Outcome o = RwtHashKeyMigration.Run(users, saves, Path.Combine(root, "Guilds"), Path.Combine(root, "park"), fakeHash);
                    var left = new HashSet<string>(Directory.GetFiles(saves).Select(Path.GetFileName), StringComparer.Ordinal);
                    bool dana = left.Contains("HD.json") && File.ReadAllText(Path.Combine(saves, "HD.json")) == "D" && !left.Contains("HL.json");
                    bool kept = left.Contains("Eve.json") && left.Contains("Finn.json") && left.Count == 3;
                    bool stopped = o.Failures.Count == 2 && o.Failures.Exists(f => f.Contains("Eve")) && o.Failures.Exists(f => f.Contains("Finn"));
                    return (dana && kept && stopped && o.Saves == 1, $"Dana's save to Dana {dana}, others left alone {kept} ({string.Join(" ", left)}), stopped {stopped}: {string.Join("; ", o.Failures)}");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            });

            Check("RWT files: a save that cannot be renamed stops the boot and stays where it was", () =>
            {
                RwtHashKeyMigration.Outcome o = null;
                bool stayed = false;
                HashFiles((u, s, g, park) =>
                {
                    Directory.CreateDirectory(Path.Combine(s, "HA.json", "blocker"));
                    o = RwtHashKeyMigration.Run(u, s, g, park, fakeHash);
                    stayed = File.Exists(Path.Combine(s, "alice.json"));
                });
                bool ok = o.Failures.Count == 1 && o.Failures[0].Contains("alice") && stayed && o.Saves == 1;
                return (ok, $"failures {o.Failures.Count}, alice's save still in place {stayed}, others renamed {o.Saves}");
            });

            Check("RWT guilds: with an RWT guild on the server, a player in no guild is looked up as in no guild instead of throwing at login", () =>
            {
                var h = new HarmonyLib.Harmony("kmh.selftest.no-guild");
                string g0 = Master.GuildsPath;
                string dir = Path.Combine(Path.GetTempPath(), "kmh-noguild-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "G.json"), "{\"Name\":\"Smiths\",\"Hash\":\"G\",\"IconID\":1,\"IconColor\":1,\"GuildMembers\":[]}");
                    Master.GuildsPath = dir;
                    h.CreateClassProcessor(typeof(Patch_GuildManagerH_NoGuild)).Patch();
                    MethodBase find = Patch_GuildManagerH_NoGuild.Target();
                    object none = find.Invoke(null, new object[] { null });
                    object other = find.Invoke(null, new object[] { "Nobody" });
                    bool atBoot = HarmonyLib.GeneralExtensions.HasHarmonyAttribute(typeof(Patch_GuildManagerH_NoGuild));
                    bool ok = atBoot && none == null && other == null && Patch_GuildManagerH_NoGuild.PrefixForTest("Smiths") == "Smiths";
                    return (ok, $"patched at boot {atBoot}, no guild -> {none ?? "none"}, unknown guild -> {other ?? "none"}");
                }
                catch (TargetInvocationException ex) { return (false, $"RWT threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}"); }
                finally { h.UnpatchAll(h.Id); Master.GuildsPath = g0; try { Directory.Delete(dir, true); } catch { } }
            });

            Check("RWT files: an RWT release whose layout was not checked renames nothing; it refuses to start while old-named saves remain, and starts once none do", () =>
            {
                string u0 = Master.UsersPath, s0 = Master.SavesPath, g0 = Master.GuildsPath;
                bool startsWithOld = true, untouched = false, startsWhenDone = false;
                try
                {
                    HashFiles((u, s, g, park) =>
                    {
                        Master.UsersPath = u; Master.SavesPath = s; Master.GuildsPath = g;
                        startsWithOld = RwtHashKeyMigration.RunAtBoot("26.12.9.1");
                        untouched = File.Exists(Path.Combine(s, "alice.json")) && !File.Exists(Path.Combine(s, "HA.json")) && File.Exists(Path.Combine(g, "Smiths.json"));
                        RwtHashKeyMigration.Run(u, s, g, park, fakeHash);
                        startsWhenDone = RwtHashKeyMigration.RunAtBoot("26.12.9.1");
                    });
                }
                finally { Master.UsersPath = u0; Master.SavesPath = s0; Master.GuildsPath = g0; }
                bool keyed = RwtHashKeyMigration.RwtKeysByHash;
                bool ok = untouched && startsWhenDone && startsWithOld == !keyed;
                return (ok, $"this RWT keys by hash {keyed}: starts with old names {startsWithOld}, files untouched {untouched}, starts once renamed {startsWhenDone}");
            });

            Check("RWT files: at boot, saves are renamed only on an RWT that finds them by hash (26.11.21.1 on), with that RWT's own hash", () =>
            {
                string version = typeof(UserFile).Assembly.GetType("RTShared.Misc.CommonValues")?.GetField("ExecutableVersion")?.GetRawConstantValue() as string;
                bool expectHash = Version.TryParse(version, out Version v) && v >= new Version(26, 11, 21, 1);
                string u0 = Master.UsersPath, s0 = Master.SavesPath, g0 = Master.GuildsPath;
                bool booted = false, renamed = false;
                try
                {
                    HashFiles((u, s, g, park) =>
                    {
                        Master.UsersPath = u; Master.SavesPath = s; Master.GuildsPath = g;
                        booted = RwtHashKeyMigration.RunAtBoot();
                        renamed = File.Exists(Path.Combine(s, "HA.json"));
                    });
                }
                finally { Master.UsersPath = u0; Master.SavesPath = s0; Master.GuildsPath = g0; }
                Func<string, string> hasher = RwtHashKeyMigration.RwtHasher();
                string sha = BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("Smiths"))).Replace("-", "");
                bool hashOk = !expectHash || hasher?.Invoke("Smiths") == sha;
                bool ok = booted && renamed == expectHash && RwtHashKeyMigration.RwtKeysByHash == expectHash && hashOk;
                return (ok, $"RWT {version ?? "?"}: keys by hash {RwtHashKeyMigration.RwtKeysByHash}, renamed at boot {renamed}, boot ok {booted}, RWT hash matches {hashOk}");
            });
#endif
            return r;
        }
    }
}
