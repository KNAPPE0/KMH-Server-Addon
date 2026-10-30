using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Moderation
{
    internal sealed class EconomyFreeze
    {
        public string Username     { get; set; } = "";
        public string Reason       { get; set; } = "";
        public string By           { get; set; } = "";
        public long   AtUnix       { get; set; }
        // Set by 'kmh freeze <user> undo confirm', so a second undo says so instead of clawing back twice.
        public long   UndoneAtUnix { get; set; }
    }

    internal sealed class DepositAllowance
    {
        public string Username  { get; set; } = "";
        public long   Extra     { get; set; }
        public long   UntilUnix { get; set; }
        public string By        { get; set; } = "";
    }

    internal sealed class DepositHour
    {
        public string Username { get; set; } = "";
        public long   Hour     { get; set; }
        public long   Value    { get; set; }
    }

    internal sealed class EconomyGuardFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<EconomyFreeze>    Frozen     { get; set; } = new List<EconomyFreeze>();
        public List<DepositAllowance> Allowances { get; set; } = new List<DepositAllowance>();
        public List<DepositHour>      Deposits   { get; set; } = new List<DepositHour>();
    }

    // A client decides what its colony holds, so a memory editor can mint goods. This bounds what one account can bank
    // per day and lets staff freeze an account's value moves; it cannot stop the minting itself.
    internal static class KmhEconomyGuard
    {
        private static readonly object _lock = new object();
        private static EconomyGuardFile _file;
        private static bool _dirty;
        private static long _nowForTest;

        internal const string FrozenRefusal =
            "Staff have paused deposits, trades and transfers on your account while they look into it. Chat and reports still work.";

        // Items unknown to the catalog still count: a minted modded item is as unknown as an honest one.
        internal const long UnknownItemUnitValue = 10;

        private static EconomyGuardFile File_
        {
            get
            {
                lock (_lock)
                {
                    if (_file != null) return _file;
                    _file = JsonFileStore.TryLoad(KmhDataPaths.EconomyGuardFile, out EconomyGuardFile loaded) && loaded != null
                          ? loaded : new EconomyGuardFile();
                    _file.Frozen     = _file.Frozen     ?? new List<EconomyFreeze>();
                    _file.Allowances = _file.Allowances ?? new List<DepositAllowance>();
                    _file.Deposits   = _file.Deposits   ?? new List<DepositHour>();
                    return _file;
                }
            }
        }

        public static void Reload() { lock (_lock) { _file = null; _dirty = false; } }

        private static bool SaveLocked()
        {
            try
            {
                if (!JsonFileStore.Save(KmhDataPaths.EconomyGuardFile, _file)) return false;
                _dirty = false;
                return true;
            }
            catch (Exception ex) { ServerLog.Warn($"Could not save EconomyGuard.json: {ex.Message}"); return false; }
        }

        // Retried by the periodic flush; deposit tallies are written there rather than on every deposit.
        public static bool SaveToDisk() { lock (_lock) return _file == null || !_dirty || SaveLocked(); }

        internal static void ResetForTest() { lock (_lock) { _file = new EconomyGuardFile(); _dirty = false; } _nowForTest = 0; }
        internal static long NowForTest { set => _nowForTest = value; }
        private static long Now() => _nowForTest != 0 ? _nowForTest : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Matched like bans: a freeze on "Bob" holds for "bob ".
        private static bool Same(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        private static long Add(long a, long b) { long s = a + b; return ((a ^ s) & (b ^ s)) < 0 ? long.MaxValue : s; }
        private static long Mul(long a, long b) => a <= 0 || b <= 0 ? 0 : (a > long.MaxValue / b ? long.MaxValue : a * b);

        internal static long UnitValue(string defOrKey)
        {
            string def = (defOrKey ?? "").Split('|')[0];
            long v = def.Length == 0 ? 0 : ItemLabels.ItemLabelCache.BaseValue(def);
            return v > 0 ? v : UnknownItemUnitValue;
        }

        public static long ValueOfItem(string defOrKey, long qty) => Mul(UnitValue(defOrKey), qty);

        public static long ValueOfPayloads(IEnumerable<Items.KmhThingPayload> payloads)
        {
            long sum = 0;
            if (payloads != null)
                foreach (Items.KmhThingPayload p in payloads)
                    if (p != null) sum = Add(sum, ValueOfItem(p.DefName, p.StackCount));
            return sum;
        }

        // 0 = no limit. Simulated accounts are run by the server itself, so they are never limited.
        public static long LimitFor(string user, out bool newPlayer)
        {
            newPlayer = false;
            if (string.IsNullOrWhiteSpace(user) || Simulation.SimulatedAccounts.IsSimulated(user)) return 0;
            Economy.AntiCheatRules ac = Economy.EconomyConfig.Current.AntiCheat;
            // Connected time is measured by the server; the client's own play-time figure is not trusted here.
            newPlayer = ac.NewPlayerHours > 0
                     && PlayerStats.PlayerStatsStore.ConnectedSecondsOf(user) < ac.NewPlayerHours * 3600L;
            return newPlayer && ac.NewPlayerDailyDepositLimit > 0 ? ac.NewPlayerDailyDepositLimit : ac.DailyDepositLimit;
        }

        public static long DepositedLast24h(string user)
        {
            long hour = Now() / 3600, sum = 0;
            lock (_lock)
                foreach (DepositHour d in File_.Deposits)
                    if (d != null && d.Hour > hour - 24 && Same(d.Username, user)) sum = Add(sum, d.Value);
            return sum;
        }

        public static long AllowanceFor(string user)
        {
            long now = Now(), sum = 0;
            lock (_lock)
                foreach (DepositAllowance a in File_.Allowances)
                    if (a != null && a.UntilUnix > now && Same(a.Username, user)) sum = Add(sum, a.Extra);
            return sum;
        }

        // Counted when the server accepts the deposit, so one never confirmed still counts: the limit errs toward caution.
        public static void Record(string user, long value)
        {
            if (string.IsNullOrWhiteSpace(user) || value <= 0) return;
            long hour = Now() / 3600;
            lock (_lock)
            {
                List<DepositHour> all = File_.Deposits;
                all.RemoveAll(d => d == null || d.Hour <= hour - 25);
                DepositHour cur = all.FirstOrDefault(d => d.Hour == hour && Same(d.Username, user));
                if (cur == null) all.Add(new DepositHour { Username = user.Trim(), Hour = hour, Value = value });
                else cur.Value = Add(cur.Value, value);
                _dirty = true;
            }
        }

        internal const string AtPreflight  = "refused before anything left their colony.";
        internal const string NoApproval   = "sent WITHOUT an approval (modified client?) - refused, nothing credited.";
        internal const string PastApproval = "sent more than its approval covered (modified client?) - refused, nothing credited.";

        // reserved is value approved but not yet sent, so parallel approvals cannot each fit under the limit.
        public static bool AllowsDeposit(string user, long value, long reserved, string what, string outcome, out string reason)
        {
            reason = null;
            if (value <= 0) return true;
            long limit = LimitFor(user, out bool newPlayer);
            if (limit <= 0) return true;
            long used = Add(DepositedLast24h(user), Math.Max(0, reserved));
            long cap  = Add(limit, AllowanceFor(user));
            if (Add(used, value) <= cap) return true;

            Economy.AntiCheatRules ac = Economy.EconomyConfig.Current.AntiCheat;
            reason = $"That deposit would pass this server's daily deposit limit{(newPlayer ? $" for new players (your first {ac.NewPlayerHours} hours here)" : "")}: "
                   + $"{Util.SilverFmt.Format(used)} of {Util.SilverFmt.Format(cap)} used in the last 24 hours, and this one is worth {Util.SilverFmt.Format(value)}. "
                   + "It stays in your colony; staff have been told and can raise your limit.";
            long hours = PlayerStats.PlayerStatsStore.ConnectedSecondsOf(user) / 3600;
            AlertStaff("limit:" + user.Trim().ToLowerInvariant(),
                $"Deposit limit: {user} ({(newPlayer ? "new player, " : "")}{hours}h connected) tried to deposit {what} worth {Util.SilverFmt.Format(value)} "
              + $"with {Util.SilverFmt.Format(used)} of {Util.SilverFmt.Format(cap)} already used in 24h - "
              + outcome
              + $" Raise it: kmh deposit-limit {user} <extra> | Freeze: kmh freeze {user} <reason>");
            return false;
        }

        public static void Allow(string user, long extra, int hours, string by, out bool saved)
        {
            saved = false;
            if (string.IsNullOrWhiteSpace(user) || extra <= 0 || hours <= 0) return;
            long now = Now();
            lock (_lock)
            {
                File_.Allowances.RemoveAll(a => a == null || a.UntilUnix <= now);
                File_.Allowances.Add(new DepositAllowance { Username = user.Trim(), Extra = extra, UntilUnix = now + hours * 3600L, By = by ?? "" });
                _dirty = true;
                saved = SaveLocked();
            }
        }

        public static bool IsFrozen(string user) => FreezeOf(user) != null;

        public static EconomyFreeze FreezeOf(string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return null;
            lock (_lock)
            {
                EconomyFreeze f = File_.Frozen.FirstOrDefault(x => x != null && Same(x.Username, user));
                return f == null ? null : Copy(f);
            }
        }

        public static List<EconomyFreeze> Frozen()
        {
            lock (_lock)
                return File_.Frozen.Where(f => f != null).Select(f => Copy(f)).ToList();
        }

        private static EconomyFreeze Copy(EconomyFreeze f)
            => new EconomyFreeze { Username = f.Username, Reason = f.Reason, By = f.By, AtUnix = f.AtUnix, UndoneAtUnix = f.UndoneAtUnix };

        // False when already frozen. A freeze that could not be written still holds this session; saved=false says so.
        public static bool Freeze(string user, string reason, string by, out bool saved)
        {
            saved = false;
            if (string.IsNullOrWhiteSpace(user)) return false;
            lock (_lock)
            {
                if (File_.Frozen.Any(f => f != null && Same(f.Username, user))) { saved = true; return false; }
                File_.Frozen.Add(new EconomyFreeze { Username = user.Trim(), Reason = reason ?? "", By = by ?? "", AtUnix = Now() });
                _dirty = true;
                saved = SaveLocked();
            }
            return true;
        }

        // An unfreeze nobody wrote down would come back on restart, so a failed save puts the entry back.
        public static bool Unfreeze(string user, out bool saved)
        {
            saved = true;
            if (string.IsNullOrWhiteSpace(user)) return false;
            lock (_lock)
            {
                List<EconomyFreeze> before = File_.Frozen;
                bool wasDirty = _dirty;
                File_.Frozen = before.Where(f => f != null && !Same(f.Username, user)).ToList();
                if (File_.Frozen.Count == before.Count) return false;
                _dirty = true;
                if (SaveLocked()) return true;
                File_.Frozen = before;
                _dirty = wasDirty;
                saved = false;
                return false;
            }
        }

        public static bool MarkUndone(string user, out bool saved)
        {
            saved = false;
            lock (_lock)
            {
                EconomyFreeze f = File_.Frozen.FirstOrDefault(x => x != null && Same(x.Username, user));
                if (f == null) return false;
                f.UndoneAtUnix = Now();
                _dirty = true;
                saved = SaveLocked();
                return true;
            }
        }

        // What a frozen account may still send: requests that move no value. A kind not listed here is refused.
        internal static bool FrozenMayStill(string kind)
        {
            if (Maintenance.KmhMutationClass.IsRead(kind)) return true;
            if (kind.StartsWith("kmh.chat.", StringComparison.Ordinal)
                || kind.StartsWith("kmh.enforcement.", StringComparison.Ordinal)
                || kind.StartsWith("kmh.config.", StringComparison.Ordinal)) return true;
            switch (kind)
            {
                // Settle deposits made before the freeze; reconcile is also how a loaded older save is caught.
                case KmhProtocol.Kind.TreasuryDepositConfirm:
                case KmhProtocol.Kind.TreasuryDepositReconcile:
                case KmhProtocol.Kind.ColonyReport:
                case KmhProtocol.Kind.PlayerActive:
                case KmhProtocol.Kind.ConditionDefs:
                case KmhProtocol.Kind.DebugLog:
                case KmhProtocol.Kind.NotifyAck:
                case KmhProtocol.Kind.DeliveryAck:
                case KmhProtocol.Kind.MailMarkRead:
                case KmhProtocol.Kind.MailDelete:
                case KmhProtocol.Kind.MailDecline:
                case KmhProtocol.Kind.GuildLeave:
                case KmhProtocol.Kind.GuildDeclineInvite:
                case KmhProtocol.Kind.LinkRequest:
                case KmhProtocol.Kind.JoinTicket:
                case KmhProtocol.Kind.FrontierPlacementPropose:
                    return true;
                default:
                    return false;
            }
        }

        public static string RefusalFor(string user, string kind)
            => !string.IsNullOrEmpty(kind) && !FrozenMayStill(kind) && IsFrozen(user) ? FrozenRefusal : null;

        private static readonly object _alertLock = new object();
        private static Util.KmhRateWindow _alertKeys = new Util.KmhRateWindow();
        private static Util.KmhRateWindow _alertAll  = new Util.KmhRateWindow();
        internal static Action<string> AlertForTest;

        // Always logged; posted to the Discord admin channel at most once per key per 30 minutes and 12 times an hour.
        internal static void AlertStaff(string key, string text)
        {
            ServerLog.Warn("Anti-cheat: " + text);
            long now = DateTime.UtcNow.Ticks;
            bool post;
            lock (_alertLock) post = _alertKeys.Allow(key ?? "", now, 1, 1800) && _alertAll.Allow("all", now, 12, 3600);
            if (!post || !Economy.EconomyConfig.Current.AntiCheat.AlertStaff) return;
            AlertForTest?.Invoke(text);
            Discord.DiscordConfig cfg = Discord.DiscordBridge.Config;
            if (cfg == null || !cfg.IsEnabled || !cfg.PostAlerts) return;
            Discord.DiscordBridge.PostToChannel(cfg.AdminChannelId, "[Colony Nexus] " + text);
        }

        internal static void ResetAlertsForTest()
        {
            lock (_alertLock) { _alertKeys = new Util.KmhRateWindow(); _alertAll = new Util.KmhRateWindow(); }
        }
    }
}
