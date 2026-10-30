using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Global.Guilds;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    // Nothing may be installed here that Global did not accept, including across an interruption.
    internal static class KmhIdentityChangeSelfTest
    {
        private const string Server = "0123456789abcdef0123456789abcde1";
        private static readonly CancellationToken Ct = CancellationToken.None;

        private sealed class Call { public string Path; public object Body; public string Bearer; }

        private sealed class FakeGlobal
        {
            public readonly List<Call> Calls = new List<Call>();
            public Func<Call, GlobalCallResult> Answer = _ => GlobalCallResult.Offline("unreachable");

            public Task<GlobalCallResult> Send(string path, object body, string bearer, CancellationToken ct)
            {
                Call c = new Call { Path = path, Body = body, Bearer = bearer };
                Calls.Add(c);
                return Task.FromResult(Answer(c));
            }
        }

        private static GlobalCallResult Ok(object body)
            => new GlobalCallResult { Ok = true, Status = 200, Body = JObject.FromObject(body) };

        private static GlobalCallResult No(string code, int status, params (string K, string V)[] args)
        {
            GlobalCallResult r = new GlobalCallResult { Ok = false, Status = status, ErrorCode = code, CorrelationId = "selftest" };
            foreach ((string k, string v) in args) r.Args[k] = v;
            return r;
        }

        private static string Key(string name) => Path.Combine(KmhDataPaths.IdentityDir, name);

        private static string[] Files => new[]
        {
            Key("global-signing.key"), Key("global-signing.next.key"), Key("global-signing.previous.key"),
            Key("identity-change.json"), KmhDataPaths.GlobalConfigFile, KmhDataPaths.GlobalSetupFile,
            KmhDataPaths.FederatedGuildsFile,
        };

        private static int _restarts;

        private static FakeGlobal Arrange(bool owned = true, bool registered = true)
        {
            foreach (string f in Files) if (File.Exists(f)) File.Delete(f);
            KmhServerKey.ResetForTest();
            KmhIdentityChange.Stop();
            KmhIdentityChange.ForgetGrantForTest();
            KmhDataMeta.SetInstanceIdForTest(Server);
            GlobalConfig cfg = new GlobalConfig
            {
                Enabled = owned && registered, RegisteredServerId = registered ? Server : "", SigningGeneration = 3,
                BaseUrl = "https://global.test.invalid",
            };
            cfg.Save();
            GlobalSetupState setup = new GlobalSetupState { BackendRegistered = registered && !owned, OwnerUserId = owned ? "owner" : "" };
            setup.Current = registered && owned ? SetupStage.Registered : registered ? SetupStage.NotStarted : SetupStage.Failed;
            setup.Save();
            FederatedGuildStore.ClearForTest();
            FederatedGuildStore.SaveToDisk();
            KmhServerKey.Load();

            FakeGlobal g = new FakeGlobal();
            KmhIdentityChange.SenderForTest = g.Send;
            _restarts = 0;
            KmhIdentityChange.RestartForTest = () => _restarts++;
            return g;
        }

        private static Dictionary<string, byte[]> Snapshot()
        {
            var saved = new Dictionary<string, byte[]>();
            foreach (string f in Files) saved[f] = File.Exists(f) ? File.ReadAllBytes(f) : null;
            return saved;
        }

        private static void Restore(Dictionary<string, byte[]> saved, string instance)
        {
            KmhIdentityChange.Stop();
            KmhIdentityChange.SenderForTest = null;
            KmhIdentityChange.RestartForTest = null;
            KmhIdentityChange.ForgetGrantForTest();
            foreach (KeyValuePair<string, byte[]> f in saved)
            {
                if (f.Value == null) { if (File.Exists(f.Key)) File.Delete(f.Key); }
                else File.WriteAllBytes(f.Key, f.Value);
            }
            KmhServerKey.ResetForTest();
            GlobalConfig.Reload();
            GlobalSetupState.Reload();
            FederatedGuildStore.ClearForTest();
            FederatedGuildStore.LoadFromDisk();
            KmhGlobalRegistrar.Reset();
            KmhGlobalRegistrar.ClearRevokedForTest();
            KmhDataMeta.SetInstanceIdForTest(instance);
        }

        private static string Run(Func<CancellationToken, Task<string>> call)
        {
            string said = call(Ct).GetAwaiter().GetResult();
            KmhIdentityChange.Stop();
            return said;
        }

        private static Func<Call, GlobalCallResult> Approving(string kind, int generation, Func<Call, GlobalCallResult> onChange = null)
            => c =>
            {
                if (c.Path == KmhIdentityChange.OwnerLinksPath)
                    return Ok(new { link_id = "link-1", code = "ABCD-EFGH", link_url = "https://global.example/link-server",
                                    expires_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 900 });
                if (c.Path == KmhIdentityChange.OwnerLinksPath + "/link-1/poll")
                    return Ok(new { state = "Completed", registration_grant = "kmhrg_selftest", owner_user_id = "owner" });
                if (c.Path == KmhIdentityChange.ChallengePath)
                    return c.Bearer == "kmhrg_selftest" ? Ok(new { challenge_id = "challenge-1" }) : No("KMH-AUTH-401", 401);
                string expected = kind == IdentityChangeKind.Rotate ? KmhIdentityChange.RotatePath(Server) : KmhIdentityChange.RecoverPath(Server);
                if (c.Path == expected)
                    return onChange != null ? onChange(c) : Ok(new { server_id = Server, signing_generation = generation });
                return No("KMH-TEST-404", 404);
            };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            string instance = KmhDataMeta.InstanceId;
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                Dictionary<string, byte[]> saved = Snapshot();
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { Restore(saved, instance); }
            }

            Check("Identity: a rotation installs only the key Global accepted, signed by both keys", () =>
            {
                FakeGlobal g = Arrange();
                string oldKey = KmhServerKey.PublicKey;
                RecoverBody sent = null;
                g.Answer = Approving(IdentityChangeKind.Rotate, 4, c => { sent = (RecoverBody)c.Body; return Ok(new { signing_generation = 4 }); });

                string begun = Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                string stagedKey = KmhServerKey.StagedPublicKey;
                bool waiting = KmhIdentityChange.Pending?.Stage == IdentityChangeStage.AwaitingApproval
                            && KmhServerKey.PublicKey == oldKey && stagedKey.Length > 0;

                string done = Run(KmhIdentityChange.StepAsync);
                byte[] canonical = sent?.Canonical();
                bool signedByNew = sent != null && sent.PublicKey == stagedKey && KmhGlobalSigning.Verify(stagedKey, canonical, sent.Signature);
                bool signedByOld = sent != null && KmhGlobalSigning.Verify(oldKey, canonical, sent.PreviousKeySignature ?? "");
                bool grantUsed = g.Calls.Exists(c => c.Path == KmhIdentityChange.RotatePath(Server) && c.Bearer == "kmhrg_selftest");
                bool installed = KmhServerKey.PublicKey == stagedKey && !KmhServerKey.StagedExists
                              && File.Exists(Key("global-signing.previous.key")) && GlobalConfig.Current.SigningGeneration == 4;
                bool cleared = KmhIdentityChange.Pending == null && _restarts == 1;

                bool ok = waiting && done == "done" && signedByNew && signedByOld && grantUsed && installed && cleared;
                return (ok, ok ? "staged first, sent with the owner's grant, installed at generation 4, old key kept aside"
                              : $"begun='{begun}', waiting={waiting}, done='{done}', new={signedByNew}, old={signedByOld}, "
                                + $"grant={grantUsed}, installed={installed}, cleared={cleared}");
            });

            Check("Identity: a recovery is signed by the new key alone", () =>
            {
                FakeGlobal g = Arrange();
                RecoverBody sent = null;
                g.Answer = Approving(IdentityChangeKind.Recover, 4, c => { sent = (RecoverBody)c.Body; return Ok(new { signing_generation = 4 }); });
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Recover, ct));
                string staged = KmhServerKey.StagedPublicKey;
                string done = Run(KmhIdentityChange.StepAsync);
                bool ok = done == "done" && sent != null && sent.PreviousKeySignature == null
                       && KmhGlobalSigning.Verify(staged, sent.Canonical(), sent.Signature) && KmhServerKey.PublicKey == staged;
                return (ok, ok ? "no outgoing-key signature, new key installed" : $"done='{done}', previous={sent?.PreviousKeySignature ?? "(none)"}");
            });

            Check("Identity: a lost answer is settled by what Global holds, never by asking again blind", () =>
            {
                FakeGlobal g = Arrange();
                string oldKey = KmhServerKey.PublicKey;
                g.Answer = Approving(IdentityChangeKind.Rotate, 4, _ => GlobalCallResult.Offline("unreachable"));
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                string staged = KmhServerKey.StagedPublicKey;
                string lost = Run(KmhIdentityChange.StepAsync);
                bool submitted = lost == null && KmhIdentityChange.Pending?.Stage == IdentityChangeStage.Submitted
                              && KmhServerKey.PublicKey == oldKey && KmhServerKey.StagedExists;

                // Global installed it: only a heartbeat signed by the staged key verifies, at the newer generation.
                int rotations = g.Calls.FindAll(c => c.Path == KmhIdentityChange.RotatePath(Server)).Count;
                g.Answer = c => c.Path == KmhIdentityChange.HeartbeatPath
                                && KmhGlobalSigning.Verify(staged, ((HeartbeatBody)c.Body).Canonical(), ((HeartbeatBody)c.Body).Signature)
                    ? No("KMH-SRV-317", 409, ("registered_generation", "4"))
                    : No("KMH-SRV-303", 401);
                string settled = Run(KmhIdentityChange.StepAsync);
                bool askedOnce = g.Calls.FindAll(c => c.Path == KmhIdentityChange.RotatePath(Server)).Count == rotations;
                bool installed = KmhServerKey.PublicKey == staged && GlobalConfig.Current.SigningGeneration == 4
                              && KmhIdentityChange.Pending == null;

                bool ok = submitted && settled == "done" && askedOnce && installed;
                return (ok, ok ? "kept Submitted with both keys, then proved Global holds the new one and finished"
                              : $"submitted={submitted} ('{lost}'), settled='{settled}', askedOnce={askedOnce}, installed={installed}");
            });

            Check("Identity: an answer lost before Global acted keeps the old key and asks for approval again", () =>
            {
                FakeGlobal g = Arrange();
                string oldKey = KmhServerKey.PublicKey;
                g.Answer = Approving(IdentityChangeKind.Rotate, 4, _ => GlobalCallResult.Offline("unreachable"));
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                Run(KmhIdentityChange.StepAsync);
                g.Answer = _ => No("KMH-SRV-303", 401);
                string said = Run(KmhIdentityChange.StepAsync);
                IdentityChangeState s = KmhIdentityChange.Pending;
                bool ok = said != null && said.Contains("never landed") && s?.Stage == IdentityChangeStage.AwaitingApproval
                       && KmhServerKey.PublicKey == oldKey && KmhServerKey.StagedExists && GlobalConfig.Current.SigningGeneration == 3;
                return (ok, ok ? "old key still active, new key kept for the retry" : $"said='{said}', stage={s?.Stage}");
            });

            Check("Identity: a crash after the key swap is finished by the next refused heartbeat", () =>
            {
                FakeGlobal g = Arrange();
                g.Answer = Approving(IdentityChangeKind.Rotate, 4, _ => GlobalCallResult.Offline("unreachable"));
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                Run(KmhIdentityChange.StepAsync);

                // Still on the old key: a newer generation for it is not the change in flight.
                bool oldKeyIgnored = !KmhIdentityChange.TryAdoptFromRefusal(No("KMH-SRV-317", 409, ("registered_generation", "4")));
                KmhServerKey.PromoteStaged();
                bool strangerIgnored = !KmhIdentityChange.TryAdoptFromRefusal(No("KMH-SRV-316", 409, ("registered_generation", "4")))
                                    && GlobalConfig.Current.SigningGeneration == 3;
                int wait = KmhGlobalService.OnBeatRefused(No("KMH-SRV-317", 409, ("registered_generation", "4")));
                bool ok = oldKeyIgnored && strangerIgnored && wait == 5 && GlobalConfig.Current.SigningGeneration == 4
                       && KmhIdentityChange.Pending == null;
                return (ok, ok ? "only a 317 for the key in flight is adopted, straight from the heartbeat's refusal"
                              : $"oldKey={oldKeyIgnored}, stranger={strangerIgnored}, wait={wait}, generation={GlobalConfig.Current.SigningGeneration}");
            });

            Check("Identity: a retried rotation keeps the key it already staged", () =>
            {
                FakeGlobal g = Arrange();
                g.Answer = Approving(IdentityChangeKind.Rotate, 4);
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                string first = KmhServerKey.StagedFingerprint;
                IdentityChangeState s = KmhIdentityChange.Pending;
                s.PendingLinkExpiresUnix = 1;
                JsonFileStore.Save(Key("identity-change.json"), s);
                KmhServerKey.ResetForTest();
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                bool ok = first.Length > 0 && KmhServerKey.StagedFingerprint == first
                       && g.Calls.FindAll(c => c.Path == KmhIdentityChange.OwnerLinksPath).Count == 2;
                return (ok, ok ? "a fresh approval link, the same staged key" : $"first={first}, now={KmhServerKey.StagedFingerprint}");
            });

            Check("Identity: without a change in flight a newer generation is never adopted", () =>
            {
                Arrange();
                bool ok = !KmhIdentityChange.TryAdoptFromRefusal(No("KMH-SRV-317", 409, ("registered_generation", "9")))
                       && GlobalConfig.Current.SigningGeneration == 3;
                string advice = KmhGlobalRegistrar.DescribeRefusalFor("KMH-SRV-317");
                bool actionable = advice.Contains("kmh global recover") && advice.IndexOf("owner panel", StringComparison.OrdinalIgnoreCase) < 0;
                return (ok && actionable, ok && actionable ? "kept generation 3 and pointed the owner at 'kmh global recover'"
                                                           : $"generation={GlobalConfig.Current.SigningGeneration}, advice='{advice}'");
            });

            Check("Identity: a refusal keeps both keys and says why", () =>
            {
                FakeGlobal g = Arrange();
                string oldKey = KmhServerKey.PublicKey;
                g.Answer = Approving(IdentityChangeKind.Rotate, 4, _ => No("KMH-SRV-302", 403));
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                string said = Run(KmhIdentityChange.StepAsync);
                IdentityChangeState s = KmhIdentityChange.Pending;
                bool ok = said != null && said.Contains("not this server's owner") && s?.LastErrorCode == "KMH-SRV-302"
                       && s.Stage == IdentityChangeStage.AwaitingApproval && KmhServerKey.PublicKey == oldKey
                       && KmhServerKey.StagedExists && GlobalConfig.Current.SigningGeneration == 3;
                return (ok, ok ? "wrong owner refused, nothing installed" : $"said='{said}', code={s?.LastErrorCode}, stage={s?.Stage}");
            });

            Check("Identity: an Official or relaying server's lost key is sent to staff, not told to try again", () =>
            {
                FakeGlobal g = Arrange();
                g.Answer = Approving(IdentityChangeKind.Recover, 4, _ => No("KMH-SRV-328", 403));
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Recover, ct));
                string said = Run(KmhIdentityChange.StepAsync) ?? "";
                bool ok = said.Contains("Official or relays Global Chat") && said.Contains("rebind") && !said.Contains("to try again")
                       && KmhIdentityChange.Pending?.LastErrorCode == "KMH-SRV-328";
                return (ok, said);
            });

            Check("Identity: cancelling never throws away a key Global may already hold", () =>
            {
                FakeGlobal g = Arrange();
                g.Answer = Approving(IdentityChangeKind.Rotate, 4, _ => GlobalCallResult.Offline("unreachable"));
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                Run(KmhIdentityChange.StepAsync);
                string refused = KmhIdentityChange.Cancel(IdentityChangeKind.Rotate);
                bool kept = KmhServerKey.StagedExists && KmhIdentityChange.Pending != null;

                Arrange();
                KmhIdentityChange.SenderForTest = new FakeGlobal { Answer = Approving(IdentityChangeKind.Rotate, 4) }.Send;
                Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                string cancelled = KmhIdentityChange.Cancel(IdentityChangeKind.Rotate);
                bool gone = !KmhServerKey.StagedExists && KmhIdentityChange.Pending == null;

                bool ok = kept && refused.Contains("find out first") && gone && cancelled.Contains("abandoned");
                return (ok, ok ? "refused while Global's answer is unknown, allowed before anything was sent"
                              : $"kept={kept} ('{refused}'), gone={gone} ('{cancelled}')");
            });

            Check("Identity: an owner-less identity is pointed at the operator, not at a dead end", () =>
            {
                Arrange(owned: false);
                string recover = Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Recover, ct));
                string rotate = Run(ct => KmhIdentityChange.BeginAsync(IdentityChangeKind.Rotate, ct));
                bool ok = recover.Contains("kmh global rebind") && rotate.Contains("kmh global setup")
                       && !KmhServerKey.StagedExists && KmhIdentityChange.Pending == null;
                return (ok, ok ? "recover names the rebind, rotate names setup; nothing staged" : $"recover='{recover}', rotate='{rotate}'");
            });

            Check("Identity: a rebind proof is this server's key over its own links, and the operator's apply is taken up", () =>
            {
                FakeGlobal g = Arrange(owned: false, registered: false);
                FederatedGuildStore.Remember("ffffffffffffffffffffffffffffffff", "guild-1", "Second Guild", "officer", 1, "inc-2");
                FederatedGuildStore.Remember("00000000000000000000000000000001", "guild-2", "First Guild", "officer", 1, "inc-1");
                string key = KmhServerKey.PublicKey;
                RebindProveBody proof = null;
                string bearer = "unset";
                string state = "Proved";
                g.Answer = c =>
                {
                    if (c.Path != KmhIdentityChange.ProvePath) return No("KMH-TEST-404", 404);
                    proof = (RebindProveBody)c.Body;
                    bearer = c.Bearer;
                    return Ok(new { rebind_id = "rebind-1", server_id = Server, state, signing_generation = 2,
                                    expires_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 });
                };

                string proved = Run(ct => KmhIdentityChange.ProveRebindAsync("ab12-cd34 ef56", ct));
                bool signed = proof != null && proof.PublicKey == key && bearer == null
                           && KmhGlobalSigning.Verify(key, proof.Canonical(), proof.Signature)
                           && proof.SortedLinks.Count == 2 && proof.SortedLinks[0] == "00000000000000000000000000000001";
                bool waiting = KmhIdentityChange.Pending?.Stage == IdentityChangeStage.AwaitingOperator
                            && proved.Contains(KmhGlobalSigning.ShortFingerprint(KmhServerKey.Fingerprint));
                string still = Run(KmhIdentityChange.RebindStepAsync);

                state = "Applied";
                string applied = Run(KmhIdentityChange.RebindStepAsync);
                GlobalConfig cfg = GlobalConfig.Current;
                GlobalSetupState setup = GlobalSetupState.Current_;
                bool adopted = applied == "done" && cfg.RegisteredServerId == Server && cfg.SigningGeneration == 2
                            && !cfg.Enabled && setup.BackendRegistered && !setup.IsOwnerRegistered && KmhServerKey.PublicKey == key;
                bool linksStale = FederatedGuildStore.ReproofRequiredCount == 2 && KmhIdentityChange.Pending == null;

                bool ok = signed && waiting && still == null && adopted && linksStale;
                return (ok, ok ? "proved over both live links, waited, then took the id over at generation 2 as a private identity"
                              : $"signed={signed}, waiting={waiting} ('{proved}'), still='{still}', adopted={adopted} ('{applied}'), "
                                + $"stale={FederatedGuildStore.ReproofRequiredCount}");
            });

            Check("Identity: a rebind over an owned registration leaves a private identity an owner links again", () =>
            {
                FakeGlobal g = Arrange();
                g.Answer = _ => Ok(new { rebind_id = "rebind-1", state = "Applied", signing_generation = 4,
                                         expires_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 });
                string said = Run(ct => KmhIdentityChange.ProveRebindAsync("ABCD-EFGH-JKMN", ct));
                GlobalConfig cfg = GlobalConfig.Current;
                GlobalSetupState setup = GlobalSetupState.Current_;
                bool ok = said.Contains("taken the id over") && !cfg.Enabled && cfg.BackendIdentity && cfg.SigningGeneration == 4
                       && setup.BackendRegistered && !setup.IsOwnerRegistered && setup.OwnerUserId.Length == 0
                       && setup.Current == SetupStage.NotStarted && KmhIdentityChange.Pending == null;
                return (ok, ok ? "listing off, owner forgotten, private identity at generation 4"
                              : $"said='{said}', enabled={cfg.Enabled}, backend={setup.BackendRegistered}, owner='{setup.OwnerUserId}', stage={setup.Current}");
            });

            Check("Identity: a rebind the operator cancelled clears itself and changes nothing", () =>
            {
                FakeGlobal g = Arrange(owned: false, registered: false);
                g.Answer = _ => Ok(new { rebind_id = "rebind-1", state = "Proved", signing_generation = 2,
                                         expires_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 });
                Run(ct => KmhIdentityChange.ProveRebindAsync("ABCD-EFGH-JKMN", ct));
                g.Answer = _ => No("KMH-SRV-324", 404);
                string said = Run(KmhIdentityChange.RebindStepAsync);
                bool ok = said != null && KmhIdentityChange.Pending == null && GlobalConfig.Current.SigningGeneration == 3
                       && string.IsNullOrEmpty(GlobalConfig.Current.RegisteredServerId);
                return (ok, ok ? "cleared, generation and registration untouched" : $"said='{said}'");
            });

            Check("Identity: a code another key already proved is reported, never waited on", () =>
            {
                FakeGlobal g = Arrange(owned: false, registered: false);
                g.Answer = _ => No("KMH-SRV-325", 409);
                string said = Run(ct => KmhIdentityChange.ProveRebindAsync("ABCD-EFGH-JKMN", ct));
                bool ok = said.Contains("different key") && KmhIdentityChange.Pending == null;
                return (ok, ok ? "told the owner to have it cancelled" : $"said='{said}'");
            });

            Check("Identity: the identity paths pass the disclosure gate, and nothing else under /servers does", () =>
            {
                bool ok = KmhGlobalDisclosure.IsSetupPath(KmhIdentityChange.RotatePath(Server))
                       && KmhGlobalDisclosure.IsSetupPath(KmhIdentityChange.RecoverPath(Server))
                       && KmhGlobalDisclosure.IsSetupPath(KmhIdentityChange.ProvePath)
                       && !KmhGlobalDisclosure.IsSetupPath(KmhIdentityChange.HeartbeatPath)
                       && !KmhGlobalDisclosure.IsSetupPath($"/api/v1/servers/{Server}/rotate-key/extra")
                       && !KmhGlobalDisclosure.IsSetupPath($"/api/v1/servers/{Server}/metadata");
                return (ok, ok ? "rotate, recover and prove admitted; heartbeat and metadata still gated" : "the gate admits the wrong paths");
            });

            Check("Identity: the rotate command says what stops working before it creates anything", () =>
            {
                Arrange();
                var lines = new List<string>();
                AdminCommands.KmhGlobalCommands.Run(new[] { "global", "rotate" }, "console", lines.Add, true);
                string all = string.Join("\n", lines);
                bool ok = all.Contains("stops working") && all.Contains("kmh global rotate confirm")
                       && !KmhServerKey.StagedExists && KmhIdentityChange.Pending == null;
                return (ok, ok ? "explains first; the key is only created on confirm" : all);
            });

            return r;
        }
    }
}
