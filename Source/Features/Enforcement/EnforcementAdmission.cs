using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Enforcement
{
    internal static class EnforcementAdmission
    {
        internal const string Capability = "preworld_enforcement_gate";
        internal const string VerifyKind = "kmh.enforcement.verified";
        internal const string FailKind = "kmh.enforcement.failed";
        private static readonly ConditionalWeakTable<ServerClient, EnforcementAdmissionSession> Sessions = new();
        private static long _generation;
        private static MethodInfo _postLogin;
        [ThreadStatic] private static ServerClient _releasing;
        internal static bool Ready { get; private set; }

        internal static bool IsControl(string kind) => kind == VerifyKind || kind == FailKind
            || kind == KmhProtocol.Kind.Hello || kind == KmhProtocol.Kind.HelloAck
            // Admitted while held: it is the message that ends a hold the client cannot satisfy.
            || kind == KmhProtocol.Kind.Incompatible
            || kind == KmhProtocol.Kind.Ping || kind == KmhProtocol.Kind.Pong
            || kind == KmhProtocol.Kind.EnforcementSnapshot || kind == KmhProtocol.Kind.EnforcementSnapshotRequest
            || kind == KmhProtocol.Kind.EnforcementProfileRequest || kind == KmhProtocol.Kind.EnforcementProfileBegin
            || kind == KmhProtocol.Kind.EnforcementProfileChunk || kind == KmhProtocol.Kind.EnforcementProfileEnd;

        // Otherwise a player online before enforcement was switched on has no session and reads as held, freezing everyone.
        private static readonly ConditionalWeakTable<ServerClient, object> AdmittedUnheld = new();

        // Refused at login but not yet disconnected: whatever it sent in the same write must not run as that account.
        private static readonly ConditionalWeakTable<ServerClient, object> RefusedAtLogin = new();

        internal static bool WasRefused(ServerClient client) => client != null && RefusedAtLogin.TryGetValue(client, out _);

        internal static bool IsHeld(ServerClient client)
        {
            if (client == null) return false;
            if (RefusedAtLogin.TryGetValue(client, out _)) return true;
            if (Sessions.TryGetValue(client, out var s)) return s.State != EnforcementAdmissionSession.Phase.Released;
            if (AdmittedUnheld.TryGetValue(client, out _)) return false;
            return EnforcementConfig.Current.Enabled;
        }

        internal static bool HoldPostLoginForTest(ServerClient client) => HoldPostLogin(client);

        // RWT's own refusals (ban, whitelist, version, mods) never reach PostLogin, and what came with the login must not run.
        internal static bool InstallDenyHold(Harmony harmony)
        {
            MethodInfo deny = AccessTools.DeclaredMethod(typeof(PM_Login), "DenyConnectionWithReason");
            if (deny == null || !deny.IsStatic || deny.GetParameters().Length < 2 || deny.GetParameters()[0].ParameterType != typeof(ServerClient))
            {
                ServerLog.Warn("Colony Nexus admission: RWT's refusal call was not found - packets sent with a login RWT refuses are not held.");
                return false;
            }
            harmony.Patch(deny, prefix: new HarmonyMethod(typeof(EnforcementAdmission), nameof(HoldDenied)));
            return true;
        }

        private static void HoldDenied(ServerClient __0) { if (__0 != null) RefusedAtLogin.AddOrUpdate(__0, true); }

        internal static void HoldDeniedForTest(ServerClient client) => HoldDenied(client);

        internal static void Install(Harmony harmony)
        {
            _postLogin = AccessTools.DeclaredMethod(typeof(PM_Login), "PostLogin", new[] { typeof(ServerClient) });
            if (_postLogin == null || !_postLogin.IsStatic || _postLogin.ReturnType != typeof(void))
                throw new InvalidOperationException("Colony Nexus admission: unsupported RWT PostLogin signature.");
            harmony.Patch(_postLogin, prefix: new HarmonyMethod(typeof(EnforcementAdmission), nameof(HoldPostLogin)));
            InstallDenyHold(harmony);
            var checks = typeof(PM_Mods).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "CheckForModConfigs" || m.Name == "CheckForModOrder").ToArray();
            if (checks.Length == 0)
            {
                // Older released schemas cannot carry settings or an order hash. Probe that exact shape,
                // rather than rejecting an owner's rebuild merely because its binary hash differs.
                if (!LegacyPresenceOnlyLayout(typeof(PM_Mods), typeof(PKT_Login)))
                    throw new InvalidOperationException("Colony Nexus admission: unknown RWT layout without settings/order targets.");
            }
            else
            {
                if (checks.Length != 2 || checks.Select(m => m.Name).Distinct().Count() != 2
                    || checks.Any(m => !IsConfigCheckSignature(m)))
                    throw new InvalidOperationException("Colony Nexus admission: unsupported RWT settings/order signature.");
                foreach (var check in checks)
                    harmony.Patch(check, prefix: new HarmonyMethod(typeof(EnforcementAdmission), nameof(SuppressConfigCheck)));
            }
            var enqueue = AccessTools.DeclaredMethod(typeof(Listener), "EnqueuePacket", new[] { typeof(PacketHeader), typeof(object) });
            if (enqueue == null || enqueue.ReturnType != typeof(void))
                throw new InvalidOperationException("Colony Nexus admission: unsupported RWT packet enqueue signature.");
            if (!RwtCompat.TargetClientResolved)
                throw new InvalidOperationException("Colony Nexus admission: RWT Listener has no TargetClient.");
            harmony.Patch(enqueue, prefix: new HarmonyMethod(typeof(EnforcementAdmission), nameof(FilterPacket)));
            KmhRouter.RegisterHandler(VerifyKind, OnVerified);
            KmhRouter.RegisterHandler(FailKind, (c, e) => { if (Matches(c, e)) Fail(c, "client declined or could not apply"); });
            Ready = true;
            ServerLog.Diag("Colony Nexus admission RWT assembly: " + typeof(PM_Login).Assembly.FullName
                + "; SHA256=" + RwtAssemblyHash() + "; target=" + _postLogin);
            ServerLog.Diag("Colony Nexus admission ready: PostLogin held; RWT presence unchanged; settings/order suppression follows Enforcement.Enabled.");
        }

        // RWT's on-disk assembly, never KMH's own, which is embedded in the single-file publish and has no Location.
        private static string RwtAssemblyHash()
        {
            try
            {
#pragma warning disable IL3000 // RWT is a separate on-disk assembly; the single-file rule is about ours.
                string path = typeof(PM_Login).Assembly.Location;
#pragma warning restore IL3000
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "(not on disk)";
                using var digest = System.Security.Cryptography.SHA256.Create();
                return Convert.ToHexString(digest.ComputeHash(System.IO.File.ReadAllBytes(path)));
            }
            catch (Exception ex) { return "(unavailable: " + ex.GetType().Name + ")"; }
        }

        internal static bool LegacyPresenceOnlyLayout(Type mods, Type login)
        {
            var presence = AccessTools.DeclaredMethod(mods, "CheckIfModConflict", new[] { typeof(ServerClient), login });
            var config = (login.GetProperty("RunningMods") ?? login.GetProperty("_runningMods"))?.PropertyType;
            var list = config?.GetProperty("ModConfigs")?.PropertyType;
            if (presence == null || presence.ReturnType != typeof(bool) || !presence.IsStatic
                || config == null || list == null || !list.IsGenericType
                || list.GetGenericTypeDefinition() != typeof(System.Collections.Generic.List<>)) return false;
            var row = list.GetGenericArguments()[0];
            return row.Name == "ModConfig" && row.GetProperty("Settings") == null
                && config.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .All(p => p.Name == "ModConfigs" || p.Name == "BypassMods");
        }

        internal static bool IsConfigCheckSignature(MethodInfo method)
            => method != null && method.IsStatic && method.ReturnType == typeof(bool)
                && method.GetParameters().Length == 2
                && method.GetParameters()[0].ParameterType == typeof(ServerClient)
                && method.GetParameters()[1].ParameterType.FullName == "RTShared.Files.Configs.FL_ModConfig";

        private static bool SuppressConfigCheck(ref bool __result)
            => RunNativeConfigCheck(EnforcementConfig.Current.Enabled, ref __result);

        internal static bool RunNativeConfigCheck(bool enabled, ref bool result)
        {
            if (!enabled) return true;
            result = false;
            return false;
        }

        private static bool HoldPostLogin(ServerClient __0)
        {
            if (ReferenceEquals(_releasing, __0)) return true;
            // Before RWT's own login sends the world, whether or not enforcement holds the session.
            if (Features.Moderation.BanGate.RefuseIfBanned(__0) || Features.Identity.AccountNameGuard.RefuseIfShared(__0))
            { RefusedAtLogin.AddOrUpdate(__0, true); return false; }
            if (Sessions.TryGetValue(__0, out _)) return false;
            if (!EnforcementConfig.Current.Enabled) { AdmittedUnheld.AddOrUpdate(__0, true); return true; }
            if (!Ready) { Fail(__0, "RWT interop is not ready"); return false; }
            var s = Sessions.GetValue(__0, c => new EnforcementAdmissionSession(KmhServerIdentity.Id,
                EnforcementProfile.Hash, Interlocked.Increment(ref _generation), PolicyFor(c)));
            KmhHandshakeHandler.SendHelloTo(__0);
            _ = Expire(__0, s);
            return false;
        }

        // Two clocks: reaching the ack is a round trip, while everything after it can include a consent prompt a
        // person has to read. One timer for both let a client that never acks hold PostLogin for five minutes.
        internal static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan AdmissionTimeout = TimeSpan.FromMinutes(5);

        private static async System.Threading.Tasks.Task Expire(ServerClient client, EnforcementAdmissionSession session)
        {
            await System.Threading.Tasks.Task.Delay(HelloTimeout);
            lock (session.Sync)
            {
                if (session.State == EnforcementAdmissionSession.Phase.Released) return;
                if (session.State == EnforcementAdmissionSession.Phase.AwaitingHello)
                {
                    Fail(client, $"no Colony Nexus handshake within {(int)HelloTimeout.TotalSeconds}s - the client is not "
                               + $"running a Colony Nexus build that speaks protocol v{KmhProtocol.CurrentVersion}");
                    return;
                }
            }

            await System.Threading.Tasks.Task.Delay(AdmissionTimeout - HelloTimeout);
            lock (session.Sync)
                if (session.State != EnforcementAdmissionSession.Phase.Released) Fail(client, "admission timed out");
        }

        internal static bool HandleHello(ServerClient client)
        {
            if (!Sessions.TryGetValue(client, out var s)) return false;
            if (s.State == EnforcementAdmissionSession.Phase.Released) return false;
            if (!s.AcceptHello(KmhHandshakeHandler.ClientDeclares(client, Capability)))
                Fail(client, "client is missing " + Capability);
            else EnforcementHandler.SendSnapshotTo(client);
            return true;
        }

        internal static object Bind(ServerClient client, object data)
        {
            var payload = data == null ? new JObject() : JObject.FromObject(data);
            if (Sessions.TryGetValue(client, out var s))
            {
                payload["admission_id"] = s.Id;
                payload["server_id"] = s.ServerId;
                payload["admission_generation"] = s.Generation;
                payload["admission_hash"] = s.ProfileHash;
            }
            return payload;
        }

        internal static bool Matches(ServerClient client, KmhEnvelope env)
            => client != null && env != null && Sessions.TryGetValue(client, out var s)
                && env.GetString("admission_id") == s.Id && env.GetString("server_id") == s.ServerId
                && env.GetLong("admission_generation") == s.Generation && env.GetString("admission_hash") == s.ProfileHash;

        private static void OnVerified(ServerClient client, KmhEnvelope env)
        {
            if (!Matches(client, env) || !KmhRouter.IsLive(client)) return;
            var s = Sessions.GetValue(client, _ => throw new InvalidOperationException());
            lock (s.Sync)
            {
                if (s.State == EnforcementAdmissionSession.Phase.Released) return;
                if (s.ProfileHash != (EnforcementProfile.Hash ?? "") || s.ServerId != KmhServerIdentity.Id || s.Policy != PolicyFor(client))
                { Fail(client, "profile changed during admission; reconnect"); return; }
                string resolution = EnforcementConfig.Current.AdminBypass && client.GetData<UserFile>()?.IsAdmin == true
                    ? "AdminBypass" : !EnforcementProfile.HasProfile ? "NoProfile" : "ProfileVerified";
                if (env.GetString("resolution") != resolution) { Fail(client, "admission resolution does not match server policy"); return; }
                if (!s.TryRelease(env.GetString("admission_id"), env.GetString("server_id"),
                    env.GetString("admission_hash"), env.GetLong("admission_generation"), env.GetBool("active"))) return;
                ServerLog.Info($"Colony Nexus admission VERIFIED -> Released ({s.Id}, {resolution}); releasing native PostLogin once.");
                var previous = _releasing;
                try { _releasing = client; _postLogin.Invoke(null, new object[] { client }); }
                catch (TargetInvocationException ex) { Fail(client, "native PostLogin failed: " + ex.InnerException?.Message); }
                finally { _releasing = previous; }
            }
        }

        internal static void Fail(ServerClient client, string reason)
        {
            if (client == null) return;
            if (Sessions.TryGetValue(client, out var s)) s.Fail();
            ServerLog.Warn("Colony Nexus admission refused: " + reason);
            client.Listener?.MarkForDisconnect();
        }

        private static string PolicyFor(ServerClient client)
        {
            var cfg = EnforcementConfig.Current;
            return string.Join("|", cfg.Enabled, cfg.AdminBypass, cfg.PreservePersonalFields,
                client?.GetData<UserFile>()?.IsAdmin == true,
                string.Join("\n", (cfg.SafeMods ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        }

        // Includes unsolicited native broadcasts, which can run independently of PostLogin.
        private static bool FilterPacket(Listener __instance, PacketHeader __0, object __1)
        {
            if (!IsHeld(RwtCompat.TargetClientOf(__instance))) return true;
            if (__0.Equals(RwtCompat.ChatHeader))
                return __1 is PKT_Chat chat && IsControl(KmhEnvelope.TryParse(chat.Message)?.Kind);
            return AllowedWhileHeld(__0.ToString());
        }

        // The browser report only ever goes to RWT's own browser connection, which has no admission session.
        internal static bool AllowedWhileHeld(string header)
            => header == "Handshake" || header == "KeepAlive" || header == "Version" || header == "Login"
                || header == "ServerPassword" || header == "HandshakeManager" || header == "KeepAliveManager"
                || header == "VersionManager" || header == "LoginManager" || header == "ServerPasswordManager"
                || header == "ServerBrowserTelemetry";
    }
}
