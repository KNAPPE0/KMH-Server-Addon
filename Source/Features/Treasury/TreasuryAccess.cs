using System;
using System.Collections.Concurrent;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Treasury
{
    // Billed on an explicit open only: kmh.treasury.request is also the auto-refresh timer, so billing it charges idle windows.
    internal static class TreasuryAccess
    {
        // Fixed, not configurable: the lease stops a reconnect double-charge, and a tunable one gets set low enough to undo it.
        internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

        private static readonly ConcurrentDictionary<string, long> _until =
            new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Leases opened free on an empty vault; the fee is still owed once the vault can pay it.
        private static readonly ConcurrentDictionary<string, byte> _unpaid =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private static string Key(string username) => (username ?? "").ToLowerInvariant();

        internal static bool Required => Economy.EconomyConfig.Current.Treasury.AccessFeeSilver > 0;

        public static bool Has(string username, long nowTicks)
            => !Required
            || (!string.IsNullOrEmpty(username) && _until.TryGetValue(Key(username), out long until) && nowTicks < until);

        public static bool Has(string username) => Has(username, DateTime.UtcNow.Ticks);

        public static long ExpiresAtUnix(string username)
            => _until.TryGetValue(Key(username), out long until)
             ? new DateTimeOffset(new DateTime(until, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;

        internal static void Grant(string username, long nowTicks, bool paid = true)
        {
            if (string.IsNullOrEmpty(username)) return;
            _until[Key(username)] = nowTicks + Lease.Ticks;
            if (paid) _unpaid.TryRemove(Key(username), out _);
            else      _unpaid[Key(username)] = 0;
        }

        internal static void Forget(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            _until.TryRemove(Key(username), out _);
            _unpaid.TryRemove(Key(username), out _);
        }

        internal static void ClearForTest() { _until.Clear(); _unpaid.Clear(); }

        // Billed at the first move the vault can pay for, or emptying it into escrow before opening skips the fee.
        // Removing the entry is the claim, so two moves arriving together charge it once.
        public static int SettleWaived(string username)
        {
            if (!Required || string.IsNullOrEmpty(username)) return 0;
            if (!_unpaid.TryRemove(Key(username), out _)) return 0;
            int fee = Economy.EconomyConfig.Current.Treasury.AccessFeeSilver;
            if (TreasuryStore.GetPersonalSilver(username) < fee
                || !TreasuryStore.ChargeHouseFee(username, fee, "treasury access fee"))
            {
                _unpaid[Key(username)] = 0;
                return 0;
            }
            ServerLog.Info($"Treasury: {username} paid the {fee}s access fee on first use of a lease opened on an empty vault.");
            return fee;
        }

        internal enum Result { AlreadyOpen, Opened, Free, Waived, InsufficientSilver }

        // Charged from the vault being opened, so it cannot half-apply. An empty vault opens without paying, or a
        // player's first deposit would be locked behind a fee they have no silver to pay; SettleWaived bills it later.
        public static Result Open(string username, long nowTicks, out int charged, out long balance)
        {
            charged = 0;
            balance = TreasuryStore.GetPersonalSilver(username);
            if (string.IsNullOrEmpty(username)) return Result.InsufficientSilver;

            int fee = Economy.EconomyConfig.Current.Treasury.AccessFeeSilver;
            if (fee <= 0) return Result.Free;

            // Before the debit, so a retry that arrives inside the window is answered, never billed again.
            if (Has(username, nowTicks)) return Result.AlreadyOpen;

            if (balance <= 0) { Grant(username, nowTicks, paid: false); return Result.Waived; }
            if (balance < fee) return Result.InsufficientSilver;

            // Debit and owed fee in one commit, so a crash before the pool's save cannot lose it.
            if (!TreasuryStore.ChargeHouseFee(username, fee, "treasury access fee")) return Result.InsufficientSilver;
            charged = fee;
            balance = TreasuryStore.GetPersonalSilver(username);
            Grant(username, nowTicks);
            ServerLog.Info($"Treasury: {username} opened an access session for {fee}s (fee -> house pool), valid {(int)Lease.TotalMinutes}m.");
            return Result.Opened;
        }
    }
}
