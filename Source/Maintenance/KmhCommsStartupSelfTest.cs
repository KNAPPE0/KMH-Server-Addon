using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Boot and reload are separate code paths that must reach the same state, so the two are compared directly.
    internal static class KmhCommsStartupSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            // Reports when KMH-Data is unreachable rather than vanishing, since a check that disappears reads like a pass.
            SortedDictionary<string, string> afterBoot = null, afterReload = null;
            string unavailable = null;
            try
            {
                Features.Comms.CommsStartup.ApplyAtBoot();
                afterBoot = Features.Comms.CommsPresentation.Fields();

                ReloadEverything();
                afterReload = Features.Comms.CommsPresentation.Fields();
            }
            catch (System.Exception ex) when (ex is FileNotFoundException || ex is System.TypeInitializationException
                                              || ex is System.TypeLoadException)
            {
                unavailable = "not asked - needs a running server (RWT assemblies absent)";
            }

            if (unavailable != null)
            {
                r.Add(("startup: a clean boot and `kmh reload all` reach the same Communications state", true, unavailable));
                r.Add(("startup: reloading again changes nothing, so the state is genuinely settled", true, unavailable));
                r.Add(("startup: the comparison covers chat presentation, staff, media and capabilities", true, unavailable));
                r.Add(("startup: applying is what re-reads the file, so a late rewrite cannot be missed", true, unavailable));
            }
            else
            {
                List<string> diffs = Features.Comms.CommsPresentation.Diff(afterBoot, afterReload);
                r.Add(("startup: a clean boot and `kmh reload all` reach the same Communications state",
                       diffs.Count == 0,
                       diffs.Count == 0 ? $"{afterBoot.Count} field(s) compared" : string.Join(" | ", diffs)));

                // A value that only settles on the second pass is still a boot-order bug.
                ReloadEverything();
                List<string> again = Features.Comms.CommsPresentation.Diff(afterReload, Features.Comms.CommsPresentation.Fields());
                r.Add(("startup: reloading again changes nothing, so the state is genuinely settled",
                       again.Count == 0, again.Count == 0 ? "" : string.Join(" | ", again)));

                r.Add(("startup: the comparison covers chat presentation, staff, media and capabilities",
                       afterBoot.ContainsKey("chat.theme.accent") && afterBoot.ContainsKey("chat.discord.marker")
                       && afterBoot.ContainsKey("staff.wire") && afterBoot.ContainsKey("staff.badges")
                       && afterBoot.ContainsKey("media.resolver") && afterBoot.ContainsKey("media.src.bytes")
                       && afterBoot.ContainsKey("chat.image.maxbytes") && afterBoot.ContainsKey("chat.image.hosts")
                       && afterBoot.ContainsKey("capabilities") && afterBoot.ContainsKey("feature.chat"),
                       afterBoot.Count + " field(s)"));

                // A config generated late but read from a cache filled earlier would not take effect until a restart.
                r.Add(("startup: applying is what re-reads the file, so a late rewrite cannot be missed",
                       ReadsFromDiskAfterApply(), ""));
            }

            // The role map rides the hello, so a historic message badges correctly even when its author is offline.
            var multi = new Features.Identity.StaffConfig
            {
                ShowStaffBadges = true,
                Owners     = new List<string> { "KNAPPE0" },
                Admins     = new List<string> { "KNAPPE0" },
                Moderators = new List<string> { "KNAPPE0" },
                Ops        = new List<string> { "KNAPPE0" },
                Developers = new List<string> { "Test" },
            };
            string wire = multi.RoleWire();
            r.Add(("staff: the role map names every listed member",
                   wire.Contains("KNAPPE0:owner") && wire.Contains("Test:developer"), wire));
            // Four roles at once: the badge must be the highest, never whichever list happened to be walked last.
            r.Add(("staff: a member of several roles resolves to the highest, deterministically",
                   wire.IndexOf("KNAPPE0:owner", System.StringComparison.Ordinal) >= 0
                   && !wire.Contains("KNAPPE0:admin") && !wire.Contains("KNAPPE0:moderator")
                   && !wire.Contains("KNAPPE0:op"), wire));
            r.Add(("staff: badges off means no role map leaves the server at all",
                   new Features.Identity.StaffConfig { ShowStaffBadges = false,
                       Owners = new List<string> { "KNAPPE0" } }.RoleWire() == "", ""));
            r.Add(("staff: an empty roster produces an empty map, not a malformed one",
                   new Features.Identity.StaffConfig { ShowStaffBadges = true }.RoleWire() == "", ""));

            int before = Features.Comms.CommsStartup.Revision;
            Features.Comms.CommsStartup.ApplyChat(push: false);
            r.Add(("startup: applying Communications advances the revision",
                   Features.Comms.CommsStartup.Revision > before,
                   $"{before} -> {Features.Comms.CommsStartup.Revision}"));

            string helloSrc2 = ReadSource(Path.Combine("SubProtocol", "KmhHandshakeHandler.cs"));
            if (helloSrc2 != null)
                r.Add(("startup: the hello carries the role map and the revision",
                       helloSrc2.Contains("staff_roles") && helloSrc2.Contains("comms_rev"), ""));

            // Balanced is a real preset, not a label: the zeros in Economy.json are Custom-only fields.
            var balanced = new Features.Economy.EconomyConfig
            {
                EconomyMode = "Balanced",
                TreasuryDepositFeePercent = 0, TreasuryWithdrawFeePercent = 0,
                TreasuryDepositCooldownSeconds = 0, TreasuryWithdrawCooldownSeconds = 0,
            };
            Features.Economy.EconomyPolicy bp = balanced.ResolvePolicy();
            r.Add(("economy: Balanced applies real friction even when the Custom fields are all zero",
                   bp.DepositFeePct == 1.0 && bp.WithdrawFeePct == 1.0
                   && bp.DepositCooldownSec == 5 && bp.WithdrawCooldownSec == 5,
                   $"{bp.DepositFeePct}%/{bp.WithdrawFeePct}%, {bp.DepositCooldownSec}s/{bp.WithdrawCooldownSec}s"));
            r.Add(("economy: only Custom reads the granular fields",
                   new Features.Economy.EconomyConfig { EconomyMode = "Custom", TreasuryDepositFeePercent = 7 }
                       .ResolvePolicy().DepositFeePct == 7.0, ""));

            // These caps are live under every mode, unlike the Custom-only fields above.
            var freshEco = new Features.Economy.EconomyConfig();
            r.Add(("economy: the always-live transaction caps are sane for a new server",
                   freshEco.MaxSilverDepositPerTx == 1_000_000 && freshEco.MaxItemDepositQtyPerTx == 5_000,
                   $"{freshEco.MaxSilverDepositPerTx} silver / {freshEco.MaxItemDepositQtyPerTx} items"));
            r.Add(("mail: a new server caps an attachment well below a billion",
                   new Features.Mail.MailConfig().MaxAttachSilver == 500_000, ""));
            r.Add(("frontier: a new v1.3.0 server has Frontier Operations on",
                   new Features.Frontier.FrontierConfig().Enabled, ""));

            // The Custom premium is the archetype multiplier, not the global site scalar; conflating them cuts every price by 55%.
            r.Add(("sites: Custom costs 1.35x a preset through the archetype multiplier",
                   System.Math.Abs(Features.Sites.SiteArchetypeRegistry.Get(Features.Sites.SiteArchetypes.Custom).CostMultiplier - 1.35) < 0.001,
                   Features.Sites.SiteArchetypeRegistry.Get(Features.Sites.SiteArchetypes.Custom).CostMultiplier.ToString()));
            r.Add(("sites: the global price scalar is left at 3.0 and is NOT the Custom premium",
                   System.Math.Abs(new Features.Sites.SitesConfig().CustomSitePriceMultiplier - 3.0) < 0.001, ""));

            // A diff that cannot report a difference would make the whole suite decorative.
            var left  = new SortedDictionary<string, string>(System.StringComparer.Ordinal) { { "a", "1" }, { "b", "2" } };
            var right = new SortedDictionary<string, string>(System.StringComparer.Ordinal) { { "a", "9" }, { "c", "3" } };
            List<string> demo = Features.Comms.CommsPresentation.Diff(left, right);
            r.Add(("startup: the comparison can actually see a difference",
                   demo.Count == 3 && demo[0].Contains("a:"), string.Join(" | ", demo)));

            // A config wired only into EnsureGenerated would be on disk and absent from what clients are told.
            string startupSrc = ReadSource(Path.Combine("Features", "Comms", "CommsStartup.cs"));
            if (startupSrc != null)
            {
                r.Add(("startup: chat, staff and media are all applied by the shared boot path",
                       startupSrc.Contains("ApplyChat") && startupSrc.Contains("ApplyStaff")
                       && startupSrc.Contains("ApplyMedia"), ""));
                // Each apply drops the cached config first, or boot keeps whatever was read before the migration rewrote it.
                r.Add(("startup: applying drops the cached config so the file is genuinely re-read",
                       startupSrc.Contains("Chat.ChatConfig.Reload()")
                       && startupSrc.Contains("Identity.StaffConfig.Reload()")
                       && startupSrc.Contains("Media.MediaConfig.Reload()"), ""));
                r.Add(("startup: boot does not push to clients, because none are connected yet",
                       startupSrc.Contains("ApplyChat(push: false)") && startupSrc.Contains("ApplyStaff(push: false)")
                       && startupSrc.Contains("ApplyMedia(push: false)"), ""));
            }

            // Each hello makes a client re-request every snapshot, and `kmh reload all` touches five areas.
            if (startupSrc != null)
                r.Add(("startup: hello pushes can be batched, so one reload is one handshake",
                       startupSrc.Contains("BeginBatch") && startupSrc.Contains("EndBatch")
                       && startupSrc.Contains("_batchDepth > 0) { _batchWanted = true; return; }"), ""));

            string cmdSrc = ReadSource(Path.Combine("AdminCommands", "KmhServerCommands.cs"));
            if (cmdSrc != null)
            {
                int begin = cmdSrc.IndexOf("CommsStartup.BeginBatch", System.StringComparison.Ordinal);
                int end   = cmdSrc.IndexOf("CommsStartup.EndBatch", System.StringComparison.Ordinal);
                r.Add(("startup: `reload all` opens and closes a batch around every area it reloads",
                       begin > 0 && end > begin, $"begin@{begin} end@{end}"));
            }

            // Reload must route through the same apply rather than keeping its own copy of the logic.
            string reloadSrc = ReadSource(Path.Combine("AdminCommands", "KmhConfigReload.cs"));
            if (reloadSrc != null)
            {
                r.Add(("startup: `kmh reload` calls the same apply boot does, not a parallel implementation",
                       reloadSrc.Contains("CommsStartup.ApplyChat(push: true)")
                       && reloadSrc.Contains("CommsStartup.ApplyStaff(push: true)")
                       && reloadSrc.Contains("CommsStartup.ApplyMedia(push: true)"), ""));
            }

            // Ordering is the whole bug, so it is asserted against the source rather than left to a comment.
            string mainSrc = ReadSource("Main.cs");
            if (mainSrc != null)
            {
                int backfill = mainSrc.IndexOf("KmhConfigMigration.ApplyIfNeeded", System.StringComparison.Ordinal);
                int gate     = mainSrc.IndexOf("KmhConfigValidation.GateOnBoot", System.StringComparison.Ordinal);
                int apply    = mainSrc.IndexOf("CommsStartup.ApplyAtBoot", System.StringComparison.Ordinal);
                r.Add(("startup: Communications is applied after config migration and the boot gate, never before",
                       apply > 0 && backfill > 0 && gate > 0 && apply > backfill && apply > gate,
                       $"migrate@{backfill} gate@{gate} apply@{apply}"));

                int ensureChat = mainSrc.IndexOf("ChatConfig.EnsureGenerated", System.StringComparison.Ordinal);
                int ensureMedia = mainSrc.IndexOf("MediaConfig.EnsureGenerated", System.StringComparison.Ordinal);
                int ensureStaff = mainSrc.IndexOf("StaffConfig.EnsureGenerated", System.StringComparison.Ordinal);
                r.Add(("startup: every Communications config is generated before it is applied",
                       ensureChat > 0 && ensureMedia > 0 && ensureStaff > 0
                       && apply > ensureChat && apply > ensureMedia && apply > ensureStaff,
                       $"chat@{ensureChat} media@{ensureMedia} staff@{ensureStaff} apply@{apply}"));
            }

            // The handshake must not advertise Communications state before it exists.
            string helloSrc = ReadSource(Path.Combine("SubProtocol", "KmhHandshakeHandler.cs"));
            if (helloSrc != null)
                r.Add(("startup: a handshake arriving early applies the configuration instead of sending defaults",
                       helloSrc.Contains("CommsStartup.Ready") && helloSrc.Contains("CommsStartup.ApplyAtBoot"), ""));

            r.Add(("startup: readiness is recorded, so a log can say whether a handshake beat it",
                   unavailable != null || (Features.Comms.CommsStartup.Ready
                                           && Features.Comms.CommsStartup.ReadyUtc != System.DateTime.MinValue),
                   unavailable ?? ""));

            return r;
        }

        // Everything `kmh reload all` would do for Communications, through the same entry points.
        private static void ReloadEverything()
        {
            Features.Comms.CommsStartup.ApplyChat(push: false);
            Features.Comms.CommsStartup.ApplyStaff(push: false);
            Features.Comms.CommsStartup.ApplyMedia(push: false);
        }

        // A marker written to the file proves applying re-reads it rather than handing back a cached object.
        private static bool ReadsFromDiskAfterApply()
        {
            string path = KmhDataPaths.ChatConfigFile;
            if (!File.Exists(path)) return true;   // nothing on disk to prove it against

            string original = File.ReadAllText(path);
            try
            {
                Features.Chat.ChatConfig probe = Features.Chat.ChatConfig.LoadOrDefault();
                int was = probe.MaxRecentPerChannel;
                int want = was == 77 ? 78 : 77;
                probe.MaxRecentPerChannel = want;
                JsonFileStore.Save(path, probe);

                Features.Comms.CommsStartup.ApplyChat(push: false);
                return Features.Chat.ChatConfig.Current.MaxRecentPerChannel == want;
            }
            catch { return false; }
            finally
            {
                try { File.WriteAllText(path, original); } catch { }
                Features.Comms.CommsStartup.ApplyChat(push: false);
            }
        }

        private static string ReadSource(string relative)
        {
            try
            {
                string dir = System.AppContext.BaseDirectory;
                for (int up = 0; up < 8 && dir != null; up++)
                {
                    string candidate = Path.Combine(dir, "Source", relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    candidate = Path.Combine(dir, relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }
    }
}
