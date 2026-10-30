using System;
using System.Linq;

namespace KMHServerAddon.AdminCommands
{
    // Also reached as `kmh inspect sites ...`, which passes the same words on.
    internal static class KmhSitesCommands
    {
        public static void Run(string[] args, Action<string> reply)
        {
            string user = null;
            switch (args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "")
            {
                case "inspect":
                    user = args.Length > 2 ? args[2] : null;
                    if (string.IsNullOrEmpty(user)) { reply("Usage: kmh sites inspect <user>"); return; }
                    break;
                default:
                    break;
            }
            var sites = Features.Sites.SiteStore.AllForApi();
            int n = sites?.Count ?? 0;
            reply(user == null ? $"=== Sites ({n}) ===  ('kmh sites inspect <user>' for one player's detail)" : $"=== Sites involving '{user}' ===");
            if (sites == null) return;
            foreach (var s in sites)
            {
                // Not IsOwnedBy: a guild site has no owner username, so the player's guild sites would be missed.
                bool manages = Features.Sites.SiteOwnership.CanManage(s, user);
                bool works = user != null && s.Workers != null && s.Workers.Any(w => Features.Sites.SiteWorkerKey.IsOwnedBy(w, user));
                if (user != null && !manages && !works) continue;
                reply($"  tile {s.Tile} {s.ItemDefName} x{s.BaseAmountPerCycle}/cycle by {Features.Sites.SiteOwnership.ControllerLabel(s)}");
                if (user == null || s.WorkerProgress == null) continue;
                foreach (var kv in s.WorkerProgress)
                {
                    var wp = kv.Value; if (wp == null) continue;
                    string st = wp.Legacy ? "LEGACY (disabled)" : wp.PawnLoadId <= 0 ? "MISSING PAWN DATA (not producing)"
                              : !string.IsNullOrEmpty(wp.BlockedReason) ? $"BLOCKED ({wp.BlockedReason})" : $"active (pawn '{wp.PawnName}')";
                    reply($"    worker {kv.Key}: {st}");
                }
            }
        }
    }
}
