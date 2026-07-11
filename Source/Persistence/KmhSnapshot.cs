using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Persistence
{
    // Versioned read-only snapshots (player + server) with checksummed manifests; see SNAPSHOTS.txt.
    internal static class KmhSnapshot
    {
        public const int SchemaVersion = 1;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
        };

        public static string Season() => "S" + Features.Seasons.SeasonStore.CurrentSeason;

        // Caller's match timestamp (pairs with their backup folder) or UTC-now.
        public static string FolderName(string matchTimestamp)
            => string.IsNullOrWhiteSpace(matchTimestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm")
                : SanitizePart(matchTimestamp.Trim());

        // ---- server snapshot (all shared state) ----

        public static bool SnapshotServer(string matchTimestamp, string sourceSaveTimestamp, out string dir, out string error)
        {
            dir = null; error = null;
            List<string> warnings = new List<string>();
            try
            {
                Maintenance.KmhDataFlush.FlushAll();   // flush so on-disk is current

                string folder = FolderName(matchTimestamp);
                dir = Path.Combine(KmhDataPaths.SnapshotsRoot, Season(), "_server", folder);

                JObject stores = new JObject();
                List<string> included = new List<string>();
                foreach (KmhDataPaths.DataFile f in KmhDataPaths.KnownDataFiles)
                {
                    try
                    {
                        if (!File.Exists(f.Path)) continue;
                        stores[f.Label] = JToken.Parse(File.ReadAllText(f.Path));
                        included.Add(f.Label);
                    }
                    catch (Exception ex) { warnings.Add($"store '{f.Label}' skipped: {ex.Message}"); }
                }

                JObject snap = new JObject
                {
                    ["schema_version"]         = SchemaVersion,
                    ["kmh_build"]              = SubProtocol.KmhProtocol.BuildVersion,
                    ["season"]                 = Season(),
                    ["kind"]                   = "server",
                    ["server_name"]            = KmhServerIdentity.Name,
                    ["server_id"]              = KmhServerIdentity.Id,
                    ["generated_utc"]          = DateTime.UtcNow.ToString("o"),
                    ["match_timestamp"]        = folder,
                    ["source_save_timestamp"]  = sourceSaveTimestamp ?? "",
                    ["stores"]                 = stores,
                };

                WriteWithManifest(dir, "kmh_server_snapshot.json", "kmh_server_manifest.json", snap, "server",
                    playerId: null, matchTs: folder, sourceSaveTs: sourceSaveTimestamp, includedStores: included,
                    sharedStateInvolved: true, requiresAdminReview: true, warnings: warnings);
                RaiseCreated("server", "", folder, dir);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // ---- player snapshot (everything tied to one player) ----

        public static bool SnapshotPlayer(string username, string matchTimestamp, string sourceSaveTimestamp, out string dir, out string error)
        {
            dir = null; error = null;
            if (string.IsNullOrWhiteSpace(username)) { error = "no username"; return false; }
            List<string> warnings = new List<string>();
            List<string> included = new List<string>();
            try
            {
                string user = username.Trim();
                JObject data = new JObject();

                Gather(data, included, warnings, "standings", () =>
                    Features.PlayerStats.PlayerStatsStore.BuildSnapshot().Entries.FirstOrDefault(e => Eq(e.Username, user)));
                Gather(data, included, warnings, "colonist", () => Features.PlayerStats.PlayerStatsStore.GetColonist(user));
                Gather(data, included, warnings, "roster", () =>
                    Features.PlayerStats.PlayerStatsStore.BuildColonistRoster().Colonists.Where(c => Eq(c.Owner, user)).ToList());
                Gather(data, included, warnings, "reputation", () =>
                    Features.Reputation.ReputationStore.BuildSnapshot().Entries.FirstOrDefault(r => Eq(r.Username, user)));
                Gather(data, included, warnings, "treasury", () => Features.Treasury.TreasuryStore.GetSnapshotFor(user));
                Gather(data, included, warnings, "guild", () => Features.Guilds.GuildStore.BuildEnvelopeFor(user));
                Gather(data, included, warnings, "guild_invites", () => Features.Guilds.GuildStore.InvitesFor(user));
                Gather(data, included, warnings, "marketplace_listings", () =>
                    Features.Marketplace.MarketplaceStore.BuildSnapshot(user).Listings.Where(l => Eq(l.SellerUsername, user)).ToList());
                Gather(data, included, warnings, "auctions", () =>
                    Features.Auctions.AuctionStore.AllForAdmin().Where(a => Eq(a.SellerUsername, user) || Eq(a.HighBidder, user)).ToList());
                Gather(data, included, warnings, "wants", () =>
                    Features.WantBoard.WantStore.AllForAdmin().Where(w => Eq(w.BuyerUsername, user)).ToList());
                Gather(data, included, warnings, "quests", () =>
                    Features.Quests.QuestStore.BuildSnapshot(user).Quests.Where(q => Eq(q.PosterUsername, user) || Eq(q.ClaimedByUsername, user)).ToList());
                Gather(data, included, warnings, "sites", () =>
                    Features.Sites.SiteStore.AllForApi().Where(s => Eq(s.OwnerUsername, user) || (s.Workers != null && s.Workers.Any(w => Eq(w, user)))).ToList());
                Gather(data, included, warnings, "mail", () => Features.Notifications.NotificationStore.PeekForUser(user));
                Gather(data, included, warnings, "ledger", () => TransactionLedger.ReadRecent(200, user));

                bool inGuild = false;
                try { inGuild = !string.IsNullOrEmpty(Features.Guilds.GuildStore.CurrentGuildOf(user)); } catch { }

                // Legacy/partial item state can't be proven on restore -> require admin review.
                bool unprovenItems = false;
                try
                {
                    foreach (JProperty it in (data["treasury"]?["items"] as JObject)?.Properties() ?? System.Linq.Enumerable.Empty<JProperty>())
                    {
                        Util.ItemKey.Split(it.Name, out string idef, out _, out _);
                        if (!Items.KmhItemSafety.IsSimpleGeneratedResource(idef)) { unprovenItems = true; break; }
                    }
                    if (!unprovenItems)
                        foreach (JToken pt in (data["treasury"]?["item_payloads"] as JArray) ?? new JArray())
                        {
                            string fid = (string)pt["fidelity"] ?? "";
                            if (((bool?)pt["legacy"] ?? false) || fid != Items.KmhThingPayload.FidelityFull) { unprovenItems = true; break; }
                        }
                }
                catch { }
                if (unprovenItems) warnings.Add("snapshot contains legacy/partial item(s) whose exact state can't be proven on restore");
                int pendingDeposits = 0;
                try { pendingDeposits = (data["treasury"]?["pending_deposits"] as JArray)?.Count ?? 0; } catch { }
                if (pendingDeposits > 0) warnings.Add($"snapshot contains {pendingDeposits} pending (unconfirmed) deposit(s) - not spendable; reconcile against the client save, don't mint");
                bool review = inGuild || unprovenItems || pendingDeposits > 0;

                string leaf = SanitizePart(user);
                if (string.Equals(leaf, "_server", StringComparison.OrdinalIgnoreCase)) leaf += "_p";   // don't collide with the reserved server folder
                string folder = FolderName(matchTimestamp);
                dir = Path.Combine(KmhDataPaths.SnapshotsRoot, Season(), leaf, folder);

                JObject snap = new JObject
                {
                    ["schema_version"]        = SchemaVersion,
                    ["kmh_build"]             = SubProtocol.KmhProtocol.BuildVersion,
                    ["season"]                = Season(),
                    ["kind"]                  = "player",
                    ["player_id"]             = user,
                    ["server_name"]           = KmhServerIdentity.Name,
                    ["server_id"]             = KmhServerIdentity.Id,
                    ["generated_utc"]         = DateTime.UtcNow.ToString("o"),
                    ["match_timestamp"]       = folder,
                    ["source_save_timestamp"] = sourceSaveTimestamp ?? "",
                    ["in_guild"]              = inGuild,
                    ["data"]                  = data,
                };

                WriteWithManifest(dir, "kmh_player_snapshot.json", "kmh_player_manifest.json", snap, "player",
                    playerId: user, matchTs: folder, sourceSaveTs: sourceSaveTimestamp, includedStores: included,
                    sharedStateInvolved: inGuild, requiresAdminReview: review, warnings: warnings);
                RaiseCreated("player", user, folder, dir);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Server snapshot + one player snapshot per known player. Returns count written.
        public static int SnapshotAll(string matchTimestamp, out string serverDir, List<string> failures)
        {
            int written = 0;
            if (SnapshotServer(matchTimestamp, null, out serverDir, out string sErr)) written++;
            else failures?.Add($"server: {sErr}");

            List<string> users = new List<string>();
            try { foreach (var e in Features.PlayerStats.PlayerStatsStore.BuildSnapshot().Entries) if (!string.IsNullOrEmpty(e.Username)) users.Add(e.Username); }
            catch (Exception ex) { failures?.Add($"enumerate players: {ex.Message}"); }

            foreach (string u in users)
            {
                if (SnapshotPlayer(u, matchTimestamp, null, out _, out string pErr)) written++;
                else failures?.Add($"{u}: {pErr}");
            }
            return written;
        }

        // ---- verify: files must parse and match the manifest hashes ----

        public static bool Verify(string dir, out string detail)
        {
            detail = null;
            try
            {
                if (!Directory.Exists(dir)) { detail = "folder not found"; return false; }
                string manifestPath = Directory.GetFiles(dir, "*_manifest.json").FirstOrDefault();
                if (manifestPath == null) { detail = "no manifest"; return false; }
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                JArray files = manifest["files"] as JArray;
                if (files == null) { detail = "manifest has no files list"; return false; }
                foreach (JToken f in files)
                {
                    string name = (string)f["name"];
                    string want = (string)f["sha256"];
                    string p = Path.Combine(dir, name ?? "");
                    if (!File.Exists(p)) { detail = $"missing {name}"; return false; }
                    string got = Sha256Hex(File.ReadAllText(p));
                    if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) { detail = $"checksum mismatch on {name}"; return false; }
                    JToken.Parse(File.ReadAllText(p));
                }
                detail = $"ok ({files.Count} file(s), schema v{(int?)manifest["schema_version"] ?? 0})";
                return true;
            }
            catch (Exception ex) { detail = ex.Message; return false; }
        }

        // ---- retention ----

        private static DateTime _lastPrune = DateTime.MinValue;

        // Drop leaves past the age cap, then trim each player/_server to the newest N. Throttled ~15 min, fully guarded.
        public static void PruneOldIfDue()
        {
            if ((DateTime.UtcNow - _lastPrune).TotalMinutes < 15) return;
            _lastPrune = DateTime.UtcNow;

            int days = Maintenance.MaintenanceConfig.Current.SnapshotRetentionDays;
            int maxPer = Maintenance.MaintenanceConfig.Current.SnapshotMaxPerPlayer;
            if (days <= 0 && maxPer <= 0) return;

            try
            {
                if (!Directory.Exists(KmhDataPaths.SnapshotsRoot)) return;
                DateTime cutoff = DateTime.UtcNow.AddDays(-days);
                int pruned = 0;

                foreach (string seasonDir in Directory.GetDirectories(KmhDataPaths.SnapshotsRoot))
                foreach (string leafDir in Directory.GetDirectories(seasonDir))   // <PlayerId> or _server
                {
                    List<(string path, DateTime when)> stamps = new List<(string, DateTime)>();
                    foreach (string ts in Directory.GetDirectories(leafDir))
                        stamps.Add((ts, LeafTime(ts)));
                    stamps.Sort((a, b) => b.when.CompareTo(a.when));   // newest first

                    for (int i = 0; i < stamps.Count; i++)
                    {
                        bool tooOld  = days > 0 && stamps[i].when < cutoff;
                        bool overCap = maxPer > 0 && i >= maxPer;
                        if (tooOld || overCap) { try { Directory.Delete(stamps[i].path, true); pruned++; } catch { } }
                    }
                }
                if (pruned > 0) ServerLog.Info($"Pruned {pruned} old snapshot folder(s).");
            }
            catch (Exception ex) { ServerLog.Warn($"Snapshot prune skipped: {ex.Message}"); }
        }

        private static DateTime LeafTime(string dir)
        {
            string name = Path.GetFileName(dir);
            if (DateTime.TryParseExact(name, "yyyy-MM-dd_HH-mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime t))
                return t;
            try { return Directory.GetLastWriteTimeUtc(dir); } catch { return DateTime.UtcNow; }
        }

        // ---- request marker ----

        // One request per line: "player <user> [ts]" | "server [ts]" | "all [ts]" (# = comment). Deleted after use.
        public static void ConsumePendingRequests()
        {
            string marker = KmhDataPaths.SnapshotRequestFile;
            string[] lines;
            try { if (!File.Exists(marker)) return; lines = File.ReadAllLines(marker); }
            catch (Exception ex) { ServerLog.Warn($"Snapshot request unreadable: {ex.Message}"); return; }
            try { File.Delete(marker); } catch { }

            int done = 0;
            foreach (string raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string kind = p[0].ToLowerInvariant();
                try
                {
                    if (kind == "player" && p.Length >= 2) { SnapshotPlayer(p[1], p.Length > 2 ? p[2] : null, null, out _, out _); done++; }
                    else if (kind == "server")             { SnapshotServer(p.Length > 1 ? p[1] : null, null, out _, out _); done++; }
                    else if (kind == "all")                { SnapshotAll(p.Length > 1 ? p[1] : null, out _, null); done++; }
                    else ServerLog.Warn($"Snapshot request: unknown line '{line}'");
                }
                catch (Exception ex) { ServerLog.Warn($"Snapshot request '{line}' failed: {ex.Message}"); }
            }
            if (done > 0) ServerLog.Info($"Snapshot requests consumed ({done}).");
        }

        // ---- restore preview (read-only) ----

        public static void PreviewPlayer(string dir, Action<string> reply)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) { reply("Usage: kmh restore-preview-player <snapshot-folder>"); return; }
                string snapPath = Path.Combine(dir, "kmh_player_snapshot.json");
                if (!File.Exists(snapPath)) { reply($"No kmh_player_snapshot.json in {dir}"); return; }

                JObject snap = JObject.Parse(File.ReadAllText(snapPath));
                string user = (string)snap["player_id"] ?? "";
                if (string.IsNullOrEmpty(user)) { reply("Snapshot has no player_id."); return; }
                JObject data = snap["data"] as JObject ?? new JObject();

                reply($"=== restore-preview (player '{user}', snapshot {(string)snap["match_timestamp"]}) ===");

                long snapSilver = (long?)(data["treasury"]?["silver_balance"]) ?? 0;
                long curSilver  = 0;
                try { curSilver = Features.Treasury.TreasuryStore.GetSnapshotFor(user).SilverBalance; } catch { }
                long delta = snapSilver - curSilver;
                if (delta == 0)      reply($"  [safe] personal treasury unchanged ({curSilver}s).");
                else if (delta < 0)  reply($"  [safe/personal] treasury would DROP {Util.SilverFmt.Format(-delta)} ({curSilver} -> {snapSilver}s) - a rollback, no mint.");
                else                 reply($"  [REVIEW] treasury would RISE {Util.SilverFmt.Format(delta)} ({curSilver} -> {snapSilver}s) - jump-forward could MINT silver; needs approval.");

                long snapWealth = (long?)(data["standings"]?["wealth"]) ?? 0;
                reply($"  [safe] standings/wealth in snapshot: {Util.SilverFmt.Format(snapWealth)} (rebuilds from colony reports).");

                bool inGuild = (bool?)snap["in_guild"] ?? false;
                if (inGuild)
                    reply("  [SHARED] player is in a guild - guild vault / contributions are shared; restore needs guild/admin approval.");

                int listings = (data["marketplace_listings"] as JArray)?.Count ?? 0;
                int auctions = (data["auctions"] as JArray)?.Count ?? 0;
                int wants    = (data["wants"] as JArray)?.Count ?? 0;
                if (listings + auctions + wants > 0)
                    reply($"  [REVIEW] open escrows in snapshot (listings {listings}, auctions {auctions}, wants {wants}) - restoring may conflict with market state that moved on.");

                // Item-state proof: legacy compact items + partial payloads can't have their exact taint/damage/comp
                // state proven, so restoring them must never silently produce clean/full-value items.
                int legacyItems = 0, partialItems = 0, fullItems = 0;
                foreach (JProperty it in (data["treasury"]?["items"] as JObject)?.Properties() ?? System.Linq.Enumerable.Empty<JProperty>())
                {
                    Util.ItemKey.Split(it.Name, out string idef, out _, out _);
                    if (!Items.KmhItemSafety.IsSimpleGeneratedResource(idef)) legacyItems++;
                }
                foreach (JToken pt in (data["treasury"]?["item_payloads"] as JArray) ?? new JArray())
                {
                    string fid = (string)pt["fidelity"] ?? "";
                    bool leg   = (bool?)pt["legacy"] ?? false;
                    if (leg || fid == KMHServerAddon.Items.KmhThingPayload.FidelityLegacy) legacyItems++;
                    else if (fid == KMHServerAddon.Items.KmhThingPayload.FidelityMetadata) partialItems++;
                    else fullItems++;
                }
                if (fullItems > 0)
                    reply($"  [safe] {fullItems} full-state item stack(s) - exact restore.");
                if (partialItems > 0)
                    reply($"  [REVIEW] {partialItems} PARTIAL item stack(s) - deep comp/modded state unknown; restore preserves material/quality/HP/taint only.");
                if (legacyItems > 0)
                    reply($"  [REVIEW] {legacyItems} LEGACY item stack(s) (pre-payload) - taint/damage/comp state cannot be proven; must NOT be silently restored as clean/full-value.");

                // Uncommitted local deposits captured in the snapshot: never spendable, and a restore that resurrects
                // them alongside a rolled-back colony could re-open the dupe window - flag for review.
                int pendingDeps = (data["treasury"]?["pending_deposits"] as JArray)?.Count ?? 0;
                if (pendingDeps > 0)
                    reply($"  [REVIEW] {pendingDeps} PENDING (unconfirmed) deposit(s) in snapshot - not spendable; must be re-reconciled against the client's saved ledger, not minted on restore.");

                bool needsReview = inGuild || (listings + auctions + wants) > 0 || legacyItems > 0 || partialItems > 0 || pendingDeps > 0;
                reply(needsReview
                    ? "  requires_admin_review = TRUE (guild/escrow/legacy/partial/pending item state involved)."
                    : "  requires_admin_review = false (personal, fully-proven state).");
                reply("Preview only - nothing changed. Per-player restore with anti-exploit reconciliation is a deliberate follow-up (not auto-applied).");
            }
            catch (Exception ex) { reply($"Preview failed: {ex.Message}"); }
        }

        // ---- helpers ----

        private static void RaiseCreated(string kind, string playerId, string matchTs, string dir)
        {
            try
            {
                Extensibility.KmhEventBus.Instance.RaiseSnapshotCreated(new KMH.Sdk.Server.Events.SnapshotCreatedEvent
                { Kind = kind, PlayerId = playerId, Season = Season(), MatchTimestamp = matchTs, Dir = dir });
            }
            catch { }
        }

        private static void Gather(JObject into, List<string> included, List<string> warnings, string key, Func<object> get)
        {
            try
            {
                object v = get();
                into[key] = v == null ? JValue.CreateNull() : JToken.FromObject(v, JsonSerializer.Create(JsonSettings));
                included.Add(key);
            }
            catch (Exception ex) { warnings.Add($"{key} skipped: {ex.Message}"); into[key] = JValue.CreateNull(); }
        }

        private static void WriteWithManifest(string dir, string snapName, string manifestName, JObject snap, string kind,
            string playerId, string matchTs, string sourceSaveTs, List<string> includedStores,
            bool sharedStateInvolved, bool requiresAdminReview, List<string> warnings)
        {
            Directory.CreateDirectory(dir);

            string snapJson = JsonConvert.SerializeObject(snap, JsonSettings);
            WriteAtomic(Path.Combine(dir, snapName), snapJson);

            JObject manifest = new JObject
            {
                ["schema_version"]        = SchemaVersion,
                ["kmh_build"]             = SubProtocol.KmhProtocol.BuildVersion,
                ["season"]                = Season(),
                ["kind"]                  = kind,
                ["player_id"]             = playerId ?? "",
                ["server_name"]           = KmhServerIdentity.Name,
                ["server_id"]             = KmhServerIdentity.Id,
                ["generated_utc"]         = DateTime.UtcNow.ToString("o"),
                ["match_timestamp"]       = matchTs,
                ["source_save_timestamp"] = sourceSaveTs ?? "",
                ["included_stores"]       = new JArray(includedStores),
                ["shared_state_involved"] = sharedStateInvolved,
                ["requires_admin_review"] = requiresAdminReview,
                ["files"]                 = new JArray(new JObject
                {
                    ["name"]   = snapName,
                    ["sha256"] = Sha256Hex(snapJson),
                    ["bytes"]  = Encoding.UTF8.GetByteCount(snapJson),
                }),
                ["warnings"]              = new JArray(warnings ?? new List<string>()),
                ["errors"]                = new JArray(),
            };
            WriteAtomic(Path.Combine(dir, manifestName), JsonConvert.SerializeObject(manifest, JsonSettings));
        }

        private static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";   // unique so concurrent writers don't clash
            try { File.WriteAllText(tmp, content); File.Move(tmp, path, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }

        private static string Sha256Hex(string s)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string SanitizePart(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            string r = sb.ToString().Trim('.', ' ');
            return r.Length == 0 ? "_" : r;
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
