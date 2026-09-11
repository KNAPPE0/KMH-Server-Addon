using System.Collections.Generic;

namespace KMHServerAddon.Features.Discord
{
    // The relay flattens sender and body into one line, so a surviving newline forges somebody else's message.
    internal static class KmhDiscordTextSelfTest
    {
        private const string Zwsp = "​";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            string spoof = "hello\n**Admin**: the server is moving, join discord.gg/evil";
            string body  = DiscordText.SafeRelayBody(spoof);
            r.Add(("Discord: relay body cannot open a new line",
                   !body.Contains("\n") && !body.Contains("\r"), body));

            r.Add(("Discord: relay body keeps markdown readable",
                   DiscordText.SafeRelayBody("*hi*") == "*hi*", ""));

            r.Add(("Discord: relay body defuses @everyone",
                   !DiscordText.SafeRelayBody("@everyone").Contains("@everyone")
                   && DiscordText.SafeRelayBody("@everyone").Contains("@" + Zwsp), ""));
            r.Add(("Discord: name defuses @everyone",
                   !DiscordText.SafeName("@everyone").Contains("@everyone"), ""));

            r.Add(("Discord: name cannot inject markdown",
                   DiscordText.SafeName("**Admin**") == "\\*\\*Admin\\*\\*", DiscordText.SafeName("**Admin**")));
            r.Add(("Discord: name cannot break the line",
                   !DiscordText.SafeName("bob\nadmin").Contains("\n"), ""));

            r.Add(("Discord: CRLF and CR are flattened",
                   !DiscordText.OneLine("a\r\nb").Contains("\r")
                   && !DiscordText.OneLine("a\rb").Contains("\r")
                   && !DiscordText.OneLine("a\r\nb").Contains("\n"), ""));

            r.Add(("Discord: Escape is markdown-only",
                   DiscordText.Escape("a*b") == "a\\*b" && DiscordText.Escape("@x") == "@x", ""));

            r.Add(("Discord: null and empty are safe",
                   DiscordText.SafeName(null) == "" && DiscordText.SafeRelayBody(null) == ""
                   && DiscordText.OneLine(null) == "" && DiscordText.NoMentions(null) == "", ""));

            // An unrecognised sort falls back to the default, so a key missing from the vocabulary is otherwise invisible.
            r.Add(("Discord: every leaderboard sort key survives normalisation, and aliases land on a real one",
                   AllSortsRoundTrip()
                   && DiscordLeaderboardBuilder.NormalizeSort("activetime") == "active"
                   && DiscordLeaderboardBuilder.NormalizeSort("notafk") == "active"
                   && DiscordLeaderboardBuilder.NormalizeSort("nonsense") == DiscordLeaderboardBuilder.DefaultSort, ""));

            r.Add(("Discord: active playtime reads in whole units",
                   DiscordLeaderboardBuilder.ActiveSpan(0) == "0m"
                   && DiscordLeaderboardBuilder.ActiveSpan(30) == "1m"
                   && DiscordLeaderboardBuilder.ActiveSpan(600) == "10m"
                   && DiscordLeaderboardBuilder.ActiveSpan(7200) == "2h"
                   && DiscordLeaderboardBuilder.ActiveSpan(172800) == "2d",
                   DiscordLeaderboardBuilder.ActiveSpan(172800)));

            return r;
        }

        private static bool AllSortsRoundTrip()
        {
            foreach (string key in DiscordLeaderboardBuilder.SupportedSorts)
                if (DiscordLeaderboardBuilder.NormalizeSort(key) != key) return false;
            return true;
        }
    }
}
