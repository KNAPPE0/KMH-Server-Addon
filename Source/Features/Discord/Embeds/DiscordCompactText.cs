using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kmh.Discord.Wire;

namespace KMHServerAddon.Features.Discord
{
    // A reply card as a few lines of message text: Discord draws no embed box, and no link unfurls into one.
    internal static class DiscordCompactText
    {
        public const int MaxChars = KmhDiscordProtocol.MaxContentChars;

        private static readonly Regex MaskedLink = new Regex(@"\]\((https?://[^\s()<>]+)\)", RegexOptions.Compiled);
        private static readonly Regex BareLink = new Regex(@"(?<![<(\w/])(https?://[^\s()<>]+)", RegexOptions.Compiled);

        public static string Render(IEnumerable<KmhEmbedSpec> cards)
        {
            var parts = new List<string>();
            foreach (KmhEmbedSpec c in cards ?? Array.Empty<KmhEmbedSpec>())
            {
                string one = Body(c);
                if (one.Length > 0) parts.Add(one);
            }
            return Finish(string.Join("\n\n", parts));
        }

        public static string Render(KmhEmbedSpec card) => Finish(Body(card));

        // Inline fields share a line where they fit; a value with a code block keeps its own lines.
        private static string Body(KmhEmbedSpec s)
        {
            if (s == null) return "";
            var lines = new List<string>();
            var run = new List<string>();
            void Flush() { if (run.Count > 0) { lines.Add(string.Join(" · ", run)); run.Clear(); } }

            string title = DiscordText.OneLine(s.Title).Trim();
            if (title.Length > 0) lines.Add("**" + title + "**");
            string description = (s.Description ?? "").Trim();
            if (description.Length > 0) lines.Add(description);

            foreach (KmhEmbedField f in s.Fields ?? new List<KmhEmbedField>())
            {
                if (f == null) continue;
                string name = DiscordText.OneLine(f.Name).Trim();
                string value = (f.Value ?? "").Replace("​", "").Trim();
                if (name.Length == 0 && value.Length == 0) continue;
                if (value.Contains("```"))
                {
                    Flush();
                    if (name.Length > 0) lines.Add("**" + name + "**");
                    lines.Add(value);
                    continue;
                }
                string joined = Joined(value);
                string label = name.Length == 0 ? "" : "**" + name + (f.Inline ? ":**" : "**");
                string line = label.Length == 0 ? joined : joined.Length == 0 ? label : label + (f.Inline ? " " : " — ") + joined;
                if (f.Inline && !value.Contains("\n") && line.Length <= 60) { run.Add(line); continue; }
                Flush();
                lines.Add(line);
            }
            Flush();

            string footer = DiscordText.OneLine(s.Footer).Trim();
            if (footer.Length > 0) lines.Add("-# " + footer);
            return string.Join("\n", lines);
        }

        private static string Joined(string value)
        {
            var bits = new List<string>();
            foreach (string l in value.Split('\n'))
            {
                string t = l.Trim();
                if (t.Length > 0) bits.Add(t);
            }
            return string.Join(" · ", bits);
        }

        private static string Finish(string text) => Cap(NoPreviews(DiscordText.NoMentions(text ?? "")), MaxChars);

        // Angle brackets keep Discord from turning a link into a preview box.
        public static string NoPreviews(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            text = MaskedLink.Replace(text, "](<$1>)");
            return BareLink.Replace(text, "<$1>");
        }

        // Cut on a line where it can, and never inside an unclosed code block.
        public static string Cap(string text, int max)
        {
            text = text ?? "";
            if (text.Length <= max) return text;
            int room = Math.Max(16, max - 6);
            int cut = text.LastIndexOf('\n', room - 1);
            string head = (cut > room / 2 ? text.Substring(0, cut) : text.Substring(0, room)).TrimEnd();
            if (Count(head, "```") % 2 == 1) head += "\n```";
            return head + "\n…";
        }

        private static int Count(string s, string what)
        {
            int n = 0;
            for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        // Silver as the activity log and boards write it: invariant, so a German host still says 1,234s.
        public static string Silver(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture) + "s";

        public static string Qty(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        public static string Hhmm(DateTime utc) => utc.ToString("HH:mm", CultureInfo.InvariantCulture);

        public static string Day(DateTime utc) => utc.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }
}
