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
            EnforcementPublish.Register();
        }

        // Client-initiated, so a reconnecting client with a matching hash never re-downloads the profile.
        private static void OnProfileRequest(ServerClient client, KmhEnvelope env)
        {
            if (EnforcementAdmission.IsHeld(client) && !EnforcementAdmission.Matches(client, env)) return;
            if (!EnforcementConfig.Current.Enabled || !EnforcementProfile.HasProfile) return;
            // A client asks only when its copy differs, so the same profile again within minutes is no retry it needs;
            // answering every request let any player have the server stream the whole profile on demand.
            if (!MayPushTo(client, EnforcementProfile.Hash, DateTime.UtcNow))
            {
                ServerLog.Verbose($"Enforcement: repeat profile request from {client?.GetData<UserFile>()?.Username ?? "?"} ignored.");
                return;
            }
            ServerLog.Verbose($"Enforcement: profile requested by {client?.GetData<UserFile>()?.Username ?? "?"}");
            PushProfileTo(client);
        }

        internal static readonly TimeSpan RepushAfter = TimeSpan.FromMinutes(2);
        private sealed class LastPush { public string Hash; public DateTime At; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServerClient, LastPush> _lastPush = new();

        internal static bool MayPushTo(ServerClient client, string hash, DateTime now)
        {
            if (client == null) return false;
            LastPush last = _lastPush.GetOrCreateValue(client);
            lock (last)
            {
                if (last.Hash == hash && now - last.At < RepushAfter) return false;
                last.Hash = hash; last.At = now;
                return true;
            }
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

        internal static bool IsAdmin(ServerClient client) => client?.GetData<UserFile>()?.IsAdmin == true;

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
                profile_revision = EnforcementProfile.Revision,
                // ModSync rides this snapshot so a client decides and applies both in one transaction.
                mod_order        = Mods.ModProfile.Current.Order.ToArray(),
                mod_order_hash   = Mods.ModProfile.Current.OrderHash,
            };
        }

        public static void SendSnapshotTo(ServerClient client)
        {
            if (client == null) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.EnforcementSnapshot, BuildSnapshotPayload(client));
        }

        internal static void BroadcastSnapshotPublic() => BroadcastSnapshot();

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
                    // One refused chunk means the peer is gone; the rest would be thousands of failed writes for nothing.
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
