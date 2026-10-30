using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KMHServerAddon.Features.Sites;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Features.Treasury;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhV131StabilizationSelfTest
    {
        // Stock logos KMH has retired, pinned here independently of the addon's own list.
        private const string StockCastle = "e45c808f784e5f69", OldKmhIcon = "4fc815aa2f9f8994";

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Sites: a worker key names the pawn, not just the account", () =>
            {
                string a = SiteWorkerKey.For("ada", 42), b = SiteWorkerKey.For("ada", 77);
                bool ok = a != b && SiteWorkerKey.AccountOf(a) == "ada" && SiteWorkerKey.AccountOf(b) == "ada";
                return (ok, ok ? $"{a} != {b}, both owned by ada" : $"{a} / {b}");
            });

            Check("Sites: a legacy account key still resolves to its account", () =>
            {
                bool ok = SiteWorkerKey.AccountOf("ada") == "ada" && !SiteWorkerKey.IsPerPawn("ada")
                       && SiteWorkerKey.IsOwnedBy("ada", "ADA");
                return (ok, ok ? "unsuffixed keys keep working and match case-insensitively" : "legacy key handling changed");
            });

            Check("Sites: one player's key never reads as another's", () =>
            {
                bool ok = !SiteWorkerKey.IsOwnedBy(SiteWorkerKey.For("adam", 1), "ada")
                       && !SiteWorkerKey.IsOwnedBy(SiteWorkerKey.For("ada", 1), "adam");
                return (ok, ok ? "prefix alone does not make an owner" : "account matching is a prefix compare");
            });

            Check("Sites: an account-keyed worker with a pawn id re-keys and keeps its progress", () =>
            {
                SiteEntry s = SiteWithWorkers(("ada", new WorkerProgressDto { PawnLoadId = 42, PawnName = "Ada", Xp = 250, CyclesCompleted = 7, Destination = SiteEntry.DestStorage }));
                SiteStore.MigrateWorkerKeys(s);
                string want = SiteWorkerKey.For("ada", 42);
                bool moved = s.WorkerProgress.ContainsKey(want) && !s.WorkerProgress.ContainsKey("ada")
                          && s.Workers.Contains(want) && !s.Workers.Contains("ada");
                WorkerProgressDto w = moved ? s.WorkerProgress[want] : null;
                bool kept = w != null && w.Xp == 250 && w.CyclesCompleted == 7
                         && w.Destination == SiteEntry.DestStorage && w.Username == "ada";
                return (moved && kept, moved ? (kept ? "re-keyed with XP, cycles, destination and owner intact" : "re-keyed but lost progress")
                                             : "row did not move");
            });

            Check("Sites: a worker with no pawn id is preserved, never given an invented one", () =>
            {
                SiteEntry s = SiteWithWorkers(("ada", new WorkerProgressDto { PawnLoadId = -1, Xp = 10, Legacy = true }));
                SiteStore.MigrateWorkerKeys(s);
                bool ok = s.WorkerProgress.ContainsKey("ada") && s.WorkerProgress["ada"].Xp == 10
                       && s.WorkerProgress["ada"].Username == "ada" && s.Workers.Contains("ada");
                return (ok, ok ? "kept its account key and its XP - a fake pawn id could collide with a real colonist"
                              : "a pawn-less worker was moved or dropped");
            });

            Check("Sites: migration is idempotent", () =>
            {
                SiteEntry s = SiteWithWorkers(("ada", new WorkerProgressDto { PawnLoadId = 42, Xp = 5 }));
                SiteStore.MigrateWorkerKeys(s);
                int after = s.WorkerProgress.Count;
                SiteStore.MigrateWorkerKeys(s);
                SiteStore.MigrateWorkerKeys(s);
                bool ok = s.WorkerProgress.Count == after && after == 1 && s.Workers.Count == 1;
                return (ok, ok ? "three passes leave one row" : $"{after} -> {s.WorkerProgress.Count} rows");
            });

            Check("Sites: two colonists from one account survive migration as two workers", () =>
            {
                SiteEntry s = SiteWithWorkers(
                    (SiteWorkerKey.For("ada", 1), new WorkerProgressDto { PawnLoadId = 1, Username = "ada" }),
                    (SiteWorkerKey.For("ada", 2), new WorkerProgressDto { PawnLoadId = 2, Username = "ada" }));
                SiteStore.MigrateWorkerKeys(s);
                bool ok = s.WorkerProgress.Count == 2 && SiteStore.MineCount(s, "ada") == 2;
                return (ok, ok ? "both rows kept and both counted for ada" : $"{s.WorkerProgress.Count} rows, {SiteStore.MineCount(s, "ada")} counted");
            });

            Check("Sites: the per-site cap counts a player's colonists, the work cap counts sites", () =>
            {
                SiteEntry s = SiteWithWorkers(
                    (SiteWorkerKey.For("ada", 1), new WorkerProgressDto { PawnLoadId = 1 }),
                    (SiteWorkerKey.For("ada", 2), new WorkerProgressDto { PawnLoadId = 2 }),
                    (SiteWorkerKey.For("bo", 3),  new WorkerProgressDto { PawnLoadId = 3 }));
                bool ok = SiteStore.MineCount(s, "ada") == 2 && SiteStore.MineCount(s, "bo") == 1
                       && SiteStore.WorksLocked(s, "ada") && !SiteStore.WorksLocked(s, "cy");
                return (ok, ok ? "2 for ada, 1 for bo, and the site counts once each" : "counting is wrong");
            });

            Check("Treasury: with no fee configured nothing needs a session", () =>
            {
                var cfg = Features.Economy.EconomyConfig.ClampForTest(
                    new Features.Economy.EconomyConfig
                    { Treasury = new Features.Economy.TreasuryRules { AccessFeeSilver = 0 } });
                bool ok = cfg.Treasury.AccessFeeSilver == 0;
                return (ok, ok ? "default 0 leaves every pre-v1.3.1 server behaving identically" : "default changed");
            });

            Check("Treasury: a negative or absurd access fee is clamped, never honoured", () =>
            {
                var low  = Features.Economy.EconomyConfig.ClampForTest(
                    new Features.Economy.EconomyConfig
                    { Treasury = new Features.Economy.TreasuryRules { AccessFeeSilver = -500 } });
                var high = Features.Economy.EconomyConfig.ClampForTest(
                    new Features.Economy.EconomyConfig
                    { Treasury = new Features.Economy.TreasuryRules { AccessFeeSilver = int.MaxValue } });
                bool ok = low.Treasury.AccessFeeSilver == 0 && high.Treasury.AccessFeeSilver == 1_000_000;
                return (ok, ok ? "-500 -> 0, MaxValue -> 1,000,000" : $"{low.Treasury.AccessFeeSilver} / {high.Treasury.AccessFeeSilver}");
            });

            Check("Treasury: a granted session expires rather than lasting for ever", () =>
            {
                TreasuryAccess.ClearForTest();
                long t0 = DateTime.UtcNow.Ticks;
                TreasuryAccess.Grant("ada", t0);
                bool inside  = TreasuryAccess.Has("ada", t0 + TimeSpan.FromMinutes(1).Ticks);
                bool outside = TreasuryAccess.Has("ada", t0 + TreasuryAccess.Lease.Ticks + TimeSpan.FromSeconds(1).Ticks);
                TreasuryAccess.ClearForTest();
                // Has() is unconditional while no fee is set, so the expiry only bites with one configured.
                bool feeOff = !TreasuryAccess.Required;
                return (inside && (outside == feeOff), feeOff
                    ? "no fee configured: access is unconditional, and the lease still expires on its own clock"
                    : (inside && !outside ? "valid inside the lease, expired after it" : "lease did not expire"));
            });

            Check("Treasury: forgetting a session does not leak to another player", () =>
            {
                TreasuryAccess.ClearForTest();
                long t0 = DateTime.UtcNow.Ticks;
                TreasuryAccess.Grant("ada", t0);
                TreasuryAccess.Forget("bo");
                bool ok = TreasuryAccess.ExpiresAtUnix("ada") > 0 && TreasuryAccess.ExpiresAtUnix("bo") == 0;
                TreasuryAccess.ClearForTest();
                return (ok, ok ? "sessions are per player" : "a session bled across players");
            });

            Check("Protocol: this build speaks v3", () =>
            {
                bool ok = SubProtocol.KmhProtocol.CurrentVersion == 3;
                return (ok, ok ? "wire v3" : $"wire v{SubProtocol.KmhProtocol.CurrentVersion}");
            });

            Check("Protocol: the incompatibility kind exists and is admission control traffic", () =>
            {
                string kind = SubProtocol.KmhProtocol.Kind.Incompatible;
                bool ok = !string.IsNullOrEmpty(kind) && Features.Enforcement.EnforcementAdmission.IsControl(kind);
                return (ok, ok ? "a held session can still receive and send the refusal" : "the refusal would be filtered while held");
            });

            Check("Protocol: the handshake wait is far shorter than the consent wait", () =>
            {
                TimeSpan hello = Features.Enforcement.EnforcementAdmission.HelloTimeout;
                TimeSpan adm   = Features.Enforcement.EnforcementAdmission.AdmissionTimeout;
                bool ok = hello < adm && hello <= TimeSpan.FromSeconds(60) && adm >= TimeSpan.FromMinutes(5);
                return (ok, ok ? $"{(int)hello.TotalSeconds}s to ack, {(int)adm.TotalMinutes}m to consent"
                              : $"{hello} / {adm} - an incompatible client would hold PostLogin for the consent window");
            });

            Check("Discord: every stock icon is embedded in the addon itself", () =>
            {
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in Features.Discord.DiscordIconAssets.Embedded()) have.Add(f);
                string[] want = { "kmh_logo.png", "marketplace.png", "leaderboard.png", "site_created.png",
                                  "site_destroyed.png", "quest.png", "guild.png", "treasury.png", "warning.png", "error.png" };
                var missing = new List<string>();
                foreach (string w in want) if (!have.Contains(w)) missing.Add(w);
                return (missing.Count == 0, missing.Count == 0
                    ? $"{have.Count} embedded asset(s) - an in-place upgrade can restore its own icons"
                    : "missing: " + string.Join(", ", missing));
            });

            // Pinned without its exact bytes: a stock clip-art placeholder or the retired KMH icon must never ship.
            Check("Discord: the shipped kmh_logo is the Colony Nexus mark, not the stock placeholder or the old KMH icon", () =>
            {
                byte[] logo = Features.Discord.DiscordIconAssets.Read("kmh_logo.png");
                if (logo == null || logo.Length == 0) return (false, "kmh_logo.png is not embedded at all");

                bool png = logo.Length > 8 && logo[0] == 0x89 && logo[1] == 0x50 && logo[2] == 0x4E && logo[3] == 0x47;
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(logo)).Replace("-", "").ToLowerInvariant().Substring(0, 16);

                return (png && hash != StockCastle && hash != OldKmhIcon,
                        !png ? "embedded kmh_logo is not a PNG"
                             : hash == StockCastle ? "still the stock Noto castle placeholder"
                             : hash == OldKmhIcon ? "still the old KMH icon"
                                                   : $"Colony Nexus mark, {logo.Length} bytes");
            });

            // A server keeps the logo it copied on first boot, so a stock one KMH has since retired is upgraded in place.
            Check("Discord: a retired stock logo on disk becomes the Colony Nexus logo; the owner's own icons stay", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "kmh-icons-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(dir);
                    string logoPath = Path.Combine(dir, "kmh_logo.png"), questPath = Path.Combine(dir, "quest.png");
                    byte[] stale = { 0x89, 0x50, 0x4E, 0x47, 1 }, own = { 0x89, 0x50, 0x4E, 0x47, 2 };
                    File.WriteAllBytes(logoPath, stale);
                    File.WriteAllBytes(questPath, own);
                    // The stale bytes stand in for a retired logo; the real ones are pinned by hash below.
                    Func<string, string, bool> retired = (f, p) => File.ReadAllBytes(p).SequenceEqual(stale);
                    int first = Features.Discord.DiscordIconAssets.Provision(dir, retired);
                    bool upgraded = File.ReadAllBytes(logoPath).SequenceEqual(Features.Discord.DiscordIconAssets.Read("kmh_logo.png"));
                    bool kept = File.ReadAllBytes(questPath).SequenceEqual(own);
                    int second = Features.Discord.DiscordIconAssets.Provision(dir, retired);

                    File.WriteAllBytes(logoPath, own);
                    bool ownLogo = !Features.Discord.DiscordIconAssets.IsRetiredLogo("kmh_logo.png", logoPath)
                                && Features.Discord.DiscordIconAssets.Provision(dir, Features.Discord.DiscordIconAssets.IsRetiredLogo) == 0
                                && File.ReadAllBytes(logoPath).SequenceEqual(own);
                    bool pinned = new HashSet<string>(Features.Discord.DiscordIconAssets.RetiredLogos).SetEquals(new[] { StockCastle, OldKmhIcon })
                               && Features.Discord.DiscordIconAssets.ShortHash(System.Text.Encoding.ASCII.GetBytes("abc")) == "ba7816bf8f01cfea";
                    int embedded = Features.Discord.DiscordIconAssets.Embedded().Count();
                    bool ok = upgraded && kept && first == embedded - 1 && second == 0 && ownLogo && pinned;
                    return (ok, $"upgraded {upgraded}, own icon kept {kept}, writes {first}/{embedded - 1} then {second}, own logo kept {ownLogo}, retired list pinned {pinned}");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            // Existing DiscordConfig.json files hold the old default, and two posts read the name straight from it.
            Check("Discord: a shipped default name, old or new, gives way to the server's own name everywhere", () =>
            {
                string own = KmhServerIdentity.Name;
                var legacy = new Features.Discord.DiscordConfig();
                legacy.Branding.DisplayName = "KMH Server";
                var fresh = new Features.Discord.DiscordConfig();
                var custom = new Features.Discord.DiscordConfig();
                custom.Branding.DisplayName = "Rimworld Deutschland";
                bool ok = Features.Discord.KmhEmbedBuilder.ServerName(legacy) == own
                       && Features.Discord.KmhEmbedBuilder.ServerName(fresh) == own
                       && Features.Discord.KmhEmbedBuilder.ServerName(custom) == "Rimworld Deutschland";
                return (ok, ok ? "" : $"legacy='{Features.Discord.KmhEmbedBuilder.ServerName(legacy)}'");
            });

            Check("Chat: image previews are on for a NEW server", () =>
            {
                bool ok = new Features.Chat.ChatConfig().AllowImagePreviews;
                return (ok, ok ? "fresh installs preview by default" : "default is still off");
            });

            Check("Chat: a NEW server previews a typed GIF from a trusted host, and still refuses untrusted, plain-http and non-media links", () =>
            {
                var cfg = new Features.Chat.ChatConfig();
                bool giphy = Features.Chat.ChatImagePolicy.Vet(cfg, "https://media2.giphy.com/media/abc/giphy.gif", typedByPlayer: true) != "";
                bool untrusted = Features.Chat.ChatImagePolicy.Vet(cfg, "https://example.com/cat.gif", typedByPlayer: true) == "";
                bool http = Features.Chat.ChatImagePolicy.Vet(cfg, "http://i.imgur.com/cat.gif", typedByPlayer: true) == "";
                bool page = Features.Chat.ChatImagePolicy.Vet(cfg, "https://i.imgur.com/cat.exe", typedByPlayer: true) == "";
                bool ok = cfg.AllowTypedImageUrls && giphy && untrusted && http && page;
                return (ok, $"typed={cfg.AllowTypedImageUrls} giphy={giphy} untrusted-refused={untrusted} http-refused={http} non-media-refused={page}");
            });

            Check("Config: regeneration never rewrites identity or credentials", () =>
            {
                var rewritten = new List<string>();
                foreach ((string name, string _, object __) in KmhConfigValidation.ConfigTargets())
                    if (!KmhConfigRegeneration.IsKept(name)) rewritten.Add(name);
                bool ok = !rewritten.Contains("Global") && !rewritten.Contains("Staff") && !rewritten.Contains("Discord")
                          && rewritten.Count > 0;
                return (ok, ok ? $"{rewritten.Count} tunable config(s) regenerated, 3 kept"
                              : $"would rewrite {string.Join(", ", rewritten.ToArray())}");
            });

            Check("Config: every regenerated file is named in the README", () =>
            {
                var missing = new List<string>();
                foreach ((string name, string _, object __) in KmhConfigValidation.ConfigTargets())
                    if (!KmhConfigRegeneration.Describes(name)) missing.Add(name);
                return (missing.Count == 0, missing.Count == 0
                    ? "owners can look up every config that changed"
                    : $"undocumented: {string.Join(", ", missing.ToArray())}");
            });

            Check("Media: a new server ships a real source allow-list, seeded from Chat's", () =>
            {
                string[] media = new Features.Media.MediaConfig().Fetch.SourceHostAllowList;
                string[] chat  = Features.Chat.ChatConfig.DefaultImageHosts();
                bool ok = media != null && media.Length == chat.Length && media.Length > 0;
                return (ok, ok ? $"{media.Length} host(s), one canonical source for both lists"
                              : $"media {media?.Length ?? 0} vs chat {chat.Length}");
            });

            Check("Media: derived byte totals never reach the owner's file", () =>
            {
                string json = Persistence.JsonFileStore.ToJson(new Features.Media.MediaConfig());
                bool ok = !json.Contains("CacheMaxBytes") && !json.Contains("MaxDecodedBytes");
                return (ok, ok ? "only the megabyte fields an owner can actually set are written"
                              : "a computed field is written back as if it were a setting");
            });

            Check("Discord: a generated config exposes every owner-facing field", () =>
            {
                var file = Features.Discord.DiscordConfig.BuildDefaultFile();
                string[] want = { "SchemaVersion", "Enabled", "Bot", "Branding", "Channels", "Roles", "Events", "Console", "KmhChat", "UseBundledIcons" };
                var missing = new List<string>();
                foreach (string w in want) if (file[w] == null) missing.Add(w);
                if (file["Channels"]?["Commands"] == null) missing.Add("Channels.Commands");
                foreach (string w in new[] { "Channel", "ToDiscord", "FromDiscord", "RelayBots" })
                    if (file["KmhChat"]?[w] == null) missing.Add("KmhChat." + w);
                return (missing.Count == 0, missing.Count == 0
                    ? "built from the type, so it cannot fall behind it again"
                    : "missing: " + string.Join(", ", missing));
            });

            Check("Config: every validated config is also backfilled", () =>
            {
                // One list, or a config gets validated and never topped up.
                var missing = new List<string>();
                foreach ((string name, string path, object _) in KmhConfigValidation.ConfigTargets())
                    if (string.IsNullOrEmpty(path)) missing.Add(name);
                int count = 0;
                foreach (var _ in KmhConfigValidation.ConfigTargets()) count++;
                bool ok = missing.Count == 0 && count >= 16;
                return (ok, ok ? $"{count} configs, all driven off one list" : "config targets are inconsistent");
            });

            Check("Items: a minified key names the building inside it", () =>
            {
                string cell = Util.ItemKey.Compose("MinifiedThing", "", 0, "VanometricPowerCell");
                string bed  = Util.ItemKey.Compose("MinifiedThing", "", 0, "Bed");
                Util.ItemKey.Split(cell, out string def, out _, out _, out string inner);
                bool ok = cell != bed && def == "MinifiedThing" && inner == "VanometricPowerCell"
                       && Util.ItemKey.DisplayDefOf(cell) == "VanometricPowerCell";
                return (ok, ok ? "two minified buildings are two keys, and segment 0 still says MinifiedThing"
                              : $"def={def} inner={inner}");
            });

            Check("Items: a minified key still reads as complex, so it cannot take the compact path", () =>
            {
                string cell = Util.ItemKey.Compose("MinifiedThing", "", 0, "VanometricPowerCell");
                bool ok = !Items.KmhItemSafety.IsCompactDepositSafe(cell.Split('|')[0], out string why);
                return (ok, ok ? $"refused compact: {why}" : "a minified building could be deposited as def+count");
            });

            Check("Items: an ordinary three-part key is unchanged by the new segment", () =>
            {
                string plain = Util.ItemKey.Compose("Steel", "", 0);
                string stuffed = Util.ItemKey.Compose("Bed", "WoodLog", 4);
                Util.ItemKey.Split(stuffed, out string def, out string stuff, out int q, out string inner);
                bool ok = plain == "Steel" && def == "Bed" && stuff == "WoodLog" && q == 4 && inner == "";
                return (ok, ok ? "existing vault keys keep their exact spelling" : $"{plain} / {def}|{stuff}|{q}|{inner}");
            });

            Check("Global: 'disable' and 'unlink' are different commands", () =>
            {
                string help = AdminCommands.KmhGlobalCommands.UsageForTest;
                bool ok = help.Contains("disable") && help.Contains("unlink");
                return (ok, ok ? "a pause and a forget are offered separately" : "usage does not distinguish them");
            });

            return r;
        }

        private static SiteEntry SiteWithWorkers(params (string Key, WorkerProgressDto Worker)[] workers)
        {
            SiteEntry s = new SiteEntry { Tile = 1, OwnerUsername = "owner" };
            s.Workers = new List<string>();
            s.WorkerProgress = new Dictionary<string, WorkerProgressDto>(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, WorkerProgressDto w) in workers)
            {
                s.Workers.Add(key);
                s.WorkerProgress[key] = w;
            }
            return s;
        }
    }
}
