using System;
using System.Collections.Generic;
using Discord;

namespace Kmh.Discord.Wire
{
    // Coordinator and direct mode both render here, so the two produce the same message.
    public static class KmhEmbedRender
    {
        public static Embed[] ToEmbeds(IEnumerable<KmhEmbedSpec> specs)
        {
            if (specs == null) return null;
            List<Embed> built = new List<Embed>();
            foreach (KmhEmbedSpec s in specs)
            {
                Embed e = ToEmbed(s);
                if (e != null) built.Add(e);
                if (built.Count >= KmhDiscordProtocol.MaxEmbedsPerMessage) break;
            }
            return built.Count == 0 ? null : built.ToArray();
        }

        // Discord counts every part toward one 6000-char budget, and Discord.NET throws from Build() instead of trimming.
        private const int TotalBudget = 5900;

        public static Embed ToEmbed(KmhEmbedSpec s)
        {
            if (s == null) return null;
            EmbedBuilder eb = new EmbedBuilder();
            int used = 0;

            string title = Take(s.Title, 256, ref used);
            if (title.Length > 0) eb.WithTitle(title);
            string footer = Take(s.Footer, 2048, ref used);
            if (footer.Length > 0) eb.WithFooter(footer);
            string description = Take(s.Description, 4096, ref used);
            if (description.Length > 0) eb.WithDescription(description);

            if (!string.IsNullOrEmpty(s.ThumbnailUrl)) eb.WithThumbnailUrl(s.ThumbnailUrl);
            if (!string.IsNullOrEmpty(s.ImageUrl))     eb.WithImageUrl(s.ImageUrl);
            eb.WithColor(new Color(s.ColorRgb));
            if (s.TimestampUnix > 0) eb.WithTimestamp(DateTimeOffset.FromUnixTimeSeconds(s.TimestampUnix));

            if (s.Fields != null)
            {
                int n = 0;
                foreach (KmhEmbedField f in s.Fields)
                {
                    if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                    // Discord rejects the whole message over 25 fields rather than dropping the extras itself.
                    if (++n > 25) break;
                    string name = Take(f.Name, 256, ref used);
                    if (name.Length == 0) break;
                    string value = Take(string.IsNullOrEmpty(f.Value) ? "​" : f.Value, 1024, ref used);
                    eb.AddField(name, value.Length == 0 ? "​" : value, f.Inline);
                }
            }
            return eb.Build();
        }

        private static string Take(string s, int cap, ref int used)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int room = TotalBudget - used;
            if (room <= 1) return "";
            string trimmed = Trim(s, Math.Min(cap, room));
            used += trimmed.Length;
            return trimmed;
        }

        public static MessageComponent ToComponents(KmhComponentSpec spec)
        {
            if (spec?.Buttons == null || spec.Buttons.Count == 0) return null;
            ComponentBuilder cb = new ComponentBuilder();
            foreach (KmhButtonSpec b in spec.Buttons)
            {
                if (b == null || string.IsNullOrEmpty(b.CustomId)) continue;
                cb.WithButton(label: Trim(b.Label, 80), customId: b.CustomId, style: Style(b.Style));
            }
            return cb.Build();
        }

        private static ButtonStyle Style(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "success": return ButtonStyle.Success;
                case "primary": return ButtonStyle.Primary;
                case "danger":  return ButtonStyle.Danger;
                default:        return ButtonStyle.Secondary;
            }
        }

        private static string Trim(string s, int cap)
            => string.IsNullOrEmpty(s) || s.Length <= cap ? s : s.Substring(0, cap - 1) + "…";
    }
}
