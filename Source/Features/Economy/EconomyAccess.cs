using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Economy
{
    // Client-reported context for a treasury action. The standalone server can't see the game, so (like deposit
    // amounts) these are trusted flags the client sends. Access modes are soft server policy on top of them; a
    // modified client could spoof context, which is inherent to the design. Known=false when no context was sent
    // (e.g. an old client or a chat command) - the checks then fall back to permissive rather than break.
    internal readonly struct EconomyContext
    {
        public readonly bool Known;
        public readonly bool HasColony;
        public readonly bool HasCaravan;
        public readonly bool InRaid;
        public readonly bool HostileEvent;
        public readonly bool NearGuildHall;     // future (P8) - always false until Guild Halls exist
        public readonly bool NearTreasurySite;  // future - always false until Treasury Sites exist

        public EconomyContext(bool known, bool colony, bool caravan, bool raid, bool hostile, bool guildHall, bool site)
        { Known = known; HasColony = colony; HasCaravan = caravan; InRaid = raid; HostileEvent = hostile; NearGuildHall = guildHall; NearTreasurySite = site; }

        public static EconomyContext FromEnvelope(KmhEnvelope env)
        {
            if (env == null || env.Data == null || env.Data["ctx_known"] == null) return default;
            return new EconomyContext(
                env.GetBool("ctx_known"),
                env.GetBool("ctx_has_colony"),
                env.GetBool("ctx_has_caravan"),
                env.GetBool("ctx_in_raid"),
                env.GetBool("ctx_hostile_event"),
                env.GetBool("ctx_near_guild_hall"),
                env.GetBool("ctx_near_treasury_site"));
        }
    }

    // Centralized treasury access gate. EVERY economy action that moves treasury value routes its allow/deny check
    // here so the mode is enforced in one place. Pending-deposit safety is separate and always applies - access
    // checks never make pending value spendable.
    internal static class EconomyAccess
    {
        // Guild Halls are implemented: GuildHallRequired / CaravanNearGuildHall now enforce via the client's
        // near_guild_hall context (true when the guild has no hall, so pre-P8 guilds keep working). Treasury Sites are
        // still a future system, so those modes fail gracefully (allow + one-time owner warning) instead of locking
        // people out. static readonly (not const) so the fallback branches don't compile to "unreachable code".
        private static readonly bool GuildHallsImplemented   = true;
        private static readonly bool TreasurySitesImplemented = false;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, long> _lastDeposit  = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> _lastWithdraw = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Queue<long>> _recent = new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _warnedUnsupported = new HashSet<string>(StringComparer.Ordinal);

        private const long HugeSilverThreshold = 5_000_000;   // audit-warn a single deposit/withdraw at or above this

        public static EconomyPolicy Policy => EconomyConfig.Current.ResolvePolicy();

        // Master gate. isWithdraw = withdraw vs deposit; isGuild = guild vault vs personal; isItem = item move (vs
        // silver). Returns false + a player-facing reason when denied.
        public static bool CheckAccess(string username, bool isWithdraw, bool isGuild, bool isItem, EconomyContext ctx, out string reason)
        {
            reason = null;
            EconomyPolicy p = Policy;

            // Danger blocks - only when the client actually reported context.
            if (ctx.Known)
            {
                if (p.BlockDuringRaid && ctx.InRaid) { reason = "Treasury is blocked during raids or hostile map events."; return false; }
                if (p.BlockDuringHostileEvent && ctx.HostileEvent) { reason = "Treasury is blocked during raids or hostile map events."; return false; }
            }

            if (isGuild) { if (!CheckGuild(p.GuildAccess, ctx, out reason)) return false; }
            else         { if (!CheckPersonal(p.PersonalAccess, ctx, out reason)) return false; }

            if (isItem && ctx.Known)
            {
                bool req = isWithdraw ? p.RequireCaravanForItemWithdraw : p.RequireCaravanForItemDeposit;
                if (req && !ctx.HasCaravan) { reason = "Moving items to/from the treasury requires a caravan."; return false; }
            }
            return true;
        }

        private static bool CheckPersonal(string mode, EconomyContext ctx, out string reason)
        {
            reason = null;
            switch ((mode ?? "Remote").ToLowerInvariant())
            {
                case "disabled":   reason = "Personal treasury is disabled on this server."; return false;
                case "colonyonly": if (ctx.Known && !ctx.HasColony)  { reason = "Treasury access requires a colony map."; return false; } return true;
                case "caravanonly":if (ctx.Known && !ctx.HasCaravan) { reason = "Treasury access requires a caravan.";   return false; } return true;
                case "treasurysiterequired":
                    if (!TreasurySitesImplemented) { WarnUnsupportedOnce("PersonalTreasuryAccessMode=TreasurySiteRequired"); return true; }
                    if (ctx.Known && !ctx.NearTreasurySite) { reason = "Treasury access requires a Treasury Site."; return false; }
                    return true;
                default: return true;   // Remote
            }
        }

        private static bool CheckGuild(string mode, EconomyContext ctx, out string reason)
        {
            reason = null;
            switch ((mode ?? "Remote").ToLowerInvariant())
            {
                case "disabled": reason = "Guild treasury is disabled on this server."; return false;
                case "guildhallrequired":
                    if (!GuildHallsImplemented) { WarnUnsupportedOnce("GuildTreasuryAccessMode=GuildHallRequired"); return true; }
                    if (ctx.Known && !ctx.NearGuildHall) { reason = "Guild treasury requires Guild Hall access."; return false; }
                    return true;
                case "caravannearguildhall":
                    if (!GuildHallsImplemented) { WarnUnsupportedOnce("GuildTreasuryAccessMode=CaravanNearGuildHall"); return true; }
                    if (ctx.Known && (!ctx.HasCaravan || !ctx.NearGuildHall)) { reason = "Guild treasury requires a caravan near your Guild Hall."; return false; }
                    return true;
                default: return true;   // Remote
            }
        }

        private static void WarnUnsupportedOnce(string what)
        {
            lock (_lock) { if (!_warnedUnsupported.Add(what)) return; }
            ServerLog.Warn($"Economy: {what} is set, but that physical system isn't implemented yet - treating treasury access as Remote until it ships. Use a different mode to enforce now.");
        }

        // --- cooldowns (read then record-on-success, so a denied/failed action doesn't start the timer) ---

        public static bool OnCooldown(string username, bool isWithdraw, out int remainingSec)
        {
            remainingSec = 0;
            EconomyPolicy p = Policy;
            int cd = isWithdraw ? p.WithdrawCooldownSec : p.DepositCooldownSec;
            if (cd <= 0 || string.IsNullOrEmpty(username)) return false;
            lock (_lock)
            {
                Dictionary<string, long> map = isWithdraw ? _lastWithdraw : _lastDeposit;
                if (!map.TryGetValue(username, out long last)) return false;
                long elapsedSec = (DateTime.UtcNow.Ticks - last) / TimeSpan.TicksPerSecond;
                if (elapsedSec >= cd) return false;
                remainingSec = (int)(cd - elapsedSec);
                return true;
            }
        }

        public static void RecordAction(string username, bool isWithdraw)
        {
            if (string.IsNullOrEmpty(username)) return;
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                (isWithdraw ? _lastWithdraw : _lastDeposit)[username] = now;
            }
        }

        // --- silver caps (deposit/contribution) ---

        // True (+reason) when crediting `add` to a `current` balance would exceed the configured cap.
        public static bool WouldExceedCap(bool isGuild, long current, long add, out string reason)
        {
            reason = null;
            EconomyPolicy p = Policy;
            long max = isGuild ? p.MaxGuildSilver : p.MaxPersonalSilver;
            if (max <= 0) return false;
            if (current + add <= max) return false;
            reason = $"That would put the {(isGuild ? "guild" : "personal")} treasury over its {Util.SilverFmt.Format(max)} silver cap.";
            return true;
        }

        // --- fees (silver only; a treasury service charge removed from circulation) ---

        public static int DepositFeeOn(int amount)  => FeeOn(amount, Policy.DepositFeePct);
        public static int WithdrawFeeOn(int amount) => FeeOn(amount, Policy.WithdrawFeePct);
        private static int FeeOn(int amount, double pct)
        {
            if (amount <= 0 || pct <= 0) return 0;
            int fee = (int)Math.Floor(amount * pct / 100.0);
            return Math.Max(0, Math.Min(amount - 1, fee));   // never fee the whole amount away
        }

        // --- audit: flag suspicious remote-treasury patterns (advisory logging only) ---

        public static void Audit(string username, bool isWithdraw, bool isGuild, long amount)
        {
            if (string.IsNullOrEmpty(username)) return;
            EconomyPolicy p = Policy;
            if (amount >= HugeSilverThreshold)
                ServerLog.Warn($"Economy audit: {username} {(isWithdraw ? "withdrew" : "deposited")} {Util.SilverFmt.Format(amount)} " +
                    $"({(isGuild ? "guild" : "personal")}) - large movement (mode {p.Mode}).");
            if (p.IsStrict && !isWithdraw && !isGuild && amount >= HugeSilverThreshold / 5)
                ServerLog.Warn($"Economy audit: {username} moved {Util.SilverFmt.Format(amount)} into remote treasury under strict mode {p.Mode}.");

            long now = DateTime.UtcNow.Ticks;
            long windowStart = now - TimeSpan.FromSeconds(60).Ticks;
            int count;
            lock (_lock)
            {
                if (!_recent.TryGetValue(username, out Queue<long> q)) { q = new Queue<long>(); _recent[username] = q; }
                q.Enqueue(now);
                while (q.Count > 0 && q.Peek() < windowStart) q.Dequeue();
                count = q.Count;
            }
            if (count >= 8)
                ServerLog.Warn($"Economy audit: {username} made {count} treasury actions in 60s - possible deposit/withdraw loop.");
        }

        // Short human description of the active policy (startup banner + audit report).
        public static string Describe()
        {
            EconomyPolicy p = Policy;
            return $"EconomyMode={p.Mode}, personal={p.PersonalAccess}, guild={p.GuildAccess}"
                 + (p.DepositCooldownSec > 0 || p.WithdrawCooldownSec > 0 ? $", cooldowns {p.DepositCooldownSec}/{p.WithdrawCooldownSec}s" : "")
                 + (p.DepositFeePct > 0 || p.WithdrawFeePct > 0 ? $", fees {p.DepositFeePct:0.#}/{p.WithdrawFeePct:0.#}%" : "")
                 + (p.MaxPersonalSilver > 0 ? $", maxPersonal {Util.SilverFmt.Format(p.MaxPersonalSilver)}" : "")
                 + (p.MaxGuildSilver > 0 ? $", maxGuild {Util.SilverFmt.Format(p.MaxGuildSilver)}" : "")
                 + (p.BlockDuringRaid ? ", block-in-raid" : "")
                 + (p.BlockDuringHostileEvent ? ", block-in-hostile-event" : "");
        }

        public static void ClearRuntimeState()
        {
            lock (_lock) { _lastDeposit.Clear(); _lastWithdraw.Clear(); _recent.Clear(); }
        }
    }
}
