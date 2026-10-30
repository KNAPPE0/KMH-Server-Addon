using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Driven against real folders: the backup and restore failures that matter are filesystem ones.
    internal static class KmhBackupSelfTest
    {
        private static string Scratch(string name)
            => Path.Combine(Path.GetTempPath(), "kmh-backup-selftest", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            // The offline runner boots nothing, so without this the checks resolve KMH-Data against the launch folder.
            KmhDataPaths.EnsureFolder();
            if (!File.Exists(KmhDataPaths.TreasuryFile)) File.WriteAllText(KmhDataPaths.TreasuryFile, "{}");

            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            // KMH-Data may own a manifest.txt; overwriting it leaves the backup unverifiable, so no migration can run.
            Check("Backup: a file KMH-Data owns is never overwritten by the backup's own manifest", () =>
            {
                string collide = Path.Combine(KmhDataPaths.Folder, KmhDataBackup.ManifestName);
                bool wouldCopy = KmhDataBackup.WouldBackUp(collide);
                bool distinct = !KmhDataBackup.ManifestName.Equals("manifest.txt", StringComparison.OrdinalIgnoreCase);
                return (!wouldCopy && distinct,
                        !distinct ? "the backup's manifest still uses a name KMH-Data can also use"
                                  : wouldCopy ? "a nested backup manifest would be copied into the next backup"
                                              : $"backup metadata is '{KmhDataBackup.ManifestName}', excluded from the copy");
            });

            Check("Backup: a KMH-Data file called manifest.txt survives a backup and verifies", () =>
            {
                string owners = Path.Combine(KmhDataPaths.Folder, "manifest.txt");
                bool preexisting = File.Exists(owners);
                string saved = preexisting ? File.ReadAllText(owners) : null;
                try
                {
                    File.WriteAllText(owners, "the owner's own manifest, not the backup's");
                    if (!KmhDataBackup.TryCreate("selftest-manifest-collision", out string dir, out string err))
                        return (false, $"backup failed: {err}");
                    string copied = Path.Combine(dir, "manifest.txt");
                    bool kept = File.Exists(copied)
                             && File.ReadAllText(copied) == "the owner's own manifest, not the backup's";
                    bool verified = KmhDataBackup.Verify(dir, out string why);
                    return (kept && verified,
                            !kept ? "THE OWNER'S FILE WAS OVERWRITTEN BY THE BACKUP'S MANIFEST"
                                  : verified ? "copied intact, and the backup still verifies" : $"did not verify: {why}");
                }
                finally
                {
                    try { if (preexisting) File.WriteAllText(owners, saved); else File.Delete(owners); } catch { }
                }
            });

            Check("Backup: the manifest names the build, schema and generation that wrote it", () =>
            {
                if (!KmhDataBackup.TryCreate("selftest-identity", out string dir, out string err))
                    return (false, $"backup failed: {err}");
                try
                {
                    string manifest = File.ReadAllText(Path.Combine(dir, KmhDataBackup.ManifestName));
                    bool build = manifest.Contains($"build: {KmhVersion.Build} ({KmhVersion.BuildTag})");
                    bool proto = manifest.Contains($"protocol: {KmhVersion.Protocol}");
                    bool schema = manifest.Contains($"config-schema: {KmhVersion.ConfigSchema}")
                               && manifest.Contains($"data-schema: {KmhVersion.DataSchema}");
                    bool gen = manifest.Contains("data-generation: ");
                    bool id = manifest.Contains("backup-id: ");
                    bool hashed = manifest.Contains("hash: sha256");
                    bool ok = build && proto && schema && gen && id && hashed;
                    return (ok, ok ? "build, protocol, both schemas, generation, id and hash algorithm are all recorded"
                                  : $"build={build}, protocol={proto}, schema={schema}, generation={gen}, id={id}, hashed={hashed}");
                }
                finally { TryDelete(dir); }
            });

            Check("Backup: every file carries a hash, and a changed byte is caught", () =>
            {
                if (!KmhDataBackup.TryCreate("selftest-verify", out string dir, out string err))
                    return (false, $"backup failed: {err}");
                try
                {
                    bool cleanVerifies = KmhDataBackup.Verify(dir, out string verifyErr);
                    if (!cleanVerifies) return (false, $"a fresh backup did not verify: {verifyErr}");

                    // Corrupt one file without changing its length, so only the hash can notice.
                    string victim = FirstDataFile(dir);
                    if (victim == null) return (false, "the backup contained no data file to corrupt");
                    byte[] bytes = File.ReadAllBytes(victim);
                    if (bytes.Length == 0) return (false, "the chosen file was empty");
                    bytes[bytes.Length - 1] ^= 0xFF;
                    File.WriteAllBytes(victim, bytes);

                    bool caught = !KmhDataBackup.Verify(dir, out string corruptErr);
                    bool named = corruptErr != null && corruptErr.IndexOf("changed", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool ok = caught && named;
                    return (ok, ok ? "a same-length edit is caught by hash and named"
                                  : $"caught={caught}, namesTheFile={named} ({corruptErr})");
                }
                finally { TryDelete(dir); }
            });

            Check("Restore: a backup that fails verification is refused before live data is touched", () =>
            {
                string fake = Scratch("bad-backup");
                try
                {
                    Directory.CreateDirectory(fake);
                    File.WriteAllText(Path.Combine(fake, "Treasury.json"), "{}");
                    // Names a file that is not there: exactly what a truncated copy looks like.
                    File.WriteAllText(Path.Combine(fake, "manifest.txt"),
                        "KMH-Data backup\nhash: sha256\n\nTreasury.json\t2\t" + "0".PadLeft(64, '0') + "\nGone.json\t5\tdeadbeef\n");

                    bool verified = KmhDataBackup.Verify(fake, out string why);
                    bool namesBoth = why != null && why.IndexOf("Gone.json", StringComparison.Ordinal) >= 0;
                    bool ok = !verified && namesBoth;
                    return (ok, ok ? "the missing and the mismatched file are both named, and nothing was restored"
                                  : $"refused={!verified}, namesMissing={namesBoth} ({why})");
                }
                finally { TryDelete(fake); }
            });

            Check("Backup: a backup taken before the stores load does not flush empty ones over the data", () =>
            {
                KmhReadyState was = KmhReadiness.State;
                string probe = KmhDataPaths.TreasuryFile;
                string original = File.Exists(probe) ? File.ReadAllText(probe) : null;
                // Stands in for a real server's data: what is on disk is the truth, and memory holds nothing yet.
                const string sentinel = "{\"kmh-selftest-preload-sentinel\":true}";
                try
                {
                    File.WriteAllText(probe, sentinel);

                    // Exactly the pre-restore backup's position in boot: stores are declared, none has loaded.
                    KmhReadiness.ResetForTest(KmhReadyState.Starting);
                    if (!KmhDataBackup.TryCreate("selftest-preload", out string dir, out string err))
                        return (false, $"backup failed: {err}");
                    TryDelete(dir);

                    bool untouched = File.Exists(probe) && File.ReadAllText(probe) == sentinel;
                    return (untouched, untouched ? "the file on disk is exactly as it was found"
                                                 : "the backup flushed a not-yet-loaded store over live data");
                }
                finally
                {
                    KmhReadiness.ResetForTest(was); if (was == KmhReadyState.Ready) KmhReadiness.Ready();
                    try { if (original != null) File.WriteAllText(probe, original); else File.Delete(probe); } catch { }
                }
            });

            Check("Restore: what a backup deliberately omits is carried across, not destroyed", () =>
            {
                // A backup holds no copy of these, so deleting them on rollback is pure loss - including its own Debug log.
                var carried = new List<string>();
                foreach (string folder in new[] { "Snapshots", "Debug", "Support", "MediaCache" })
                {
                    string probe = Path.Combine(KmhDataPaths.Folder, folder, "probe.txt");
                    if (KmhDataBackup.WouldBackUp(probe)) return (false, $"{folder} is backed up after all - this test is out of date");
                    if (KmhDataBackup.SurvivesRestore(probe)) carried.Add(folder);
                }
                bool ok = carried.Count == 4;
                return (ok, ok ? "Snapshots, Debug, Support and MediaCache all survive a rollback"
                              : "carried across: " + string.Join(", ", carried));
            });

            // File.Copy gives a copied signing key the backup folder's access list instead of its own.
            Check("Backup: a key file's copy is readable only by this account, like the key itself", () =>
            {
                string probe = Path.Combine(KmhDataPaths.Folder, "Identity", "selftest-probe-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".key");
                string dir = null;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(probe));
                    File.WriteAllText(probe, "not a key");
                    if (!KmhDataBackup.TryCreate("selftest-keyacl", out dir, out string err)) return (false, "backup failed: " + err);
                    string copy = Path.Combine(dir, "Identity", Path.GetFileName(probe));
                    if (!File.Exists(copy)) return (false, "the probe was not backed up");
                    bool tight;
                    string seen;
                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                    {
                        var acl = new FileInfo(copy).GetAccessControl();
                        int rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count;
                        tight = acl.AreAccessRulesProtected && rules == 1;
                        seen = $"protected={acl.AreAccessRulesProtected} rules={rules}";
                    }
                    else
                    {
                        UnixFileMode mode = File.GetUnixFileMode(copy);
                        tight = mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        seen = "mode=" + mode;
                    }
                    return (tight, seen);
                }
                finally
                {
                    try { File.Delete(probe); } catch { }
                    if (dir != null) try { Directory.Delete(dir, true); } catch { }
                }
            });

            // Inherited access makes the bot token readable by every local account and writable by any signed-in one.
            Check("Credentials: the Discord config holding the bot token is readable only by this account once loaded", () =>
            {
                string path = KmhDataPaths.DiscordConfigFile;
                bool created = !File.Exists(path);
                try
                {
                    if (created) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "{}"); }
                    Features.Discord.DiscordConfig.LoadOrDefault();
                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                    {
                        var acl = new FileInfo(path).GetAccessControl();
                        int rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count;
                        return (acl.AreAccessRulesProtected && rules == 1, $"protected={acl.AreAccessRulesProtected} rules={rules}");
                    }
                    UnixFileMode mode = File.GetUnixFileMode(path);
                    return (mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "mode=" + mode);
                }
                finally { if (created) try { File.Delete(path); } catch { } }
            });

            // Matching these folder names anywhere in the absolute path makes a server under bin\Debug back up nothing.
            Check("Backup: a server installed under a folder named Debug or Support still backs up its data", () =>
            {
                var lost = new List<string>();
                foreach (string ancestor in new[] { "Debug", "Support", "Snapshots", "MediaCache" })
                {
                    string root = Path.Combine(Path.GetTempPath(), ancestor, "srv", "KMH-Data");
                    string live = Path.Combine(root, "Treasury", "Treasury.json");
                    if (!KmhDataBackup.WouldBackUp(root, live) || KmhDataBackup.SurvivesRestore(root, live)) lost.Add(ancestor);
                    if (KmhDataBackup.WouldBackUp(root, Path.Combine(root, ancestor, "x.txt"))) lost.Add(ancestor + "/ inside");
                }
                return (lost.Count == 0, lost.Count == 0 ? "only KMH-Data's own Debug, Support, Snapshots and MediaCache are left out"
                                                         : "wrong under: " + string.Join(", ", lost));
            });

            // The folder name is a second and a reason, and the migration journal stores that name to find its backup later.
            Check("Backup: two backups taken in the same second are two backups, not one merged folder", () =>
            {
                var made = new List<string>();
                try
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (!KmhDataBackup.TryCreate("selftest-collision", out string dir, out string err))
                            return (false, $"backup {i + 1} failed: {err}");
                        made.Add(dir);
                    }
                    var distinct = new HashSet<string>(made, StringComparer.OrdinalIgnoreCase);
                    bool unique = distinct.Count == made.Count;
                    bool allExist = made.TrueForAll(Directory.Exists);
                    // A merged folder still parses, so the stamp has to survive whatever makes the name unique.
                    bool stamped = made.TrueForAll(d => KmhDataBackup.StampUtcIso(Path.GetFileName(d)).Length > 0);
                    bool ok = unique && allExist && stamped;
                    return (ok, ok ? $"{made.Count} same-reason backups produced {distinct.Count} distinct, readable folders"
                                  : $"distinct={distinct.Count}/{made.Count}, allExist={allExist}, stampParses={stamped}");
                }
                finally { foreach (string d in made) TryDelete(d); }
            });

            // Retention sorts snapshots by the time in the folder name, so an unreadable name prunes the wrong one.
            Check("Snapshot: snapshots taken in the same minute keep their own folders and stay sortable", () =>
            {
                string parent = Scratch("snapleaf");
                try
                {
                    string baseName = KmhSnapshot.FolderName(null);
                    var made = new List<string>();
                    for (int i = 0; i < 3; i++)
                    {
                        string d = KmhSnapshot.ReserveLeaf(parent, baseName, named: false);
                        Directory.CreateDirectory(d);
                        made.Add(d);
                    }
                    bool unique = new HashSet<string>(made, StringComparer.OrdinalIgnoreCase).Count == made.Count;
                    // A named snapshot is an identity the caller chose, so it must NOT be made unique.
                    string named1 = KmhSnapshot.ReserveLeaf(parent, "match-42", named: true);
                    Directory.CreateDirectory(named1);
                    string named2 = KmhSnapshot.ReserveLeaf(parent, "match-42", named: true);
                    bool namedStable = string.Equals(named1, named2, StringComparison.OrdinalIgnoreCase);
                    bool secondsInName = baseName.Length == KmhSnapshot.LeafFormat.Length;
                    bool ok = unique && namedStable && secondsInName;
                    return (ok, ok ? $"3 auto snapshots kept 3 folders; a named one stays '{Path.GetFileName(named1)}'"
                                  : $"unique={unique}, namedStable={namedStable}, secondsInName={secondsInName}");
                }
                finally { TryDelete(parent); }
            });

            // A migration rewrites data no restore can reconstruct, so the override must not survive one use.
            Check("Migration: the force-without-backup override is consumed, never left armed", () =>
            {
                string token = KmhDataPaths.ForceMigrationFile;
                try
                {
                    bool withoutToken = KmhDataMeta.TakeForceMigrationToken();
                    File.WriteAllText(token, "forced by selftest");
                    bool firstUse  = KmhDataMeta.TakeForceMigrationToken();
                    bool secondUse = KmhDataMeta.TakeForceMigrationToken();
                    bool gone = !File.Exists(token);
                    bool ok = !withoutToken && firstUse && !secondUse && gone;
                    return (ok, ok ? "absent reads false, one use consumes it, the next boot is refused again"
                                  : $"absent={withoutToken}, firstUse={firstUse}, secondUse={secondUse}, consumed={gone}");
                }
                finally { try { if (File.Exists(token)) File.Delete(token); } catch { } }
            });

            // A send reaching the transport is not receipt, so the queue must survive it and only an ack may clear it.
            Check("Notices: only an acknowledgement removes a queued notice, and acking twice is harmless", () =>
            {
                const string who = "selftest-ack-user";
                const string other = "selftest-ack-other";
                try
                {
                    Features.Notifications.NotificationStore.ClearUser(who);
                    Features.Notifications.NotificationStore.ClearUser(other);
                    Features.Notifications.NotificationStore.Enqueue(who, "neutral", "one", "first");
                    Features.Notifications.NotificationStore.Enqueue(who, "neutral", "two", "second");
                    Features.Notifications.NotificationStore.Enqueue(other, "neutral", "theirs", "not yours");

                    var mine = Features.Notifications.NotificationStore.PeekForUser(who);
                    bool peekKeeps = mine.Count == 2
                        && Features.Notifications.NotificationStore.PeekForUser(who).Count == 2;
                    bool haveIds = mine.TrueForAll(n => !string.IsNullOrEmpty(n.Id));

                    string firstId = mine[0].Id;
                    int removed = Features.Notifications.NotificationStore.AckDelivered(who, new List<string> { firstId });
                    bool removedOne = removed == 1
                        && Features.Notifications.NotificationStore.PeekForUser(who).Count == 1;
                    // Idempotent: a repeated ack is what a lost reply looks like from the client's side.
                    bool twiceHarmless = Features.Notifications.NotificationStore.AckDelivered(who, new List<string> { firstId }) == 0
                        && Features.Notifications.NotificationStore.PeekForUser(who).Count == 1;
                    // Another player's id must not clear anything of theirs - the ack is scoped to its own queue.
                    string theirId = Features.Notifications.NotificationStore.PeekForUser(other)[0].Id;
                    bool crossRejected = Features.Notifications.NotificationStore.AckDelivered(who, new List<string> { theirId }) == 0
                        && Features.Notifications.NotificationStore.PeekForUser(other).Count == 1;

                    bool ok = peekKeeps && haveIds && removedOne && twiceHarmless && crossRejected;
                    return (ok, ok ? "peek keeps the queue, one ack removes one, repeats and cross-user acks do nothing"
                                  : $"peekKeeps={peekKeeps}, ids={haveIds}, removedOne={removedOne}, twice={twiceHarmless}, crossUser={crossRejected}");
                }
                finally
                {
                    Features.Notifications.NotificationStore.ClearUser(who);
                    Features.Notifications.NotificationStore.ClearUser(other);
                }
            });

            Check("Restore: an interrupted swap leaves the old data authoritative, never a mix", () =>
            {
                string dataRoot = KmhDataPaths.Folder;
                string previous = dataRoot + ".previous";
                string staging  = dataRoot + ".restoring";
                // Beside the live folder, not in TEMP: Directory.Move cannot cross volumes.
                string moved    = dataRoot + ".selftest-aside";
                if (Directory.Exists(moved)) return (false, "an interrupted run left " + moved + " - remove it and rerun");
                bool hadData = Directory.Exists(dataRoot), aside = false;
                try
                {
                    // The exact state a crash between the two Directory.Move calls leaves behind.
                    if (hadData) { Directory.Move(dataRoot, moved); aside = true; }
                    Directory.CreateDirectory(previous);
                    File.WriteAllText(Path.Combine(previous, "Treasury.json"), "{\"old\":true}");
                    Directory.CreateDirectory(staging);
                    File.WriteAllText(Path.Combine(staging, "Treasury.json"), "{\"new\":true}");

                    KmhDataBackup.FinishInterruptedRestore();

                    bool liveBack = Directory.Exists(dataRoot);
                    bool isOld = liveBack && File.Exists(Path.Combine(dataRoot, "Treasury.json"))
                              && File.ReadAllText(Path.Combine(dataRoot, "Treasury.json")).Contains("old");
                    bool cleaned = !Directory.Exists(staging) && !Directory.Exists(previous);
                    bool ok = liveBack && isOld && cleaned;
                    return (ok, ok ? "the pre-restore data is authoritative again and both scratch folders are gone"
                                  : $"liveRestored={liveBack}, isOldData={isOld}, scratchCleaned={cleaned}");
                }
                finally
                {
                    // On any outcome: the fixture goes and the real folder comes back before the suite reads it again.
                    // A Move that never happened leaves the live data where it is.
                    if (aside || !hadData) { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
                    if (aside && Directory.Exists(moved)) Directory.Move(moved, dataRoot);
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                    if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
                }
            });

            Check("Restore: the backup being restored is pinned while the safety backup prunes to retention", () =>
            {
                if (!KmhDataBackup.TryCreate("selftest-pin", out string dir, out string err)) return (false, "backup: " + err);
                string name = Path.GetFileName(dir);
                bool pinnedDuring = false;
                // Stopped before the safety backup, so nothing live is touched.
                KmhDataBackup.AfterSourcePinnedForTest = n =>
                {
                    pinnedDuring = n == name && KmhDataBackup.IsPinned(n);
                    throw new OperationCanceledException("selftest: stopped before anything is touched");
                };
                try { KmhDataBackup.Restore(name, out _, out _); }
                catch (OperationCanceledException) { }
                finally { KmhDataBackup.AfterSourcePinnedForTest = null; TryDelete(dir); }
                bool released = !KmhDataBackup.IsPinned(name);
                bool ok = pinnedDuring && released;
                return (ok, ok ? "pinned for the restore, released after"
                              : $"pinnedDuring={pinnedDuring}, releasedAfter={released} - THE SAFETY BACKUP CAN PRUNE THE BACKUP BEING RESTORED");
            });

            Check("Restore: a restore that lands names itself in KMH-Data until its generation bump is written", () =>
            {
                string src = null, safety = null;
                try
                {
                    // Restores a backup taken a moment ago, so the data it puts back is the data already there.
                    if (!KmhDataBackup.TryCreate("selftest-land", out src, out string err)) return (false, "backup: " + err);
                    string name = Path.GetFileName(src);
                    if (!KmhDataBackup.Restore(name, out safety, out string why)) return (false, "restore: " + why);
                    string marker = Path.Combine(KmhDataPaths.Folder, KmhDataBackup.RestoredMarkerName);
                    bool named = File.Exists(marker) && File.ReadAllText(marker).Trim() == name && KmhDataBackup.RestoredThisBoot == name;
                    KmhDataBackup.ClearRestoredThisBoot();
                    bool cleared = !File.Exists(marker) && string.IsNullOrEmpty(KmhDataBackup.RestoredThisBoot);
                    bool ok = named && cleared;
                    return (ok, ok ? "the landed restore is named on disk until the bump clears it"
                                  : $"namedOnDisk={named}, clearedAfterBump={cleared}");
                }
                finally
                {
                    KmhDataBackup.ClearRestoredThisBoot();
                    if (src != null) TryDelete(src);
                    if (safety != null) TryDelete(Path.Combine(KmhDataPaths.BackupRoot, safety));
                }
            });

            // Restoring to undo a cheat is the common case, and it must not lift the ban that followed the cheat.
            Check("Restore: a ban issued after the backup is still in force once that backup is restored", () =>
            {
                string src = null, safety = null;
                string bans = KmhDataPaths.BansFile;
                string had = File.Exists(bans) ? File.ReadAllText(bans) : null;
                try
                {
                    if (!KmhDataBackup.TryCreate("selftest-sanction", out src, out string err)) return (false, "backup: " + err);
                    // Same document, different bytes: what the live list holds now that the backup does not.
                    string issued = (had ?? "{}") + "\n ";
                    Directory.CreateDirectory(Path.GetDirectoryName(bans));
                    File.WriteAllText(bans, issued);
                    if (!KmhDataBackup.Restore(Path.GetFileName(src), out safety, out string why)) return (false, "restore: " + why);
                    bool kept = File.Exists(bans) && File.ReadAllText(bans) == issued;
                    return (kept, kept ? "the live ban list came through the restore" : "THE RESTORE PUT BACK THE OLDER BAN LIST");
                }
                finally
                {
                    KmhDataBackup.ClearRestoredThisBoot();
                    if (had != null) File.WriteAllText(bans, had); else if (File.Exists(bans)) File.Delete(bans);
                    if (src != null) TryDelete(src);
                    if (safety != null) TryDelete(Path.Combine(KmhDataPaths.BackupRoot, safety));
                }
            });

            Check("Boot: recovery waits while KMH-Data is damaged, so a restore of the damaged file cannot erase its credits", () =>
            {
                string marker = Path.Combine(Path.GetTempPath(), "kmh-damage-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                string had = JsonFileStore.DamageMarkerOverrideForTest;
                try
                {
                    JsonFileStore.DamageMarkerOverrideForTest = marker;
                    bool cleanRuns = KmhBootRecovery.ShouldRun();
                    File.WriteAllText(marker, "Treasury/Treasury.json could not be read");
                    bool damagedWaits = !KmhBootRecovery.ShouldRun();
                    string main = KmhSecurityAuditSelfTest.ReadSource("Main.cs") ?? "";
                    int gate = main.IndexOf("if (Maintenance.KmhBootRecovery.ShouldRun())", StringComparison.Ordinal);
                    int first = main.IndexOf("Maintenance.KmhSeasonReset.RecoverOnBoot();", StringComparison.Ordinal);
                    int last = main.IndexOf("Features.Economy.KmhEconomyReset.RecoverOnBoot();", StringComparison.Ordinal);
                    bool gated = gate >= 0 && first > gate && last > first;
                    bool ok = cleanRuns && damagedWaits && gated;
                    return (ok, ok ? "runs on intact data, waits on damaged data, and every pass sits behind the gate"
                                  : $"runsWhenClean={cleanRuns}, waitsWhenDamaged={damagedWaits}, passesBehindGate={gated}");
                }
                finally
                {
                    JsonFileStore.DamageMarkerOverrideForTest = had;
                    try { File.Delete(marker); } catch { }
                }
            });

            // The real KMH-Data is moved aside for each of these and put back whatever happens.
            (bool, string) Aside(Func<string, string, string, (bool, string)> body)
            {
                string dataRoot = KmhDataPaths.Folder;
                string moved = dataRoot + ".selftest-aside";
                // An interrupted run's leftover: swapping it back in would delete this run's data.
                if (Directory.Exists(moved)) return (false, "an interrupted run left " + moved + " - remove it and rerun");
                bool aside = false;
                try
                {
                    if (Directory.Exists(dataRoot)) { Directory.Move(dataRoot, moved); aside = true; }
                    return body(dataRoot, dataRoot + ".previous", dataRoot + ".restoring");
                }
                finally
                {
                    KmhDataBackup.ClearRestoredThisBoot();
                    TryDelete(dataRoot + ".restoring");
                    TryDelete(dataRoot + ".previous");
                    if (aside && Directory.Exists(moved)) { TryDelete(dataRoot); Directory.Move(moved, dataRoot); }
                }
            }
            string Put(string dir, string rel, string text)
            {
                string p = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, text);
                return p;
            }
            string Read(string p) => File.Exists(p) ? File.ReadAllText(p) : "(missing)";

            Check("Restore: a crash between the two moves is undone even though boot has already recreated KMH-Data", () => Aside((root, previous, staging) =>
            {
                Put(previous, "Treasury/Treasury.json", "{\"live\":true}");
                Put(staging, "Treasury/Treasury.json", "{\"restored\":true}");
                Put(staging, KmhDataBackup.RestoredMarkerName, "20260101-000000-boot");
                Put(staging, "Snapshots/dispute.json", "evidence");
                // What a boot has made by the time it gets here: the folder scaffolding and this boot's log.
                Directory.CreateDirectory(Path.Combine(root, "Treasury"));
                Put(root, "Debug/KMH-this-boot.log", "log");

                KmhDataBackup.FinishInterruptedRestore();

                string treasury = Read(Path.Combine(root, "Treasury", "Treasury.json"));
                bool live = treasury.Contains("live");
                bool snapshot = File.Exists(Path.Combine(root, "Snapshots", "dispute.json"));
                bool log = File.Exists(Path.Combine(root, "Debug", "KMH-this-boot.log"));
                bool notLanded = string.IsNullOrEmpty(KmhDataBackup.RestoredThisBoot);
                bool cleaned = !Directory.Exists(previous) && !Directory.Exists(staging);
                bool ok = live && snapshot && log && notLanded && cleaned;
                return (ok, ok ? "the live data is back, with the snapshot archive and this boot's log"
                              : $"treasury={treasury}, snapshot={snapshot}, log={log}, landed={!notLanded}, cleaned={cleaned}"
                                + (!live ? " - THE LIVE DATA WAS DELETED" : ""));
            }));

            Check("Restore: a restore that landed before its boot died still bumps the data generation", () => Aside((root, previous, staging) =>
            {
                Put(root, "Treasury/Treasury.json", "{\"restored\":true}");
                Put(root, KmhDataBackup.RestoredMarkerName, "20260101-000000-boot");
                Put(previous, "Treasury/Treasury.json", "{\"replaced\":true}");

                KmhDataBackup.FinishInterruptedRestore();

                string treasury = Read(Path.Combine(root, "Treasury", "Treasury.json"));
                string landed = KmhDataBackup.RestoredThisBoot;
                bool ok = treasury.Contains("restored") && landed == "20260101-000000-boot" && !Directory.Exists(previous);
                return (ok, ok ? "kept the restored data and owes the bump"
                              : $"treasury={treasury}, restoredThisBoot={landed ?? "null"}, previousLeft={Directory.Exists(previous)}");
            }));

            Check("Restore: logs and snapshots carried out for a swap that never happened go back to KMH-Data", () => Aside((root, previous, staging) =>
            {
                Put(root, "Treasury/Treasury.json", "{\"live\":true}");
                Put(staging, "Treasury/Treasury.json", "{\"restored\":true}");
                Put(staging, "Snapshots/dispute.json", "evidence");

                KmhDataBackup.FinishInterruptedRestore();

                bool live = Read(Path.Combine(root, "Treasury", "Treasury.json")).Contains("live");
                bool snapshot = File.Exists(Path.Combine(root, "Snapshots", "dispute.json"));
                bool ok = live && snapshot && !Directory.Exists(staging) && string.IsNullOrEmpty(KmhDataBackup.RestoredThisBoot);
                return (ok, ok ? "the snapshot archive is back beside the untouched live data"
                              : $"live={live}, snapshot={snapshot}, stagingLeft={Directory.Exists(staging)}");
            }));

            return r;
        }

        private static string FirstDataFile(string dir)
        {
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f).Equals(KmhDataBackup.ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                if (new FileInfo(f).Length > 0) return f;
            }
            return null;
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
