using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using KMHServerAddon.Features.Enforcement;
using KMHServerAddon.Features.Transport;
using KMHServerAddon.SubProtocol;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    // Driven as a modified client would: only a complete, owned, verified transaction may reach the active profile.
    internal static class KmhConfigPublishHandlerSelfTest
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly FieldInfo VerifiedField =
            typeof(ServerClient).GetField("<IsVerified>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Type Publish = typeof(EnforcementPublish);

        private sealed class Sent
        {
            public string Kind = "";
            public JObject Body = new JObject();
            public string Error => (string)Body["error"] ?? "";
            public bool Ok => (bool?)Body["ok"] == true;
        }

        private static readonly List<Sent> Outbox = new List<Sent>();

        private struct Stats
        {
            public bool Ok; public string Error;
            public long RawBytes, SentBytes; public int Frames, Wanted, ReusedFiles;
        }

        // Mod settings compress ~10x, so the corpus must be real-ish XML or the measured transfer flatters the compressor.
        private static Dictionary<string, byte[]> Corpus(int count, string salt = "")
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                StringBuilder sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModSettings>\n");
                for (int f = 0; f < 60; f++)
                    sb.Append("  <setting").Append(f).Append('>').Append(salt).Append(i * 7 + f)
                      .Append("</setting").Append(f).Append(">\n");
                sb.Append("</ModSettings>\n");
                files[$"Mod_{i:D4}.xml"] = Encoding.UTF8.GetBytes(sb.ToString());
            }
            return files;
        }

        private static string Begin(ServerClient c, Dictionary<string, byte[]> files, out int[] wanted)
        {
            wanted = Array.Empty<int>();
            var rows = new List<object>();
            long total = 0;
            foreach (KeyValuePair<string, byte[]> kv in files)
            {
                rows.Add(new { path = kv.Key, size = (long)kv.Value.Length, hash = EnforcementProfile.Sha256Hex(kv.Value) });
                total += kv.Value.Length;
            }
            Outbox.Clear();
            Call("OnBegin", c, KmhProtocol.Kind.EnforcementPublishBegin, new
            {
                files = rows, profile_hash = "", total_bytes = total,
                expected_revision = EnforcementProfile.Revision,
            });
            if (Last == null || Last.Kind != KmhProtocol.Kind.EnforcementPublishAccepted) return null;
            var arr = Last.Body["wanted"] as JArray;
            var list = new List<int>();
            if (arr != null) foreach (JToken t in arr) { int? v = (int?)t; if (v.HasValue) list.Add(v.Value); }
            wanted = list.ToArray();
            return (string)Last.Body["upload_id"];
        }

        private static int SendFile(ServerClient c, string id, int index, Dictionary<string, byte[]> files)
            => SendBytes(c, id, index, files[OrderedPath(files, index)]);

        // The server indexes the manifest in the order it was sent, which is this dictionary's order.
        private static string OrderedPath(Dictionary<string, byte[]> files, int index)
        {
            int i = 0;
            foreach (string k in files.Keys) { if (i++ == index) return k; }
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        private static void RunTransaction(ServerClient c, Dictionary<string, byte[]> files, out Stats stats)
        {
            stats = new Stats();
            foreach (byte[] b in files.Values) stats.RawBytes += b.Length;

            string id = Begin(c, files, out int[] wanted);
            if (id == null) { stats.Error = Last?.Error ?? "begin refused"; return; }
            stats.Wanted = wanted.Length;
            stats.ReusedFiles = (int?)Last.Body["reused_files"] ?? 0;

            foreach (int index in wanted)
            {
                string path = OrderedPath(files, index);
                stats.SentBytes += Deflate(files[path]).Length;
                stats.Frames += SendFile(c, id, index, files);
            }

            Outbox.Clear();
            Call("OnCommit", c, KmhProtocol.Kind.EnforcementPublishCommit, new { upload_id = id });
            stats.Ok = Last != null && Last.Ok;
            if (!stats.Ok) stats.Error = Last?.Error ?? "no result";
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // The managed set of a REAL Config folder, read through the server's own rule rather than a copy of it.
        private static Dictionary<string, byte[]> RealManaged(string root, out int discovered)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string[] all = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            discovered = all.Length;
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(f);
                if (!ManagedPath.IsManagedName(name)) continue;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > EnforcementPublish.MaxFileBytes) continue;
                    files[name] = File.ReadAllBytes(f);
                }
                catch (IOException) { }
            }
            return files;
        }

        private static int SendBytes(ServerClient c, string id, int index, byte[] raw)
        {
            byte[] packed = Deflate(raw);
            int frames = 0;
            for (int off = 0; off < packed.Length; off += 32 * 1024)
            {
                int take = Math.Min(32 * 1024, packed.Length - off);
                Call("OnPart", c, KmhProtocol.Kind.EnforcementPublishPart, new
                {
                    upload_id = id, index,
                    data = Convert.ToBase64String(packed, off, take),
                    packed_size = packed.Length,
                    last = off + take >= packed.Length,
                });
                frames++;
            }
            return frames;
        }

        // Puts the active profile back however the check ends.
        private sealed class SavedProfile : IDisposable
        {
            private readonly byte[] _zip = EnforcementProfile.SerializeBytes();
            private readonly string _hash = EnforcementProfile.Hash;
            private readonly long _ticks = EnforcementProfile.UpdatedTicks;
            public void Dispose() => EnforcementProfile.SetProfile(_zip, _hash, _ticks);
        }

        private static ServerClient Admin(string name, bool admin = true)
        {
            ServerClient c = new ServerClient(null, null, createListener: false);
            VerifiedField.SetValue(c, true);
            c.GetData<UserFile>(new UserFile { Username = name, IsAdmin = admin });
            return c;
        }

        private static void Call(string method, ServerClient c, string kind, object data)
        {
            Publish.GetMethod(method, Flags).Invoke(null, new object[] { c, new KmhEnvelope(kind, data) });
        }

        private static Sent Last => Outbox.Count == 0 ? null : Outbox[Outbox.Count - 1];

        private static byte[] Deflate(byte[] raw)
        {
            using (MemoryStream into = new MemoryStream())
            {
                using (DeflateStream z = new DeflateStream(into, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
                return into.ToArray();
            }
        }

        private static object Manifest(string path, byte[] raw, long expectedRevision)
            => new
            {
                files = new[] { new { path, size = (long)raw.Length, hash = EnforcementProfile.Sha256Hex(raw) } },
                profile_hash = "",
                total_bytes = (long)raw.Length,
                expected_revision = expectedRevision,
            };

        private static string Begin(ServerClient c, string path, byte[] raw, long revision = -1)
        {
            Outbox.Clear();
            Call("OnBegin", c, KmhProtocol.Kind.EnforcementPublishBegin,
                 Manifest(path, raw, revision < 0 ? EnforcementProfile.Revision : revision));
            return Last != null && Last.Kind == KmhProtocol.Kind.EnforcementPublishAccepted
                 ? (string)Last.Body["upload_id"] : null;
        }

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            string root = Path.Combine(Path.GetTempPath(), "kmh-publish-handler-" + Guid.NewGuid().ToString("N"));
            byte[] raw = Encoding.UTF8.GetBytes("<ModSettings><speed>3</speed></ModSettings>");

            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex}")); }
                finally { Outbox.Clear(); }
            }

            KmhRouter.OutboundForTest = (client, kind, data) =>
                Outbox.Add(new Sent { Kind = kind, Body = JObject.FromObject(data) });
            EnforcementPublish.StagingRootOverrideForTest = root;
            try
            {
                Directory.CreateDirectory(root);

                Check("Publish: a non-admin is refused before any staging happens", () =>
                {
                    ServerClient player = Admin("player", admin: false);
                    Begin(player, "Mod_A.xml", raw);
                    bool refused = Last?.Error == "not_admin";
                    return (refused && Directory.GetDirectories(root).Length == 0,
                            refused ? "refused, nothing staged" : $"got '{Last?.Error}'");
                });

                Check("Publish: a part without a Begin is refused", () =>
                {
                    Outbox.Clear();
                    Call("OnPart", Admin("taz"), KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = "0123456789abcdef0123456789abcdef", index = 0, data = "AAAA", last = true });
                    return (Last?.Error == "unknown_upload", Last?.Error ?? "nothing was sent back");
                });

                Check("Publish: a commit without a Begin is refused", () =>
                {
                    Outbox.Clear();
                    Call("OnCommit", Admin("taz"), KmhProtocol.Kind.EnforcementPublishCommit,
                         new { upload_id = "0123456789abcdef0123456789abcdef" });
                    return (Last?.Error == "unknown_upload", Last?.Error ?? "nothing was sent back");
                });

                Check("Publish: one admin cannot send parts into another admin's transaction", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "the first admin's Begin was refused");
                    Outbox.Clear();
                    Call("OnPart", Admin("mallory"), KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = id, index = 0, data = Convert.ToBase64String(Deflate(raw)), last = true });
                    bool refused = Last?.Error == "not_yours";
                    Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = id });
                    return (refused, refused ? "refused as not_yours" : $"got '{Last?.Error}'");
                });

                Check("Publish: one admin cannot cancel another admin's transaction", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    Call("OnCancel", Admin("mallory"), KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = id });
                    Outbox.Clear();
                    // Still open, so the owner's own part is still accepted.
                    Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = id, index = 0, data = Convert.ToBase64String(Deflate(raw)), last = true });
                    bool alive = Last == null || Last.Kind == KmhProtocol.Kind.EnforcementPublishAck;
                    Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = id });
                    return (alive, alive ? "a stranger's cancel did nothing" : $"the transaction was torn down: {Last?.Error}");
                });

                Check("Publish: admin removed mid-upload aborts the transaction", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    taz.GetData<UserFile>().IsAdmin = false;
                    Outbox.Clear();
                    Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = id, index = 0, data = Convert.ToBase64String(Deflate(raw)), last = true });
                    bool refused = Last?.Error == "not_admin";
                    taz.GetData<UserFile>().IsAdmin = true;
                    Outbox.Clear();
                    // Aborted, not merely refused: the id must be gone.
                    Call("OnCommit", taz, KmhProtocol.Kind.EnforcementPublishCommit, new { upload_id = id });
                    bool gone = Last?.Error == "unknown_upload";
                    return (refused && gone, refused && gone ? "refused and torn down" : $"part='{refused}' commit='{Last?.Error}'");
                });

                Check("Publish: a part outside the manifest is refused", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    Outbox.Clear();
                    Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = id, index = 7, data = "AAAA", last = true });
                    bool refused = Last?.Error == "bad_index";
                    Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = id });
                    return (refused, refused ? "refused as bad_index" : $"got '{Last?.Error}'");
                });

                Check("Publish: sending more bytes than declared is refused", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    string chunk = Convert.ToBase64String(Deflate(raw));
                    Outbox.Clear();
                    string err = "";
                    // Repeating a complete file over and over is the simplest way to walk past the declared size.
                    for (int i = 0; i < 400 && err == ""; i++)
                    {
                        Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                             new { upload_id = id, index = 0, data = chunk, last = false });
                        err = Last?.Error ?? "";
                    }
                    Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = id });
                    return (err == "overrun", err == "" ? "IT NEVER STOPPED" : err);
                });

                Check("Publish: a file that never arrives is refused at commit", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    Outbox.Clear();
                    Call("OnCommit", taz, KmhProtocol.Kind.EnforcementPublishCommit, new { upload_id = id });
                    bool refused = Last != null && !Last.Ok;
                    return (refused, refused ? $"refused: {Last.Error}" : "AN EMPTY PROFILE COMMITTED");
                });

                Check("Publish: content that does not match its declared hash is refused at commit", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    byte[] other = Encoding.UTF8.GetBytes("<ModSettings><speed>999</speed></ModSettin>");
                    Outbox.Clear();
                    Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = id, index = 0, data = Convert.ToBase64String(Deflate(other)), last = true });
                    Call("OnCommit", taz, KmhProtocol.Kind.EnforcementPublishCommit, new { upload_id = id });
                    bool refused = Last != null && !Last.Ok
                                && (Last.Error == "hash_mismatch" || Last.Error == "size_mismatch");
                    return (refused, refused ? Last.Error : $"got '{Last?.Error}', ok={Last?.Ok}");
                });

                Check("Publish: a manifest naming a path outside the managed set is refused", () =>
                {
                    ServerClient taz = Admin("taz");
                    Outbox.Clear();
                    Call("OnBegin", taz, KmhProtocol.Kind.EnforcementPublishBegin, new
                    {
                        files = new[] { new { path = "../../Mods/evil.xml", size = 4L, hash = new string('a', 64) } },
                        profile_hash = "", total_bytes = 4L, expected_revision = EnforcementProfile.Revision,
                    });
                    return (Last?.Error == "bad_path", Last?.Error ?? "nothing was sent back");
                });

                Check("Publish: a manifest listing the same file twice is refused", () =>
                {
                    ServerClient taz = Admin("taz");
                    string hash = EnforcementProfile.Sha256Hex(raw);
                    Outbox.Clear();
                    Call("OnBegin", taz, KmhProtocol.Kind.EnforcementPublishBegin, new
                    {
                        files = new[]
                        {
                            new { path = "Mod_A.xml", size = (long)raw.Length, hash },
                            new { path = "Mod_A.xml", size = (long)raw.Length, hash },
                        },
                        profile_hash = "", total_bytes = (long)raw.Length * 2, expected_revision = EnforcementProfile.Revision,
                    });
                    return (Last?.Error == "duplicate_path", Last?.Error ?? "nothing was sent back");
                });

                Check("Publish: a publish built on a superseded revision is refused", () =>
                {
                    ServerClient taz = Admin("taz");
                    Outbox.Clear();
                    Call("OnBegin", taz, KmhProtocol.Kind.EnforcementPublishBegin,
                         // +1, not -1: with no profile yet the revision is 0, and -1 is the client's "never seen one" value.
                         Manifest("Mod_A.xml", raw, EnforcementProfile.Revision + 1));
                    return (Last?.Error == "stale_revision", Last?.Error ?? "nothing was sent back");
                });

                Check("Publish: a second Begin replaces the first rather than running both", () =>
                {
                    ServerClient taz = Admin("taz");
                    string first = Begin(taz, "Mod_A.xml", raw);
                    string second = Begin(taz, "Mod_A.xml", raw);
                    if (first == null || second == null || first == second) return (false, "the two Begins did not both open");
                    Outbox.Clear();
                    Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = first, index = 0, data = "AAAA", last = true });
                    bool firstGone = Last?.Error == "unknown_upload";
                    Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = second });
                    return (firstGone, firstGone ? "the first was torn down" : $"the first is still open ('{Last?.Error}')");
                });

                Check("Publish: two admins can hold their own transactions at once", () =>
                {
                    ServerClient taz = Admin("taz"), kim = Admin("kim");
                    string a = Begin(taz, "Mod_A.xml", raw);
                    string b = Begin(kim, "Mod_B.xml", raw);
                    bool both = a != null && b != null && a != b;
                    if (both)
                    {
                        Outbox.Clear();
                        Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                             new { upload_id = a, index = 0, data = Convert.ToBase64String(Deflate(raw)), last = true });
                        both = Last == null || Last.Kind == KmhProtocol.Kind.EnforcementPublishAck;
                    }
                    Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = a });
                    Call("OnCancel", kim, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = b });
                    return (both, both ? "one admin's publish did not evict the other's" : "a second admin evicted the first");
                });

                Check("Publish: a cancelled transaction leaves nothing staged", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = id, index = 0, data = Convert.ToBase64String(Deflate(raw)), last = true });
                    Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = id });
                    bool clean = !Directory.Exists(Path.Combine(root, id));
                    return (clean, clean ? "staging removed" : "A CANCELLED PUBLISH LEFT ITS BYTES ON DISK");
                });

                Check("Publish: an expired transaction is purged with its staging", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    Call("OnPart", taz, KmhProtocol.Kind.EnforcementPublishPart,
                         new { upload_id = id, index = 0, data = Convert.ToBase64String(Deflate(raw)), last = true });

                    // Age it past the expiry rather than waiting five minutes for it.
                    object uploads = Publish.GetField("_uploads", Flags).GetValue(null);
                    object up = uploads.GetType().GetProperty("Item").GetValue(uploads, new object[] { id });
                    up.GetType().GetField("StartedTicks").SetValue(up, DateTime.UtcNow.Ticks - TimeSpan.FromHours(1).Ticks);
                    EnforcementPublish.PurgeExpired();

                    bool gone = !Directory.Exists(Path.Combine(root, id));
                    Outbox.Clear();
                    Call("OnCommit", taz, KmhProtocol.Kind.EnforcementPublishCommit, new { upload_id = id });
                    bool refused = Last?.Error == "unknown_upload";
                    return (gone && refused, gone && refused ? "purged and refused" : $"staged={!gone} commit='{Last?.Error}'");
                });

                Check("Publish: abandoned staging from a previous run is swept at boot", () =>
                {
                    string orphan = Path.Combine(root, "orphan-from-a-crash");
                    Directory.CreateDirectory(orphan);
                    File.WriteAllText(Path.Combine(orphan, "Mod_A.xml"), "<x/>");
                    EnforcementPublish.SweepStagingAtBoot();
                    bool gone = !Directory.Exists(orphan);
                    return (gone, gone ? "swept" : "A CRASHED PUBLISH'S BYTES SURVIVED A RESTART");
                });

                // Through the same handlers a client reaches, not a mirror of the production path.
                Check("Publish: a realistic profile commits end to end", () =>
                {
                    ServerClient taz = Admin("taz");
                    var files = Corpus(587);

                    using (new SavedProfile())
                    {
                        long beforeRev = EnforcementProfile.Revision;
                        var sw = Stopwatch.StartNew();
                        RunTransaction(taz, files, out Stats first);
                        sw.Stop();

                        if (!first.Ok) return (false, "the first publish did not commit: " + first.Error);
                        if (EnforcementProfile.FileCount != files.Count)
                            return (false, $"server holds {EnforcementProfile.FileCount} file(s), published {files.Count}");
                        if (EnforcementProfile.Revision <= beforeRev)
                            return (false, "the revision did not advance");

                        // Read back through the stored zip, which is what a joining client is handed.
                        int stored = 0;
                        using (MemoryStream ms = new MemoryStream(EnforcementProfile.SerializeBytes()))
                        using (ZipArchive za = new ZipArchive(ms, ZipArchiveMode.Read))
                            foreach (ZipArchiveEntry e in za.Entries)
                            {
                                if (!files.TryGetValue(e.Name, out byte[] want)) return (false, $"'{e.Name}' is not in the manifest");
                                using MemoryStream got = new MemoryStream();
                                using (Stream es = e.Open()) es.CopyTo(got);
                                if (!Same(got.ToArray(), want)) return (false, $"'{e.Name}' differs from what was published");
                                stored++;
                            }
                        if (stored != files.Count) return (false, $"the stored profile has {stored} of {files.Count}");

                        return (true, $"{files.Count} file(s), {first.RawBytes / 1024}KB raw, "
                                    + $"{first.SentBytes / 1024}KB sent in {first.Frames} frame(s), "
                                    + $"rev {EnforcementProfile.Revision}, {sw.ElapsedMilliseconds}ms");
                    }
                });

                Check("Publish: republishing the same profile transfers nothing", () =>
                {
                    ServerClient taz = Admin("taz");
                    var files = Corpus(120);
                    using (new SavedProfile())
                    {
                        RunTransaction(taz, files, out Stats first);
                        if (!first.Ok) return (false, "first publish failed: " + first.Error);

                        RunTransaction(taz, files, out Stats again);
                        if (!again.Ok) return (false, "republish failed: " + again.Error);

                        bool reused = again.Wanted == 0 && again.SentBytes == 0 && again.ReusedFiles == files.Count;
                        return (reused, reused
                            ? $"first {first.SentBytes / 1024}KB in {first.Frames} frame(s) -> "
                              + $"republish {again.SentBytes} byte(s), {again.ReusedFiles} reused"
                            : $"republish still wanted {again.Wanted} file(s) and sent {again.SentBytes} byte(s)");
                    }
                });

                Check("Publish: changing one file transfers only that file", () =>
                {
                    ServerClient taz = Admin("taz");
                    var files = Corpus(120);
                    using (new SavedProfile())
                    {
                        RunTransaction(taz, files, out Stats _);

                        var changed = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase);
                        changed["Mod_0007.xml"] = Encoding.UTF8.GetBytes("<ModSettings><v>changed</v></ModSettings>");
                        RunTransaction(taz, changed, out Stats second);

                        bool one = second.Ok && second.Wanted == 1 && second.ReusedFiles == files.Count - 1;
                        return (one, one
                            ? $"1 of {files.Count} wanted, {second.SentBytes} byte(s) sent"
                            : $"ok={second.Ok} wanted={second.Wanted} reused={second.ReusedFiles}");
                    }
                });

                Check("Publish: a transfer abandoned halfway leaves the active profile untouched", () =>
                {
                    ServerClient taz = Admin("taz");
                    var files = Corpus(40);
                    using (new SavedProfile())
                    {
                        RunTransaction(taz, files, out Stats seed);
                        if (!seed.Ok) return (false, "could not seed a profile");
                        long seededRev = EnforcementProfile.Revision;
                        string seededHash = EnforcementProfile.Hash;

                        var other = Corpus(40, salt: "second");
                        string id = Begin(taz, other, out int[] wanted);
                        if (id == null) return (false, "the second Begin was refused");
                        for (int i = 0; i < wanted.Length / 2; i++) SendFile(taz, id, wanted[i], other);
                        Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = id });

                        bool intact = EnforcementProfile.Revision == seededRev
                                   && string.Equals(EnforcementProfile.Hash, seededHash, StringComparison.OrdinalIgnoreCase)
                                   && !Directory.Exists(Path.Combine(root, id));
                        return (intact, intact ? "profile and revision unchanged, staging removed"
                                               : $"rev {seededRev}->{EnforcementProfile.Revision}");
                    }
                });

                Check("Publish: Begin stages no content of its own", () =>
                {
                    ServerClient taz = Admin("taz");
                    string id = Begin(taz, "Mod_A.xml", raw);
                    if (id == null) return (false, "Begin was refused");
                    // Staging reused files would let a client drive tens of MB of writes per Begin without committing.
                    string[] staged = Directory.GetFiles(Path.Combine(root, id));
                    Call("OnCancel", taz, KmhProtocol.Kind.EnforcementPublishCancel, new { upload_id = id });
                    return (staged.Length == 0, staged.Length == 0 ? "empty until a part arrives" : $"{staged.Length} file(s) written by Begin");
                });

                // Real files are larger and less compressible than the corpus; set KMH_REAL_CONFIG=<Config folder>.
                string realRoot = Environment.GetEnvironmentVariable("KMH_REAL_CONFIG") ?? "";
                if (realRoot.Length == 0 || !Directory.Exists(realRoot))
                    r.Add(Diagnostics.KmhSelfTestResult.Skipped(
                        "Publish: a real Config folder commits end to end",
                        "KMH_REAL_CONFIG is not set to a Config folder"));
                else
                {
                    Check("Publish: a real Config folder commits end to end", () =>
                    {
                        ServerClient taz = Admin("taz");
                        var files = RealManaged(realRoot, out int discovered);
                        if (files.Count == 0) return (false, $"no managed file in {discovered} discovered file(s)");

                        using (new SavedProfile())
                        {
                            var sw = Stopwatch.StartNew();
                            RunTransaction(taz, files, out Stats st);
                            sw.Stop();
                            if (!st.Ok) return (false, "the real profile did not commit: " + st.Error);

                            int stored = 0;
                            using (MemoryStream ms = new MemoryStream(EnforcementProfile.SerializeBytes()))
                            using (ZipArchive za = new ZipArchive(ms, ZipArchiveMode.Read))
                                foreach (ZipArchiveEntry e in za.Entries)
                                {
                                    if (!files.TryGetValue(e.Name, out byte[] want)) return (false, $"'{e.Name}' was not published");
                                    using MemoryStream got = new MemoryStream();
                                    using (Stream es = e.Open()) es.CopyTo(got);
                                    if (!Same(got.ToArray(), want)) return (false, $"'{e.Name}' differs from the file on disk");
                                    stored++;
                                }
                            if (stored != files.Count) return (false, $"stored {stored} of {files.Count}");

                            return (true, $"{discovered} discovered -> {files.Count} managed, "
                                        + $"{st.RawBytes / 1024}KB raw, {st.SentBytes / 1024}KB sent, "
                                        + $"{st.Frames} frame(s), {sw.ElapsedMilliseconds}ms, byte-exact");
                        }
                    });

                    Check("Publish: a real profile republished identically transfers nothing", () =>
                    {
                        ServerClient taz = Admin("taz");
                        var files = RealManaged(realRoot, out _);
                        using (new SavedProfile())
                        {
                            RunTransaction(taz, files, out Stats first);
                            if (!first.Ok) return (false, "first publish failed: " + first.Error);
                            RunTransaction(taz, files, out Stats again);
                            bool reused = again.Ok && again.Wanted == 0 && again.SentBytes == 0;
                            return (reused, reused
                                ? $"{first.SentBytes / 1024}KB -> 0 bytes, {again.ReusedFiles} reused"
                                : $"republish wanted {again.Wanted}, sent {again.SentBytes} byte(s)");
                        }
                    });

                    Check("Publish: a real profile with one corrupted file is refused and changes nothing", () =>
                    {
                        ServerClient taz = Admin("taz");
                        var files = RealManaged(realRoot, out _);
                        using (new SavedProfile())
                        {
                            // DIFFERENT seed content: the same files would all be reused, leaving nothing to corrupt.
                            RunTransaction(taz, Corpus(8, salt: "seed"), out Stats seed);
                            if (!seed.Ok) return (false, "could not seed: " + seed.Error);
                            long seededRev = EnforcementProfile.Revision;
                            string seededHash = EnforcementProfile.Hash;

                            // Same manifest, one file's bytes replaced in flight: the declared hash no longer matches.
                            string victim = null;
                            foreach (string k in files.Keys) { victim = k; break; }
                            string id = Begin(taz, files, out int[] wanted);
                            if (id == null) return (false, "Begin refused");
                            foreach (int index in wanted)
                            {
                                string path = OrderedPath(files, index);
                                byte[] body = path == victim
                                    ? Encoding.UTF8.GetBytes("<ModSettings><tampered/></ModSettings>")
                                    : files[path];
                                SendBytes(taz, id, index, body);
                            }
                            Outbox.Clear();
                            Call("OnCommit", taz, KmhProtocol.Kind.EnforcementPublishCommit, new { upload_id = id });

                            bool refused = Last != null && !Last.Ok;
                            bool intact = EnforcementProfile.Revision == seededRev
                                       && string.Equals(EnforcementProfile.Hash, seededHash, StringComparison.OrdinalIgnoreCase);
                            return (refused && intact, refused && intact
                                ? $"refused ({Last.Error}), profile unchanged"
                                : $"refused={refused} intact={intact}");
                        }
                    });
                }
            }
            catch (Exception ex) { r.Add(("Publish: the handler harness ran", false, ex.ToString())); }
            finally
            {
                KmhRouter.OutboundForTest = null;
                EnforcementPublish.StagingRootOverrideForTest = null;
                Outbox.Clear();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
            return r;
        }
    }
}
