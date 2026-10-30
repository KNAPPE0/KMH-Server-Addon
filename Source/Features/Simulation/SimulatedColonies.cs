using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Simulation
{
    internal static class SimulatedColonies
    {
        internal sealed class Colony
        {
            public int Tile;
            public string Username;
            public string SettlementFile;
            public string MapFile;
            public bool HasMap => MapFile != null && File.Exists(MapFile);
        }

        public static void Command(string[] args, Action<string> reply)
        {
            SimulatedAccounts.Reload();
            if (args == null || args.Length < 2 || !string.Equals(args[1], "colonies", StringComparison.OrdinalIgnoreCase))
            {
                SimulatedAccounts.Report(reply);
                return;
            }
            string action = args.Length > 2 ? args[2].ToLowerInvariant() : "list";
            switch (action)
            {
                case "list": List(reply); break;
                case "remove" when args.Length > 3: Remove(c => string.Equals(c.Username, args[3], StringComparison.OrdinalIgnoreCase), args[3], reply); break;
                case "remove-all": Remove(_ => true, "every simulated player", reply); break;
                default: reply("Usage: kmh sim colonies [list | remove <name> | remove-all]"); break;
            }
        }

        internal static string RwtPath(string property)
            => SimulatedColonyGate.RwtType("Core.Master")?.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;

        // Settlement files RWT wrote for registered simulated accounts; a real player's file is never returned.
        internal static List<Colony> Find(string settlementsDir, string mapsDir, Func<string, bool> isSimulated)
        {
            var found = new List<Colony>();
            if (string.IsNullOrEmpty(settlementsDir) || !Directory.Exists(settlementsDir)) return found;
            foreach (string f in Directory.GetFiles(settlementsDir, "*.json"))
            {
                try
                {
                    JObject o = JObject.Parse(File.ReadAllText(f));
                    string user = (string)o["Username"];
                    if (o["Tile"] == null || !isSimulated(user)) continue;
                    int tile = (int)o["Tile"];
                    found.Add(new Colony { Tile = tile, Username = user, SettlementFile = f,
                                           MapFile = string.IsNullOrEmpty(mapsDir) ? null : Path.Combine(mapsDir, tile + ".json") });
                }
                catch { }
            }
            return found.OrderBy(c => c.Username, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Tile).ToList();
        }

        // Same count as `kmh sim`: anyone named who is not simulated; a session still logging in is nobody yet.
        internal static string RemovalRefusal(IEnumerable<string> rwtOnline, IEnumerable<string> linkedOnline)
        {
            int rwt = SimulatedAccounts.People(rwtOnline), linked = SimulatedAccounts.People(linkedOnline);
            int people = Math.Max(rwt, linked);
            return people == 0 ? null
                : $"Refused: {people} {(people == 1 ? "person is" : "people are")} online (RWT {rwt}, Colony Nexus {linked}). "
                + "A settlement vanishing under someone's caravan or raid is worse than waiting - run it when nobody is on.";
        }

        private static void List(Action<string> reply)
        {
            string settlements = RwtPath("SettlementsPath"), maps = RwtPath("MapsPath");
            if (settlements == null) { reply("This RWT build does not say where its settlements are kept."); return; }
            List<Colony> all = Find(settlements, maps, SimulatedAccounts.IsSimulated);
            int total = Directory.Exists(settlements) ? Directory.GetFiles(settlements, "*.json").Length : 0;
            reply($"=== Simulated colonies: {all.Count} of {total} settlement(s) ===");
            foreach (Colony c in all)
                reply($"  {c.Username,-20} tile {c.Tile,-7} map {(c.HasMap ? $"yes ({new FileInfo(c.MapFile).Length / 1024} KB, {File.GetLastWriteTimeUtc(c.MapFile):yyyy-MM-dd HH:mm}Z)" : "NO - view, raid and wealth have nothing to load")}");
            if (all.Count == 0) reply("  None. The population tool founds them when control.json Colonies.Enabled is true.");
        }

        private static void Remove(Func<Colony, bool> which, string label, Action<string> reply)
        {
            string refusal = RemovalRefusal(SimulatedAccounts.RwtOnline(), PlayerStats.PlayerStatsStore.OnlineNow());
            if (refusal != null) { reply(refusal); return; }
            string settlements = RwtPath("SettlementsPath"), maps = RwtPath("MapsPath");
            if (settlements == null) { reply("This RWT build does not say where its settlements are kept - nothing removed."); return; }
            List<Colony> targets = Find(settlements, maps, SimulatedAccounts.IsSimulated).Where(which).ToList();
            if (targets.Count == 0) { reply($"No simulated colony for {label}. Only accounts in Simulation/accounts.json are touched."); return; }
            foreach (Colony c in targets)
            {
                string how = RemoveSettlement(c);
                bool mapGone = false;
                try { if (c.HasMap) { File.Delete(c.MapFile); mapGone = true; } } catch (Exception ex) { how += $"; map NOT deleted ({ex.Message})"; }
                string line = $"Simulated players: removed {c.Username}'s colony at tile {c.Tile} ({how}{(mapGone ? ", map deleted" : "")}).";
                ServerLog.Info(line);
                reply("  " + line);
            }
        }

        // RWT's own removal (what its player reset uses): deletes the file and tells every client to drop the settlement.
        private static string RemoveSettlement(Colony c)
        {
            try
            {
                Type pm = SimulatedColonyGate.RwtType("PacketManagers.PM_Settlements") ?? SimulatedColonyGate.RwtType("PacketManager.PM_Settlements");
                MethodInfo rm = pm?.GetMethod("RemoveSettlement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                Type packetType = rm?.GetParameters().Length == 2 ? rm.GetParameters()[1].ParameterType : null;
                PropertyInfo fileProp = packetType?.GetProperty("File");
                if (fileProp != null)
                {
                    object packet = Activator.CreateInstance(packetType);
                    object file = fileProp.GetValue(packet) ?? Activator.CreateInstance(fileProp.PropertyType);
                    file.GetType().GetProperty("Tile")?.SetValue(file, c.Tile);
                    file.GetType().GetProperty("Username")?.SetValue(file, c.Username);
                    fileProp.SetValue(packet, file);
                    rm.Invoke(null, new[] { null, packet });
                    if (!File.Exists(c.SettlementFile)) return "removed by RWT, clients told";
                }
            }
            catch (Exception ex) { ServerLog.Warn($"Simulated players: RWT's settlement removal threw for tile {c.Tile} ({(ex.InnerException ?? ex).Message})."); }
            // No RWT removal to call: the file goes, and clients drop the settlement only when they next join.
            try { File.Delete(c.SettlementFile); } catch (Exception ex) { return $"settlement NOT deleted: {ex.Message}"; }
            return "settlement file deleted; connected clients still draw it until they rejoin or the server restarts";
        }
    }
}
