using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Moderation
{
    // The one place a ban is acted on, so there is a single disconnect path to audit.
    internal static class BanGate
    {
        public static bool RefuseIfBanned(ServerClient client)
        {
            if (client == null) return false;
            try
            {
                string username = client.GetData<UserFile>()?.Username ?? "";
                string ip = client.IP ?? "";
                string why = RefusalFor(username, ip, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (why == null) return false;

                // The address is logged, not sent: the refused client is told it is banned and nothing more.
                ServerLog.Warn($"Ban: refused '{username}' from {BanStore.HostOnly(ip)} - {why}");
                client.Listener?.MarkForDisconnect();
                return true;
            }
            catch (Exception ex)
            {
                // A ban list that throws must not become a server that refuses everyone.
                ServerLog.Warn($"Ban: check failed, admitting ({ex.GetType().Name}).");
                return false;
            }
        }

        // Clients obey chat sent under the system name and RWT relays chat under account names, so a look-alike account
        // could forge grants; format characters and spacing are ignored so zero-width or padded variants still match.
        internal static bool IsReservedName(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            var sb = new System.Text.StringBuilder(username.Length);
            foreach (char c in username)
                if (!char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format) sb.Append(c);
            // The product's own name, or a name that reads as the platform speaking ("Global", "System").
            return sb.ToString().IndexOf("[KMH-", StringComparison.OrdinalIgnoreCase) >= 0 || KmhBrandWords.PosesAsPlatform(username);
        }

        // Spelled like one of this server's staff (digits or look-alike letters for theirs) without being that account.
        internal static string StaffLookAlike(string username, Identity.StaffConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(username) || cfg == null) return null;
            foreach (List<string> list in new[] { cfg.Owners, cfg.Developers, cfg.Admins, cfg.Moderators, cfg.Ops })
                foreach (string s in list ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(s) && !Identity.StaffRegistry.SameAccount(s.Trim(), username)
                        && KmhBrandWords.LooksLike(username, s)) return s.Trim();
            return null;
        }

        // For what acts as a player with no session, like Discord commands, where the login check never runs.
        internal static bool IsBannedAccount(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            try
            {
                return RefusalFor(username, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds()) != null
                    || Identity.StaffRegistry.IsRwtBanned(username);
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Ban: account check failed, allowing ({ex.GetType().Name}).");
                return false;
            }
        }

        // A network ban matches the Global account this name last verified as, so skipping the ticket does not skip it.
        internal static string RefusalFor(string username, string ip, long nowUnix)
        {
            if (IsReservedName(username)) return "that name is reserved for Colony Nexus itself";
            string staff = StaffLookAlike(username, Identity.StaffConfig.Current);
            if (staff != null) return $"that name is too close to this server's staff member {staff}";

            BanEntry hit = BanStore.Find(username, "", ip, nowUnix);
            if (hit != null) return string.IsNullOrEmpty(hit.Reason) ? "banned here" : hit.Reason;

            string known = Global.KmhKnownIdentities.IdFor(username);
            Global.NetworkBanEntry net = known.Length > 0 ? Global.KmhNetworkBans.Find(known, nowUnix) : null;
            if (net != null) return "banned from official servers (account last verified as this name)"
                                  + (string.IsNullOrEmpty(net.Reason) ? "" : $" - {net.Reason}");
            return null;
        }
    }
}
