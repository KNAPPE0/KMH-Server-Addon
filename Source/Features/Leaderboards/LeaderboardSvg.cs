using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KMHServerAddon.Features.Leaderboards
{
    // One layout drawn through a canvas, so this SVG and Global's PNG cannot disagree; the same board gives the same bytes.
    internal static class LeaderboardSvg
    {
        internal sealed class Options
        {
            public int Width = WideWidth;
            public int MaxRows = 10;
            public string ServerName = "";
            public string Link = "";
        }

        public const int WideWidth = 1080, CompactWidth = 640;

        private const string Font = "'Segoe UI Variable Display','Segoe UI',Inter,'Helvetica Neue',Arial,sans-serif";
        // Colony Nexus's dark theme, as the website's; the medals keep their own metal colours.
        internal const string Bg0 = "#0b0e13", Bg1 = "#121722", Surface = "#141922", SurfaceHi = "#1a202b", Line = "#242c39", Accent = "#f0a24a";
        internal const string Cream = "#eceff4", Muted = "#929bab", Online = "#4cc38a", Ring = "#b9bfca", Teal = "#4fb3bf";
        internal static readonly (string Base, string Light)[] Medals =
        {
            ("#f4b83c", "#ffe39a"), ("#b9c3d1", "#f2f5f9"), ("#c47a3d", "#f0b07a"),
        };

        internal enum Anchor { Start, Middle, End }

        internal sealed class Paint
        {
            public string From = Bg0, To;
            public double FromAlpha = 1, ToAlpha = 1;
            public bool Across;

            public static Paint Of(string colour, double alpha = 1) => new Paint { From = colour, FromAlpha = alpha };
            public static Paint Down(string top, double topAlpha, string bottom, double bottomAlpha)
                => new Paint { From = top, FromAlpha = topAlpha, To = bottom, ToAlpha = bottomAlpha };
            public static Paint Over(string left, double leftAlpha, string right, double rightAlpha)
                => new Paint { From = left, FromAlpha = leftAlpha, To = right, ToAlpha = rightAlpha, Across = true };
        }

        internal interface ICanvas
        {
            void Begin(int w, int h, string label);
            void Rect(double x, double y, double w, double h, double r, Paint fill, Paint stroke = null, double strokeWidth = 1, bool dashed = false);
            void Circle(double cx, double cy, double r, Paint fill, Paint stroke = null, double strokeWidth = 1, bool dashed = false);
            void Polygon(double[] xy, Paint fill);
            void Line(double x1, double y1, double x2, double y2, Paint stroke);
            void Glow(double cx, double cy, double r, string colour, double alpha, double w, double h, double rx);
            // maxWidth cuts with an ellipsis, measured as the canvas measures its own text.
            void Text(double x, double y, string text, int size, Paint fill, int weight, Anchor anchor = Anchor.Start, double spacing = 0, double maxWidth = 0);
            void Mark(double x, double y, double size);
        }

        public static string Render(LeaderboardBoardDto board, Options o)
        {
            var svg = new SvgCanvas();
            Draw(board, o, svg);
            return svg.ToString();
        }

        internal static void Draw(LeaderboardBoardDto board, Options o, ICanvas c)
        {
            o = o ?? new Options();
            int w = Math.Max(CompactWidth, Math.Min(1600, o.Width));
            bool compact = w < 900;
            int pad = compact ? 24 : 48;
            List<LeaderboardRowDto> rows = board?.Rows ?? new List<LeaderboardRowDto>();
            int places = Math.Max(3, Math.Min(10, o.MaxRows));
            bool reported = board?.Source == LeaderboardSource.Reported;
            string bar = reported ? Teal : Accent;
            long top = rows.Count > 0 ? rows[0].Value : 0;
            int online = 0;
            foreach (LeaderboardRowDto r in rows) if (r.Online) online++;

            int headerH = compact ? 150 : 172, podiumH = compact ? 304 : 424;
            int cellH = compact ? 58 : 70, cellGap = compact ? 8 : 12, colGap = compact ? 0 : 24;
            int listed = places - 3, cells = listed + 1, cols = compact ? 1 : 2;
            int perCol = (cells + cols - 1) / cols;
            int listTop = headerH + podiumH + (compact ? 18 : 26);
            int h = listTop + perCol * cellH + (perCol - 1) * cellGap + (compact ? 16 : 20) + (compact ? 56 : 64);

            c.Begin(w, h, board?.Title ?? "Leaderboard");
            c.Rect(0, 0, w, h, 20, Paint.Down(Bg1, 1, Bg0, 1));
            c.Glow(0, 0, w * 0.55, "#5a82ff", 0.08, w, h, 20);
            c.Glow(w * 0.86, 0, w * 0.62, Accent, 0.17, w, h, 20);

            double markSize = compact ? 52 : 64, tx = pad + markSize + (compact ? 14 : 20), side = compact ? 0 : 270;
            c.Mark(pad, compact ? 26 : 34, markSize);
            c.Text(tx, compact ? 44 : 54, "COLONY NEXUS", compact ? 13 : 15, Paint.Of(Accent), 700, Anchor.Start, compact ? 3 : 5);
            c.Text(tx, compact ? 82 : 104, board?.Title ?? "Leaderboard", compact ? 30 : 44, Paint.Of(Cream), 800, maxWidth: w - tx - pad - side);
            string sub = string.Join("  ·  ", NonEmpty(o.ServerName, board == null ? "" : $"Season {board.Season}", board?.Group));
            c.Text(tx, compact ? 108 : 140, sub, compact ? 14 : 18, Paint.Of(Muted), 500, maxWidth: w - tx - pad - side);
            string badge = reported ? "Reported by players' games" : "Reported by the server";
            if (compact) c.Text(tx, 130, badge, 13, Paint.Of(bar), 600, maxWidth: w - tx - pad);
            else
            {
                double bw = Math.Ceiling(Width(badge, 15, true)) + 48;
                c.Rect(w - pad - bw, 34, bw, 36, 18, Paint.Of(reported ? "#16343a" : "#29211a"), Paint.Of(bar, 0.6));
                c.Circle(w - pad - bw + 20, 52, 5.5, Paint.Of(bar));
                c.Text(w - pad - bw + 34, 57, badge, 15, Paint.Of(Cream), 600);
                if (board != null) c.Text(w - pad, 104, board.Total == 1 ? "1 ranked" : $"{board.Total:N0} ranked", 16, Paint.Of(Cream), 600, Anchor.End);
                if (board != null && board.SeasonStartedUnix > 0)
                    c.Text(w - pad, 132, "Season began " + Day(board.SeasonStartedUnix), 14, Paint.Of(Muted), 500, Anchor.End);
            }
            c.Line(pad, headerH - 10, w - pad, headerH - 10, Paint.Of(Line));

            double gap = compact ? 10 : 24, cw = (w - 2 * pad - 2 * gap) / 3.0, face = compact ? 10 : 14;
            double bottom = headerH + podiumH - (compact ? 6 : 10);
            double[] step = compact ? new[] { 92.0, 70, 52 } : new[] { 136.0, 104, 78 };
            int[] order = { 1, 0, 2 };
            for (int slot = 0; slot < 3; slot++)
            {
                int i = order[slot];
                LeaderboardRowDto r = i < rows.Count ? rows[i] : null;
                int medal = r == null ? i : Math.Min(2, Math.Max(0, r.Rank - 1));
                (string metal, string light) = Medals[medal];
                double x = pad + slot * (cw + gap), cx = x + cw / 2, py = bottom - step[i];
                int numeral = (int)Math.Min(compact ? 40 : 64, step[i] - (compact ? 16 : 22));
                string place = (r?.Rank ?? i + 1).ToString(CultureInfo.InvariantCulture);
                if (r != null)
                {
                    c.Polygon(new[] { x, py, x + cw, py, x + cw - face, py - face, x + face, py - face }, Paint.Of(light, 0.26));
                    c.Rect(x, py, cw, step[i], 8, Paint.Down(metal, 0.34, Surface, 1), Paint.Of(metal, 0.5));
                    c.Text(cx, py + step[i] / 2 + numeral * 0.36, place, numeral, Paint.Of(light, 0.92), 800, Anchor.Middle);
                }
                else
                {
                    c.Polygon(new[] { x, py, x + cw, py, x + cw - face, py - face, x + face, py - face }, Paint.Of(Line, 0.45));
                    c.Rect(x, py, cw, step[i], 8, Paint.Of(Surface, 0.4), Paint.Of(Line), 1.5, true);
                    c.Text(cx, py + step[i] / 2 + numeral * 0.36, place, numeral, Paint.Of(Muted, 0.45), 800, Anchor.Middle);
                }

                double inner = cw - (compact ? 12 : 28);
                double detailY = py - face - (compact ? 12 : 16), valueY = detailY - (compact ? 20 : 26);
                double secondY = valueY - (compact ? 28 : 42), nameY = secondY - (compact ? 20 : 26);
                double ar = i == 0 ? (compact ? 34 : 50) : (compact ? 30 : 44), ay = nameY - (compact ? 22 : 30) - ar;
                if (r == null)
                {
                    c.Circle(cx, ay, ar, Paint.Of(Surface, 0.5), Paint.Of(Line), 2, true);
                    string open = rows.Count == 0 && i == 0 ? "No rankings yet" : "Open spot";
                    c.Text(cx, nameY, open, compact ? 16 : 24, Paint.Of(Muted), 700, Anchor.Middle, maxWidth: inner);
                    c.Text(cx, valueY, "—", compact ? 20 : 30, Paint.Of(Muted, 0.6), 800, Anchor.Middle);
                    continue;
                }
                if (i == 0) Crown(c, cx, ay - ar - 4, compact ? 30 : 40, compact ? 16 : 22, metal, light);
                Avatar(c, cx, ay, ar, r, metal, compact ? 2.5 : 3.5, compact ? 6 : 8);
                c.Text(cx, nameY, r.Name, compact ? (i == 0 ? 18 : 17) : (i == 0 ? 30 : 28), Paint.Of(Cream), 700, Anchor.Middle, maxWidth: inner);
                c.Text(cx, secondY, Second(r, board), compact ? 12 : 17, Paint.Of(Muted), 500, Anchor.Middle, maxWidth: inner);
                c.Text(cx, valueY, r.Display, compact ? 20 : 32, Paint.Of(light), 800, Anchor.Middle, maxWidth: inner);
                c.Text(cx, detailY, Detail(rows, i, top), compact ? 11 : 14, Paint.Of(Muted), 600, Anchor.Middle, maxWidth: inner);
            }

            double cellW = (w - 2 * pad - (cols - 1) * colGap) / (double)cols;
            for (int k = 0; k < cells; k++)
            {
                double x = pad + (k / perCol) * (cellW + colGap), y = listTop + (k % perCol) * (cellH + cellGap);
                if (k == listed)
                {
                    c.Rect(x, y, cellW, cellH, 14, Paint.Down(bar, 0.12, Surface, 0.9), Paint.Of(bar, 0.45));
                    string count = rows.Count == 0 ? "No rankings yet - be the first"
                        : (board.Total == 1 ? "1 ranked" : $"{board.Total:N0} ranked") + $"  ·  {online} online now";
                    c.Text(x + 18, y + (compact ? 24 : 30), count, compact ? 14 : 17, Paint.Of(Cream), 700, maxWidth: cellW - 36);
                    string how = string.IsNullOrWhiteSpace(board?.Description) ? (reported ? "As each colony's game reports it" : "Measured by this server") : board.Description;
                    c.Text(x + 18, y + (compact ? 44 : 54), how, compact ? 12 : 14, Paint.Of(Muted), 500, maxWidth: cellW - 36);
                    continue;
                }
                int index = 3 + k;
                LeaderboardRowDto r = index < rows.Count ? rows[index] : null;
                double mid = y + cellH / 2.0, ar = compact ? 17 : 22, ax = x + (compact ? 64 : 84);
                double nx = ax + ar + (compact ? 12 : 16), valueW = compact ? 120 : 150;
                double nameW = x + cellW - nx - valueW - 16, right = x + cellW - (compact ? 14 : 18);
                string place = (r?.Rank ?? index + 1).ToString(CultureInfo.InvariantCulture);
                if (r == null)
                {
                    c.Rect(x, y, cellW, cellH, 14, Paint.Of(Surface, 0.35), Paint.Of(Line), 1.5, true);
                    c.Text(x + (compact ? 30 : 38), mid + (compact ? 6 : 8), place, compact ? 17 : 22, Paint.Of(Muted, 0.55), 800, Anchor.Middle);
                    c.Circle(ax, mid, ar, null, Paint.Of(Line), 1.5, true);
                    c.Text(nx, mid + 6, "Open spot", compact ? 15 : 18, Paint.Of(Muted, 0.8), 600, maxWidth: nameW);
                    c.Text(right, mid + 7, "—", compact ? 17 : 21, Paint.Of(Muted, 0.6), 800, Anchor.End);
                    continue;
                }
                c.Rect(x, y, cellW, cellH, 14, Paint.Of(SurfaceHi, 0.92), Paint.Of(Line));
                c.Text(x + (compact ? 30 : 38), mid + (compact ? 6 : 8), place, compact ? 17 : 22, Paint.Of(Muted), 800, Anchor.Middle);
                Avatar(c, ax, mid, ar, r, Line, 1.5, compact ? 4 : 5);
                string second = Second(r, board);
                if (second.Length > 0)
                {
                    c.Text(nx, y + (compact ? 25 : 30), r.Name, compact ? 16 : 21, Paint.Of(Cream), 700, maxWidth: nameW);
                    c.Text(nx, y + (compact ? 44 : 53), second, compact ? 12 : 14, Paint.Of(Muted), 500, maxWidth: nameW);
                }
                else c.Text(nx, mid + 7, r.Name, compact ? 16 : 21, Paint.Of(Cream), 700, maxWidth: nameW);
                c.Text(right, y + (compact ? 27 : 32), r.Display, compact ? 16 : 21, Paint.Of(Cream), 800, Anchor.End, maxWidth: valueW);
                double bw = valueW - 10, frac = top > 0 ? Math.Max(0.04, Math.Min(1.0, r.Value / (double)top)) : 0.04;
                c.Rect(right - bw, y + (compact ? 38 : 45), bw, 6, 3, Paint.Of(Line));
                c.Rect(right - bw, y + (compact ? 38 : 45), Math.Max(6, Math.Round(bw * frac)), 6, 3, Paint.Over(bar, 0.55, bar, 1));
            }

            int fy = h - (compact ? 22 : 26), fs = compact ? 12 : 14;
            string when = board == null || board.GeneratedUnix <= 0 ? ""
                : "As of " + DateTimeOffset.FromUnixTimeSeconds(board.GeneratedUnix).UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);
            c.Text(pad, fy, when, fs, Paint.Of(Muted), 500);
            // Whatever the date leaves: a slug address is long, and a cut one cannot be typed in.
            if (!string.IsNullOrEmpty(o.Link))
                c.Text(w - pad, fy, o.Link, fs, Paint.Of(Accent), 600, Anchor.End, maxWidth: w - 2 * pad - Math.Ceiling(Width(when, fs, false)) - 32);
        }

        // Ratios only, so every board's unit reads the same.
        private static string Detail(List<LeaderboardRowDto> rows, int i, long top)
        {
            if (i == 0)
            {
                if (rows.Count < 2 || rows[1].Value <= 0) return "Top of the board";
                long second = rows[1].Value;
                if (top <= second) return "Tied for first";
                double ahead = (top - second) / (double)second;
                return ahead < 1 ? $"Leads #2 by {Math.Max(1, (int)Math.Round(ahead * 100)).ToString(CultureInfo.InvariantCulture)}%"
                                 : $"Leads #2 by {(top / (double)second).ToString("0.#", CultureInfo.InvariantCulture)}x";
            }
            if (top <= 0 || rows[i].Value < 0) return "";
            return $"{(int)Math.Round(rows[i].Value * 100.0 / top)}% of #1";
        }

        private static string Second(LeaderboardRowDto r, LeaderboardBoardDto board)
        {
            if (board?.Scope == LeaderboardScope.Guild) return "";
            string guild = (r.Guild ?? "").Trim(), colony = (r.Colony ?? "").Trim();
            if (guild.Length > 0 && colony.Length > 0) return guild + "  ·  " + colony;
            return guild.Length > 0 ? guild : colony;
        }

        private static void Avatar(ICanvas c, double cx, double cy, double r, LeaderboardRowDto row, string ring, double ringWidth, double dot)
        {
            c.Circle(cx, cy, r, Paint.Down(AvatarColour(row.Name, 0.5), 1, AvatarColour(row.Name, 0.32), 1), Paint.Of(ring), ringWidth);
            int size = (int)Math.Round(r * 0.74);
            c.Text(cx, cy + size * 0.36, Initials(row.Name), size, Paint.Of("#ffffff", 0.95), 800, Anchor.Middle);
            if (row.Online) c.Circle(cx + r * 0.72, cy + r * 0.72, dot, Paint.Of(Online), Paint.Of(Bg0), dot * 0.45);
        }

        private static void Crown(ICanvas c, double cx, double baseY, double w, double h, string metal, string light)
        {
            double x = cx - w / 2, y = baseY - h;
            c.Polygon(new[] { x, baseY, x, y + h * 0.3, x + w * 0.27, y + h * 0.62, cx, y, x + w * 0.73, y + h * 0.62, x + w, y + h * 0.3, x + w, baseY },
                      Paint.Down(light, 1, metal, 1));
        }

        // Every player keeps one colour, from their name, so a face is recognisable from board to board.
        internal static string AvatarColour(string name, double lightness)
        {
            uint hash = 2166136261;
            foreach (char ch in name ?? "") { hash ^= ch; hash *= 16777619; }
            double hue = hash % 360, s = 0.46, l = lightness;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            double Ch(double t)
            {
                t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
                double v = t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
                return Math.Round(v * 255);
            }
            return "#" + ((int)Ch(hue / 360 + 1 / 3.0)).ToString("x2") + ((int)Ch(hue / 360)).ToString("x2") + ((int)Ch(hue / 360 - 1 / 3.0)).ToString("x2");
        }

        internal static string Initials(string name)
        {
            var words = new List<List<string>>();
            foreach (string word in (name ?? "").Split(new[] { ' ', '_', '-', '.', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var letters = new List<string>();
                var t = new StringInfo(word);
                for (int i = 0; i < t.LengthInTextElements && letters.Count < 2; i++)
                {
                    string e = t.SubstringByTextElements(i, 1);
                    if (char.IsLetterOrDigit(e, 0)) letters.Add(e);
                }
                if (letters.Count > 0) words.Add(letters);
            }
            string s = words.Count == 0 ? "?" : words.Count == 1 ? CamelPair(name) ?? string.Concat(words[0]) : words[0][0] + words[1][0];
            return s.ToUpperInvariant();
        }

        private static string CamelPair(string name)
        {
            string s = (name ?? "").Trim();
            for (int i = 1; i < s.Length; i++)
                if (char.IsUpper(s[i]) && char.IsLower(s[i - 1]) && char.IsLetter(s[0])) return s[0].ToString() + s[i];
            return null;
        }

        private static string Day(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        // SVG cannot measure text, so widths are estimated from the font size; generous, so a name never overruns.
        internal static double Width(string s, int size, bool bold)
        {
            double em = 0;
            foreach (char ch in s ?? "")
                em += char.IsUpper(ch) || char.IsDigit(ch) ? 0.64 : ch == ' ' ? 0.30 : "iljt.,:;'!|".IndexOf(ch) >= 0 ? 0.30
                    : ch > 0x2E7F ? 1.0 : 0.54;
            return em * size * (bold ? 1.06 : 1.0);
        }

        internal static string Fit(string s, int maxWidth, int size, bool bold)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (Width(s, size, bold) <= maxWidth) return s;
            var t = new StringInfo(s);
            for (int n = t.LengthInTextElements - 1; n > 0; n--)
            {
                string cut = t.SubstringByTextElements(0, n).TrimEnd() + "…";
                if (Width(cut, size, bold) <= maxWidth) return cut;
            }
            return "…";
        }

        private static IEnumerable<string> NonEmpty(params string[] parts)
        {
            foreach (string p in parts) if (!string.IsNullOrWhiteSpace(p)) yield return p.Trim();
        }

        internal static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // Names come from players; every one is text, never markup.
        internal static string X(string s)
        {
            s = s ?? "";
            var b = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '&': b.Append("&amp;"); break;
                    case '<': b.Append("&lt;"); break;
                    case '>': b.Append("&gt;"); break;
                    case '"': b.Append("&quot;"); break;
                    case '\'': b.Append("&#39;"); break;
                    default:
                        // XML has no way to carry these; one stray character must not void the whole image.
                        if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { b.Append(ch).Append(s[++i]); break; }
                        if (char.IsSurrogate(ch) || ch == (char)0xFFFE || ch == (char)0xFFFF || (ch < 0x20 && ch != '\t')) break;
                        b.Append(ch);
                        break;
                }
            }
            return b.ToString();
        }

        private sealed class SvgCanvas : ICanvas
        {
            private readonly StringBuilder _defs = new StringBuilder(), _body = new StringBuilder(24 * 1024);
            private readonly Dictionary<string, string> _ids = new Dictionary<string, string>();
            private int _w, _h;
            private string _label = "";
            private bool _markDef;

            public void Begin(int w, int h, string label) { _w = w; _h = h; _label = label ?? ""; }

            public override string ToString()
                => $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{_w}\" height=\"{_h}\" viewBox=\"0 0 {_w} {_h}\" role=\"img\" aria-label=\"{X(_label)}\">"
                 + (_defs.Length > 0 ? "<defs>" + _defs + "</defs>" : "") + _body + "</svg>";

            private static string Opacity(string attr, double a) => a < 1 ? $" {attr}=\"{N(a)}\"" : "";

            private string Fill(Paint p)
            {
                if (p == null) return " fill=\"none\"";
                if (p.To == null) return $" fill=\"{p.From}\"" + Opacity("fill-opacity", p.FromAlpha);
                string key = $"{p.From}|{N(p.FromAlpha)}|{p.To}|{N(p.ToAlpha)}|{p.Across}";
                if (!_ids.TryGetValue(key, out string id))
                {
                    id = "p" + _ids.Count.ToString(CultureInfo.InvariantCulture);
                    _ids[key] = id;
                    _defs.Append($"<linearGradient id=\"{id}\" x1=\"0\" y1=\"0\" x2=\"{(p.Across ? 1 : 0)}\" y2=\"{(p.Across ? 0 : 1)}\">")
                         .Append($"<stop offset=\"0\" stop-color=\"{p.From}\"{Opacity("stop-opacity", p.FromAlpha)}/>")
                         .Append($"<stop offset=\"1\" stop-color=\"{p.To}\"{Opacity("stop-opacity", p.ToAlpha)}/></linearGradient>");
                }
                return $" fill=\"url(#{id})\"";
            }

            private static string Stroke(Paint p, double width, bool dashed)
                => p == null ? "" : $" stroke=\"{p.From}\"" + Opacity("stroke-opacity", p.FromAlpha)
                   + (width != 1 ? $" stroke-width=\"{N(width)}\"" : "") + (dashed ? " stroke-dasharray=\"7 6\"" : "");

            public void Rect(double x, double y, double w, double h, double r, Paint fill, Paint stroke = null, double strokeWidth = 1, bool dashed = false)
                => _body.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"{N(r)}\"{Fill(fill)}{Stroke(stroke, strokeWidth, dashed)}/>");

            public void Circle(double cx, double cy, double r, Paint fill, Paint stroke = null, double strokeWidth = 1, bool dashed = false)
                => _body.Append($"<circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(r)}\"{Fill(fill)}{Stroke(stroke, strokeWidth, dashed)}/>");

            public void Polygon(double[] xy, Paint fill)
            {
                var pts = new StringBuilder();
                for (int i = 0; i + 1 < xy.Length; i += 2) pts.Append(i == 0 ? "" : " ").Append(N(xy[i])).Append(',').Append(N(xy[i + 1]));
                _body.Append($"<polygon points=\"{pts}\"{Fill(fill)}/>");
            }

            public void Line(double x1, double y1, double x2, double y2, Paint stroke)
                => _body.Append($"<line x1=\"{N(x1)}\" y1=\"{N(y1)}\" x2=\"{N(x2)}\" y2=\"{N(y2)}\"{Stroke(stroke, 1, false)}/>");

            public void Glow(double cx, double cy, double r, string colour, double alpha, double w, double h, double rx)
            {
                string id = "g" + _ids.Count.ToString(CultureInfo.InvariantCulture);
                _ids[id] = id;
                _defs.Append($"<radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(r)}\">")
                     .Append($"<stop offset=\"0\" stop-color=\"{colour}\" stop-opacity=\"{N(alpha)}\"/><stop offset=\"1\" stop-color=\"{colour}\" stop-opacity=\"0\"/></radialGradient>");
                _body.Append($"<rect width=\"{N(w)}\" height=\"{N(h)}\" rx=\"{N(rx)}\" fill=\"url(#{id})\"/>");
            }

            public void Text(double x, double y, string text, int size, Paint fill, int weight, Anchor anchor = Anchor.Start, double spacing = 0, double maxWidth = 0)
            {
                if (maxWidth > 0) text = Fit(text, (int)maxWidth, size, weight >= 600);
                if (string.IsNullOrEmpty(text)) return;
                _body.Append($"<text x=\"{N(x)}\" y=\"{N(y)}\" font-family=\"{Font}\" font-size=\"{size}\" font-weight=\"{weight}\" fill=\"{fill.From}\"")
                     .Append(Opacity("fill-opacity", fill.FromAlpha))
                     .Append(anchor == Anchor.Start ? "" : $" text-anchor=\"{(anchor == Anchor.Middle ? "middle" : "end")}\"")
                     .Append(spacing > 0 ? $" letter-spacing=\"{N(spacing)}\"" : "").Append('>').Append(X(text)).Append("</text>");
            }

            // Must match the website's mark (PublicSite.Mark).
            public void Mark(double x, double y, double size)
            {
                if (!_markDef)
                {
                    _markDef = true;
                    _defs.Append("<radialGradient id=\"mw\" cx=\"0.36\" cy=\"0.3\" r=\"0.8\"><stop offset=\"0\" stop-color=\"#ffd7a3\"/><stop offset=\"0.42\" stop-color=\"#f0a24a\"/><stop offset=\"1\" stop-color=\"#b4560f\"/></radialGradient>");
                }
                string ring = $"fill=\"none\" stroke=\"{Ring}\" stroke-width=\"1.7\" stroke-linecap=\"round\"";
                _body.Append($"<g transform=\"translate({N(x)} {N(y)}) scale({N(size / 32.0)})\">")
                     .Append($"<path transform=\"translate(16 17) rotate(-22)\" d=\"M-14 0A14 4.6 0 0 1 14 0\" {ring}/>")
                     .Append("<circle cx=\"16\" cy=\"17\" r=\"8\" fill=\"url(#mw)\"/>")
                     .Append($"<path transform=\"translate(16 17) rotate(-22)\" d=\"M-14 0A14 4.6 0 0 0 14 0\" {ring}/>")
                     .Append($"<circle cx=\"27\" cy=\"6.5\" r=\"2\" fill=\"{Muted}\"/>").Append("</g>");
            }
        }
    }
}
