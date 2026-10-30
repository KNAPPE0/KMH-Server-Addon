using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Moderation
{
    internal static class KmhEconomyGuardCommands
    {
        private static string Fmt(long v) => Util.SilverFmt.Format(v);
        private static string Utc(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm") + "Z";

        public static void Freeze(string[] args, string actor, Action<string> reply)
        {
            string user = args != null && args.Length > 1 ? args[1].Trim() : "";
            if (user.Length == 0) { reply("Usage: kmh freeze <user> [reason]  |  kmh freeze <user> undo <dry|confirm>"); return; }
            if (args.Length > 2 && string.Equals(args[2], "undo", StringComparison.OrdinalIgnoreCase))
            {
                Undo(user, args.Length > 3 && string.Equals(args[3], "confirm", StringComparison.OrdinalIgnoreCase), actor, reply);
                return;
            }
            string reason = args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : "";
            if (!KmhEconomyGuard.Freeze(user, reason, actor, out bool saved))
            {
                EconomyFreeze had = KmhEconomyGuard.FreezeOf(user);
                reply($"{user} is already frozen (since {Utc(had?.AtUnix ?? 0)} by {had?.By}). Undo what they banked: kmh freeze {user} undo dry");
                return;
            }
            reply($"{user} is frozen: no deposits, withdrawals, trades, bids, donations, sending mail or quest actions; off the boards; "
                + "their listings and auctions cannot be bought. Chat and reports still work.");
            if (!PlayerStats.PlayerStatsStore.HasPlayer(user)) reply($"Note: nobody called {user} has been seen this season - check the spelling.");
            if (!saved) reply("WARNING: EconomyGuard.json could not be written. The freeze holds until a restart and the save is retried every minute.");
            reply($"Next: 'kmh freeze {user} undo dry' shows what undoing their gains would take back; 'kmh unfreeze {user}' lifts it.");
            Notifications.KmhNotify.ToUser(user, "negative", "Account paused by staff", KmhEconomyGuard.FrozenRefusal);
            KmhEconomyGuard.AlertStaff("freeze:" + user.ToLowerInvariant(),
                $"Frozen: {user} by {actor}{(reason.Length > 0 ? $" ({reason})" : "")}. Undo what they banked: kmh freeze {user} undo dry");
        }

        public static void Unfreeze(string[] args, string actor, Action<string> reply)
        {
            string user = args != null && args.Length > 1 ? args[1].Trim() : "";
            if (user.Length == 0) { reply("Usage: kmh unfreeze <user>"); return; }
            if (KmhEconomyGuard.Unfreeze(user, out bool saved))
            {
                reply($"{user} is no longer frozen.");
                ServerLog.Warn($"Anti-cheat: {user} unfrozen by {actor}.");
                Notifications.KmhNotify.ToUser(user, "positive", "Account restored", "Staff have lifted the pause on your account.");
            }
            else reply(saved ? $"{user} is not frozen." : "Could not write EconomyGuard.json, so the freeze stays - fix free space and permissions, then retry.");
        }

        public static void List(Action<string> reply)
        {
            List<EconomyFreeze> all = KmhEconomyGuard.Frozen();
            reply($"=== Frozen accounts ({all.Count}) ===");
            if (all.Count == 0) { reply("  none"); return; }
            foreach (EconomyFreeze f in all)
                reply($"  {f.Username}  since {Utc(f.AtUnix)} by {f.By}" + (f.Reason.Length > 0 ? $"  ({f.Reason})" : "")
                    + (f.UndoneAtUnix > 0 ? $"  [undone {Utc(f.UndoneAtUnix)}]" : ""));
        }

        public static void DepositLimit(string[] args, string actor, Action<string> reply)
        {
            string user = args != null && args.Length > 1 ? args[1].Trim() : "";
            if (user.Length == 0) { reply("Usage: kmh deposit-limit <user> [extra silver] [hours, default 24]"); return; }
            if (args.Length > 2)
            {
                int hours = 24;
                if (!long.TryParse(args[2], out long extra) || extra <= 0 || (args.Length > 3 && (!int.TryParse(args[3], out hours) || hours <= 0 || hours > 24 * 30)))
                { reply("Extra must be a positive amount of silver, hours 1-720."); return; }
                KmhEconomyGuard.Allow(user, extra, hours, actor, out bool saved);
                reply($"{user} may deposit {Fmt(extra)} more over the next {hours} hour(s)." + (saved ? "" : " (not written to disk yet - retried every minute)"));
                ServerLog.Warn($"Anti-cheat: {actor} raised {user}'s deposit limit by {extra} for {hours}h.");
            }
            long limit = KmhEconomyGuard.LimitFor(user, out bool newPlayer);
            long used = KmhEconomyGuard.DepositedLast24h(user), extraNow = KmhEconomyGuard.AllowanceFor(user);
            long hoursOn = PlayerStats.PlayerStatsStore.ConnectedSecondsOf(user) / 3600;
            if (limit <= 0) { reply($"{user}: no daily deposit limit applies ({Fmt(used)} deposited in the last 24 hours)."); return; }
            long room = Math.Max(0, limit + extraNow - used);
            reply($"{user}: {(newPlayer ? "new player" : "established")} ({hoursOn}h connected), limit {Fmt(limit)}"
                + (extraNow > 0 ? $" + {Fmt(extraNow)} allowed by staff" : "")
                + $"; deposited {Fmt(used)} in the last 24 hours; {Fmt(room)} left." + (KmhEconomyGuard.IsFrozen(user) ? " FROZEN." : ""));
        }

        private static void Undo(string user, bool commit, string actor, Action<string> reply)
        {
            EconomyFreeze f = KmhEconomyGuard.FreezeOf(user);
            if (f == null) { reply($"Freeze {user} first ('kmh freeze {user} <reason>'), so nothing new comes in while it is undone."); return; }
            if (commit && f.UndoneAtUnix > 0)
            { reply($"Already undone at {Utc(f.UndoneAtUnix)}. 'kmh wipe-economy {f.Username} confirm' clears anything banked since."); return; }
            if (commit && !Persistence.KmhDataBackup.TryCreate($"freeze-undo-{f.Username}", out string dir, out string err))
            { reply($"Backup failed ({err}) - nothing was changed."); return; }

            reply($"=== Undo for frozen account {f.Username} ({(commit ? "CONFIRM" : "dry run - nothing changes")}) ===");
            long took = 0;
            int guilds = 0;
            bool allTaken = true;
            // One marker per freeze, so a retry after a crash finds the debit already made instead of taking it twice.
            string marker = $"freeze-clawback:{f.Username.ToLowerInvariant()}:{f.AtUnix}";
            foreach (string guild in Guilds.Contributions.KmhGuildContributionLedger.GuildsOf(f.Username))
            {
                if (Treasury.TreasuryStore.KnowsGuildTxnMarker(guild, marker)) { reply($"  guild '{guild}': already taken back."); continue; }
                long given = Guilds.Contributions.KmhGuildContributionLedger
                    .SummaryFor(guild, f.Username, Guilds.GuildStore.EarliestJoinTicks(guild)).Silver;
                if (given <= 0) continue;
                guilds++;
                long held = Treasury.TreasuryStore.GetGuildSilver(guild);
                int take = (int)Math.Min(int.MaxValue, Math.Min(given, held));
                reply($"  guild '{guild}': donated {Fmt(given)} (net of returns), vault holds {Fmt(held)} -> {(commit ? "taking" : "would take")} {Fmt(take)}"
                    + (take < given ? $" ({Fmt(given - take)} already spent by the guild)" : ""));
                if (!commit || take <= 0) continue;
                if (Treasury.TreasuryStore.WithdrawGuildSilverOnce(guild, marker, take, actor, $"clawback of {f.Username}'s donations (frozen by staff)"))
                {
                    Guilds.Contributions.KmhGuildContributionLedger.RecordReturn(guild, f.Username, take, marker, $"clawback by {actor}: account frozen");
                    took += take;
                }
                else
                {
                    allTaken = false;
                    reply($"  guild '{guild}': could not take it (the vault changed or could not be saved) - nothing taken from this guild.");
                }
            }
            if (guilds == 0) reply("  no guild donations to take back.");

            reply(commit ? "Clearing what they hold:" : "What clearing what they hold would do:");
            bool wiped = Maintenance.KmhPlayerCleanup.WipeEconomy(f.Username, commit, reply);
            if (!commit) { reply($"Run 'kmh freeze {f.Username} undo confirm' to do it; KMH-Data is backed up first. Silver they paid other players is not taken back."); return; }
            if (!wiped || !allTaken)
            {
                reply($"Not marked undone - part of it did not land. Run 'kmh freeze {f.Username} undo confirm' again; what was taken is not taken twice.");
                return;
            }

            KmhEconomyGuard.MarkUndone(f.Username, out _);
            KmhEconomyGuard.AlertStaff("undo:" + f.Username.ToLowerInvariant(),
                $"Undone: {f.Username} by {actor} - {Fmt(took)} taken back from {guilds} guild(s) and wipe-economy run. They stay frozen until 'kmh unfreeze {f.Username}'.");
        }
    }
}
