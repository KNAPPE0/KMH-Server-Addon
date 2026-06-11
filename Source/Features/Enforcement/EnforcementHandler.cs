using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Enforcement
{
    // Server side of enforcement: pushes the snapshot (with server-authoritative is_admin) on handshake/change,
    // streams the chunked profile when enforcing, and sends a restore signal when it's turned off
    internal static class EnforcementHandler
    {
        // 32KB raw/chunk: base64 (~43KB) + envelope overhead stays under the 64KB cap.
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

        // -- admin publishes their configs as the server profile (chunked upload) --

        // Hard cap so a crafted packet can't OOM the server (4096 * 32KB = 128 MB, far more than any real config
        // zip)
        private const int MaxUploadChunks = 4096;

        private sealed class UploadState
        {
            public string Hash;
            public int ChunkCount;
            public int TotalBytes;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
        }

        private static readonly Dictionary<string, UploadState> _uploads = new Dictionary<string, UploadState>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _uploadLock = new object();

        private static void OnUploadBegin(ServerClient client, KmhEnvelope env)
        {
            if (!IsAdmin(client)) { KmhRouter.Notify(client, "negative", "Publishing configs is admin-only."); return; }
            int chunkCount = env?.GetInt("chunk_count", 0) ?? 0;
            if (chunkCount <= 0 || chunkCount > MaxUploadChunks)
            {
                KmhRouter.Notify(client, "negative", "Config upload rejected (bad size).");
                return;
            }
            string user = client.GetData<UserFile>()?.Username ?? "?";
            lock (_uploadLock)
                _uploads[user] = new UploadState
                {
                    Hash       = env?.GetString("hash") ?? "",
                    ChunkCount = chunkCount,
                    TotalBytes = env?.GetInt("total_bytes", 0) ?? 0,
                };
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
                try { st.Chunks[idx] = Convert.FromBase64String(data); } catch { /* skip bad chunk */ }
            }
        }

        private static void OnUploadEnd(ServerClient client, KmhEnvelope env)
        {
            if (!IsAdmin(client)) return;
            string user = client.GetData<UserFile>()?.Username ?? "?";

            UploadState st;
            lock (_uploadLock) { _uploads.TryGetValue(user, out st); _uploads.Remove(user); }
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

                // Verify the assembled zip against the hash the client declared.
                string computed = EnforcementProfile.Sha256Hex(all);
                if (!string.IsNullOrEmpty(st.Hash) && !string.Equals(computed, st.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    ServerLog.Warn($"Enforcement: upload hash mismatch from {user} (got {computed}, declared {st.Hash}).");
                    KmhRouter.Notify(client, "negative", "Publish failed - the upload was corrupted. Try again.");
                    return;
                }

                EnforcementProfile.SetProfile(all, computed, DateTime.UtcNow.Ticks);
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

        // Client requests the profile only when its hash differs, so we don't re-stream it on every reconnect
        private static void OnProfileRequest(ServerClient client, KmhEnvelope env)
        {
            if (!EnforcementConfig.Current.Enabled || !EnforcementProfile.HasProfile) return;
            ServerLog.Verbose($"Enforcement: profile requested by {client?.GetData<UserFile>()?.Username ?? "?"}");
            PushProfileTo(client);
        }

        // In-game admin dialog toggles enforcement. Server-authoritative admin check - a non-admin packet is
        // ignored with a notice
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

        // -- snapshot --

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

        // -- hard-enforcement profile push --

        public static void PushProfileTo(ServerClient client)
        {
            if (client == null || !EnforcementProfile.HasProfile) return;
            try
            {
                byte[] bytes = EnforcementProfile.SerializeBytes();
                string hash  = EnforcementProfile.Hash;
                int chunkCount = (bytes.Length + ChunkRawBytes - 1) / ChunkRawBytes;

                KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementProfileBegin, new
                {
                    hash,
                    file_count  = EnforcementProfile.FileCount,
                    chunk_count = chunkCount,
                    total_bytes = bytes.Length,
                });

                for (int i = 0; i < chunkCount; i++)
                {
                    int off = i * ChunkRawBytes;
                    int len = Math.Min(ChunkRawBytes, bytes.Length - off);
                    KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementProfileChunk, new
                    {
                        hash,
                        index = i,
                        data  = Convert.ToBase64String(bytes, off, len),
                    });
                }

                KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementProfileEnd, new { hash });
            }
            catch (Exception ex) { ServerLog.Warn($"Enforcement: profile push failed: {ex.Message}"); }
        }

        // Tell every client to lift enforcement and restore their personal configs.
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
