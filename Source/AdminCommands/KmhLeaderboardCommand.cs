using System;
using System.IO;
using System.Text;
using KMHServerAddon.Features.Leaderboards;

namespace KMHServerAddon.AdminCommands
{
    internal static class KmhLeaderboardCommand
    {
        public static void Run(string[] args, Action<string> reply)
        {
            string key = args.Length > 1 ? args[1] : "";
            if (key.Length == 0 || key.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                reply("=== Leaderboards (season " + Features.Seasons.SeasonStore.CurrentSeason + ") ===");
                foreach (LeaderboardBoardDto b in LeaderboardEngine.BuildAll())
                {
                    string lead = b.Rows.Count == 0 ? "nobody yet" : $"{b.Rows[0].Name} {b.Rows[0].Display}";
                    reply($"  {b.Key,-15} {b.Title,-20} {(b.Source == LeaderboardSource.Reported ? "reported" : "verified"),-8} {b.Total,4} ranked, lead: {lead}");
                }
                reply("kmh leaderboard <category> shows one; add 'image' to write it as an SVG.");
                return;
            }

            LeaderboardCategory c = LeaderboardCatalog.Find(key);
            if (c == null) { reply($"No leaderboard '{key}'. 'kmh leaderboard' lists them."); return; }
            LeaderboardBoardDto board = LeaderboardEngine.Build(c.Key);
            reply($"=== {board.Title} - season {board.Season}, {board.Total} ranked ({(board.Source == LeaderboardSource.Reported ? "reported by players' games" : "verified by the server")}) ===");
            int n = 0;
            foreach (LeaderboardRowDto r in board.Rows)
            {
                if (++n > 15) break;
                string second = !string.IsNullOrEmpty(r.Guild) ? $" [{r.Guild}]" : "";
                reply($"  {r.Rank,3}. {r.Name}{second}{(r.Online ? " (online)" : "")} - {r.Display}");
            }
            if (board.Total == 0) reply("  Nobody has a value above zero yet.");

            if (args.Length > 2 && args[2].Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.Combine(Persistence.KmhDataPaths.Folder, "Leaderboards");
                Directory.CreateDirectory(dir);
                string server = KmhServerIdentity.Name ?? "";
                foreach ((string suffix, int width) in new[] { ("", LeaderboardSvg.WideWidth), ("-compact", LeaderboardSvg.CompactWidth) })
                {
                    string path = Path.Combine(dir, c.Key + suffix + ".svg");
                    File.WriteAllText(path, LeaderboardSvg.Render(board, new LeaderboardSvg.Options { Width = width, ServerName = server }), new UTF8Encoding(false));
                    reply("Written " + path);
                }
            }
        }
    }
}
