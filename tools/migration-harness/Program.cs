using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// The REAL migration over a real schema-3 KMH-Data tree, one case per process: the migration uses process-wide
// statics, and a crash case is only honest if the next boot really is a next boot.
class P
{
    static string[] _probe;
    static Assembly _addon;
    static int _pass, _fail;

    static int Main(string[] argv)
    {
        if (argv.Length < 2) { Console.WriteLine("usage: migrate <addon.dll> <case> <probe-dir>..."); return 2; }
        string target = Path.GetFullPath(argv[0]);
        string which = argv[1];
        _probe = argv.Skip(2).Select(Path.GetFullPath).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        string root = Path.Combine(Path.GetTempPath(), "kmh-migrate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Directory.SetCurrentDirectory(root);          // KMH-Data and KMH-Data-Backups hang off the cwd
        _addon = Assembly.LoadFrom(target);

        Console.WriteLine($"=== {which} ===");
        Console.WriteLine($"tree: {root}");
        try
        {
            switch (which)
            {
                case "happy":             Happy(root); break;
                case "backup-fail":       BackupFail(root); break;
                case "verify-fail":       VerifyFail(root); break;
                case "journal-fail":      JournalFail(root); break;
                case "config-write-fail": ConfigWriteFail(root); break;
                case "config-read-fail":  ConfigReadFail(root); break;
                case "schema-fail":       SchemaFail(root); break;
                case "crash-journal":     CrashJournal(root); break;
                case "schema5-minutes":   Schema5Minutes(root); break;
                default: Console.WriteLine("unknown case: " + which); return 2;
            }
        }
        catch (Exception ex)
        {
            Exception e = ex.InnerException ?? ex;
            Console.WriteLine($"  THREW {e.GetType().Name}: {e.Message}");
            _fail++;
        }
        finally { try { Directory.SetCurrentDirectory(Path.GetTempPath()); Directory.Delete(root, true); } catch { } }

        Console.WriteLine($"checks: {_pass} pass, {_fail} fail");
        return _fail == 0 ? 0 : 1;
    }

    static void Happy(string root)
    {
        var seeded = SeedV130Tree(root);
        // The old single Connected switch (Federation + treasury), on: it must not survive as value movement.
        string features = Path.Combine(root, "KMH-Data", "Config", "Features.json");
        seeded["Features"] = "{\n  \"SchemaVersion\": 1,\n  \"Guilds\": true,\n  \"Connected\": true,\n  \"" + Marker + "\": 4242\n}";
        File.WriteAllText(features, seeded["Features"]);
        var unmarked = seeded.Where(kv => !kv.Value.Contains(Marker)).Select(kv => kv.Key).ToList();
        Check($"every one of the {seeded.Count} configs starts out carrying the owner's marker", unmarked.Count == 0,
              unmarked.Count == 0 ? "the reset assertions below cannot pass vacuously"
                                  : "NOT SEEDED: " + string.Join(", ", unmarked));
        Boot();

        // The boot scan runs before the migration, when this release's new stores are rightly absent.
        var since = new[] { "Connected treasury", "Federated guilds", "Federation outbox", "Identity/GlobalSetup" };
        var flagged = ScanFiles().Where(f => since.Contains(f.Label) && !f.AbsentIsNormal).Select(f => f.Label).ToList();
        int seen = ScanFiles().Count(f => since.Contains(f.Label));
        Check("the first scan of a v1.3.0 tree does not report this release's new stores missing",
              seen == since.Length && flagged.Count == 0,
              flagged.Count == 0 ? $"{seen} new store(s) absent as expected" : "REPORTED: " + string.Join(", ", flagged));

        object plan = Prepare();
        Check("a migration is due from the v1.3.0 tree", Needed(plan) && From(plan) < Schema && To(plan) == Schema,
              $"v{From(plan)} -> v{To(plan)}");
        Check("it is not blocked", Blocked(plan) == null, Blocked(plan) ?? "clear");

        string backupName = BackupName(plan);
        string backupDir = Path.Combine(root, "KMH-Data-Backups", backupName ?? "");
        Check("a dedicated pre-migration backup was created", backupName != null
              && backupName.Contains("premigration-config") && Directory.Exists(backupDir), backupName ?? "(none)");
        Check("that backup verifies against its own manifest", Verify(backupDir, out string verr), verr ?? "ok");
        string backedUpEconomy = Path.Combine(backupDir, "Config", "Economy.json");
        Check("the backup holds the owner's ORIGINAL values, not the defaults",
              File.Exists(backedUpEconomy) && File.ReadAllText(backedUpEconomy).Contains(Marker),
              "pre-migration Economy.json is recoverable from the backup");

        Pin(backupName);
        object journal = JournalBegin(backupName);
        Check("the journal opened and names the pre-migration backup", journal != null
              && JournalBackup(journal) == backupName, JournalBackup(journal) ?? "(none)");

        // Retention is 1 in the seeded Maintenance.json, so an unpinned rollback point is exactly what gets pruned.
        MakeBackup("noise");
        Check("retention cannot prune the pinned rollback point", Directory.Exists(backupDir),
              $"{Directory.GetDirectories(Path.Combine(root, "KMH-Data-Backups")).Length} backup(s) on disk");

        string failure = Apply(plan);
        Check("the migration completed", failure == null, failure ?? "no failure reported");
        Unpin(backupName);

        var after = ReadConfigs(root);
        var reset = seeded.Where(kv => !Kept(kv.Key)).ToList();
        var stillMarked = reset.Where(kv => after[kv.Key].Contains(Marker)).Select(kv => kv.Key).ToList();
        Check($"all {reset.Count} regenerated config(s) lost the owner's marker value", stillMarked.Count == 0,
              stillMarked.Count == 0 ? "every one is back at defaults" : "NOT RESET: " + string.Join(", ", stillMarked));

        var kept = seeded.Where(kv => Kept(kv.Key)).ToList();
        var lost = kept.Where(kv => !after[kv.Key].Contains(Marker)).Select(kv => kv.Key).ToList();
        Check($"all {kept.Count} preserved config(s) kept the owner's values", lost.Count == 0,
              lost.Count == 0 ? "identity and credentials untouched" : "WIPED: " + string.Join(", ", lost));

        var unparseable = after.Where(kv => !Parses(kv.Key, kv.Value)).Select(kv => kv.Key).ToList();
        Check("every config on disk parses into its class", unparseable.Count == 0,
              unparseable.Count == 0 ? $"{after.Count} file(s)" : "BROKEN: " + string.Join(", ", unparseable));

        List<string> report = Report();
        Check("the migration report says what happened", report.Count > 0
              && report.Any(l => l.Contains("regenerated and verified")), string.Join(" | ", report));

        Check($"ConfigSchema {Schema} is on disk, not only in memory",
              File.ReadAllText(Path.Combine(root, "KMH-Data", ".kmh-meta.json")).Contains(Stamp),
              "meta file stamped");

        var feat = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(features));
        Check("Connected=true comes out as Guild Federation on and the Connected Treasury off",
              feat["Connected"] == null && (bool?)feat["GuildFederation"] == true && (bool?)feat["ConnectedTreasury"] == false,
              feat.ToString(Newtonsoft.Json.Formatting.None));

        Check("the journal commits", JournalCommit(journal), "committed");
        JournalClear();
        Check("no journal is left behind after a clean migration",
              !File.Exists(Path.Combine(root, "KMH-Data", "Migrations", "journal.json")), "cleared");

        // Second boot: the whole point of persisting the stamp is that this does nothing.
        string marker = "second-boot-marker";
        File.WriteAllText(Path.Combine(root, "KMH-Data", "Config", "Chat.json"),
                          File.ReadAllText(Path.Combine(root, "KMH-Data", "Config", "Chat.json"))
                              .Replace("\"SchemaVersion\"", $"\"_{marker}\": 1,\n  \"SchemaVersion\""));
        Boot();
        object again = Prepare();
        Check("a second boot does NOT migrate again", !Needed(again), Needed(again) ? "IT RESET AGAIN" : "no-op");
        Check("and the owner's post-migration edit survived it",
              File.ReadAllText(Path.Combine(root, "KMH-Data", "Config", "Chat.json")).Contains(marker), marker);
    }

    static void BackupFail(string root)
    {
        SeedV130Tree(root);
        // A real refusal, not a mock: KMH-Data-Backups exists as a FILE, so the copy cannot reserve a folder.
        string backups = Path.Combine(root, "KMH-Data-Backups");
        if (Directory.Exists(backups)) Directory.Delete(backups, true);
        File.WriteAllText(backups, "not a directory");
        Boot();

        object plan = Prepare();
        Refused(plan, root, "the backup could not be taken");
    }

    static void VerifyFail(string root)
    {
        SeedV130Tree(root);
        Boot();
        object plan;
        using (FailVerify(_ => "injected: a file does not match its manifest hash")) plan = Prepare();
        Refused(plan, root, "the backup did not verify");
    }

    static void JournalFail(string root)
    {
        SeedV130Tree(root);
        Boot();
        object plan = Prepare();
        Check("the plan itself is fine", Needed(plan) && Blocked(plan) == null, Blocked(plan) ?? "clear");

        object journal;
        string journalFile = Path.Combine(root, "KMH-Data", "Migrations", "journal.json");
        using (FailWrites(p => string.Equals(p, journalFile, StringComparison.OrdinalIgnoreCase)
                               ? "injected: disk full" : null))
            journal = JournalBegin(BackupName(plan));

        Check("an unwritable journal stops the migration before it starts", journal == null,
              journal == null ? "Begin returned null" : "a migration would have run with no crash barrier");
        Check("and nothing was rewritten", OriginalValuesIntact(root), "configs untouched");
    }

    static void ConfigWriteFail(string root)
    {
        SeedV130Tree(root);
        Boot();
        object plan = Prepare();
        string chat = Path.Combine(root, "KMH-Data", "Config", "Chat.json");

        string failure;
        using (FailWrites(p => string.Equals(p, chat, StringComparison.OrdinalIgnoreCase)
                               ? "injected: disk full" : null))
            failure = Apply(plan);

        Check("one config that cannot be written fails the whole migration", failure != null,
              failure ?? "IT REPORTED SUCCESS");
        Check("the failure names the config", failure != null && failure.Contains("Chat"), failure ?? "");
        NoMixedGeneration(root);
    }

    static void ConfigReadFail(string root)
    {
        SeedV130Tree(root);
        Boot();
        object plan = Prepare();
        string sites = Path.Combine(root, "KMH-Data", "Config", "Sites.json");

        string failure;
        using (ReadBack(p => string.Equals(p, sites, StringComparison.OrdinalIgnoreCase)
                             ? "{ truncated" : File.ReadAllText(p)))
            failure = Apply(plan);

        Check("a config that writes but does not read back fails the migration", failure != null,
              failure ?? "IT REPORTED SUCCESS");
        Check("the failure names the config", failure != null && failure.Contains("Sites"), failure ?? "");
        NoMixedGeneration(root);
    }

    static void SchemaFail(string root)
    {
        SeedV130Tree(root);
        Boot();
        object plan = Prepare();
        string meta = Path.Combine(root, "KMH-Data", ".kmh-meta.json");

        string failure;
        using (FailWrites(p => string.Equals(p, meta, StringComparison.OrdinalIgnoreCase)
                               ? "injected: disk full" : null))
            failure = Apply(plan);

        Check("a schema stamp that cannot be persisted fails the migration", failure != null,
              failure ?? "IT REPORTED SUCCESS");
        Check("the failure says the schema could not be recorded",
              failure != null && failure.Contains("schema"), failure ?? "");
        Check($"the schema on disk is NOT {Schema}, so the next boot retries rather than skipping",
              !File.Exists(meta) || !File.ReadAllText(meta).Contains(Stamp),
              "stamp not written");
        NoMixedGeneration(root);
    }

    static void CrashJournal(string root)
    {
        SeedV130Tree(root);
        Boot();

        // What a crash leaves: a verified backup, an uncommitted journal naming it, configs already at defaults.
        object plan = Prepare();
        string backupName = BackupName(plan);
        Pin(backupName);
        object journal = JournalBegin(backupName);
        Check("the crash case starts from a real journal", journal != null, backupName ?? "(none)");
        string failure = Apply(plan);
        Check("and from a real completed rewrite", failure == null, failure ?? "applied");
        Check("the owner's values are gone at this point", !OriginalValuesIntact(root), "rewritten to defaults");
        // No Commit, no Clear: the process died here.

        Unpin(backupName);
        MakeBackup("noise-after-crash");
        Check("the rollback point survived a backup taken after the crash",
              Directory.Exists(Path.Combine(root, "KMH-Data-Backups", backupName)),
              "the next boot pins it before anything prunes");

        Boot();
        PinJournalBackup();
        RecoverIfIncomplete();
        Check("recovery restored the owner's ORIGINAL values from the pinned backup", OriginalValuesIntact(root),
              "Economy/Sites/Chat/World/Media all carry the owner's marker again");
        Check("the journal is cleared once recovery has run",
              !File.Exists(Path.Combine(root, "KMH-Data", "Migrations", "journal.json")), "cleared");
    }

    // A schema-5 server is not regenerated again; only World.json's one hours key moves to minutes.
    static void Schema5Minutes(string root)
    {
        string data = Path.Combine(root, "KMH-Data");
        Directory.CreateDirectory(Path.Combine(data, "Config", "Discord"));
        foreach ((string name, string path, object sample) in ConfigTargets())
        {
            var o = Newtonsoft.Json.Linq.JObject.FromObject(sample);
            o[Marker] = 4242;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, o.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        // What an older schema-5 server wrote: the window in hours and no minutes key.
        string world = Path.Combine(data, "Config", "World.json");
        var w = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(world));
        var scaling = (Newtonsoft.Json.Linq.JObject)w.SelectToken("WorldQuests.Scaling");
        scaling.Remove("ActivityWindowMinutes");
        scaling["ActivityWindowHours"] = 24;
        File.WriteAllText(world, w.ToString(Newtonsoft.Json.Formatting.Indented));
        string meta = Path.Combine(data, ".kmh-meta.json");
        File.WriteAllText(meta,
            "{\n  \"DataFormatVersion\": 1,\n  \"LastBuildVersion\": \"1.3.1\",\n" +
            "  \"LastRunUtc\": \"2026-01-01T00:00:00.0000000Z\",\n" +
            "  \"ServerId\": \"harness0000000000000000000000000\",\n  \"ConfigSchema\": 5\n}");

        Boot();
        // Boot backfills before it migrates, so the default lands beside the owner's value exactly as on a server.
        foreach ((string _, string path, object sample) in ConfigTargets())
            Call("KMHServerAddon.Persistence.JsonFileStore", "BackfillMissingFields", new[] { path, sample });
        var backfilled = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(world)).SelectToken("WorldQuests.Scaling");
        Check("the backfill wrote the default minutes beside the owner's hours, as a real boot does",
              backfilled?["ActivityWindowMinutes"] != null && (long?)backfilled?["ActivityWindowHours"] == 24,
              backfilled?.ToString(Newtonsoft.Json.Formatting.None) ?? "(no Scaling)");

        object plan = Prepare();
        Check("a schema 5 server has a migration due", Needed(plan) && From(plan) == 5 && To(plan) == Schema,
              $"v{From(plan)} -> v{To(plan)}");
        Check("it is not blocked", Blocked(plan) == null, Blocked(plan) ?? "clear");
        string backupName = BackupName(plan);
        Check("it still takes its own pre-migration backup", backupName != null
              && backupName.Contains("premigration-config-v5"), backupName ?? "(none)");

        Pin(backupName);
        string failure = Apply(plan);
        Unpin(backupName);
        Check("the migration completed", failure == null, failure ?? "no failure reported");

        var s = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(world)).SelectToken("WorldQuests.Scaling");
        Check("the owner's 24 hours arrive as 1440 minutes, not the default",
              (long?)s?["ActivityWindowMinutes"] == 1440 && s?["ActivityWindowHours"] == null,
              s?.ToString(Newtonsoft.Json.Formatting.None) ?? "(no Scaling)");

        var regenerated = ReadConfigs(root).Where(kv => !kv.Value.Contains(Marker)).Select(kv => kv.Key).ToList();
        Check("no config is regenerated on a server already past v1.3.0", regenerated.Count == 0,
              regenerated.Count == 0 ? "every owner value kept" : "REGENERATED: " + string.Join(", ", regenerated));

        Check($"ConfigSchema {Schema} is on disk", File.ReadAllText(meta).Contains(Stamp), "meta file stamped");

        Boot();
        object again = Prepare();
        Check("a second boot does NOT migrate again", !Needed(again), Needed(again) ? "IT MIGRATED AGAIN" : "no-op");
    }

    static void Refused(object plan, string root, string what)
    {
        Check($"the migration refuses when {what}", Needed(plan) && Blocked(plan) != null,
              Blocked(plan) ?? "IT WAS ALLOWED TO PROCEED");
        Check("no backup name is handed out to journal", BackupName(plan) == null, BackupName(plan) ?? "null");
        Check("the owner's configs are untouched", OriginalValuesIntact(root), "still the owner's values");
        Check("the schema on disk was not advanced",
              !File.Exists(Path.Combine(root, "KMH-Data", ".kmh-meta.json"))
              || !File.ReadAllText(Path.Combine(root, "KMH-Data", ".kmh-meta.json")).Contains(Stamp),
              "not stamped");
    }

    // A failed migration must not leave some files new and some old with the new schema stamped over both.
    static void NoMixedGeneration(string root)
    {
        string meta = Path.Combine(root, "KMH-Data", ".kmh-meta.json");
        Check("a failed migration is never stamped as current",
              !File.Exists(meta) || !File.ReadAllText(meta).Contains(Stamp),
              "schema stayed behind the failure");
    }

    static bool OriginalValuesIntact(string root)
    {
        foreach (string name in new[] { "Economy", "Sites", "Chat", "World", "Media" })
        {
            string p = Path.Combine(root, "KMH-Data", "Config", name + ".json");
            if (!File.Exists(p) || !File.ReadAllText(p).Contains(Marker)) return false;
        }
        return true;
    }

    // A recognisable value (4242, absurd for any real setting) answers "was it reset" without trusting a count.
    static Dictionary<string, string> SeedV130Tree(string root)
    {
        string data = Path.Combine(root, "KMH-Data");
        Directory.CreateDirectory(Path.Combine(data, "Config"));
        Directory.CreateDirectory(Path.Combine(data, "Config", "Discord"));
        Directory.CreateDirectory(Path.Combine(data, "Migrations"));

        var seeded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string path, object sample) in ConfigTargets())
        {
            string json = Marked(sample);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json);
            seeded[name] = json;
        }

        // Schema 3, so the schema-4 regeneration step is the one that is due.
        File.WriteAllText(Path.Combine(data, ".kmh-meta.json"),
            "{\n  \"DataFormatVersion\": 1,\n  \"LastBuildVersion\": \"1.3.0\",\n" +
            "  \"LastRunUtc\": \"2026-01-01T00:00:00.0000000Z\",\n" +
            "  \"ServerId\": \"harness0000000000000000000000000\",\n  \"ConfigSchema\": 3\n}");
        return seeded;
    }

    internal const string Marker = "_kmhOwnerTuned4242";

    // Assertions key on the marker: Staff.json and Discord.json have no integers, so "kept 4242" would pass vacuously.
    static string Marked(object sample)
    {
        var o = Newtonsoft.Json.Linq.JObject.FromObject(sample);
        Mark(o);
        if (o["SchemaVersion"] != null) o["SchemaVersion"] = 1;   // an old-schema file
        o[Marker] = 4242;
        return o.ToString(Newtonsoft.Json.Formatting.Indented);
    }

    static void Mark(Newtonsoft.Json.Linq.JToken node)
    {
        if (node is Newtonsoft.Json.Linq.JObject obj)
        {
            foreach (Newtonsoft.Json.Linq.JProperty p in obj.Properties().ToList())
            {
                if (p.Name == "SchemaVersion") continue;
                if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Integer) p.Value = 4242;
                else if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                    p.Value = !(bool)p.Value;
                else Mark(p.Value);
            }
        }
        else if (node is Newtonsoft.Json.Linq.JArray arr) foreach (var c in arr) Mark(c);
    }

    static Dictionary<string, string> ReadConfigs(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string path, object _) in ConfigTargets())
            map[name] = File.Exists(path) ? File.ReadAllText(path) : "";
        return map;
    }

    static bool Parses(string name, string json)
    {
        foreach ((string n, string _, object sample) in ConfigTargets())
            if (n == name) return (bool)Call("KMHServerAddon.Maintenance.KmhConfigValidation", "LoadTest",
                                             new object[] { json, sample, null });
        return false;
    }

    static Type T(string full) => _addon.GetType(full) ?? throw new InvalidOperationException("no type " + full);

    // The schema this build migrates to, read from the build: a pinned number goes stale on the next bump.
    static int Schema => (int)T("KMHServerAddon.KmhVersion")
        .GetField("ConfigSchema", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    static string Stamp => $"\"ConfigSchema\": {Schema}";

    static MethodInfo M(string type, string name) =>
        T(type).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"no {type}.{name}");

    static object Call(string type, string name, object[] args) => M(type, name).Invoke(null, args);

    static IEnumerable<(string, string, object)> ConfigTargets()
    {
        object seq = Call("KMHServerAddon.Maintenance.KmhConfigValidation", "ConfigTargets", null);
        foreach (object row in (IEnumerable)seq)
        {
            Type rt = row.GetType();
            yield return ((string)rt.GetField("Item1").GetValue(row),
                          (string)rt.GetField("Item2").GetValue(row),
                          rt.GetField("Item3").GetValue(row));
        }
    }

    static void Boot()
    {
        Call("KMHServerAddon.Persistence.KmhDataMeta", "ReconcileOnBoot", null);
        M("KMHServerAddon.Maintenance.KmhReadiness", "Ready").Invoke(null, null);
    }

    static List<(string Label, bool AbsentIsNormal)> ScanFiles()
    {
        Type integrity = T("KMHServerAddon.Persistence.KmhDataIntegrity");
        object result = integrity.GetMethod("Scan", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                        .Invoke(null, null);
        var files = (IEnumerable)result.GetType().GetField("Files").GetValue(result);
        var list = new List<(string, bool)>();
        foreach (object f in files)
            list.Add(((string)f.GetType().GetField("Label").GetValue(f), (bool)f.GetType().GetField("AbsentIsNormal").GetValue(f)));
        return list;
    }

    static object Prepare() => Call("KMHServerAddon.Maintenance.KmhConfigMigration", "Prepare", null);
    static string Apply(object plan) => (string)Call("KMHServerAddon.Maintenance.KmhConfigMigration", "Apply", new[] { plan });

    static List<string> Report()
    {
        object list = T("KMHServerAddon.Maintenance.KmhConfigMigration")
            .GetProperty("Report", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        return ((IEnumerable)list).Cast<string>().ToList();
    }

    static bool Field(object plan, string name) => (bool)plan.GetType().GetField(name).GetValue(plan);
    static bool Needed(object plan) => Field(plan, "Needed");
    static int From(object plan) => (int)plan.GetType().GetField("From").GetValue(plan);
    static int To(object plan) => (int)plan.GetType().GetField("To").GetValue(plan);
    static string BackupName(object plan) => (string)plan.GetType().GetField("BackupName").GetValue(plan);
    static string Blocked(object plan) => (string)plan.GetType().GetField("Blocked").GetValue(plan);

    static object JournalBegin(string backup)
    {
        object[] args = { backup, null };
        return M("KMHServerAddon.Maintenance.KmhMigrationJournal", "Begin").Invoke(null, args);
    }

    static string JournalBackup(object journal) =>
        (string)journal.GetType().GetProperty("BackupDirName").GetValue(journal);

    static bool JournalCommit(object journal)
    {
        object[] args = { null };
        return (bool)journal.GetType()
            .GetMethod("Commit", BindingFlags.Public | BindingFlags.Instance).Invoke(journal, args);
    }

    static void JournalClear() => Call("KMHServerAddon.Maintenance.KmhMigrationJournal", "Clear", null);
    static void RecoverIfIncomplete() => Call("KMHServerAddon.Maintenance.KmhMigrationRecovery", "RecoverIfIncomplete", null);

    static void Pin(string name) => Call("KMHServerAddon.Persistence.KmhDataBackup", "Pin", new object[] { name });
    static void Unpin(string name) => Call("KMHServerAddon.Persistence.KmhDataBackup", "Unpin", new object[] { name });

    // What Main.cs does before anything prunes: pin whatever an uncommitted journal names.
    static void PinJournalBackup()
    {
        object j = Call("KMHServerAddon.Maintenance.KmhMigrationJournal", "Read", null);
        if (j != null) Pin(JournalBackup(j));
    }

    static bool Verify(string dir, out string error)
    {
        object[] args = { dir, null };
        bool ok = (bool)M("KMHServerAddon.Persistence.KmhDataBackup", "Verify").Invoke(null, args);
        error = (string)args[1];
        return ok;
    }

    static void MakeBackup(string reason)
    {
        object[] args = { reason, null, null };
        M("KMHServerAddon.Persistence.KmhDataBackup", "TryCreate").Invoke(null, args);
    }

    static IDisposable FailWrites(Func<string, string> reason) => (IDisposable)
        M("KMHServerAddon.Persistence.JsonFileStore", "FailWritesForTest").Invoke(null, new object[] { reason });

    static IDisposable FailVerify(Func<string, string> reason) => (IDisposable)
        M("KMHServerAddon.Persistence.KmhDataBackup", "FailVerifyForTest").Invoke(null, new object[] { reason });

    static IDisposable ReadBack(Func<string, string> reader) => (IDisposable)
        M("KMHServerAddon.Maintenance.KmhConfigRegeneration", "ReadBackForTest").Invoke(null, new object[] { reader });

    static bool Kept(string name) => (bool)
        M("KMHServerAddon.Maintenance.KmhConfigRegeneration", "IsKept").Invoke(null, new object[] { name });

    static void Check(string what, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"  ok    {what}  ({detail})"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}  -> {detail}"); }
    }

    static Assembly Resolve(object s, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name + ".dll";
        foreach (string dir in _probe)
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { } }
        }
        return null;
    }
}
