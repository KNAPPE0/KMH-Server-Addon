using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KMHServerAddon.Features.Global;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhPlatformSelfTest
    {
        // Global's CanonicalVectorTests pins the same answer for the same input.
        internal const string ExpectedListingDetailsVector = "7a36de9cea4929f07f6738fff72a8bcbbd6b0d0faedacacaf5725d39cf432a94";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            List<(string, bool, string)> r = new List<(string, bool, string)>();
            Vector(r);
            Moves(r);
            Listing(r);
            Notices(r);
            Refusals(r);
            Doctor(r);
            return r;
        }

        private static void Vector(List<(string, bool, string)> r)
        {
            ListingDetailsBody b = new ListingDetailsBody
            {
                ServerId = "0123456789abcdef0123456789abcdef", IssuedAtUnix = 1_757_000_000, Nonce = "ffffffffffffffffffffffffffffffff",
                Generation = 3, Tagline = "Cosy: PvE \u00fcber alles", PlayStyle = "PvE", NewPlayers = true, Whitelist = true,
                ApplyUrl = "https://apply.example/form", RulesUrl = "https://rules.example", WorkshopCollection = "3012345678",
                Announcement = "Line one\nline two", MaintenanceMessage = "Back soon", MaintenanceUntilUnix = 1_757_003_600,
            };
            string actual;
            using (SHA256 sha = SHA256.Create())
                actual = string.Concat(sha.ComputeHash(b.Canonical()).Select(x => x.ToString("x2")));
            r.Add(("platform: listing details sign the exact bytes Global verifies", actual == ExpectedListingDetailsVector, actual));

            ListingDetailsBody other = new ListingDetailsBody { Tagline = "x" };
            r.Add(("platform: a details push is needed only when an owner field changes",
                   other.ContentKey() != new ListingDetailsBody().ContentKey()
                   && new ListingDetailsBody { Nonce = "a" }.ContentKey() == new ListingDetailsBody { Nonce = "b" }.ContentKey(), ""));

            Republish(r);
        }

        // A visibility change must republish now; once per boot leaves an Unlisted server listed until a restart.
        private static void Republish(List<(string, bool, string)> r)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            string wasPublished = KmhGlobalService.PublishedMetadataForTest, wasVisibility = cfg.Visibility;
            try
            {
                KmhGlobalService.PublishedMetadataForTest = KmhGlobalService.CurrentMetadataKeyForTest();
                bool quiet = !KmhGlobalService.MetadataDue();
                cfg.Visibility = wasVisibility == "Unlisted" ? "Public" : "Unlisted";
                bool due = KmhGlobalService.MetadataDue();
                cfg.Visibility = wasVisibility;
                bool settled = !KmhGlobalService.MetadataDue();
                r.Add(("platform: a listing field changed while running is published again, and nothing else is",
                       quiet && due && settled
                       && new MetadataBody { Nonce = "a", IssuedAtUnix = 1 }.ContentKey() == new MetadataBody { Nonce = "b", IssuedAtUnix = 2 }.ContentKey(),
                       $"unchanged due={!quiet}, after a visibility change due={due}, changed back due={!settled}"));
            }
            finally { cfg.Visibility = wasVisibility; KmhGlobalService.PublishedMetadataForTest = wasPublished; }

            string src = KmhSecurityAuditSelfTest.ReadSource(Path.Combine("Features", "Global", "KmhGlobalService.cs"));
            const string gate = "platform: the heartbeat loop publishes the listing whenever it is due, not once per boot";
            if (src == null) { r.Add(Diagnostics.KmhSelfTestResult.Skipped(gate, "no source tree beside this build")); return; }
            int loop = src.IndexOf("private static async Task LoopAsync(", StringComparison.Ordinal);
            int push = loop < 0 ? -1 : src.IndexOf("PushMetadataAsync(client, ct)", loop, StringComparison.Ordinal);
            int tryAt = push < 0 ? -1 : src.LastIndexOf("try {", push, StringComparison.Ordinal);
            int cond = tryAt < 0 ? -1 : src.LastIndexOf("if (", tryAt, StringComparison.Ordinal);
            string guard = cond < 0 ? "" : src.Substring(cond, tryAt - cond);
            r.Add((gate, guard.Contains("MetadataDue()") && !guard.Contains("!listingPublished"), guard.Replace("\r", "").Replace("\n", " ").Trim()));
        }

        // Regroup trap: a default backfilled into the new block must never beat the owner's moved value.
        private static void Moves(List<(string, bool, string)> r)
        {
            JObject transport = JObject.Parse("{\"MaxConnections\": 77, \"MaxFrameKb\": 0, \"Advanced\": {\"MaxConnections\": 200, \"MaxFrameKb\": 64}}");
            JsonFileStore.ApplyMoves(transport, Features.Transport.TransportConfig.Moves);
            r.Add(("platform: an old flat transport value wins over the default backfilled into Advanced",
                   (int)transport.SelectToken("Advanced.MaxConnections") == 77 && transport["MaxConnections"] == null,
                   transport.ToString(Newtonsoft.Json.Formatting.None)));
            r.Add(("platform: an explicit 0 moves as 0, not as missing", (int)transport.SelectToken("Advanced.MaxFrameKb") == 0, ""));

            JObject staff = JObject.Parse("{\"LabelModerator\": \"Warden\", \"ColorOwner\": \"ff0000\"}");
            JsonFileStore.ApplyMoves(staff, Features.Identity.StaffConfig.Moves);
            r.Add(("platform: staff labels and colours move into Badges",
                   (string)staff.SelectToken("Badges.Moderator.Label") == "Warden" && (string)staff.SelectToken("Badges.Owner.Color") == "ff0000"
                   && staff["LabelModerator"] == null, staff.ToString(Newtonsoft.Json.Formatting.None)));

            string dir = Path.Combine(Path.GetTempPath(), "kmh-platform-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "Global.json");
                File.WriteAllText(path, "{\"Enabled\": true, \"Description\": \"Old words\", \"Region\": \"EU\", \"Tags\": [\"pve\"], " +
                                        "\"RegisteredServerId\": \"abc\", \"SigningGeneration\": 4, \"PublicListingAcknowledgedUtc\": \"x\"}");
                bool loaded = JsonFileStore.TryLoad(path, GlobalConfig.Moves, out GlobalConfig cfg);
                r.Add(("platform: an older Global.json loads its listing into the Listing block",
                       loaded && cfg.Listing.Description == "Old words" && cfg.Listing.Region == "EU" && cfg.Listing.Tags.SequenceEqual(new[] { "pve" }),
                       loaded ? cfg.Listing.Description + "/" + cfg.Listing.Region : "not loaded"));
                r.Add(("platform: an older Global.json's identity is still read until it moves",
                       loaded && cfg.RegisteredServerId == "abc" && cfg.SigningGeneration == 4, ""));

                JsonFileStore.Save(path, cfg);
                JObject written = JObject.Parse(File.ReadAllText(path));
                bool clean = written["RegisteredServerId"] == null && written["SigningGeneration"] == null && written["LastSequence"] == null
                          && written["PublicListingAcknowledgedUtc"] == null && written["Description"] == null
                          && (string)written.SelectToken("Listing.Description") == "Old words";
                r.Add(("platform: Global.json is written without identity, state or the unused acknowledgement",
                       clean, written.ToString(Newtonsoft.Json.Formatting.None)));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static void Listing(List<(string, bool, string)> r)
        {
            r.Add(("platform: play style is read case-insensitively and anything else is left off",
                   ListingLimits.NormalizePlayStyle("pve") == "PvE" && ListingLimits.NormalizePlayStyle("PvP vE") == "PvPvE"
                   && ListingLimits.NormalizePlayStyle("hardcore") == "", ""));
            r.Add(("platform: a Workshop link is reduced to its id and a look-alike host refused",
                   ListingLimits.TryCollection("https://steamcommunity.com/sharedfiles/filedetails/?id=42", out string id) && id == "42"
                   && !ListingLimits.TryCollection("https://steamcommunity.com.evil.example/?id=42", out _), id));
            r.Add(("platform: a maintenance time is read as UTC, and nonsense refused",
                   ListingLimits.TryMaintenanceUntil("2026-10-12 18:00", out long at) && at == new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
                   && !ListingLimits.TryMaintenanceUntil("tomorrow", out _), at.ToString()));

            GlobalConfig cfg = new GlobalConfig();
            cfg.Listing.PlayStyle = "Chaos"; cfg.Listing.ApplyUrl = "javascript:alert(1)"; cfg.Listing.Whitelist = true;
            ListingDetailsBody body = ListingLimits.Build(cfg);
            r.Add(("platform: a bad field is left off the push rather than failing all of it",
                   body.PlayStyle == "" && body.ApplyUrl == "" && body.Whitelist, ""));
            List<GlobalFinding> f = ListingLimits.Inspect(cfg);
            r.Add(("platform: the owner is told which listing field is wrong and how to fix it",
                   f.Any(x => x.Code == "LISTING-PLAYSTYLE") && f.Any(x => x.Code == "LISTING-APPLY-URL"),
                   string.Join(", ", f.Select(x => x.Code))));

            // Global refuses a whole listing over one link that is not https, so the link is dropped here and named.
            GlobalConfig links = new GlobalConfig();
            links.Listing.DiscordUrl = "http://discord.gg/abc"; links.Listing.WebsiteUrl = "javascript:alert(1)";
            MetadataBody dropped = KmhGlobalService.BuildMetadata(links);
            List<GlobalFinding> lf = ListingLimits.Inspect(links);
            links.Listing.DiscordUrl = "https://discord.gg/abc"; links.Listing.WebsiteUrl = "https://example.org";
            MetadataBody kept = KmhGlobalService.BuildMetadata(links);
            r.Add(("platform: a Discord or website link that is not https is left off the listing and named",
                   dropped.DiscordUrl == "" && dropped.WebsiteUrl == "" && kept.DiscordUrl == "https://discord.gg/abc"
                   && kept.WebsiteUrl == "https://example.org" && lf.Any(x => x.Code == "LISTING-DISCORD-URL") && lf.Any(x => x.Code == "LISTING-WEBSITE-URL"),
                   $"dropped '{dropped.DiscordUrl}' '{dropped.WebsiteUrl}'; findings {string.Join(", ", lf.Select(x => x.Code))}"));

            // Half an emoji is text Global cannot store, so a cut at the limit drops the whole one.
            GlobalConfig cut = new GlobalConfig();
            cut.Listing.Tagline = new string('a', ListingLimits.Tagline - 1) + "😀 more";
            cut.Listing.Description = new string('b', 1999) + "😀";
            GlobalConfig.ClampForTest(cut);
            r.Add(("platform: listing text cut at its limit never ends on half an emoji",
                   cut.Listing.Tagline.Length == ListingLimits.Tagline - 1 && cut.Listing.Description.Length == 1999
                   && !char.IsHighSurrogate(cut.Listing.Tagline[cut.Listing.Tagline.Length - 1]),
                   cut.Listing.Tagline.Length + "/" + cut.Listing.Description.Length));
        }

        private static void Notices(List<(string, bool, string)> r)
        {
            KmhModerationNotice.ResetForTest();
            JObject suspended = JObject.Parse("{\"listing\":\"Suspended\",\"listing_until\":1800000000,\"edits\":\"Review\",\"owner\":\"Normal\"," +
                                              "\"pending_review\":true,\"message\":\"Remove the \\u0007giveaway\",\"message_at\":1}");
            KmhModerationNotice.OnHeartbeatResponse(suspended);
            KmhModerationNotice n = KmhModerationNotice.Current;
            r.Add(("platform: a moderation notice says what applies, in words",
                   n != null && n.Summary.Contains("suspended") && n.Summary.Contains("wait for a moderator") && !n.Blocks, n?.Summary ?? "null"));
            r.Add(("platform: moderator text reaches the console without control characters",
                   n != null && !n.Message.Any(char.IsControl), n?.Message ?? ""));
            KmhModerationNotice.OnHeartbeatResponse(JObject.Parse("{\"pending_review\":true}"));
            GlobalFinding held = GlobalConfigCheck.Inspect(new GlobalConfig { Enabled = true, BaseUrl = "https://example.org", PublicHost = "play.example.org" }, true)
                .Find(f => f.Code == "GLOBAL-MODERATION");
            r.Add(("platform: a listing change held for review says what to do about it",
                   held != null && held.Action.Contains("last approved listing") && held.Action.Contains("withdraws"), held?.Action ?? "no finding"));
            KmhModerationNotice.OnHeartbeatResponse(null);
            r.Add(("platform: a beat with nothing to say clears the notice", KmhModerationNotice.Current == null, ""));
            KmhModerationNotice.OnBanned();
            r.Add(("platform: a ban refusal is reported as a ban that blocks", KmhModerationNotice.Current?.Blocks == true, ""));
            KmhModerationNotice.ResetForTest();
        }

        private static void Refusals(List<(string, bool, string)> r)
        {
            GlobalCallResult blocked = new GlobalCallResult { Ok = false, Status = 422, ErrorCode = "KMH-MOD-801" };
            blocked.Args["message"] = "The description contains advertising, so the change was not published.";
            GlobalCallResult other = new GlobalCallResult { Ok = false, Status = 500, ErrorCode = "KMH-SRV-500" };
            r.Add(("platform: a moderation refusal is final and reported, any other failure is retried",
                   KmhGlobalService.ReportModerationRefusal("listing", blocked) && !KmhGlobalService.ReportModerationRefusal("listing", other), ""));
            GlobalCallResult held = new GlobalCallResult { Ok = true, Status = 202, Body = JObject.Parse("{\"review\":\"held\",\"message\":\"waits\"}") };
            GlobalCallResult applied = new GlobalCallResult { Ok = true, Status = 200, Body = JObject.Parse("{\"details_revision\":2}") };
            r.Add(("platform: a held change is told apart from a published one",
                   KmhGlobalService.ReportHeld("listing", held) && !KmhGlobalService.ReportHeld("listing", applied), ""));
        }

        private static void Doctor(List<(string, bool, string)> r)
        {
            var found = new List<AdminCommands.DoctorFinding>
            {
                new AdminCommands.DoctorFinding(AdminCommands.DoctorLevel.Info, "a"),
                new AdminCommands.DoctorFinding(AdminCommands.DoctorLevel.Problem, "b"),
            };
            string summary = AdminCommands.KmhDoctor.Summary(found);
            r.Add(("platform: the health summary counts problems and points at 'kmh doctor'",
                   summary.Contains("1 problem") && summary.Contains("kmh doctor"), summary));
            r.Add(("platform: a clean server is told so plainly",
                   AdminCommands.KmhDoctor.Summary(new List<AdminCommands.DoctorFinding>()) == "Health: all checks passed.", ""));
        }
    }
}
