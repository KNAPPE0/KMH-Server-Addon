using System;
using System.Collections.Generic;
using KMHServerAddon.AdminCommands;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhConfigEditorSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            List<string> areas = KmhConfigSchema.Areas();
            r.Add(("config editor: every validated config is offered for editing",
                   areas.Count == CountTargets(), $"{areas.Count} area(s)"));

            // A credential must never travel, however it was named and wherever it was nested.
            bool secretRule = KmhConfigSchema.IsSecret("Token")
                           && KmhConfigSchema.IsSecret("BotToken")
                           && KmhConfigSchema.IsSecret("ApiKey")
                           && KmhConfigSchema.IsSecret("DbPassword")
                           && KmhConfigSchema.IsSecret("ClientSecret")
                           && !KmhConfigSchema.IsSecret("Enabled")
                           && !KmhConfigSchema.IsSecret("MaxPlayers");
            r.Add(("config editor: credential-shaped names are recognised without being listed one by one",
                   secretRule, ""));

            List<ConfigFieldSpec> discord = KmhConfigSchema.Describe("Discord");
            ConfigFieldSpec token = discord.Find(f => f.Path == "Bot.Token");
            r.Add(("config editor: a nested setting is reachable by its dotted path",
                   token != null && discord.Exists(f => f.Path == "Bot.GuildId"), $"{discord.Count} field(s)"));

            r.Add(("config editor: the Discord bot token is marked secret and carries no value",
                   token != null && token.Secret && token.Value.Length == 0 && token.Default.Length == 0, ""));

            // The count is asserted too: with nothing marked secret, "none carried a value" would pass vacuously.
            int secretsFound = 0;
            bool noSecretValues = true;
            foreach (string area in areas)
                foreach (ConfigFieldSpec f in KmhConfigSchema.Describe(area))
                {
                    if (!f.Secret) continue;
                    secretsFound++;
                    if (f.Value.Length > 0 || f.Default.Length > 0) noSecretValues = false;
                }
            r.Add(("config editor: every secret found is sent without a value, and at least one was found",
                   secretsFound > 0 && noSecretValues, $"{secretsFound} secret(s)"));

            // Setting one must be refused even when the client asks directly, not merely hidden in the UI.
            var edits = new Dictionary<string, string> { ["Bot.Token"] = "stolen" };
            bool refused = !KmhConfigApply.Apply("Discord", edits, "tester", out string why);
            r.Add(("config editor: writing a credential is refused, not just hidden",
                   refused && why.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0, why));

            bool unknownArea = !KmhConfigApply.Apply("NoSuchArea",
                new Dictionary<string, string> { ["X"] = "1" }, "tester", out string areaWhy);
            bool unknownField = !KmhConfigApply.Apply("Chat",
                new Dictionary<string, string> { ["NoSuchSetting"] = "1" }, "tester", out string fieldWhy);
            r.Add(("config editor: an unknown area or setting is refused rather than silently ignored",
                   unknownArea && unknownField, areaWhy + " / " + fieldWhy));

            bool emptyRefused = !KmhConfigApply.Apply("Chat", new Dictionary<string, string>(), "tester", out _);
            r.Add(("config editor: an empty edit set does not rewrite the file", emptyRefused, ""));

            bool parse = KmhConfigApply.TryParse(typeof(bool), "true", out object b, out _) && (bool)b
                      && KmhConfigApply.TryParse(typeof(int), "42", out object i, out _) && (int)i == 42
                      && !KmhConfigApply.TryParse(typeof(int), "4.2", out _, out _)
                      && !KmhConfigApply.TryParse(typeof(bool), "yes", out _, out _);
            r.Add(("config editor: a value of the wrong shape is refused with a reason", parse, ""));

            // A German client sends "1,5"; parsed by culture that becomes 15 on an English server.
            bool invariant = KmhConfigApply.TryParse(typeof(double), "1.5", out object d, out _)
                          && Math.Abs((double)d - 1.5) < 0.0001
                          && !KmhConfigApply.TryParse(typeof(double), "1,5", out _, out _);
            r.Add(("config editor: decimals parse invariantly, so a comma is refused not misread", invariant, ""));

            string[] split = KmhConfigApply.SplitList(" a , b ,, c ");
            r.Add(("config editor: a list drops blanks and trims each entry",
                   split.Length == 3 && split[0] == "a" && split[2] == "c", string.Join("|", split)));

            r.Add(("config editor: an empty list is empty, not one blank entry",
                   KmhConfigApply.SplitList("").Length == 0 && KmhConfigApply.SplitList("  ,  ").Length == 0, ""));

            List<ConfigFieldSpec> global = KmhConfigSchema.Describe("Global");
            ConfigFieldSpec assigned = global.Find(f => f.Path == "RegisteredServerId");
            ConfigFieldSpec schema   = global.Find(f => f.Path == "SchemaVersion");
            r.Add(("config editor: values the server assigns itself are shown read-only",
                   assigned != null && assigned.ReadOnly && schema != null && schema.ReadOnly, ""));

            Features.Admin.ConfigEditorGrant.ResetForTest();
            DateTimeOffset t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            r.Add(("config editor: nobody has access until the console opens it",
                   !Features.Admin.ConfigEditorGrant.Allows("Knappe", t0), ""));

            Features.Admin.ConfigEditorGrant.Open("Knappe", TimeSpan.FromMinutes(30), "console", t0);
            r.Add(("config editor: an opened window admits that account and nobody else",
                   Features.Admin.ConfigEditorGrant.Allows("Knappe", t0.AddMinutes(1))
                && !Features.Admin.ConfigEditorGrant.Allows("SomeoneElse", t0.AddMinutes(1)), ""));

            // Case only, because RWT account names are compared case-insensitively everywhere else.
            r.Add(("config editor: the window follows the account however it is cased",
                   Features.Admin.ConfigEditorGrant.Allows("KNAPPE", t0.AddMinutes(1)), ""));

            r.Add(("config editor: the window closes on its own",
                   !Features.Admin.ConfigEditorGrant.Allows("Knappe", t0.AddMinutes(31)), ""));

            Features.Admin.ConfigEditorGrant.Open("Knappe", TimeSpan.FromMinutes(30), "console", t0);
            r.Add(("config editor: revoking closes it immediately",
                   Features.Admin.ConfigEditorGrant.Close("Knappe")
                && !Features.Admin.ConfigEditorGrant.Allows("Knappe", t0.AddMinutes(1))
                && !Features.Admin.ConfigEditorGrant.Close("Knappe"), ""));

            // A window nobody can close is worse than no window, so the ceiling is enforced on the way in.
            Features.Admin.ConfigEditorGrant.Open("Knappe", TimeSpan.FromDays(3650), "console", t0);
            bool capped = !Features.Admin.ConfigEditorGrant.Allows(
                "Knappe", t0 + Features.Admin.ConfigEditorGrant.MaxWindow + TimeSpan.FromMinutes(1));
            r.Add(("config editor: an absurd window is clamped rather than accepted", capped, ""));

            r.Add(("config editor: a zero or negative window falls back to the default, never to forever",
                   Features.Admin.ConfigEditorGrant.Clamp(TimeSpan.Zero) == Features.Admin.ConfigEditorGrant.DefaultWindow
                && Features.Admin.ConfigEditorGrant.Clamp(TimeSpan.FromMinutes(-5)) == Features.Admin.ConfigEditorGrant.DefaultWindow, ""));

            Features.Admin.ConfigEditorGrant.ResetForTest();

            // Reflected from the posted bodies, so the disclosure can never describe less than what actually travels.
            List<string> beat = Features.Global.KmhGlobalDisclosure.Fields(typeof(Features.Global.HeartbeatBody));
            r.Add(("disclosure: the heartbeat's fields are read off the body that is sent",
                   beat.Contains("players") && beat.Contains("rwt_version") && beat.Contains("endpoint")
                && !beat.Contains("signature"), $"{beat.Count} field(s)"));

            List<string> meta = Features.Global.KmhGlobalDisclosure.Fields(typeof(Features.Global.MetadataBody));
            r.Add(("disclosure: the metadata body's fields are read off the body that is sent",
                   meta.Count > 0 && meta.Contains("tags"), $"{meta.Count} field(s)"));

            var said = new List<string>();
            Features.Global.KmhGlobalDisclosure.Describe(said.Add);
            string all = string.Join("\n", said.ToArray());
            r.Add(("disclosure: it states the two conditions and how to stop it",
                   all.IndexOf("completed registration", StringComparison.OrdinalIgnoreCase) >= 0
                && all.IndexOf("kmh global disable", StringComparison.Ordinal) >= 0
                && all.IndexOf("Never sent", StringComparison.Ordinal) >= 0, ""));

            // Nothing reaches a directory unless on AND registered; setup is exempt only because the owner started it.
            bool setupAllowed = Features.Global.KmhGlobalDisclosure.IsSetupPath("/api/v1/servers/register")
                             && Features.Global.KmhGlobalDisclosure.IsSetupPath("/api/v1/owner-links")
                             && Features.Global.KmhGlobalDisclosure.IsSetupPath("/api/v1/servers/abc/registration")
                             && !Features.Global.KmhGlobalDisclosure.IsSetupPath("/api/v1/servers/heartbeat")
                             && !Features.Global.KmhGlobalDisclosure.IsSetupPath("/api/v1/servers/abc/metadata")
                             && !Features.Global.KmhGlobalDisclosure.IsSetupPath("/api/v1/servers/abc/mod-profile")
                             && !Features.Global.KmhGlobalDisclosure.IsSetupPath("/api/v1/chat/global/relay");
            r.Add(("disclosure: only the owner-initiated setup calls bypass the publish guard", setupAllowed, ""));

            bool guarded = !Features.Global.KmhGlobalDisclosure.MayPublish("/api/v1/servers/heartbeat", out string wontWhy)
                        && wontWhy.Length > 0;
            r.Add(("disclosure: an unregistered server publishes nothing, and says why", guarded, wontWhy));

            // Read off the registration source: a call missing from the allow-list blocks first-run setup.
            SetupPathsCoverRegistration(r);

            // Off by default: a directory asking is not the same as a server agreeing.
            r.Add(("diagnostics: sharing is off until the owner turns it on",
                   new Features.Global.GlobalConfig().ShareDiagnostics == false, ""));

            string snap = Features.Global.KmhDiagnosticsResponder.Snapshot();
            bool everyFieldPresent = true;
            foreach (string f in Features.Global.KmhDiagnosticsResponder.SnapshotFields)
                if (snap.IndexOf(f + "=", StringComparison.Ordinal) < 0) everyFieldPresent = false;
            r.Add(("diagnostics: the snapshot carries exactly the fields the disclosure names", everyFieldPresent,
                   $"{Features.Global.KmhDiagnosticsResponder.SnapshotFields.Length} field(s)"));

            // The projection drops these two on purpose; a path or a player name must not reach a directory.
            bool redacted = snap.IndexOf("DataFolder", StringComparison.OrdinalIgnoreCase) < 0
                         && snap.IndexOf("Leaders", StringComparison.OrdinalIgnoreCase) < 0
                         && snap.IndexOf(":\\", StringComparison.Ordinal) < 0
                         && snap.IndexOf("/opt/", StringComparison.Ordinal) < 0;
            r.Add(("diagnostics: the snapshot carries no file path and no player name", redacted, ""));

            List<SetupStep> steps = KmhSetupCommands.Steps();
            r.Add(("setup: the checklist reports every step it knows about", steps.Count >= 4, $"{steps.Count} step(s)"));

            bool actionable = true, named = true;
            foreach (SetupStep s in steps)
            {
                if (s.Name.Length == 0 || s.Detail.Length == 0) named = false;
                // A step that is still to do without a command to run is a checklist that only nags.
                if (s.State == SetupState.Todo && s.Next.Length == 0) actionable = false;
            }
            r.Add(("setup: every step names itself and says what it found", named, ""));
            r.Add(("setup: every outstanding step carries the command that clears it", actionable, ""));

            SetupStep owner = steps.Find(s => s.Name == "Owner");
            r.Add(("setup: the owner step reads the live staff list, not a remembered answer",
                   owner != null
                && (owner.State == SetupState.Done) == (Features.Identity.StaffConfig.Current?.Owners?.Count > 0), ""));

            bool everyAreaDescribes = true;
            foreach (string area in areas)
                if (KmhConfigSchema.Describe(area).Count == 0) everyAreaDescribes = false;
            r.Add(("config editor: every area yields at least one field", everyAreaDescribes, ""));

            // All three states are asserted, or a guard that always refuses (or always allows) would read as a pass.
            {
                string who = "selftest_owner_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Action<string> say = _ => { };
                List<string> owners = Features.Identity.StaffConfig.Current.Owners;

                bool strangerRefused = !KmhServerCommands.OwnerWithSettingsAccess(who, say, "Restarting");

                owners.Add(who);
                Features.Identity.StaffRegistry.ResetLookupForTest();
                bool ownerNoGrantRefused = !KmhServerCommands.OwnerWithSettingsAccess(who, say, "Restarting");

                Features.Admin.ConfigEditorGrant.Open(who, TimeSpan.FromMinutes(5), "selftest", DateTimeOffset.UtcNow);
                bool ownerWithGrantAllowed = KmhServerCommands.OwnerWithSettingsAccess(who, say, "Restarting");

                Features.Admin.ConfigEditorGrant.ResetForTest();
                owners.Remove(who);
                Features.Identity.StaffRegistry.ResetLookupForTest();

                r.Add(("restart: in game it needs owner AND an open grant, exactly like the settings window",
                       strangerRefused && ownerNoGrantRefused && ownerWithGrantAllowed,
                       $"stranger={strangerRefused} ownerNoGrant={ownerNoGrantRefused} ownerGrant={ownerWithGrantAllowed}"));
            }

            // The helper guards only what routes through it; rewired to the plain admin gate, any RWT admin could restart.
            string cmdSrc = ReadSource(System.IO.Path.Combine("AdminCommands", "KmhServerCommands.cs"));
            if (cmdSrc != null)
                r.Add(("restart: the command is dispatched with fromConsole, not the plain admin gate",
                       cmdSrc.Contains("case \"restart\":        RestartCmd(args, actorName, reply, fromConsole);")
                    && !cmdSrc.Contains("Admin(isAdmin, \"restart\""), ""));

            return r;
        }

        private static int CountTargets()
        {
            int n = 0;
            foreach (var _ in KmhConfigValidation.ConfigTargets()) n++;
            return n;
        }

        // Every /api path registration calls must be admitted while unregistered, the state setup runs in.
        private static void SetupPathsCoverRegistration(List<(string, bool, string)> r)
        {
            string src = ReadSource(System.IO.Path.Combine("Features", "Global", "KmhGlobalRegistration.cs"));
            if (src == null)
            {
                r.Add(Diagnostics.KmhSelfTestResult.Skipped("disclosure: the registration path is the one the editor names", "registration source not available (shipped build)"));
                return;
            }

            var blocked = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(src, @"""(/api/v1/[^""]*)"""))
            {
                // An interpolated segment stands for any id, so it is probed with one rather than skipped.
                string path = m.Groups[1].Value.Replace("{serverId}", "abc").Replace("{state.PendingLinkId}", "abc");
                if (path.Contains('{')) continue;
                if (!Features.Global.KmhGlobalDisclosure.IsSetupPath(path)) blocked.Add(path);
            }

            r.Add(("disclosure: every path registration calls is reachable before the server is registered",
                   blocked.Count == 0, blocked.Count == 0 ? "" : "BLOCKED: " + string.Join(", ", blocked.ToArray())));
        }

        private static string ReadSource(string relative)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/'));
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    string candidate = System.IO.Path.Combine(dir, "Source", relative);
                    if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
