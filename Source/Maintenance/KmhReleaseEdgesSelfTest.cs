using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features;
using KMHServerAddon.Features.Global;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhReleaseEdgesSelfTest
    {
        // Computed outside this codebase: SHA-256 over 91 bytes (i * 7 + 3) % 256, first 32 hex characters.
        private const string VectorKey =
            "AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dzj6vH4_wYNFBsiKTA3PkVMU1phaG92fYSLkpmgp661vMPK0djf5u30-wIJEBceJSwzOkFIT1ZdZGtyeQ";
        private const string VectorId = "85373e64524dff047f5423100fa3b932";
        private const string ServerHex = "0fedcba9876543210fedcba987654321";

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool, string)> body)
            {
                try { (bool ok, string detail) = body(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, "threw " + ex.GetType().Name + ": " + ex.Message)); }
            }

            Launcher(Check);
            Integrity(Check);
            Identity(Check);
            return r;
        }

        private static Process Sleeper(int seconds)
        {
            ProcessStartInfo psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/c ping -n {seconds + 1} 127.0.0.1 >nul")
                : new ProcessStartInfo("sleep", seconds.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            return Process.Start(psi);
        }

        private static void Launcher(Action<string, Func<(bool, string)>> Check)
        {
            Check("launcher: the server notices its launcher has gone, and not before", () =>
            {
                using Process parent = Sleeper(2);
                // Not disposed: the watch thread may still set it after a failed wait.
                var gone = new ManualResetEventSlim();
                bool watching = KmhParentWatch.Start(parent.Id.ToString(), () => gone.Set(), 100);
                bool early = gone.Wait(600);
                parent.WaitForExit(15000);
                bool fired = gone.Wait(3000);
                return (watching && !early && fired, $"watching={watching} fired-early={early} fired-after-exit={fired}");
            });

            Check("launcher: a launcher already gone stops the server at once; no launcher means no watch", () =>
            {
                int calls = 0;
                bool goneWatch = KmhParentWatch.Start((int.MaxValue - 7).ToString(), () => Interlocked.Increment(ref calls), 100);
                int afterGone = Volatile.Read(ref calls);
                bool none = KmhParentWatch.Start("", () => Interlocked.Increment(ref calls));
                bool junk = KmhParentWatch.Start("not-a-pid", () => Interlocked.Increment(ref calls));
                bool ok = goneWatch && afterGone == 1 && !none && !junk && Volatile.Read(ref calls) == 1;
                return (ok, $"gone={goneWatch}/{afterGone} empty={none} junk={junk} calls={calls}");
            });

            Check("launcher: the re-run server watches its launcher, and the launcher passes a stop on", () =>
            {
                List<string> child = KmhIlReader.CallsMadeBy("KMHServerAddon.Bootstrap", "Main");
                List<string> parent = KmhIlReader.CallsMadeBy("KMHServerAddon.Bootstrap", "ReExecWithR2RDisabled");
                bool watches = child.Contains("Start") && child.Contains("LauncherGone");
                bool forwards = parent.Contains("OnLauncherExit") && parent.Contains("add_ProcessExit");
                var psi = new ProcessStartInfo("kmh-never-started");
                KmhParentWatch.MarkChild(psi);
                bool named = parent.Contains("MarkChild")
                          && psi.Environment[KmhParentWatch.ParentVar] == Environment.ProcessId.ToString();
                return (watches && forwards && named, $"child watches={watches} launcher forwards={forwards} launcher named={named}");
            });
        }

        private static void Integrity(Action<string, Func<(bool, string)>> Check)
        {
            Check("integrity: a file this release introduced is not reported missing on data an older build wrote", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "kmh-since-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                try
                {
                    var files = new List<KmhDataPaths.DataFile>
                    {
                        new KmhDataPaths.DataFile("Old store", Path.Combine(dir, "old.json"), false),
                        new KmhDataPaths.DataFile("New store", Path.Combine(dir, "new.json"), false, sinceSchema: 5),
                    };
                    KmhDataIntegrity.ScanResult before = KmhDataIntegrity.Scan(files, 4);
                    KmhDataIntegrity.ScanResult after = KmhDataIntegrity.Scan(files, 5);
                    bool newIsNormal = before.Files.Find(f => f.Label == "New store")?.AbsentIsNormal == true;
                    bool ok = before.MissingRequired == 1 && before.MissingOptional == 1 && newIsNormal
                           && after.MissingRequired == 2;
                    return (ok, $"older data: required={before.MissingRequired} optional={before.MissingOptional} | "
                              + $"this release's data: required={after.MissingRequired}");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            Check("integrity: the files new since v1.3.0 say so, so an upgrade's first scan does not report them", () =>
            {
                var expected = new Dictionary<string, int>
                {
                    ["Identity/GlobalSetup"] = 4, ["Connected treasury"] = 4, ["Federated guilds"] = 4, ["Federation outbox"] = 5,
                };
                var wrong = new List<string>();
                foreach (KmhDataPaths.DataFile f in KmhDataPaths.KnownDataFiles)
                    if (expected.TryGetValue(f.Label, out int since) && f.SinceSchema != since) wrong.Add($"{f.Label}={f.SinceSchema}");
                int found = 0;
                foreach (KmhDataPaths.DataFile f in KmhDataPaths.KnownDataFiles) if (expected.ContainsKey(f.Label)) found++;
                return (found == expected.Count && wrong.Count == 0, wrong.Count == 0 ? $"{found} marked" : string.Join(", ", wrong));
            });

            Check("integrity: the boot scan judges them by the schema the data was written under", () =>
            {
                string id = KmhServerIdentity.Id;
                int had = KmhDataMeta.AppliedConfigSchema;
                try
                {
                    bool Normal() => KmhDataIntegrity.Scan().Files.Find(f => f.Label == "Federation outbox")?.AbsentIsNormal == true;
                    KmhDataMeta.ResetIdentityForTest(id, 4);
                    bool older = Normal();
                    KmhDataMeta.ResetIdentityForTest(id, 5);
                    bool current = Normal();
                    return (older && !current, $"written under 4: absence normal={older} | under 5: absence normal={current}");
                }
                finally { KmhDataMeta.ResetIdentityForTest(id, had); }
            });
        }

        private static Dictionary<string, byte[]> Snapshot(params string[] paths)
        {
            var saved = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths) saved[p] = File.Exists(p) ? File.ReadAllBytes(p) : null;
            return saved;
        }

        private static void Restore(Dictionary<string, byte[]> saved)
        {
            foreach (var kv in saved)
            {
                if (kv.Value != null) File.WriteAllBytes(kv.Key, kv.Value);
                else if (File.Exists(kv.Key)) File.Delete(kv.Key);
            }
        }

        private static void Identity(Action<string, Func<(bool, string)>> Check)
        {
            Check("identity: a new server's id is the start of its own signing key's fingerprint", () =>
            {
                bool vector = KmhServerIdentity.DeriveId(VectorKey) == VectorId;
                bool empty = KmhServerIdentity.DeriveId("") == "";
                string minted = KmhDataMeta.NewInstanceId();
                bool fromKey = minted == KmhServerIdentity.DeriveId(KmhServerKey.PublicKey) && minted.Length == 32;
                return (vector && empty && fromKey, $"vector={vector} empty={empty} minted from this server's key={fromKey}");
            });

            Check("Global unlink: the id, key, sequence and generation are kept, so setup links the same identity", () =>
            {
                Dictionary<string, byte[]> saved = Snapshot(KmhDataPaths.GlobalConfigFile, KmhDataPaths.GlobalSetupFile);
                string hadId = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest(ServerHex);
                GlobalConfig hadCfg = GlobalConfig.ApplyForTest(new GlobalConfig
                {
                    Enabled = true, BaseUrl = "https://global.test.invalid", RegisteredServerId = ServerHex,
                    LastSequence = 77, SigningGeneration = 3,
                });
                GlobalSetupState hadState = GlobalSetupState.ApplyForTest(new GlobalSetupState { Current = SetupStage.Registered });
                try
                {
                    string keyBefore = KmhServerKey.Fingerprint;
                    var said = new List<string>();
                    AdminCommands.KmhServerCommands.Dispatch(new[] { "global", "unlink", "confirm" }, true, "console", said.Add, fromConsole: true);
                    GlobalConfig cfg = GlobalConfig.Current;
                    string text = string.Join(" ", said);
                    bool keyKept = KmhServerKey.Exists && KmhServerKey.Fingerprint == keyBefore && keyBefore.Length > 0;
                    bool countersKept = cfg.LastSequence == 77 && cfg.SigningGeneration == 3;
                    bool forgotten = cfg.RegisteredServerId == "" && !cfg.Enabled && !GlobalSetupState.Current_.IsRegistered;
                    bool honest = text.Contains("same identity") && !text.Contains("NEW identity");
                    bool ok = keyKept && countersKept && forgotten && honest;
                    return (ok, $"key kept={keyKept} sequence/generation kept={countersKept} forgotten={forgotten} honest={honest}");
                }
                finally
                {
                    KmhGlobalService.Stop();
                    KmhDataMeta.SetInstanceIdForTest(hadId);
                    GlobalConfig.ApplyForTest(hadCfg); GlobalSetupState.ApplyForTest(hadState);
                    Restore(saved);
                }
            });

            Check("Global: a revoked registration closes Federation here, and says why", () =>
            {
                Dictionary<string, byte[]> saved = Snapshot(KmhDataPaths.GlobalConfigFile, KmhDataPaths.GlobalSetupFile);
                string hadId = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest(ServerHex);
                GlobalConfig hadCfg = GlobalConfig.ApplyForTest(new GlobalConfig
                {
                    Enabled = true, BaseUrl = "https://global.test.invalid", RegisteredServerId = ServerHex,
                });
                GlobalSetupState hadState = GlobalSetupState.ApplyForTest(new GlobalSetupState { Current = SetupStage.Registered });
                FeaturesConfig f = FeaturesConfig.Current;
                bool hadFed = f.GuildFederation, hadGuilds = f.Guilds;
                f.GuildFederation = true; f.Guilds = true;
                try
                {
                    KmhGlobalRegistrar.Reset();
                    string before = Features.Global.Federation.FederationService.UnavailableReason();
                    KmhGlobalRegistrar.NoteFailure(new GlobalCallResult { Ok = false, Status = 403, ErrorCode = "KMH-SRV-319" });
                    string after = Features.Global.Federation.FederationService.UnavailableReason();
                    return (before != "global.revoked" && after == "global.revoked", $"before='{before}' after='{after}'");
                }
                finally
                {
                    KmhGlobalRegistrar.ClearRevokedForTest();
                    KmhGlobalRegistrar.Reset();
                    KmhGlobalService.Stop();
                    f.GuildFederation = hadFed; f.Guilds = hadGuilds;
                    KmhDataMeta.SetInstanceIdForTest(hadId);
                    GlobalConfig.ApplyForTest(hadCfg); GlobalSetupState.ApplyForTest(hadState);
                    Restore(saved);
                }
            });

            Check("Global: a private identity Global no longer knows registers itself again; nothing else does", () =>
            {
                Dictionary<string, byte[]> saved = Snapshot(KmhDataPaths.GlobalConfigFile, KmhDataPaths.GlobalSetupFile);
                string hadId = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest(ServerHex);
                GlobalConfig hadCfg = GlobalConfig.ApplyForTest(new GlobalConfig
                {
                    Enabled = false, BackendIdentity = true, BaseUrl = "https://global.test.invalid", RegisteredServerId = ServerHex,
                });
                GlobalSetupState hadState = GlobalSetupState.ApplyForTest(new GlobalSetupState { BackendRegistered = true });
                var calls = new List<string>();
                KmhGlobalPost.SenderForTest = (path, body, ct) =>
                {
                    lock (calls) calls.Add(path);
                    var res = path.EndsWith("/challenge") ? new JObject { ["challenge_id"] = "ch-1" } : new JObject { ["owned"] = false };
                    return Task.FromResult(new GlobalCallResult { Ok = true, Status = 200, Body = res });
                };
                GlobalCallResult Refusal(string code) => new GlobalCallResult { Ok = false, Status = code == "KMH-SRV-301" ? 404 : 500, ErrorCode = code };
                try
                {
                    KmhGlobalService.SetBackendModeForTest(false);
                    KmhGlobalService.OnBeatRefused(Refusal("KMH-SRV-301"));
                    bool ownerLeft = GlobalSetupState.Current_.BackendRegistered && calls.Count == 0;

                    KmhGlobalService.SetBackendModeForTest(true);
                    KmhGlobalService.OnBeatRefused(Refusal("KMH-SRV-500"));
                    bool otherLeft = GlobalSetupState.Current_.BackendRegistered && calls.Count == 0;

                    KmhGlobalService.OnBeatRefused(Refusal("KMH-SRV-301"));
                    bool cleared = !GlobalSetupState.Current_.BackendRegistered;
                    DateTime until = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < until && !GlobalSetupState.Current_.BackendRegistered) Thread.Sleep(50);
                    bool again;
                    lock (calls) again = GlobalSetupState.Current_.BackendRegistered && calls.Contains(KmhGlobalBackend.ChallengePath)
                                         && calls.Contains(KmhGlobalBackend.RegisterPath);
                    bool ok = ownerLeft && otherLeft && cleared && again;
                    return (ok, $"owner mode untouched={ownerLeft} other refusal untouched={otherLeft} cleared={cleared} registered again={again}");
                }
                finally
                {
                    // Registration flips BackendRegistered before saving and restarting; wait it out before restoring.
                    KmhGlobalBackend.StopRegistration();
                    bool settled = KmhGlobalBackend.AwaitRegistrationForTest(TimeSpan.FromSeconds(15));
                    if (!settled) Diagnostics.ServerLog.Warn("selftest: the background registration did not finish within 15 s");
                    KmhGlobalService.Stop();
                    KmhGlobalService.SetBackendModeForTest(false);
                    KmhGlobalPost.SenderForTest = null;
                    KmhGlobalRegistrar.Reset();
                    KmhDataMeta.SetInstanceIdForTest(hadId);
                    GlobalConfig.ApplyForTest(hadCfg); GlobalSetupState.ApplyForTest(hadState);
                    Restore(saved);
                }
            });
        }
    }
}
