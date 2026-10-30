using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Enforcement
{
    // The active profile is swapped in one step at commit; no path, size or hash the client sends is trusted.
    internal static class EnforcementPublish
    {
        internal const int  MaxFiles       = 4_000;
        internal const long MaxFileBytes   = 8L * 1024 * 1024;
        internal const long MaxTotalBytes  = 48L * 1024 * 1024;
        internal const int  PartRawBytes   = 32 * 1024;
        // Bounds a legitimate publish's burst on the router without needing the flood limiter to arbitrate it.
        internal const int  CreditWindow   = 8;
        internal static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

        // Staged compressed; inflated once at commit, so a staging folder never holds a half-inflated file.
        private const string PackedSuffix = ".z";

        private sealed class Entry
        {
            public string Path = "";
            public long   Size;
            public string Hash = "";
            public bool   Reused;
            public long   Received;
        }

        private sealed class Upload
        {
            public string Id = "";
            public string User = "";
            public string ProfileHash = "";
            public List<Entry> Entries = new List<Entry>();
            public long StartedTicks;
            public long ReceivedBytes;
            public string Staging = "";
            public bool Committing;
        }

        private static readonly Dictionary<string, Upload> _uploads =
            new Dictionary<string, Upload>(StringComparer.Ordinal);
        private static readonly object _lock = new object();

        internal static string StagingRootOverrideForTest;
        private static string StagingRoot => StagingRootOverrideForTest
                                          ?? Path.Combine(KmhDataPaths.Sub("Enforcement"), "Staging");

        // Only an open transaction earns the bulk budget; parts sent without a Begin are throttled like any flood.
        internal static bool HasOpenTransaction(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            lock (_lock)
                foreach (Upload u in _uploads.Values)
                    if (string.Equals(u.User, username, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static string OpenUploadForTest(string username)
        {
            string id = Guid.NewGuid().ToString("N");
            lock (_lock)
                _uploads[id] = new Upload
                {
                    Id = id, User = username, Entries = new List<Entry>(),
                    Staging = Path.Combine(StagingRoot, id), StartedTicks = DateTime.UtcNow.Ticks,
                };
            return id;
        }

        internal static void CloseUploadForTest(string id)
        {
            lock (_lock) _uploads.Remove(id ?? "");
        }

        // Derived, never guessed: every file costs at least one frame, and the byte cap decides the rest.
        internal static void SizeBulkBudget()
            => SubProtocol.KmhTrafficClass.BulkPerWindow = MaxFiles + (int)(MaxTotalBytes / PartRawBytes);

        public static void Register()
        {
            SubProtocol.KmhTrafficClass.BulkAuthorized = HasOpenTransaction;
            SizeBulkBudget();
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementPublishBegin,  OnBegin);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementPublishPart,   OnPart);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementPublishCommit, OnCommit);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementPublishCancel, OnCancel);
        }

        private static void Fail(ServerClient client, string uploadId, string code, string message)
        {
            KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementPublishResult,
                new { upload_id = uploadId ?? "", ok = false, error = code, message });
        }

        private static void OnBegin(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username ?? "?";
            if (!EnforcementHandler.IsAdmin(client)) { Fail(client, null, "not_admin", "Publishing configs is admin-only."); return; }

            if (!(env?.Data?["files"] is JArray rows)) { Fail(client, null, "bad_manifest", "No manifest."); return; }
            if (rows.Count == 0 || rows.Count > MaxFiles)
            { Fail(client, null, "bad_manifest", $"A profile carries 1-{MaxFiles} files; this one declares {rows.Count}."); return; }

            // The client's expected revision, so two admins publishing at once cannot silently overwrite each other.
            long expected = env.GetLong("expected_revision", -1);
            if (expected >= 0 && expected != EnforcementProfile.Revision)
            {
                Fail(client, null, "stale_revision",
                     $"The profile changed to revision {EnforcementProfile.Revision} while you were preparing. Reopen and publish again.");
                return;
            }

            List<Entry> entries = new List<Entry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (JToken row in rows)
            {
                string path = (string)row["path"] ?? "";
                long size   = (long?)row["size"] ?? -1;
                string hash = ((string)row["hash"] ?? "").ToLowerInvariant();

                // Validated, not trusted: a modified client is the whole reason this runs server-side.
                if (!ManagedPath.IsManagedName(path)) { Fail(client, null, "bad_path", $"'{path}' is not a managed config name."); return; }
                if (!seen.Add(path)) { Fail(client, null, "duplicate_path", $"'{path}' appears twice."); return; }
                if (size < 0 || size > MaxFileBytes) { Fail(client, null, "bad_size", $"'{path}' declares {size} bytes."); return; }
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) { Fail(client, null, "bad_hash", $"'{path}' has no usable hash."); return; }

                total += size;
                if (total > MaxTotalBytes) { Fail(client, null, "too_large", $"A profile may total {MaxTotalBytes / (1024 * 1024)}MB."); return; }
                entries.Add(new Entry { Path = path, Size = size, Hash = hash });
            }

            HashSet<string> have = ActiveHashes();
            string id = Guid.NewGuid().ToString("N");
            string staging = Path.Combine(StagingRoot, id);

            Upload up = new Upload
            {
                Id = id, User = user, Entries = entries, Staging = staging,
                ProfileHash = (env.GetString("profile_hash") ?? "").ToLowerInvariant(),
                StartedTicks = DateTime.UtcNow.Ticks,
            };

            try
            {
                Directory.CreateDirectory(staging);
                // Hashes only: copying reused content here would let a client drive tens of MB of writes per Begin.
                foreach (Entry e in entries) e.Reused = have.Contains(e.Hash);
            }
            catch (Exception ex)
            {
                TryDelete(staging);
                ServerLog.Warn($"Enforcement: could not stage a publish from {user}: {ex.Message}");
                Fail(client, null, "staging_failed", "The server could not open a staging area for this publish.");
                return;
            }

            lock (_lock)
            {
                PurgeExpiredLocked(DateTime.UtcNow.Ticks);
                // One publish per admin: a second Begin replaces the first rather than running both.
                foreach (string k in _uploads.Where(kv => kv.Value.User == user).Select(kv => kv.Key).ToList())
                { TryDelete(_uploads[k].Staging); _uploads.Remove(k); }
                _uploads[id] = up;
            }

            int[] wanted = entries.Select((e, i) => new { e, i }).Where(x => !x.e.Reused).Select(x => x.i).ToArray();
            long reusedBytes = entries.Where(e => e.Reused).Sum(e => e.Size);

            KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementPublishAccepted, new
            {
                upload_id       = id,
                current_revision = EnforcementProfile.Revision,
                wanted,
                reused_files    = entries.Count - wanted.Length,
                reused_bytes    = reusedBytes,
                part_bytes      = PartRawBytes,
                credit          = CreditWindow,
                expires_in      = (int)Expiry.TotalSeconds,
            });
            ServerLog.Info($"Enforcement: publish {Short(id)} opened by {user} - {entries.Count} file(s), " +
                           $"{wanted.Length} wanted, {entries.Count - wanted.Length} reused ({reusedBytes / 1024}KB).");
        }

        private static void OnPart(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username ?? "?";
            string id   = env?.GetString("upload_id") ?? "";
            int index   = env?.GetInt("index", -1) ?? -1;
            string data = env?.GetString("data");
            bool last   = env?.GetBool("last", false) ?? false;
            if (data == null) return;

            Upload up;
            lock (_lock)
            {
                if (!_uploads.TryGetValue(id, out up) || up == null) { Fail(client, id, "unknown_upload", "That publish is not open."); return; }
                // Ownership, not just admin: another admin's upload id must not be writable.
                if (!string.Equals(up.User, user, StringComparison.Ordinal)) { Fail(client, id, "not_yours", "That publish belongs to another admin."); return; }
                if (up.Committing) return;
            }
            if (!EnforcementHandler.IsAdmin(client)) { Abort(id); Fail(client, id, "not_admin", "Admin was removed during the publish."); return; }
            if (index < 0 || index >= up.Entries.Count) { Fail(client, id, "bad_index", "Part outside the manifest."); return; }

            Entry entry = up.Entries[index];
            if (entry.Reused) return;

            byte[] part;
            try { part = Convert.FromBase64String(data); }
            catch { Abort(id); Fail(client, id, "bad_part", "A part was not valid base64."); return; }
            if (part.Length > PartRawBytes) { Abort(id); Fail(client, id, "part_too_big", "A part exceeded the frame size."); return; }

            // Parts are deflated, so more packed bytes than the declared raw size is refused; the commit-time
            // inflation cap at the manifest size is what actually stops a bomb.
            long after = entry.Received + part.Length;
            long packedCap = Math.Max(4096, entry.Size + 4096);
            if (after > packedCap) { Abort(id); Fail(client, id, "overrun", $"'{entry.Path}' sent more bytes than its declared size allows."); return; }
            if (up.ReceivedBytes + part.Length > MaxTotalBytes) { Abort(id); Fail(client, id, "too_large", "Publish exceeded the total byte budget."); return; }

            try
            {
                string dest = Path.Combine(up.Staging, entry.Path + PackedSuffix);
                using (FileStream fs = new FileStream(dest, FileMode.Append, FileAccess.Write, FileShare.None))
                    fs.Write(part, 0, part.Length);
            }
            catch (Exception ex)
            {
                Abort(id);
                ServerLog.Warn($"Enforcement: staging write failed for {Short(id)}: {ex.Message}");
                Fail(client, id, "staging_failed", "The server could not write the staged profile.");
                return;
            }

            entry.Received = after;
            up.ReceivedBytes += part.Length;

            if (last && entry.Received <= 0)
            { Abort(id); Fail(client, id, "short_file", $"'{entry.Path}' carried no content."); return; }

            KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementPublishAck,
                new { upload_id = id, index, received = entry.Received, credit = CreditWindow });
        }

        private static void OnCommit(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username ?? "?";
            string id   = env?.GetString("upload_id") ?? "";

            Upload up;
            lock (_lock)
            {
                if (!_uploads.TryGetValue(id, out up) || up == null) { Fail(client, id, "unknown_upload", "That publish is not open."); return; }
                if (!string.Equals(up.User, user, StringComparison.Ordinal)) { Fail(client, id, "not_yours", "That publish belongs to another admin."); return; }
                if (up.Committing) return;
                up.Committing = true;
            }
            if (!EnforcementHandler.IsAdmin(client)) { Abort(id); Fail(client, id, "not_admin", "Admin was removed during the publish."); return; }

            long started = DateTime.UtcNow.Ticks;
            try
            {
                if (!StageReused(up, out string reuseWhy))
                { Abort(id); Fail(client, id, "reuse_failed", reuseWhy); return; }

                foreach (Entry e in up.Entries)
                {
                    string p = Path.Combine(up.Staging, e.Path);
                    if (!e.Reused && !Inflate(Path.Combine(up.Staging, e.Path + PackedSuffix), p, e.Size, out string why))
                    { Abort(id); Fail(client, id, "bad_content", $"'{e.Path}': {why}"); return; }
                    if (!File.Exists(p)) { Abort(id); Fail(client, id, "missing_file", $"'{e.Path}' never arrived."); return; }
                    FileInfo fi = new FileInfo(p);
                    if (fi.Length != e.Size) { Abort(id); Fail(client, id, "size_mismatch", $"'{e.Path}' is {fi.Length} bytes, not {e.Size}."); return; }
                    // Re-hashed from what landed, so a client that declared one thing and sent another is refused.
                    if (!string.Equals(HashFile(p), e.Hash, StringComparison.OrdinalIgnoreCase))
                    { Abort(id); Fail(client, id, "hash_mismatch", $"'{e.Path}' does not match its declared hash."); return; }
                }

                byte[] zip = BuildZip(up);
                string hash = EnforcementProfile.Sha256Hex(zip);
                if (up.ProfileHash.Length == 64 && !string.Equals(hash, up.ProfileHash, StringComparison.OrdinalIgnoreCase))
                { Abort(id); Fail(client, id, "profile_hash_mismatch", "The assembled profile did not match its declared hash."); return; }

                if (!EnforcementProfile.SetProfile(zip, hash, DateTime.UtcNow.Ticks))
                { Abort(id); Fail(client, id, "commit_failed", "The server could not save that profile. The previous one is still active."); return; }

                // Read back before reporting success: a profile that cannot be reopened is not committed.
                if (!EnforcementProfile.HasProfile || !string.Equals(EnforcementProfile.Hash, hash, StringComparison.OrdinalIgnoreCase))
                { Fail(client, id, "readback_failed", "The committed profile did not read back. The previous one is still active."); return; }

                lock (_lock) { _uploads.Remove(id); }
                TryDelete(up.Staging);

                EnforcementHandler.BroadcastSnapshotPublic();

                long ms = (DateTime.UtcNow.Ticks - started) / TimeSpan.TicksPerMillisecond;
                int reused = up.Entries.Count(e => e.Reused);
                KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementPublishResult, new
                {
                    upload_id = id, ok = true,
                    revision  = EnforcementProfile.Revision,
                    hash,
                    files     = up.Entries.Count,
                    reused_files = reused,
                    bytes     = zip.LongLength,
                    received_bytes = up.ReceivedBytes,
                });
                ServerLog.Info($"Enforcement: publish {Short(id)} committed by {user} - rev {EnforcementProfile.Revision}, " +
                               $"{up.Entries.Count} file(s) ({reused} reused), {up.ReceivedBytes / 1024}KB received, " +
                               $"{zip.Length / 1024}KB stored, hash {hash.Substring(0, 8)}, {ms}ms.");
            }
            catch (Exception ex)
            {
                Abort(id);
                ServerLog.Warn($"Enforcement: publish {Short(id)} failed to commit: {ex.Message}");
                Fail(client, id, "commit_failed", "The server could not assemble that profile. The previous one is still active.");
            }
        }

        private static void OnCancel(ServerClient client, KmhEnvelope env)
        {
            string id = env?.GetString("upload_id") ?? "";
            string user = client?.GetData<UserFile>()?.Username ?? "?";
            lock (_lock)
            {
                if (_uploads.TryGetValue(id, out Upload up) && up != null
                    && string.Equals(up.User, user, StringComparison.Ordinal))
                { TryDelete(up.Staging); _uploads.Remove(id); }
            }
            KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementPublishResult,
                new { upload_id = id, ok = false, error = "cancelled", message = "Publish cancelled." });
        }

        private static void Abort(string id)
        {
            lock (_lock)
            {
                if (!_uploads.TryGetValue(id, out Upload up) || up == null) return;
                _uploads.Remove(id);
                TryDelete(up.Staging);
            }
        }

        internal static void PurgeExpired()
        {
            lock (_lock) PurgeExpiredLocked(DateTime.UtcNow.Ticks);
        }

        private static void PurgeExpiredLocked(long nowTicks)
        {
            List<string> gone = null;
            foreach (KeyValuePair<string, Upload> kv in _uploads)
                if (nowTicks - kv.Value.StartedTicks > Expiry.Ticks) (gone ??= new List<string>()).Add(kv.Key);
            if (gone == null) return;
            foreach (string k in gone) { TryDelete(_uploads[k].Staging); _uploads.Remove(k); }
            ServerLog.Verbose($"Enforcement: dropped {gone.Count} expired publish transaction(s).");
        }

        // Staging from a previous process is not resumable, so it is swept at boot rather than accumulating.
        internal static void SweepStagingAtBoot()
        {
            try
            {
                if (!Directory.Exists(StagingRoot)) return;
                int n = 0;
                foreach (string d in Directory.GetDirectories(StagingRoot)) { TryDelete(d); n++; }
                if (n > 0) ServerLog.Info($"Enforcement: cleared {n} abandoned publish staging folder(s).");
            }
            catch (Exception ex) { ServerLog.Warn($"Enforcement: could not clear publish staging: {ex.Message}"); }
        }

        // Cached per active profile hash: Begin runs on a client's say-so, so it must not inflate the profile each time.
        private static string _hashesFor = "";
        private static HashSet<string> _hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> ActiveHashes()
        {
            string active = EnforcementProfile.Hash ?? "";
            lock (_lock)
                if (string.Equals(_hashesFor, active, StringComparison.OrdinalIgnoreCase)) return _hashes;

            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, byte[]> kv in ActiveContentByHash()) set.Add(kv.Key);
            lock (_lock) { _hashes = set; _hashesFor = active; }
            return set;
        }

        // A reused file gone since Begin is reported rather than silently dropped from the profile.
        private static bool StageReused(Upload up, out string why)
        {
            why = "";
            bool any = false;
            foreach (Entry e in up.Entries) if (e.Reused) { any = true; break; }
            if (!any) return true;

            Dictionary<string, byte[]> have = ActiveContentByHash();
            foreach (Entry e in up.Entries)
            {
                if (!e.Reused) continue;
                if (!have.TryGetValue(e.Hash, out byte[] content) || content.LongLength != e.Size)
                { why = $"'{e.Path}' was reused from the active profile, which no longer holds it - publish it again."; return false; }
                try { File.WriteAllBytes(Path.Combine(up.Staging, e.Path), content); }
                catch (Exception ex) { why = $"'{e.Path}' could not be staged: {ex.Message}"; return false; }
            }
            return true;
        }

        // Content the active profile already holds, so an unchanged file is never transferred again.
        private static Dictionary<string, byte[]> ActiveContentByHash()
        {
            Dictionary<string, byte[]> map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                byte[] zip = EnforcementProfile.SerializeBytes();
                if (zip == null || zip.Length == 0) return map;
                using (MemoryStream ms = new MemoryStream(zip))
                using (ZipArchive a = new ZipArchive(ms, ZipArchiveMode.Read))
                    foreach (ZipArchiveEntry e in a.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name) || e.Length > MaxFileBytes) continue;
                        using (Stream s = e.Open())
                        using (MemoryStream into = new MemoryStream())
                        {
                            s.CopyTo(into);
                            byte[] bytes = into.ToArray();
                            map[HashBytes(bytes)] = bytes;
                        }
                    }
            }
            catch (Exception ex) { ServerLog.Verbose($"Enforcement: could not index the active profile for reuse: {ex.Message}"); }
            return map;
        }

        // Capped at the manifest's declared size, so a crafted endless stream stops there instead of filling the disk.
        internal static bool Inflate(string packed, string dest, long maxBytes, out string why)
        {
            why = "";
            if (!File.Exists(packed)) { why = "never arrived"; return false; }
            try
            {
                byte[] buffer = new byte[64 * 1024];
                long written = 0;
                using (FileStream src = new FileStream(packed, FileMode.Open, FileAccess.Read, FileShare.None))
                using (DeflateStream z = new DeflateStream(src, CompressionMode.Decompress))
                using (FileStream outFs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        if (written > maxBytes) { why = $"expands past its declared {maxBytes} bytes"; return false; }
                        outFs.Write(buffer, 0, read);
                    }
                }
                try { File.Delete(packed); } catch { }
                return true;
            }
            catch (InvalidDataException) { why = "is not a valid compressed stream"; return false; }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        private static byte[] BuildZip(Upload up)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                    foreach (Entry e in up.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
                        zip.CreateEntryFromFile(Path.Combine(up.Staging, e.Path), e.Path, CompressionLevel.Optimal);
                return ms.ToArray();
            }
        }

        private static void TryDelete(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        private static string Short(string id) => string.IsNullOrEmpty(id) ? "?" : id.Substring(0, Math.Min(8, id.Length));

        internal static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return Hex(sha.ComputeHash(fs));
        }

        internal static string HashBytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes ?? Array.Empty<byte>()));
        }

        private static string Hex(byte[] h)
        {
            StringBuilder sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
