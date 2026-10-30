using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global;

namespace KMHServerAddon.Maintenance
{
    // Console output is the widest thing the server exposes, so its off-switch and bounding ring are proven here.
    internal static class KmhOperatorConsoleSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Console tail: a reader that has seen up to N gets only what came after", () =>
            {
                KmhConsoleTail.ResetForTest();
                for (int i = 1; i <= 10; i++) KmhConsoleTail.Append("line " + i);
                List<KmhConsoleLine> after = KmhConsoleTail.Since(7, 100);
                bool ok = after.Count == 3 && after[0].Sequence == 8 && after[2].Text == "line 10";
                return (ok, ok ? "3 lines after sequence 7" : $"{after.Count} line(s)");
            });

            Check("Console tail: the ring drops the oldest rather than growing", () =>
            {
                KmhConsoleTail.ResetForTest();
                for (int i = 1; i <= KmhConsoleTail.Capacity + 50; i++) KmhConsoleTail.Append("line " + i);
                List<KmhConsoleLine> all = KmhConsoleTail.Since(0, 100000);
                bool ok = all.Count == KmhConsoleTail.Capacity
                          && KmhConsoleTail.OldestSequence == 51
                          && KmhConsoleTail.NewestSequence == KmhConsoleTail.Capacity + 50;
                return (ok, ok ? $"held {all.Count}, oldest {KmhConsoleTail.OldestSequence}"
                              : $"{all.Count} line(s), oldest {KmhConsoleTail.OldestSequence}");
            });

            // A reader whose cursor is past the window would otherwise be told "nothing new" for ever.
            Check("Console tail: a cursor ahead of the window reports zero, not a negative page", () =>
            {
                KmhConsoleTail.ResetForTest();
                KmhConsoleTail.Append("only line");
                bool ok = KmhConsoleTail.Since(999, 100).Count == 0;
                return (ok, ok ? "no lines claimed" : "reported lines it does not have");
            });

            Check("Console push: the batch is capped in bytes and cut on a line boundary", () =>
            {
                var lines = new List<KmhConsoleLine>();
                for (int i = 1; i <= 400; i++) lines.Add(new KmhConsoleLine(i, new string('x', 300)));
                string packed = KmhOperatorConsole.Pack(lines);
                bool ok = KmhLogText.Utf8Bytes(packed) <= KmhOperatorConsole.MaxLineBytes
                          && packed.EndsWith("\n", StringComparison.Ordinal);
                return (ok, ok ? $"{KmhLogText.Utf8Bytes(packed)} bytes, whole lines"
                              : $"{KmhLogText.Utf8Bytes(packed)} bytes");
            });

            // A newline would forge a second line with an unissued sequence; the far side splits on the FIRST tab.
            Check("Console push: an embedded newline cannot forge a second line", () =>
            {
                string packed = KmhOperatorConsole.Pack(new List<KmhConsoleLine>
                                                        { new KmhConsoleLine(4, "before\r\n9\tafter") });
                string[] parts = packed.Split('\n');
                bool ok = parts.Length == 2 && parts[1].Length == 0
                          && parts[0].StartsWith("4\t", StringComparison.Ordinal)
                          && parts[0].IndexOf('\n') < 0 && parts[0].IndexOf('\r') < 0;
                return (ok, ok ? "one line, and its sequence is the one this server issued"
                              : $"packed as {parts.Length} line(s)");
            });

            Check("Console push: a player's address never leaves the machine, as the disclosure promises", () =>
            {
                string packed = KmhOperatorConsole.Pack(new List<KmhConsoleLine>
                {
                    new KmhConsoleLine(7, "[warn] Ban: refused 'someone' from 203.0.113.7 - banned"),
                    new KmhConsoleLine(8, "[protocol] API: refused [2001:db8::42]:20013 - throttled"),
                });
                bool ok = !packed.Contains("203.0.113.7") && !packed.Contains("2001:db8::42")
                          && packed.Contains("refused 'someone' from " + KmhRedact.AddressMask);
                return (ok, ok ? "both addresses masked, the rest of each line kept" : packed);
            });

            Check("Console: no session means the normal heartbeat interval", () =>
            {
                long past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
                bool ok = KmhOperatorConsole.NextWaitSecondsOrZero(past) == 0
                          && KmhOperatorConsole.NextWaitSecondsOrZero(0) == 0;
                return (ok, ok ? "the loop keeps its own cadence" : "an expired session still shortened the wait");
            });

            Check("Console: an open session shortens the beat", () =>
            {
                long soon = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600;
                int wait = KmhOperatorConsole.NextWaitSecondsOrZero(soon);
                bool ok = wait == KmhOperatorConsole.PollSeconds;
                return (ok, ok ? $"{wait}s while watched" : $"{wait}s");
            });

            // Without the owner's switch, a directory-set "official" flag would grant console access to their machine.
            Check("Console: it is off on a new server", () =>
            {
                bool ok = !new GlobalConfig().AllowOperatorConsole;
                return (ok, ok ? "opt-in, not opt-out" : "a fresh server would stream its console");
            });

            Check("Console: the disclosure changes when the switch is on", () =>
            {
                GlobalConfig prev = GlobalConfig.ApplyForTest(new GlobalConfig
                {
                    Enabled = true, AllowOperatorConsole = true, RegisteredServerId = "selftest",
                });
                try
                {
                    var lines = new List<string>();
                    KmhGlobalDisclosure.Describe(lines.Add);
                    bool ok = lines.Exists(l => l.IndexOf("AllowOperatorConsole is on (currently ON)",
                                                          StringComparison.Ordinal) >= 0)
                              && lines.Exists(l => l.IndexOf("Console lines CAN contain player names",
                                                             StringComparison.Ordinal) >= 0);
                    return (ok, ok ? "the 'never sent' promise is corrected"
                                  : "the disclosure still claims names and paths never leave");
                }
                finally { GlobalConfig.ApplyForTest(prev); }
            });

            Check("Console: the disclosure keeps the stronger promise while it is off", () =>
            {
                GlobalConfig prev = GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = true });
                try
                {
                    var lines = new List<string>();
                    KmhGlobalDisclosure.Describe(lines.Add);
                    bool ok = lines.Exists(l => l.IndexOf("player names, save files", StringComparison.Ordinal) >= 0);
                    return (ok, ok ? "unchanged for a server that never opted in" : "the promise was weakened for everyone");
                }
                finally { GlobalConfig.ApplyForTest(prev); }
            });

            Check("Console: the status payload is parseable and answers what an operator asks first", () =>
            {
                string json = KmhOperatorConsole.Status();
                Newtonsoft.Json.Linq.JObject o = Newtonsoft.Json.Linq.JObject.Parse(json);
                string[] need = { "server_id", "build", "protocol", "uptime_seconds", "readiness",
                                  "persistence_degraded", "config_schema", "verified_clients", "players",
                                  "log_file", "log_queue_depth", "working_set_mb" };
                var missing = new List<string>();
                foreach (string k in need) if (o[k] == null) missing.Add(k);
                return (missing.Count == 0,
                        missing.Count == 0 ? $"{o.Count} field(s), {json.Length} chars"
                                           : "missing: " + string.Join(", ", missing));
            });

            // Every ServerLog line reaches the tail unfiltered and the tail leaves the machine, so secrets are masked here.
            // One canary per credential KMH handles: a single passing case proves only one shape.
            Check("Console push: every shape of credential is masked before it leaves the machine", () =>
            {
                // Registered the way the real code paths register them, so this measures the wiring, not the regex.
                const string linkCode = "K7Q4-9WTX";
                const string grant    = "kmhg_7f3c19a4b2e85d60c1a9f4e2";
                const string wrapKey  = "Uk1IR2xvYmFsV3JhcEtleUV4YW1wbGUxMjM0NTY3OD0=";
                KmhRedact.RegisterSecret(linkCode);
                KmhRedact.RegisterSecret(grant);
                KmhRedact.RegisterSecret(wrapKey);
                try
                {
                    var canaries = new (string Name, string Line, string Secret)[]
                    {
                        ("owner link code",   $"enter it yourself: {linkCode})", linkCode),
                        ("registration grant", $"grant issued: {grant}", grant),
                        ("directory wrap key", $"Global:DirectoryKeyWrapKey={wrapKey}", wrapKey),
                        ("player bearer",     "player token=kmhp_ab12cd34ef56gh78ij90kl", "kmhp_ab12cd34ef56gh78ij90kl"),
                        ("Authorization header", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload",
                                                 "eyJhbGciOiJIUzI1NiJ9.payload"),
                        ("Discord bot token",  "bot ready MTIzNDU2Nzg5MDEyMzQ1Njc4.Gh3Kx9.abcdefghijklmnopqrstuvwxyz01",
                                               "MTIzNDU2Nzg5MDEyMzQ1Njc4.Gh3Kx9.abcdefghijklmnopqrstuvwxyz01"),
                        ("server signing key", "privateKey=MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg",
                                               "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg"),
                        ("url query token",   "GET /api/v1/chat/global/stream?token=abcdef0123456789abcdef",
                                              "abcdef0123456789abcdef"),
                        ("json secret field", "body {\"client_secret\":\"s3cr3t-value-goes-here\"}",
                                              "s3cr3t-value-goes-here"),
                    };

                    var leaked = new List<string>();
                    var lines = new List<KmhConsoleLine>();
                    for (int i = 0; i < canaries.Length; i++) lines.Add(new KmhConsoleLine(i + 1, canaries[i].Line));
                    lines.Add(new KmhConsoleLine(canaries.Length + 1, "world: 4 events expired"));

                    string packed = KmhOperatorConsole.Pack(lines);
                    foreach (var c in canaries)
                        if (packed.Contains(c.Secret, StringComparison.Ordinal)) leaked.Add(c.Name);

                    bool ordinaryKept = packed.Contains("world: 4 events expired");
                    return (leaked.Count == 0 && ordinaryKept,
                            leaked.Count > 0 ? "WENT OUT IN CLEAR: " + string.Join(", ", leaked)
                            : ordinaryKept ? $"{canaries.Length} canaries masked, ordinary lines untouched"
                                           : "redaction ate an ordinary line");
                }
                finally { KmhRedact.ClearSecrets(); }
            });

            // A purpose-built DTO, not the owner's status export, which carries the data folder, backups and leaderboard.
            Check("Console: the remote status carries no paths, backups or player names", () =>
            {
                Newtonsoft.Json.Linq.JObject o =
                    Newtonsoft.Json.Linq.JObject.Parse(KmhOperatorConsole.Status());

                string[] banned = { "DataFolder", "data_folder", "Backups", "backups", "Leaders", "leaders" };
                var present = new List<string>();
                foreach (string k in banned) if (o[k] != null) present.Add(k);

                // The log file is named, never located: the absolute path carries the account the server runs as.
                string log = (string)o["log_file"] ?? "";
                bool pathless = log.IndexOf('\\') < 0 && log.IndexOf('/') < 0 && log.IndexOf(':') < 0;

                bool useful = o["readiness"] != null && o["persistence_degraded"] != null
                           && o["verified_clients"] != null && o["log_queue_depth"] != null;

                return (present.Count == 0 && pathless && useful,
                        present.Count > 0 ? "REMOTE VIEW EXPOSES: " + string.Join(", ", present)
                        : !pathless ? $"log_file is a path: {log}"
                        : useful ? $"{o.Count} field(s), none of them a path or a person"
                                 : "the operator lost a field they need");
            });

            return r;
        }
    }
}
