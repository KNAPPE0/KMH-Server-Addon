using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using KMHServerAddon.Features.Enforcement;
using KMHServerAddon.Features.Transport;

namespace KMHServerAddon.Maintenance
{
    // Every joiner adopts the profile, so what matters is what the server refuses and that a refusal never disturbs it.
    internal static class KmhConfigPublishSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Config publish: a path with a separator is not a managed name", () =>
            {
                string[] hostile =
                {
                    "sub/Mod_X.xml", "sub\\Mod_X.xml", "../Mod_X.xml", "..\\..\\Mod_X.xml",
                    "C:\\Windows\\Mod_X.xml", "/etc/passwd", "..", ".",
                };
                foreach (string h in hostile)
                    if (ManagedPath.IsManagedName(h)) return (false, $"'{h}' was accepted");
                return (true, $"{hostile.Length} traversal shape(s) refused");
            });

            // The host's own rules differ by OS: a Linux server must refuse what no Windows player could write.
            Check("Config publish: device names and characters Windows refuses are never managed, on any host", () =>
            {
                string[] hostile = { "COM1.xml", "nul.json", "LPT9.settings.xml", "CON.xml", "Mod_A|B.xml", "Mod_A?.xml", "Mod_\"A\".xml", "Mod_A*.xml", "Mod_A\u0001.xml" };
                foreach (string h in hostile)
                    if (ManagedPath.IsManagedName(h)) return (false, $"'{h}' was accepted");
                if (!ManagedPath.IsManagedName("Mod_Compost_CompostMod.xml") || !ManagedPath.IsManagedName("Console_Settings.xml"))
                    return (false, "a real settings name was refused");
                return (true, $"{hostile.Length} name(s) refused");
            });

            Check("Config publish: only enforceable settings extensions are managed", () =>
            {
                if (ManagedPath.IsManagedName("preview.png")) return (false, "a png was accepted");
                if (ManagedPath.IsManagedName("server.log")) return (false, "a log was accepted");
                if (ManagedPath.IsManagedName("profile.zip")) return (false, "a zip was accepted");
                if (!ManagedPath.IsManagedName("Mod_Something_Settings.xml")) return (false, "a real mod settings file was refused");
                if (!ManagedPath.IsManagedName("ZombielandAdvancedSettings.json")) return (false, "a json settings file was refused");
                return (true, "xml and json only");
            });

            Check("Config publish: personal and ModSync-owned files are never managed", () =>
            {
                string[] personal =
                {
                    "ModsConfig.xml", "backup_ModsConfig.xml", "Prefs.xml", "KeyPrefs.xml", "Knowledge.xml",
                    "Docked.xml", "Floating.xml", "TrueTerrainColorsCache.xml", "KMH_DirectoryCache.json",
                };
                foreach (string p in personal)
                    if (ManagedPath.IsManagedName(p)) return (false, $"'{p}' would be published");
                return (true, $"{personal.Length} personal/ModSync file(s) excluded");
            });

            // The rule lives in two repos; contract 5 compares the source text, and this proves the behaviour.
            Check("Config publish: the managed rule is case-insensitive about the extension", () =>
            {
                bool ok = ManagedPath.IsManagedName("Mod_A.XML") && ManagedPath.IsManagedName("Mod_B.Json");
                return (ok, ok ? "extension casing does not decide" : "CASE CHANGED THE ANSWER");
            });

            Check("Config publish: an inflate that overruns its declared size is refused", () =>
            {
                string dir = TempDir();
                try
                {
                    string packed = Path.Combine(dir, "bomb.z");
                    byte[] big = new byte[512 * 1024];              // compresses to almost nothing
                    using (FileStream fs = new FileStream(packed, FileMode.Create))
                    using (DeflateStream z = new DeflateStream(fs, CompressionLevel.Optimal))
                        z.Write(big, 0, big.Length);

                    // Declared as 1KB; the stream expands to 512KB.
                    bool refused = !InflateProbe(packed, Path.Combine(dir, "out.xml"), 1024, out string why);
                    return (refused, refused ? $"refused: {why}" : "A DECOMPRESSION BOMB WAS ACCEPTED");
                }
                finally { TryDelete(dir); }
            });

            Check("Config publish: a stream that is not deflate is refused rather than written", () =>
            {
                string dir = TempDir();
                try
                {
                    string packed = Path.Combine(dir, "junk.z");
                    File.WriteAllBytes(packed, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                    string dest = Path.Combine(dir, "out.xml");
                    bool refused = !InflateProbe(packed, dest, 1024 * 1024, out string why);
                    return (refused, refused ? $"refused: {why}" : "A MALFORMED STREAM WAS ACCEPTED");
                }
                finally { TryDelete(dir); }
            });

            Check("Config publish: an inflate within its size is accepted and byte-exact", () =>
            {
                string dir = TempDir();
                try
                {
                    byte[] want = System.Text.Encoding.UTF8.GetBytes("<ModSettings><v>1</v></ModSettings>");
                    string packed = Path.Combine(dir, "ok.z");
                    using (FileStream fs = new FileStream(packed, FileMode.Create))
                    using (DeflateStream z = new DeflateStream(fs, CompressionLevel.Optimal))
                        z.Write(want, 0, want.Length);

                    string dest = Path.Combine(dir, "out.xml");
                    if (!InflateProbe(packed, dest, want.Length, out string why)) return (false, $"refused a good stream: {why}");
                    byte[] got = File.ReadAllBytes(dest);
                    bool same = got.Length == want.Length;
                    if (same) for (int i = 0; i < got.Length; i++) if (got[i] != want[i]) { same = false; break; }
                    return (same, same ? "round-trips byte for byte" : "INFLATED CONTENT DIFFERS");
                }
                finally { TryDelete(dir); }
            });

            Check("Config publish: publish parts are bulk only while a transaction is open", () =>
            {
                bool closed = SubProtocol.KmhTrafficClass.Effective(
                                  SubProtocol.KmhProtocol.Kind.EnforcementPublishPart, "nobody-here")
                              == SubProtocol.TrafficClass.Interactive;
                return (closed, closed
                    ? "parts without a transaction are charged as ordinary traffic"
                    : "AN UNAUTHORISED CLIENT GOT THE BULK BUDGET");
            });

            Check("Config publish: control traffic has a budget bulk cannot spend", () =>
            {
                bool ok = SubProtocol.KmhTrafficClass.Of(SubProtocol.KmhProtocol.Kind.Ping) == SubProtocol.TrafficClass.Control
                       && SubProtocol.KmhTrafficClass.Of(SubProtocol.KmhProtocol.Kind.EnforcementPublishPart) == SubProtocol.TrafficClass.Bulk
                       && SubProtocol.KmhTrafficClass.LimitFor(SubProtocol.TrafficClass.Control) > 0;
                return (ok, ok ? "control and bulk are separate windows" : "CLASSES COLLAPSED ONTO ONE BUDGET");
            });

            // Every file costs at least one frame, so MaxFiles bounds the worst legitimate transaction the budget must cover.
            Check("Config publish: the bulk budget covers the largest profile the server accepts", () =>
            {
                Features.Enforcement.EnforcementPublish.SizeBulkBudget();
                long worstFrames = Features.Enforcement.EnforcementPublish.MaxFiles
                                 + Features.Enforcement.EnforcementPublish.MaxTotalBytes
                                   / Features.Enforcement.EnforcementPublish.PartRawBytes;
                int budget = SubProtocol.KmhTrafficClass.LimitFor(SubProtocol.TrafficClass.Bulk);
                bool ok = budget >= worstFrames;
                return (ok, ok
                    ? $"{budget} frame(s)/window covers the worst legitimate {worstFrames}"
                    : $"BUDGET {budget} IS SMALLER THAN THE {worstFrames} FRAME(S) A LEGAL PROFILE CAN NEED");
            });

            // Through the router's gate: calling OnPart directly never meets the limiter, so a dropped-frame bug would pass.
            Check("Config publish: an admin's real profile transfer is not throttled", () =>
            {
                var c = new ServerClient(null, null, createListener: false);
                typeof(ServerClient)
                    .GetField("<IsVerified>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(c, true);
                string who = "selftest_bulk_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                c.GetData<UserFile>(new UserFile { Username = who, IsAdmin = true });

                Features.Enforcement.EnforcementPublish.SizeBulkBudget();
                Func<string, bool> prior = SubProtocol.KmhTrafficClass.BulkAuthorized;
                SubProtocol.KmhTrafficClass.BulkAuthorized = u => u == who;   // as if a transaction were open
                try
                {
                    // 587 files is the measured shape of a real Config folder; every one costs a frame.
                    int allowed = 0;
                    for (int i = 0; i < 587; i++)
                        if (SubProtocol.KmhRouter.AllowInboundForTest(c, SubProtocol.KmhProtocol.Kind.EnforcementPublishPart))
                            allowed++;
                    return (allowed == 587, allowed == 587
                        ? "587 of 587 frames accepted"
                        : $"ONLY {allowed} OF 587 FRAMES ACCEPTED - the rest are dropped and the commit fails");
                }
                finally { SubProtocol.KmhTrafficClass.BulkAuthorized = prior; }
            });

            // Read from IL: the check above installs its own predicate, so the one Register() wires is otherwise untested.
            Check("Config publish: publish parts are bulk only while that user has an open transaction", () =>
            {
                bool wired = KmhIlReader.CallsMadeBy(
                    "KMHServerAddon.Features.Enforcement.EnforcementPublish", "Register")
                    .Contains("HasOpenTransaction");

                string who = "selftest_bulkgate_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                bool before = !Features.Enforcement.EnforcementPublish.HasOpenTransaction(who);
                string id = Features.Enforcement.EnforcementPublish.OpenUploadForTest(who);
                bool during = Features.Enforcement.EnforcementPublish.HasOpenTransaction(who);
                Features.Enforcement.EnforcementPublish.CloseUploadForTest(id);
                bool after = !Features.Enforcement.EnforcementPublish.HasOpenTransaction(who);

                // The predicate exists to pick the budget, so the budget it picks is asserted too.
                const string Part = SubProtocol.KmhProtocol.Kind.EnforcementPublishPart;
                Func<string, bool> prior = SubProtocol.KmhTrafficClass.BulkAuthorized;
                bool maps;
                try
                {
                    SubProtocol.KmhTrafficClass.BulkAuthorized = _ => false;
                    maps = SubProtocol.KmhTrafficClass.Effective(Part, who) == SubProtocol.TrafficClass.Interactive;
                    SubProtocol.KmhTrafficClass.BulkAuthorized = _ => true;
                    maps = maps && SubProtocol.KmhTrafficClass.Effective(Part, who) == SubProtocol.TrafficClass.Bulk;
                }
                finally { SubProtocol.KmhTrafficClass.BulkAuthorized = prior; }

                bool ok = wired && before && during && after && maps;
                return (ok, ok ? "Register wires HasOpenTransaction, which is false without an upload and true with one"
                              : $"wiredInRegister={wired}, falseWithout={before}, trueWith={during}, "
                                + $"falseAgain={after}, decidesBudget={maps}");
            });

            Check("Config publish: the revision never goes backwards across a save", () =>
            {
                byte[] zip = EnforcementProfile.SerializeBytes();
                string hash = EnforcementProfile.Hash;
                long ticks = EnforcementProfile.UpdatedTicks;
                long before, after;
                bool saved;
                try
                {
                    // A current save first: from an empty profile any later save looks like progress.
                    saved = EnforcementProfile.SetProfile(zip, hash, DateTime.UtcNow.Ticks);
                    before = EnforcementProfile.Revision;
                    // An older write time is what a restored backup or a skewed clock hands in.
                    saved &= EnforcementProfile.SetProfile(zip, hash, DateTime.UtcNow.AddDays(-30).Ticks);
                    after = EnforcementProfile.Revision;
                }
                finally { EnforcementProfile.SetProfile(zip, hash, ticks); }
                bool ok = saved && after > before;
                return (ok, $"revision {before}, then {after} after a save stamped 30 days back");
            });

            return r;
        }

        // The production inflate itself, not a copy of it: a mirror would let the two drift and still pass.
        private static bool InflateProbe(string packed, string dest, long maxBytes, out string why)
            => EnforcementPublish.Inflate(packed, dest, maxBytes, out why);

        private static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "kmh-publish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static void TryDelete(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }
    }
}
