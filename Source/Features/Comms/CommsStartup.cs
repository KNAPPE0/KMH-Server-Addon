using System;
using System.Text;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Comms
{
    // Boot and `kmh reload` both come through here, so the two can never drift apart.
    internal static class CommsStartup
    {
        // The handshake stays quiet until this is true, or a client would be handed defaults and left holding them.
        public static bool Ready { get; private set; }
        public static DateTime ReadyUtc { get; private set; } = DateTime.MinValue;

        // Two transports both deliver a hello, so without this the slower one lands stale values by arriving second.
        public static int Revision { get; private set; } = 1;

        private static void Bump() => Revision++;

        // Runs after generate/migrate/backfill, so dropping the caches here is what makes a first boot use what KMH just wrote.
        public static void ApplyAtBoot()
        {
            ApplyChat(push: false);
            ApplyStaff(push: false);
            ApplyMedia(push: false);

            Ready = true;
            ReadyUtc = DateTime.UtcNow;
            LogSummary();
        }

        public static void ApplyChat(bool push)
        {
            Chat.ChatConfig.Reload();
            Bump();
            _ = Chat.ChatConfig.Current;     // re-read now, so the next reader is not the one that pays for it
            if (push) PushHello();
        }

        public static void ApplyStaff(bool push)
        {
            Identity.StaffConfig.Reload();
            Bump();
            _ = Identity.StaffConfig.Current;
            if (!push) return;
            PushHello();
            // Labels ride the hello but WHO holds a role rides the stats snapshot, so both have to go out.
            try { StaffRolesPush?.Invoke(); }
            catch (Exception ex) { ServerLog.Warn($"Comms: staff role broadcast failed - {ex.Message}"); }
        }

        public static void ApplyMedia(bool push)
        {
            Media.MediaConfig.Reload();
            Bump();
            _ = Media.MediaConfig.Current;
            // The byte cap rides the hello, so without a re-send a client keeps enforcing the old limit.
            if (push) PushHello();
        }

        // Injected rather than called, so this class touches no RWT type and stays testable outside a server.
        public static Action HelloPush;
        public static Action StaffRolesPush;

        // One `kmh reload all` touches five areas, and a hello per area makes every client re-request every snapshot.
        private static int _batchDepth;
        private static bool _batchWanted;

        public static void BeginBatch() => _batchDepth++;

        public static void EndBatch()
        {
            if (_batchDepth > 0) _batchDepth--;
            if (_batchDepth > 0 || !_batchWanted) return;
            _batchWanted = false;
            SendHello();
        }

        public static void PushHello()
        {
            if (_batchDepth > 0) { _batchWanted = true; return; }
            SendHello();
        }

        private static void SendHello()
        {
            if (HelloPush == null) { ServerLog.Warn("Comms: no hello push wired - connected clients will not see this change until they reconnect."); return; }
            try { HelloPush(); }
            catch (Exception ex) { ServerLog.Warn($"Comms: could not push the hello - {ex.Message}"); }
        }

        // Deliberately carries no value that could be a token or a player's name.
        private static void LogSummary()
        {
            Chat.ChatConfig chat = Chat.ChatConfig.Current;
            Identity.StaffConfig staff = Identity.StaffConfig.Current;
            Media.MediaConfig media = Media.MediaConfig.Current;

            int named = staff.Owners.Count + staff.Developers.Count + staff.Admins.Count
                      + staff.Moderators.Count + staff.Ops.Count;

            // Reported rather than applied, because Discord's config is read once at Start and needs a restart.
            bool discordOn = false;
            try
            {
                Discord.DiscordConfig dcfg = Discord.DiscordConfig.LoadOrDefault();
                discordOn = dcfg?.Enabled == true;
                // Cosmetic, so a bad value warns rather than stopping boot.
                if (!Discord.KmhEmbedBuilder.TryBrandColor(dcfg?.Branding?.EmbedColorHex, out _, out string why)
                    && !string.IsNullOrEmpty(why))
                    ServerLog.Warn($"Discord branding: EmbedColorHex {why} - using KMH's default colour.");
            }
            catch { }

            var sb = new StringBuilder();
            sb.AppendLine("Communications startup:");
            sb.AppendLine($"  Chat loaded       - previews {(chat.AllowImagePreviews ? "on" : "off")}, "
                        + $"{chat.ImageHostAllowList?.Length ?? 0} allowed host(s), {chat.MaxImageBytes / (1024 * 1024)}MB image cap");
            sb.AppendLine($"  Staff loaded      - badges {(staff.ShowStaffBadges ? "on" : "off")}, {named} name(s) listed, "
                        + $"RWT admins {(staff.TreatRwtAdminsAsStaff ? "badged" : "not badged")}");
            sb.AppendLine($"  Media loaded      - resolver {(media.ServerMediaResolverEnabled ? "on" : "off")}, "
                        + $"{media.MaxSourceBytes / (1024 * 1024)}MB source / {media.MaxOutputBytes / (1024 * 1024)}MB output");
            sb.AppendLine($"  Discord bridge    - {(discordOn ? "configured" : "disabled")} (restart-only config)");

            // The resolved policy, since the raw fee fields are only read under EconomyMode=Custom.
            try
            {
                Economy.EconomyPolicy p = Economy.EconomyConfig.Current.ResolvePolicy();
                sb.AppendLine($"  Economy in force  - mode {p.Mode}: {p.DepositFeePct:0.##}%/{p.WithdrawFeePct:0.##}% fees, "
                            + $"{p.DepositCooldownSec}s/{p.WithdrawCooldownSec}s cooldowns, personal {p.PersonalAccess}, "
                            + $"guild {p.GuildAccess}"
                            + (p.BlockDuringRaid ? ", blocked during raids" : ""));
                if (!string.Equals(p.Mode, "Custom", System.StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("                      (the fee/cooldown/raid fields in Economy.json apply only to EconomyMode=Custom)");
            }
            catch { }
            sb.Append    ("  presentation state ready - handshakes from here carry it");
            ServerLog.Info(sb.ToString());
        }
    }
}
