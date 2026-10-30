using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Reports
{
    // Its own folder and a counting transport: a run never touches an owner's reports or the network.
    internal static class KmhReportSelfTest
    {
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01 };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            string dir = Path.Combine(Path.GetTempPath(), "kmh-reports-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ReportStore.DirForTest = dir;
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                ReportHandler.ResetForTest();
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.GetType().Name}: {ex.Message}")); }
            }

            try
            {
                Check("Reports: a report is stored with the player's words, the server's diagnostics and the screenshot", () =>
                {
                    string id = NewId();
                    ReportResultWire res = ReportHandler.Accept(Client("tester"), "tester", Submit(id, shot: Jpeg), DateTimeOffset.UtcNow);
                    StoredReport s = ReportStore.Load(id);
                    JObject diag = s == null ? null : JObject.Parse(s.ServerDiagnostics);
                    bool sections = diag != null && new[] { "server", "global", "transport", "runtime", "reporter", "console" }
                                                    .All(k => diag[k] != null);
                    bool shot = s != null && (ReportStore.Screenshot(s) ?? Array.Empty<byte>()).SequenceEqual(Jpeg);
                    bool ok = res.Ok && res.Forwarding == "local" && s != null && s.Title == "Market freezes"
                              && s.Body == "line one\nline two" && sections && shot && s.Reporter == "tester"
                              && s.ForwardState == ReportForwardStates.Off;
                    return (ok, $"ok={res.Ok} forwarding={res.Forwarding} title='{s?.Title}' sections={sections} screenshot={shot} state={s?.ForwardState}");
                });

                Check("Reports: a resend is the same report, and another player cannot take its id", () =>
                {
                    string id = NewId();
                    ReportResultWire first  = ReportHandler.Accept(Client("tester"), "tester", Submit(id), DateTimeOffset.UtcNow);
                    ReportResultWire again  = ReportHandler.Accept(Client("tester"), "tester", Submit(id, title: "changed"), DateTimeOffset.UtcNow);
                    ReportResultWire other  = ReportHandler.Accept(Client("someone"), "someone", Submit(id), DateTimeOffset.UtcNow);
                    StoredReport s = ReportStore.Load(id);
                    bool ok = first.Ok && again.Ok && !other.Ok && other.Reason == ReportHandler.ReasonInvalid && s?.Title == "Market freezes";
                    return (ok, $"first={first.Ok} resend={again.Ok} (kept '{s?.Title}') other player={other.Ok}/{other.Reason}");
                });

                Check("Reports: a kind and a title are required, and a bad screenshot is dropped rather than the report", () =>
                {
                    ReportResultWire noKind  = ReportHandler.Accept(Client("tester"), "tester", Submit(NewId(), kind: "rant"), DateTimeOffset.UtcNow);
                    ReportResultWire noTitle = ReportHandler.Accept(Client("tester"), "tester", Submit(NewId(), title: " \u0001 "), DateTimeOffset.UtcNow);
                    ReportResultWire badId   = ReportHandler.Accept(Client("tester"), "tester", Submit("../escape"), DateTimeOffset.UtcNow);
                    ReportHandler.ResetForTest();
                    string id = NewId();
                    ReportResultWire junk = ReportHandler.Accept(Client("tester"), "tester", Submit(id, shot: new byte[] { 1, 2, 3, 4 }), DateTimeOffset.UtcNow);
                    StoredReport s = ReportStore.Load(id);
                    bool ok = !noKind.Ok && !noTitle.Ok && !badId.Ok && junk.Ok && s != null && string.IsNullOrEmpty(s.ScreenshotFile)
                              && !File.Exists(Path.Combine(ReportStore.DirForTest, "..", "escape.json"));
                    return (ok, $"no kind={noKind.Ok} no title={noTitle.Ok} bad id={badId.Ok} junk screenshot kept report={junk.Ok}, file='{s?.ScreenshotFile}'");
                });

                Check("Reports: a player gets a short burst and a day's worth, then is told to wait", () =>
                {
                    DateTimeOffset t = DateTimeOffset.UtcNow;
                    int accepted = 0;
                    for (int i = 0; i < ReportLimits.BurstPerWindow; i++)
                        if (ReportHandler.Accept(Client("burst"), "burst", Submit(NewId()), t).Ok) accepted++;
                    ReportResultWire fourth = ReportHandler.Accept(Client("burst"), "burst", Submit(NewId()), t);

                    // Spread over the day the burst allows, the daily count still ends it.
                    int day = accepted;
                    for (int w = 1; day < ReportLimits.PerDay + 1 && w < 20; w++)
                        for (int i = 0; i < ReportLimits.BurstPerWindow && day < ReportLimits.PerDay + 1; i++)
                        {
                            ReportResultWire x = ReportHandler.Accept(Client("burst"), "burst", Submit(NewId()),
                                                                      t.AddSeconds(w * (ReportLimits.BurstWindowSec + 1)));
                            if (x.Ok) day++; else { w = 99; break; }
                        }
                    bool ok = accepted == ReportLimits.BurstPerWindow && !fourth.Ok && fourth.Reason == ReportHandler.ReasonRateLimited
                              && day == ReportLimits.PerDay;
                    return (ok, $"burst {accepted}/{ReportLimits.BurstPerWindow} then {fourth.Reason}; {day} over the day (limit {ReportLimits.PerDay})");
                });

                Check("Reports: retention removes reports past ninety days and keeps a fresh one", () =>
                {
                    string old = NewId(), fresh = NewId();
                    ReportStore.Add(new StoredReport { ReportId = old, Kind = ReportKinds.Bug, Title = "old", ReceivedAtUnix = 1 }, null);
                    ReportStore.Add(new StoredReport { ReportId = fresh, Kind = ReportKinds.Bug, Title = "fresh" }, null);
                    // Aged after both exist, so the prune the second Add ran cannot have taken it already.
                    File.SetLastWriteTimeUtc(Path.Combine(ReportStore.DirForTest, old + ".json"), DateTime.UtcNow.AddDays(-100));
                    int gone = ReportStore.Prune(DateTimeOffset.UtcNow);
                    bool ok = gone == 1 && ReportStore.Load(old) == null && ReportStore.Load(fresh) != null;
                    return (ok, $"pruned {gone}; old present={ReportStore.Load(old) != null}; fresh present={ReportStore.Load(fresh) != null}");
                });

                Check("Reports: the copy for Global keeps warnings, errors and the reporter's lines, and every line is redacted", () =>
                {
                    const string secret = "selftest-not-a-real-secret-7f3a9c";
                    Maintenance.KmhRedact.RegisterSecret(secret);
                    string stamp = DateTime.UtcNow.ToString("O");
                    KmhConsoleTail.Append($"[{stamp}] [info] Bob said something private");
                    KmhConsoleTail.Append($"[{stamp}] [warn] Persistence: a store is slow");
                    KmhConsoleTail.Append($"[{stamp}] [info] reporter-one joined the server");
                    KmhConsoleTail.Append($"[{stamp}] [error] leaked {secret} in a message");
                    var (full, forward) = ReportDiagnostics.Build(Client("reporter-one"), "reporter-one", DateTimeOffset.UtcNow);
                    bool fullHasBob = full.Contains("Bob said something private");
                    bool fwdHasBob = forward.Contains("Bob said something private");
                    bool fwdHasWarn = forward.Contains("a store is slow") && forward.Contains("reporter-one joined");
                    bool redacted = !full.Contains(secret) && !forward.Contains(secret) && forward.Contains("leaked");
                    bool ok = fullHasBob && !fwdHasBob && fwdHasWarn && redacted;
                    return (ok, $"local copy has other players' lines: {fullHasBob}; Global copy: {fwdHasBob}; warnings and reporter kept: {fwdHasWarn}; secret redacted: {redacted}");
                });

                Check("Reports: a player's address in a forwarded console line reaches Global masked, and stays in the local copy", () =>
                {
                    string stamp = DateTime.UtcNow.ToString("O");
                    KmhConsoleTail.Append($"[{stamp}] [warn] Ban: refused 'someone' from 203.0.113.7 - banned");
                    KmhConsoleTail.Append($"[{stamp}] [error] peer [2001:db8::7]:5000 dropped by reporter-two");
                    var (full, forward) = ReportDiagnostics.Build(Client("reporter-two"), "reporter-two", DateTimeOffset.UtcNow);
                    bool local = full.Contains("203.0.113.7") && full.Contains("2001:db8::7");
                    bool masked = !forward.Contains("203.0.113.7") && !forward.Contains("2001:db8::7")
                                  && forward.Contains("refused 'someone' from " + Maintenance.KmhRedact.AddressMask) && forward.Contains("dropped by reporter-two");
                    bool stampKept = forward.Contains(stamp);
                    return (local && masked && stampKept, $"local copy keeps them: {local}; Global copy masked: {masked}; timestamps intact: {stampKept}");
                });

                Check("Reports: a reporter named like a word pulls in others' lines, but Global's copy names no other player", () =>
                {
                    var accounts = new Dictionary<string, Features.Identity.StaffRegistry.RwtAccount>(StringComparer.Ordinal)
                    {
                        ["Server"] = default, ["Bob"] = default, ["Bob Lee"] = default, ["Ann"] = default,
                    };
                    Features.Identity.StaffRegistry.AccountsForTest(accounts);
                    try
                    {
                        string stamp = DateTime.UtcNow.ToString("O");
                        KmhConsoleTail.Append($"[{stamp}] [info] Server: Bob paid ann 40 silver for Bob Lee's steel");
                        var (full, forward) = ReportDiagnostics.Build(Client("Server"), "Server", DateTimeOffset.UtcNow);
                        bool local = full.Contains("Server: Bob paid ann 40 silver for Bob Lee's steel");
                        bool hidden = forward.Contains("Server: [player] paid [player] 40 silver for [player]'s steel")
                                      && !forward.Contains("Server: Bob paid") && !forward.Contains("paid ann");
                        string own = ReportDiagnostics.OthersHidden("Bob Lee filed it; Bob saw", "Bob Lee", accounts.Keys);
                        bool ownKept = own == "Bob Lee filed it; [player] saw";
                        return (local && hidden && ownKept, $"local copy names them: {local}; Global copy hides them: {hidden}; own name kept: {own}");
                    }
                    finally { Features.Identity.StaffRegistry.AccountsForTest(null); }
                });

                Check("Reports: the signed body hashes to the vector Global pins", () =>
                {
                    ReportBody v = new ReportBody
                    {
                        ServerId = "0123456789abcdef0123456789abcdef", Generation = 2, IssuedAtUnix = 1_757_000_000,
                        Nonce = "ffffffffffffffffffffffffffffffff", ReportId = "rpt-0001_x", Kind = "bug",
                        Title = "Crash: on join – Ünïcode", Body = "line one\nline two", Reporter = "Tëster",
                        ReporterGlobalId = "fedcba9876543210fedcba9876543210", ClientVersion = "1.3.1 rel-270",
                        CreatedAtUnix = 1_756_999_990, ClientDiagnostics = "{\"summary\":{\"fps\":60}}",
                        ServerDiagnostics = "{\"server\":{\"build\":\"1.3.1\"}}", Screenshot = "/9j/4AAQSkZJRg==",
                    };
                    string got = BitConverter.ToString(SHA256.HashData(v.Canonical())).Replace("-", "").ToLowerInvariant();
                    const string pinned = "96a7c4926a249bc3a69801f94616a35ad59781edb33d0bfc3fbe85ce28ed8c15";
                    return (got == pinned, got == pinned ? "matches Global's CanonicalVectorTests" : $"got {got}, Global pins {pinned}");
                });

                Check("Reports: forwarding is off when the owner says so, held back by test isolation, and signed when sent", () =>
                {
                    using Rig rig = new Rig();
                    rig.Config.ForwardReports = false;
                    bool offRefused = !ReportForwarder.CanForward(out string offWhy) && offWhy.Contains("ForwardReports");

                    // Production, as the acceptance server is registered: the report waits and nothing leaves.
                    rig.Config.ForwardReports = true;
                    bool allowed = ReportForwarder.CanForward(out string why);
                    string id = NewId();
                    ReportResultWire res = ReportHandler.Accept(Client("tester"), "tester", Submit(id, shot: Jpeg), DateTimeOffset.UtcNow);
                    // Accept nudged a pass of its own; on a busy run it lands after the reset below and backs the report off again.
                    ReportForwarder.WaitIdleForTest(TimeSpan.FromSeconds(5));
                    ReportForwarder.PassAsync(DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
                    StoredReport held = ReportStore.Load(id);
                    bool isolated = rig.Sent == 0 && held?.ForwardState == ReportForwardStates.Pending && held.ForwardAttempts == 1
                                    && (held.LastError ?? "").Contains("self-test isolation");

                    // A local test Global takes it: one request, signed by this server's key, and the report is settled.
                    rig.Config.BaseUrl = "http://127.0.0.1:9";
                    held.NextAttemptUnix = 0;
                    ReportStore.Update(held);
                    rig.Answer = "{\"report_id\":\"g-0001\",\"duplicate\":false}";
                    ReportForwarder.PassAsync(DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
                    StoredReport sent = ReportStore.Load(id);
                    ReportBody body = rig.LastBody == null ? null : JsonConvert.DeserializeObject<ReportBody>(rig.LastBody);
                    bool signed = body != null && KmhGlobalSigning.Verify(KmhServerKey.PublicKey, body.Canonical(), body.Signature)
                                  && body.ReportId == id && body.Screenshot == Convert.ToBase64String(Jpeg)
                                  && rig.LastPath == ReportForwarder.Path;
                    bool settled = sent?.ForwardState == ReportForwardStates.Sent && sent.ForwardedId == "g-0001" && rig.Sent == 1;

                    ReportForwarder.PassAsync(DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
                    bool once = rig.Sent == 1;
                    bool ok = offRefused && allowed && res.Forwarding == "global" && isolated && signed && settled && once;
                    return (ok, $"off refused={offRefused}; allowed={allowed} ({why}); held back in self-test={isolated}; "
                              + $"signed and on the report path={signed}; settled={settled}; sent once={once} "
                              + $"(sends={rig.Sent}, state={sent?.ForwardState}, last error={sent?.LastError})");
                });

                Check("Reports: switched off, the button's kind is refused and the hello says so", () =>
                {
                    FeaturesConfig f = FeaturesConfig.Current;
                    bool was = f.Reports;
                    try
                    {
                        f.Reports = false;
                        string[] needs = FeaturesConfig.FeaturesForKind(SubProtocol.KmhProtocol.Kind.ReportSubmit);
                        bool blocked = f.FirstDisabled(needs) == "reports";
                        bool listed = f.DisabledList().Contains("reports");
                        bool mode = ReportHandler.Mode == "off";
                        return (blocked && listed && mode, $"refused at the router={blocked}; in the disabled list={listed}; hello says off={mode}");
                    }
                    finally { f.Reports = was; }
                });

                // Opt-in, like the console: an owner who never turned it on promised Global no player names.
                Check("Reports: forwarding to Global is off until the owner turns it on", () =>
                {
                    bool offByDefault = !new GlobalConfig().ForwardReports;
                    GlobalConfig prev = GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = true });
                    try
                    {
                        var lines = new List<string>();
                        KmhGlobalDisclosure.Describe(lines.Add);
                        bool promise = lines.Exists(l => l.Contains("player names, save files"));
                        string mode = ReportHandler.Mode;
                        return (offByDefault && promise && mode != "global",
                                $"default off={offByDefault}; 'no player names' kept={promise}; the hello says '{mode}'");
                    }
                    finally { GlobalConfig.ApplyForTest(prev); }
                });

                Check("Reports: maintenance does not hold a report back", () =>
                {
                    bool read = Maintenance.KmhMutationClass.IsRead(SubProtocol.KmhProtocol.Kind.ReportSubmit);
                    return (read, read ? "a report is let through while the economy is frozen" : "maintenance would refuse a report");
                });

                Check("Reports: the disclosure lists what a forwarded report carries and promises nothing it breaks", () =>
                {
                    using Rig rig = new Rig();
                    var on = new List<string>();
                    KmhGlobalDisclosure.Describe(on.Add);
                    string onText = string.Join("\n", on);
                    rig.Config.ForwardReports = false;
                    var off = new List<string>();
                    KmhGlobalDisclosure.Describe(off.Add);
                    string offText = string.Join("\n", off);

                    List<string> fields = KmhGlobalDisclosure.Fields(typeof(ReportBody));
                    bool listsAll = fields.All(onText.Contains);
                    bool noFalsePromise = !onText.Contains("Never sent: player IP addresses, player names");
                    bool offSaysHere = offText.Contains("every report stays on this server") && !offText.Contains("client_diagnostics");
                    return (listsAll && noFalsePromise && offSaysHere,
                            $"{fields.Count} fields listed={listsAll}; 'no player names' withdrawn while on={noFalsePromise}; off keeps them here={offSaysHere}");
                });

                Check("Reports: 'kmh reports' lists, shows by a short id, and deletes", () =>
                {
                    string id = NewId();
                    ReportHandler.Accept(Client("lister"), "lister", Submit(id, title: "Listed one"), DateTimeOffset.UtcNow);
                    List<string> list = Said("reports");
                    List<string> show = Said("reports", "show", id.Substring(0, 8));
                    List<string> del = Said("reports", "delete", id.Substring(0, 8));
                    bool listed = list.Any(l => l.Contains("Listed one") && l.Contains(id.Substring(0, 8)));
                    bool shown = show.Any(l => l.Contains("title: Listed one")) && show.Any(l => l.Contains("| line one"));
                    bool deleted = del.Any(l => l.StartsWith("Deleted report")) && ReportStore.Load(id) == null;
                    return (listed && shown && deleted, $"listed={listed} shown={shown} deleted={deleted}");
                });

                Check("Reports: a report for this server's staff only never reaches Global - not from the handler, the queue or a retry", () =>
                {
                    Fresh(dir, "staff-only");
                    using Rig rig = new Rig();
                    rig.Config.BaseUrl = "http://127.0.0.1:9";
                    string id = NewId();
                    ReportResultWire res = ReportHandler.Accept(Client("tester"), "tester", Submit(id, destinations: new[] { "server" }), DateTimeOffset.UtcNow);
                    StoredReport kept = ReportStore.Load(id);
                    bool handler = res.Ok && res.Forwarding == "local" && res.Developers == "" && kept?.ForwardState == ReportForwardStates.Off
                                   && kept.Destinations == "server";

                    // Queued by hand or by an older build: the queue itself still refuses it.
                    string planted = NewId();
                    ReportStore.Add(new StoredReport { ReportId = planted, Kind = ReportKinds.Bug, Title = "planted", Reporter = "tester",
                                                       Destinations = "server", ForwardState = ReportForwardStates.Pending }, null);
                    ReportForwarder.PassAsync(DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
                    bool queue = rig.Sent == 0 && ReportStore.Load(planted)?.ForwardState == ReportForwardStates.Off;

                    int requeued = ReportForwarder.Requeue();
                    bool retry = requeued == 0 && ReportStore.Load(id)?.ForwardState == ReportForwardStates.Off
                                 && ReportStore.Load(planted)?.ForwardState == ReportForwardStates.Off;
                    ReportForwarder.WaitIdleForTest(TimeSpan.FromSeconds(5));
                    bool ok = handler && queue && retry && rig.Sent == 0;
                    return (ok, $"handler kept it here={handler} ({res.Forwarding}/{res.Developers}); queue refused a planted one={queue}; "
                              + $"'kmh reports retry' left it alone={retry} (requeued {requeued}); requests to Global={rig.Sent}");
                });

                Check("Reports: a report for the developers only is relayed, never shown to staff, and not kept once Global has it", () =>
                {
                    Fresh(dir, "developers-only");
                    using Rig rig = new Rig();
                    rig.Answer = "{\"report_id\":\"g-0002\",\"duplicate\":false}";
                    string id = NewId();
                    ReportDiscord.LastAskedId = "";
                    // Accepted while test isolation holds Global back, so the pass its Nudge starts sends nothing.
                    ReportResultWire res = ReportHandler.Accept(Client("tester"), "tester",
                        Submit(id, title: "Only for the developers", destinations: new[] { "developers" }), DateTimeOffset.UtcNow);
                    ReportForwarder.WaitIdleForTest(TimeSpan.FromSeconds(5));
                    StoredReport held = ReportStore.Load(id);
                    bool relayed = res.Ok && res.Developers == ReportDevelopers.Forwarding && held?.ForwardState == ReportForwardStates.Pending
                                   && held.StaffVisible == false;
                    bool hidden = !Said("reports", "list", "all").Any(l => l.Contains("Only for the developers"))
                                  && Said("reports", "show", id.Substring(0, 8)).Any(l => l.StartsWith("No report starts with"))
                                  && ReportDiscord.LastAskedId != id;
                    var cfg = new Discord.DiscordConfig { Enabled = true };
                    cfg.Bot.Token = "selftest-token";
                    cfg.Channels.Admin = "123456789012345678";
                    bool staffPost = ReportDiscord.ShouldPost(new StoredReport { Destinations = "server" }, cfg);
                    bool devPost = ReportDiscord.ShouldPost(held, cfg);
                    cfg.Events.Reports = false;
                    bool switchedOff = !ReportDiscord.ShouldPost(new StoredReport { Destinations = "server" }, cfg);

                    rig.Config.BaseUrl = "http://127.0.0.1:9";
                    held.NextAttemptUnix = 0;
                    ReportStore.Update(held);
                    ReportForwarder.PassAsync(DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
                    bool sentThenGone = rig.Sent == 1 && ReportStore.Load(id) == null;

                    // A server that cannot forward keeps nothing it may not show.
                    rig.Config.ForwardReports = false;
                    string other = NewId();
                    ReportResultWire refused = ReportHandler.Accept(Client("tester"), "tester", Submit(other, destinations: new[] { "developers" }), DateTimeOffset.UtcNow);
                    bool notKept = refused.Ok && refused.Developers == ReportDevelopers.NotForwarded && ReportStore.Load(other) == null;
                    bool ok = relayed && hidden && staffPost && !devPost && switchedOff && sentThenGone && notKept;
                    return (ok, $"relayed={relayed}; hidden from list, show and Discord={hidden}; Discord would post a staff one={staffPost}, "
                              + $"this one={devPost}, with Events.Reports off={!switchedOff}; sent then removed={sentThenGone}; not kept when it cannot go={notKept}");
                });

                Check("Reports: a client that names no destinations is handled exactly as before", () =>
                {
                    Fresh(dir, "legacy");
                    using Rig rig = new Rig();
                    string on = NewId();
                    ReportResultWire forwarding = ReportHandler.Accept(Client("legacy"), "legacy", Submit(on, title: "Legacy one"), DateTimeOffset.UtcNow);
                    ReportForwarder.WaitIdleForTest(TimeSpan.FromSeconds(5));
                    StoredReport a = ReportStore.Load(on);
                    ReportResultWire again = ReportHandler.Accept(Client("legacy"), "legacy", Submit(on, title: "Legacy one"), DateTimeOffset.UtcNow);
                    rig.Config.ForwardReports = false;
                    string off = NewId();
                    ReportResultWire local = ReportHandler.Accept(Client("legacy"), "legacy", Submit(off, title: "Legacy two"), DateTimeOffset.UtcNow);
                    StoredReport b = ReportStore.Load(off);
                    List<string> list = Said("reports", "list", "all");
                    bool ok = forwarding.Ok && forwarding.Forwarding == "global" && a?.ForwardState == ReportForwardStates.Pending
                              && a.Destinations == "" && a.StaffVisible && a.WantsDevelopers
                              && again.Ok && again.Forwarding == "global"
                              && local.Ok && local.Forwarding == "local" && b?.ForwardState == ReportForwardStates.Off && b.StaffVisible
                              && list.Any(l => l.Contains("Legacy one")) && list.Any(l => l.Contains("Legacy two"));
                    return (ok, $"forwarding on: {forwarding.Forwarding}/{a?.ForwardState}, resend {again.Forwarding}; off: {local.Forwarding}/{b?.ForwardState}; both listed");
                });

                Check("Reports: a kind it does not know is refused, and an unknown area, severity or frequency falls back", () =>
                {
                    Fresh(dir, "fields");
                    string q = NewId(), odd = NewId(), idea = NewId();
                    ReportResultWire question = ReportHandler.Accept(Client("tester"), "tester",
                        Submit(q, kind: ReportKinds.Question, destinations: new[] { "server" }, area: "chat"), DateTimeOffset.UtcNow);
                    ReportResultWire rant = ReportHandler.Accept(Client("tester"), "tester", Submit(NewId(), kind: "rant"), DateTimeOffset.UtcNow);
                    ReportHandler.ResetForTest();
                    ReportHandler.Accept(Client("tester"), "tester",
                        Submit(odd, destinations: new[] { "server" }, area: "nowhere", severity: "catastrophic", frequency: ReportFrequencies.Always), DateTimeOffset.UtcNow);
                    ReportHandler.Accept(Client("tester"), "tester",
                        Submit(idea, kind: ReportKinds.Suggestion, destinations: new[] { "server", "elsewhere" }, area: "sites",
                               severity: ReportSeverities.Blocks, frequency: ReportFrequencies.Once), DateTimeOffset.UtcNow);
                    StoredReport sq = ReportStore.Load(q), so = ReportStore.Load(odd), si = ReportStore.Load(idea);
                    bool ok = question.Ok && sq?.Kind == ReportKinds.Question && sq.Area == "chat" && !rant.Ok && rant.Reason == ReportHandler.ReasonInvalid
                              && so?.Area == ReportAreas.General && so.Severity == "" && so.Frequency == ReportFrequencies.Always
                              && si?.Severity == "" && si.Frequency == "" && si.Area == "sites" && si.Destinations == "server"
                              && sq.Status == ReportStatuses.New;
                    return (ok, $"question={question.Ok}/{sq?.Kind}/{sq?.Area}; rant={rant.Ok}/{rant.Reason}; unknown area '{so?.Area}' severity '{so?.Severity}' "
                              + $"frequency '{so?.Frequency}'; a suggestion's severity '{si?.Severity}' destinations '{si?.Destinations}'");
                });

                Check("Reports: the Discord post is a few plain lines that cannot ping anyone and say how to read the report", () =>
                {
                    var r = new StoredReport
                    {
                        ReportId = "abcdef0123456789", Kind = ReportKinds.Bug, Title = "@everyone look <@123456789> here",
                        Body = "ping @here and <@&987654321>\nthen *bold* `code`", Reporter = "@Mallory", ClientVersion = "1.3.1+@x",
                        Area = "marketplace", Severity = ReportSeverities.Blocks, Frequency = ReportFrequencies.Sometimes,
                    };
                    string text = ReportDiscord.Text(r);
                    string[] lines = text.Split('\n');
                    bool noPing = !Pings(text);
                    bool reads = text.Contains("kmh reports show abcdef01") && lines[0].Contains("Blocks them")
                                 && lines[0].Contains("Sometimes") && lines[0].Contains("Marketplace");
                    // The player's own line break stays inside the excerpt's line instead of starting a new one.
                    bool compact = lines.Length >= 3 && lines.Length <= 5 && lines[1].Contains("then \\*bold\\*");
                    return (noPing && reads && compact, $"no mention survives={noPing}; command, severity, frequency, area={reads}; "
                                                      + $"{lines.Length} line(s): {text.Replace("\n", " / ")}");
                });

                Check("Reports: triage sets a status and note by short reference, and a closed report leaves the open list", () =>
                {
                    Fresh(dir, "triage");
                    string id = NewId();
                    ReportHandler.Accept(Client("triage"), "triage", Submit(id, title: "Triage me", destinations: new[] { "server" }), DateTimeOffset.UtcNow);
                    string reference = id.Substring(0, 8);
                    List<string> bad = Said("reports", "status", reference, "exploded");
                    Said("reports", "status", reference, "won't", "fix", "dup", "of", "1a2b");
                    StoredReport afterOdd = ReportStore.Load(id);
                    Said("reports", "status", reference, "fixed", "in", "rel-280");
                    StoredReport after = ReportStore.Load(id);
                    bool open = !Said("reports", "list").Any(l => l.Contains("Triage me"));
                    bool all = Said("reports", "list", "all").Any(l => l.Contains("Triage me") && l.Contains("fixed"));
                    bool shown = Said("reports", "show", reference).Any(l => l.Contains("status: fixed") && l.Contains("note: in rel-280"));
                    bool ok = bad.Any(l => l.Contains("is not a state")) && afterOdd?.Status == ReportStatuses.WontFix && afterOdd.StaffNote == "dup of 1a2b"
                              && after?.Status == ReportStatuses.Fixed && after.StaffNote == "in rel-280" && after.StatusAtUnix > 0 && open && all && shown;
                    return (ok, $"bad state refused; \"won't fix\" -> {afterOdd?.Status}; fixed with note '{after?.StaffNote}'; gone from open={open}; in all={all}; shown={shown}");
                });

                Check("Reports: Global is sent a kind it knows, the true one riding in the hashed diagnostics", () =>
                {
                    Fresh(dir, "kind");
                    using Rig rig = new Rig();
                    string id = NewId();
                    ReportHandler.Accept(Client("tester"), "tester", Submit(id, kind: ReportKinds.Question, area: "guilds"), DateTimeOffset.UtcNow);
                    ReportForwarder.WaitIdleForTest(TimeSpan.FromSeconds(5));
                    StoredReport s = ReportStore.Load(id);
                    ReportBody b = ReportForwarder.BodyFor(s, DateTimeOffset.UtcNow);
                    JObject report = (JObject)JObject.Parse(b.ServerDiagnostics)["report"];
                    bool ok = s?.Kind == ReportKinds.Question && b.Kind == ReportKinds.Suggestion && (string)report?["kind"] == ReportKinds.Question
                              && (string)report?["area"] == "guilds";
                    return (ok, $"stored '{s?.Kind}', Global's field '{b.Kind}', server diagnostics say '{report?["kind"]}'/'{report?["area"]}'");
                });

                Check("Reports: retention runs as a daily job, not only when a report arrives", () =>
                {
                    Fresh(dir, "retention");
                    var jobs = new List<(string Name, TimeSpan Every, Action Tick)>();
                    ReportHandler.Schedule((name, every, tick, kind, first) => jobs.Add((name, every, tick)));
                    var prune = jobs.FirstOrDefault(j => j.Name == "report-prune");
                    string old = NewId();
                    ReportStore.Add(new StoredReport { ReportId = old, Kind = ReportKinds.Bug, Title = "old" }, null);
                    File.SetLastWriteTimeUtc(Path.Combine(ReportStore.DirForTest, old + ".json"), DateTime.UtcNow.AddDays(-100));
                    prune.Tick?.Invoke();
                    bool ok = prune.Tick != null && prune.Every == TimeSpan.FromHours(24) && ReportStore.Load(old) == null;
                    return (ok, $"jobs [{string.Join(", ", jobs.Select(j => j.Name + "/" + j.Every.TotalHours + "h"))}]; the daily one pruned an old report={ReportStore.Load(old) == null}");
                });
            }
            finally
            {
                ReportStore.DirForTest = null;
                ReportHandler.ResetForTest();
                try { Directory.Delete(dir, true); } catch { }
            }
            return r;
        }

        private static string NewId() => Guid.NewGuid().ToString("N");

        // Its own folder, so a requeue or a pass sees only the reports this check made.
        private static void Fresh(string root, string name) => ReportStore.DirForTest = Path.Combine(root, name);

        private static ReportSubmit Submit(string id, string kind = ReportKinds.Bug, string title = "Market freezes", byte[] shot = null,
                                           string[] destinations = null, string area = "", string severity = "", string frequency = "")
            => new ReportSubmit
            {
                ReportId = id, Kind = kind, Title = title, Body = "line one\r\nline two\u0007", ClientVersion = "1.3.1 rel-270",
                CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ClientDiagnostics = "{\"summary\":{\"fps\":59}}",
                Screenshot = shot == null ? "" : Convert.ToBase64String(shot),
                Destinations = destinations == null ? null : new List<string>(destinations),
                Area = area, Severity = severity, Frequency = frequency,
            };

        private static List<string> Said(params string[] args)
        {
            var lines = new List<string>();
            AdminCommands.KmhReportCommands.Run(args, lines.Add);
            return lines;
        }

        // An @ that Discord would read as a mention: anything but the zero-width space NoMentions puts after it.
        private static bool Pings(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = text.IndexOf('@'); i >= 0; i = text.IndexOf('@', i + 1))
                if (i + 1 >= text.Length || text[i + 1] != (char)0x200B) return true;
            return false;
        }

        private static ServerClient Client(string name)
        {
            ServerClient c = new ServerClient(null, null, createListener: false);
            c.GetData<UserFile>(new UserFile { Username = name });
            return c;
        }

        // Registered, forwarding on, test isolation on: how the acceptance server runs its self-tests.
        private sealed class Rig : IDisposable
        {
            public int Sent;
            public string Answer = "{}", LastBody, LastPath;
            public readonly GlobalConfig Config;
            private readonly GlobalConfig _cfg;
            private readonly GlobalSetupState _setup;
            private readonly bool? _selfTest;
            private readonly string _identity;

            public Rig()
            {
                _identity = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest("5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f");
                Config = new GlobalConfig { Enabled = true, BaseUrl = GlobalConfigCheck.OfficialBaseUrl,
                                            RegisteredServerId = "5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f", ForwardReports = true };
                _cfg = GlobalConfig.ApplyForTest(Config);
                GlobalSetupState s = new GlobalSetupState();
                s.Current = SetupStage.Registered;
                _setup = GlobalSetupState.ApplyForTest(s);
                _selfTest = KmhGlobalSafety.SelfTestOverride;
                KmhGlobalSafety.SelfTestOverride = true;
                KmhGlobalClient.TransportForTest = async (req, ct) =>
                {
                    Interlocked.Increment(ref Sent);
                    LastPath = req.RequestUri?.AbsolutePath;
                    LastBody = req.Content == null ? null : await req.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(Answer) };
                };
            }

            public void Dispose()
            {
                KmhGlobalClient.TransportForTest = null;
                KmhGlobalSafety.SelfTestOverride = _selfTest;
                GlobalSetupState.ApplyForTest(_setup);
                GlobalConfig.ApplyForTest(_cfg);
                KmhDataMeta.SetInstanceIdForTest(_identity);
            }
        }
    }
}
