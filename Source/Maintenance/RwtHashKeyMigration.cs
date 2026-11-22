using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json.Linq;

#if RWT_RT
namespace KMHServerAddon.Maintenance
{
    // RWT 26.11.21.1 finds saves and guild files by hash and migrates neither: a returning player gets a new colony, and the old save is deleted on the next upload.
    internal static class RwtHashKeyMigration
    {
        internal sealed class Outcome
        {
            public int Saves, Guilds, Parked;
            public readonly List<string> Failures = new List<string>();
        }

        private static Type RwtType(string name) => typeof(UserFile).Assembly.GetType(name, throwOnError: false);

        internal static bool RwtKeysByHash => RwtType("RTShared.Files.Default.FL_Guild")?.GetProperty("Hash") != null;

        internal static Func<string, string> RwtHasher()
        {
            MethodInfo m = RwtType("RTShared.Misc.Hasher")?.GetMethod("GetHashFromString", new[] { typeof(object), typeof(bool) });
            return m == null ? null : s => (string)m.Invoke(null, new object[] { s, true });
        }

        // The only RWT whose save and guild layout this rename was checked against; another release may key them differently.
        internal static readonly string[] CheckedRwt = { "26.11.21.1" };

        internal static string RwtVersion => RwtType("RTShared.Misc.CommonValues")?.GetField("ExecutableVersion")?.GetRawConstantValue() as string ?? "";

        internal static bool RunAtBoot() => RunAtBoot(RwtVersion);

        internal static bool RunAtBoot(string rwtVersion)
        {
            if (!RwtKeysByHash) return true;
            bool checkedLayout = Array.IndexOf(CheckedRwt, rwtVersion) >= 0;
            Outcome o = Run(Master.UsersPath, Master.SavesPath, Master.GuildsPath,
                            Path.Combine(Persistence.KmhDataPaths.Folder, "RwtLegacyFiles"), RwtHasher(), apply: checkedLayout);
            if (!checkedLayout)
            {
                if (o.Saves + o.Guilds + o.Parked == 0 && o.Failures.Count == 0) return true;
                ServerLog.Error($"RWT files: RWT {rwtVersion} is not a release this Colony Nexus checked the save rename against, and {o.Saves + o.Parked} save(s) and {o.Guilds} RWT guild(s) still use the old names - not starting, and nothing was changed. Start once with RWT 26.11.21.1, or update Colony Nexus.");
                return false;
            }
            if (o.Saves + o.Guilds + o.Parked > 0)
                ServerLog.Info($"RWT files: {o.Saves} save(s) and {o.Guilds} RWT guild(s) renamed to the names this RWT looks for; {o.Parked} older copy(ies) kept in KMH-Data/RwtLegacyFiles.");
            foreach (string f in o.Failures) ServerLog.Error($"RWT files: {f}");
            return o.Failures.Count == 0;
        }

        internal static Outcome Run(string usersDir, string savesDir, string guildsDir, string parkDir, Func<string, string> hasher, bool apply = true)
        {
            var o = new Outcome();
            if (Directory.Exists(usersDir) && Directory.Exists(savesDir))
            {
                var accounts = new List<(string Name, string Hash)>();
                foreach (string userPath in Directory.GetFiles(usersDir, "*.json"))
                {
                    try
                    {
                        JObject user = JObject.Parse(File.ReadAllText(userPath));
                        accounts.Add(((string)user["Username"], (string)user["Hash"]));
                    }
                    catch (Exception ex) { ServerLog.Warn($"RWT files: skipped unreadable account file {Path.GetFileName(userPath)} ({ex.GetType().Name})."); }
                }
                // Exact names only: on a case-insensitive disk "Bob" must never take "bob.json".
                var saveNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (string p in Directory.GetFiles(savesDir, "*.json")) saveNames.Add(Path.GetFileName(p));
                var refused = new HashSet<string>(StringComparer.Ordinal);
                foreach ((string name, string hash) in accounts)
                {
                    if (!SafeName(name) || !saveNames.Contains(name + ".json")) continue;
                    int owners = 0;
                    foreach ((string other, _) in accounts) if (string.Equals(other, name, StringComparison.Ordinal)) owners++;
                    if (owners > 1) { if (refused.Add(name)) o.Failures.Add($"{owners} account files are named {name}, so whose save Saves/{name}.json is cannot be told - not starting; remove the stale account file."); continue; }
                    if (!SafeName(hash)) { o.Failures.Add($"{name}'s account has no hash, so RWT cannot keep its save Saves/{name}.json - not starting."); continue; }
                    string legacy = Path.Combine(savesDir, name + ".json"), keyed = Path.Combine(savesDir, hash + ".json");
                    if (string.Equals(legacy, keyed, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!apply) { if (File.Exists(keyed)) o.Parked++; else o.Saves++; continue; }
                    try
                    {
                        if (File.Exists(keyed)) { Park(legacy, Path.Combine(parkDir, "Saves")); o.Parked++; ServerLog.Warn($"RWT files: {name} has saves under both names; kept the account one, moved the older one to KMH-Data/RwtLegacyFiles/Saves."); }
                        else { File.Move(legacy, keyed); o.Saves++; ServerLog.Info($"RWT files: {name}'s save renamed to its account file name."); }
                    }
                    catch (Exception ex) { o.Failures.Add($"could not rename {name}'s save ({ex.GetType().Name}: {ex.Message}) - not starting, or RWT would give {name} a new colony."); }
                }
            }

            if (Directory.Exists(guildsDir))
                foreach (string guildPath in Directory.GetFiles(guildsDir, "*.json"))
                {
                    try
                    {
                        JObject guild = JObject.Parse(File.ReadAllText(guildPath));
                        string name = (string)guild["Name"];
                        if (!string.IsNullOrEmpty((string)guild["Hash"]) || string.IsNullOrEmpty(name)) continue;
                        if (!apply) { o.Guilds++; continue; }
                        if (hasher == null) { o.Failures.Add($"RWT guild '{name}' needs a hash, and this RWT build's hasher was not found."); continue; }
                        string hash = hasher(name), keyed = Path.Combine(guildsDir, hash + ".json");
                        if (File.Exists(keyed)) { Park(guildPath, Path.Combine(parkDir, "Guilds")); o.Parked++; ServerLog.Warn($"RWT files: guild '{name}' exists under both names; kept the hashed one."); continue; }
                        guild["Hash"] = hash;
                        string tmp = keyed + ".kmh-tmp";
                        File.WriteAllText(tmp, guild.ToString());
                        File.Move(tmp, keyed);
                        File.Delete(guildPath);
                        o.Guilds++;
                        ServerLog.Info($"RWT files: guild '{name}' given its hash and file name.");
                    }
                    catch (Exception ex) { o.Failures.Add($"could not migrate RWT guild file {Path.GetFileName(guildPath)} ({ex.GetType().Name}: {ex.Message}) - not starting, or its members would lose it."); }
                }
            return o;
        }

        private static bool SafeName(string s)
            => !string.IsNullOrEmpty(s) && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && s != "." && s != "..";

        private static void Park(string file, string dir)
        {
            Directory.CreateDirectory(dir);
            string to = Path.Combine(dir, Path.GetFileName(file));
            if (File.Exists(to)) to = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(file, to);
        }
    }
}
#endif
