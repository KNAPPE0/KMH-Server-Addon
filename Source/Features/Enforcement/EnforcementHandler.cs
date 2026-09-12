using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Enforcement
{
    internal static class EnforcementHandler
    {
        // 32KB raw, because base64 inflates it to roughly 43KB and the frame ceiling is 64KB.
        private const int ChunkRawBytes = 32 * 1024;

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementSnapshotRequest, (c, e) => SendSnapshotTo(c));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementProfileRequest, OnProfileRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementSetEnabled,     OnSetEnabled);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementSetSafe,        OnSetSafe);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementSetFlag,        OnSetFlag);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementUploadBegin,    OnUploadBegin);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementUploadChunk,    OnUploadChunk);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.EnforcementUploadEnd,      OnUploadEnd);
        }

        // Caps a crafted upload well above any real config zip, so it cannot exhaust server memory.
        private const int MaxUploadChunks = 4096;

        // A separate ceiling, because the chunk cap alone does not bound the declared size.
        private const int MaxUploadBytes = MaxUploadChunks * ChunkRawBytes;

        // An upload begun and never finished would otherwise hold its chunks until the server restarts.
        private static readonly TimeSpan UploadStaleAfter = TimeSpan.FromMinutes(10);

        private sealed class UploadState
        {
            public string Hash;
            public int ChunkCount;
            public int TotalBytes;
            public long StartedUtcTicks;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
        }

        private static readonly Dictionary<string, UploadState> _uploads = new Dictionary<string, UploadState>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _uploadLock = new object();

        private static void OnUploadBegin(ServerClient client, KmhEnvelope env)
        {
            if (!IsAdmin(client)) { KmhRouter.Notify(client, "negative", "Publishing configs is admin-only."); return; }
            int chunkCount = env?.GetInt("chunk_count", 0) ?? 0;
            int totalBytes = env?.GetInt("total_bytes", 0) ?? 0;
            // total_bytes is client-declared and sizes the buffer, so the declaration alone could demand gigabytes.
            if (chunkCount <= 0 || chunkCount > MaxUploadChunks || totalBytes <= 0 || totalBytes > MaxUploadBytes)
            {
                KmhRouter.Notify(client, "negative", "Config upload rejected (bad size).");
                return;
            }
            string user = client.GetData<UserFile>()?.Username ?? "?";
            long now = DateTime.UtcNow.Ticks;
            lock (_uploadLock)
            {
                PurgeStaleUploadsLocked(now);
                _uploads[user] = new UploadState
                {
                    Hash            = env?.GetString("hash") ?? "",
                    ChunkCount      = chunkCount,
                    TotalBytes      = totalBytes,
                    StartedUtcTicks = now,
                };
            }
        }

        // Caller holds _uploadLock.
        private static void PurgeStaleUploadsLocked(long nowTicks)
        {
            List<string> stale = null;
            foreach (KeyValuePair<string, UploadState> kv in _uploads)
                if (kv.Value != null && nowTicks - kv.Value.StartedUtcTicks > UploadStaleAfter.Ticks)
                    (stale ??= new List<string>()).Add(kv.Key);
            if (stale == null) return;
            foreach (string k in stale) _uploads.Remove(k);
            ServerLog.Verbose($"Enforcement: dropped {stale.Count} abandoned config upload(s).");
        }

        private static void OnUploadChunk(ServerClient client, KmhEnvelope env)
        {
            if (!IsAdmin(client)) return;
            string user = client.GetData<UserFile>()?.Username ?? "?";
            int    idx  = env?.GetInt("index", -1) ?? -1;
            string data = env?.GetString("data");
            if (idx < 0 || data == null) return;
            lock (_uploadLock)
            {
                if (!_uploads.TryGetValue(user, out UploadState st)) return;
                // Bounded by the declared count, or Chunks grows past the cap meant to bound it.
                if (idx >= st.ChunkCount) return;
                try { st.Chunks[idx] = Convert.FromBase64String(data); } catch { }
            }
        }

        private static void OnUploadEnd(ServerClient client, KmhEnvelope env)
        {
            if (!IsAdmin(client)) return;
            string user = client.GetData<UserFile>()?.Username ?? "?";

            UploadState st;
            lock (_uploadLock) { _uploads.TryGetValue(user, out st); _uploads.Remove(user); PurgeStaleUploadsLocked(DateTime.UtcNow.Ticks); }
            if (st == null) return;
            if (st.Chunks.Count != st.ChunkCount)
            {
                KmhRouter.Notify(client, "negative", "Config upload incomplete - try Publish again.");
                return;
            }

            try
            {
                byte[] all = new byte[st.TotalBytes];
                int off = 0;
                for (int i = 0; i < st.ChunkCount; i++)
                {
                    if (!st.Chunks.TryGetValue(i, out byte[] p)) { KmhRouter.Notify(client, "negative", "Upload missing a chunk."); return; }
                    Buffer.BlockCopy(p, 0, all, off, p.Length);
                    off += p.Length;
                }

                string computed = EnforcementProfile.Sha256Hex(all);
                if (!string.IsNullOrEmpty(st.Hash) && !string.Equals(computed, st.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    ServerLog.Warn($"Enforcement: upload hash mismatch from {user} (got {computed}, declared {st.Hash}).");
                    KmhRouter.Notify(client, "negative", "Publish failed - the upload was corrupted. Try again.");
                    return;
                }

                if (!EnforcementProfile.SetProfile(all, computed, DateTime.UtcNow.Ticks))
                {
                    KmhRouter.Notify(client, "negative", "Publish failed - the server couldn't save that profile. " +
                                                         "The previous one is still active. Try again shortly.");
                    return;
                }
                BroadcastSnapshot(); // new hash -> enforcing clients pull the profile

                KmhRouter.Notify(client, "positive",
                    $"Published the server config profile ({EnforcementProfile.FileCount} file(s), {all.Length / 1024} KB).");
                ServerLog.Info($"Enforcement: {user} published the profile - {EnforcementProfile.FileCount} file(s), hash {EnforcementProfile.Hash}");
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Enforcement: upload assembly failed: {ex.Message}");
                KmhRouter.Notify(client, "negative", "Publish failed - see the server log.");
            }
        }

        // Client-initiated, so a reconnecting client with a matching hash never re-downloads the profile.
        private static void OnProfileRequest(ServerClient client, KmhEnvelope env)
        {
            if (!EnforcementConfig.Current.Enabled || !EnforcementProfile.HasProfile) return;
            ServerLog.Verbose($"Enforcement: profile requested by {client?.GetData<UserFile>()?.Username ?? "?"}");
            PushProfileTo(client);
        }

        private static void OnSetEnabled(ServerClient client, KmhEnvelope env)
        {
            if (!IsAdmin(client)) { KmhRouter.Notify(client, "negative", "Config enforcement is admin-only."); return; }
            bool enabled = env?.GetBool("enabled", false) ?? false;
            EnforcementConfig cfg = EnforcementConfig.Current;
            cfg.Enabled = enabled; cfg.Save();
            BroadcastSnapshot();
            if (!enabled) SendRestoreToAll();
            ServerLog.Info($"Enforcement {(enabled ? "ENABLED" : "DISABLED")} in-game by {client.GetData<UserFile>()?.Username}");
        }

        private static void OnSetSafe(ServerClient client, KmhEnvelope env)
        {
            if (!IsAdmin(client)) { KmhRouter.Notify(client, "negative", "Config enforcement is admin-only."); return; }
            string mod = env?.GetString("mod") ?? "";
            bool   add = env?.GetBool("add", false) ?? false;
            if (string.IsNullOrWhiteSpace(mod)) return;

            EnforcementConfig cfg = EnforcementConfig.Current;
            bool changed = add ? cfg.AddSafe(mod) : cfg.RemoveSafe(mod);
            if (changed)
            {
                BroadcastSnapshot();
                ServerLog.Info($"Enforcement safe {(add ? "+" : "-")}'{mod}' by {client.GetData<UserFile>()?.Username}");
            }
        }

        private static void OnSetFlag(ServerClient client, KmhEnvelope env)
        {
            if (!IsAdmin(client)) { KmhRouter.Notify(client, "negative", "Config enforcement is admin-only."); return; }
            string flag  = env?.GetString("flag") ?? "";
            bool   value = env?.GetBool("value", false) ?? false;
            if (EnforcementConfig.Current.SetFlag(flag, value))
            {
                BroadcastSnapshot();
                ServerLog.Info($"Enforcement flag '{flag}'={value} by {client.GetData<UserFile>()?.Username}");
            }
        }

        private static bool IsAdmin(ServerClient client) => client?.GetData<UserFile>()?.IsAdmin == true;

        private static object BuildSnapshotPayload(ServerClient client)
        {
            EnforcementConfig cfg = EnforcementConfig.Current;
            bool isAdmin = client?.GetData<UserFile>()?.IsAdmin == true;
            return new
            {
                enabled          = cfg.Enabled,
                admin_bypass     = cfg.AdminBypass,
                preserve_personal = cfg.PreservePersonalFields,
                is_admin         = isAdmin,
                safe_mods        = cfg.SafeMods ?? Array.Empty<string>(),
                has_profile      = EnforcementProfile.HasProfile,
                profile_files    = EnforcementProfile.FileCount,
                profile_hash     = EnforcementProfile.Hash,
            };
        }

        public static void SendSnapshotTo(ServerClient client)
        {
            if (client == null) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementSnapshot, BuildSnapshotPayload(client));
        }

        public static void BroadcastSnapshot()
        {
            int sent = 0;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (KmhRouter.SendTo(c, KmhProtocol.Kind.EnforcementSnapshot, BuildSnapshotPayload(c))) sent++;
            }
            EnforcementConfig cfg = EnforcementConfig.Current;
            ServerLog.Info($"Enforcement: broadcast snapshot to {sent} client(s) (enabled={cfg.Enabled}, safe={cfg.SafeMods?.Length ?? 0})");
        }

        public static void PushProfileTo(ServerClient client)
        {
            if (client == null || !EnforcementProfile.HasProfile) return;
            try
            {
                byte[] bytes = EnforcementProfile.SerializeBytes();
                string hash  = EnforcementProfile.Hash;
                int chunkCount = (bytes.Length + ChunkRawBytes - 1) / ChunkRawBytes;

                if (!KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementProfileBegin, new
                {
                    hash,
                    file_count  = EnforcementProfile.FileCount,
                    chunk_count = chunkCount,
                    total_bytes = bytes.Length,
                }))
                { LogPushAbandoned(client, 0, chunkCount); return; }

                for (int i = 0; i < chunkCount; i++)
                {
                    int off = i * ChunkRawBytes;
                    int len = Math.Min(ChunkRawBytes, bytes.Length - off);
                    // One refused chunk means the peer is gone; pushing the rest is thousands of failed writes and a profile nobody can assemble.
                    if (!KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementProfileChunk, new
                    {
                        hash,
                        index = i,
                        data  = Convert.ToBase64String(bytes, off, len),
                    }))
                    { LogPushAbandoned(client, i, chunkCount); return; }
                }

                KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementProfileEnd, new { hash });
            }
            catch (Exception ex) { ServerLog.Warn($"Enforcement: profile push failed: {ex.Message}"); }
        }

        private static void LogPushAbandoned(ServerClient client, int sent, int total)
            => ServerLog.Warn($"Enforcement: profile push to {client?.GetData<UserFile>()?.Username ?? "?"} abandoned "
                            + $"after {sent}/{total} chunk(s) - it will be re-requested when that client reconnects.");

        public static void SendRestoreToAll()
        {
            int sent = 0;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (KmhRouter.SendTo(c, KmhProtocol.Kind.EnforcementRestore, null)) sent++;
            }
            ServerLog.Info($"Enforcement: sent restore to {sent} client(s)");
        }
    }
}
