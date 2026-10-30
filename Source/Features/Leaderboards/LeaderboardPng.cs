using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

#nullable disable
namespace KMHServerAddon.Features.Leaderboards
{
    // LeaderboardSvg's layout for hosts that will not show SVG, in Inter (SIL OFL 1.1), embedded, so every host draws
    // it alike. Only ever encodes an image it drew itself: nothing here decodes anything.
    internal static class LeaderboardPng
    {
        // The mark's own colours; everything else arrives from the shared layout.
        private static readonly Color Accent = Hex("#f0a24a"), Muted = Hex("#929bab"), Ring = Hex("#b9bfca");

        public static byte[] Render(LeaderboardBoardDto board, LeaderboardSvg.Options o)
        {
            var canvas = new PngCanvas();
            LeaderboardSvg.Draw(board, o, canvas);
            using Image<Rgba32> image = canvas.Paint();
            using var ms = new MemoryStream();
            image.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, CompressionLevel = PngCompressionLevel.BestCompression });
            return ms.ToArray();
        }

        private sealed class PngCanvas : LeaderboardSvg.ICanvas
        {
            private readonly List<Action<DrawingCanvas>> _ops = new List<Action<DrawingCanvas>>();
            private int _w, _h;

            public void Begin(int w, int h, string label) { _w = w; _h = h; }

            public Image<Rgba32> Paint()
            {
                var image = new Image<Rgba32>(_w, _h, Color.Transparent.ToPixel<Rgba32>());
                image.Mutate(ctx => ctx.Paint(canvas => { foreach (Action<DrawingCanvas> op in _ops) op(canvas); }));
                return image;
            }

            private static Color Solid(string hex, double alpha) => Hex(hex).WithAlpha((float)alpha);

            private static Brush BrushFor(LeaderboardSvg.Paint p, float x, float y, float w, float h)
            {
                if (p.To == null) return new SolidBrush(Solid(p.From, p.FromAlpha));
                PointF end = p.Across ? new PointF(x + w, y) : new PointF(x, y + h);
                return new LinearGradientBrush(new PointF(x, y), end, GradientRepetitionMode.None, StraightAlphaStops(p));
            }

            // Browsers mix SVG gradients in straight alpha and ImageSharp.Drawing 3 mixes premultiplied; close stops keep the SVG's look.
            private static ColorStop[] StraightAlphaStops(LeaderboardSvg.Paint p)
            {
                System.Numerics.Vector4 from = Solid(p.From, p.FromAlpha).ToScaledVector4(), to = Solid(p.To, p.ToAlpha).ToScaledVector4();
                var stops = new ColorStop[17];
                for (int i = 0; i < stops.Length; i++)
                {
                    float t = i / 16f;
                    stops[i] = new ColorStop(t, Color.FromScaledVector(System.Numerics.Vector4.Lerp(from, to, t)));
                }
                return stops;
            }

            private static Pen PenFor(LeaderboardSvg.Paint p, double width, bool dashed)
                => dashed ? Pens.Dash(Solid(p.From, p.FromAlpha), (float)width) : new SolidPen(Solid(p.From, p.FromAlpha), (float)width);

            public void Rect(double x, double y, double w, double h, double r, LeaderboardSvg.Paint fill, LeaderboardSvg.Paint stroke = null,
                             double strokeWidth = 1, bool dashed = false)
            {
                IPath box = Rounded((float)x, (float)y, (float)w, (float)h, (float)r);
                _ops.Add(ctx =>
                {
                    if (fill != null) ctx.Fill(BrushFor(fill, (float)x, (float)y, (float)w, (float)h), box);
                    if (stroke != null) ctx.Draw(PenFor(stroke, strokeWidth, dashed), box);
                });
            }

            public void Circle(double cx, double cy, double r, LeaderboardSvg.Paint fill, LeaderboardSvg.Paint stroke = null,
                               double strokeWidth = 1, bool dashed = false)
            {
                var disc = new EllipsePolygon((float)cx, (float)cy, (float)r);
                _ops.Add(ctx =>
                {
                    if (fill != null) ctx.Fill(BrushFor(fill, (float)(cx - r), (float)(cy - r), (float)(2 * r), (float)(2 * r)), disc);
                    if (stroke != null) ctx.Draw(PenFor(stroke, strokeWidth, dashed), disc);
                });
            }

            public void Polygon(double[] xy, LeaderboardSvg.Paint fill)
            {
                var pts = new PointF[xy.Length / 2];
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < pts.Length; i++)
                {
                    pts[i] = new PointF((float)xy[2 * i], (float)xy[2 * i + 1]);
                    minX = Math.Min(minX, pts[i].X); maxX = Math.Max(maxX, pts[i].X);
                    minY = Math.Min(minY, pts[i].Y); maxY = Math.Max(maxY, pts[i].Y);
                }
                var shape = new SixLabors.ImageSharp.Drawing.Polygon(new LinearLineSegment(pts));
                _ops.Add(ctx => ctx.Fill(BrushFor(fill, minX, minY, maxX - minX, maxY - minY), shape));
            }

            public void Line(double x1, double y1, double x2, double y2, LeaderboardSvg.Paint stroke)
                => _ops.Add(ctx => ctx.DrawLine(new SolidPen(Solid(stroke.From, stroke.FromAlpha), 1f), new PointF((float)x1, (float)y1), new PointF((float)x2, (float)y2)));

            public void Glow(double cx, double cy, double r, string colour, double alpha, double w, double h, double rx)
            {
                IPath card = Rounded(0, 0, (float)w, (float)h, (float)rx);
                _ops.Add(ctx => ctx.Fill(new RadialGradientBrush(new PointF((float)cx, (float)cy), (float)r, GradientRepetitionMode.None,
                                                                 new ColorStop(0, Solid(colour, alpha)), new ColorStop(1, Solid(colour, 0))), card));
            }

            public void Text(double x, double y, string text, int size, LeaderboardSvg.Paint fill, int weight,
                             LeaderboardSvg.Anchor anchor = LeaderboardSvg.Anchor.Start, double spacing = 0, double maxWidth = 0)
            {
                if (maxWidth > 0) text = Fit(text, (int)maxWidth, size, weight);
                Color colour = Solid(fill.From, fill.FromAlpha);
                _ops.Add(ctx => DrawText(ctx, (float)x, (float)y, text, size, colour, weight, anchor, (float)spacing));
            }

            public void Mark(double x, double y, double size) => _ops.Add(ctx => DrawMark(ctx, (float)x, (float)y, (float)size));
        }

        // Must match the SVG's mark, in the same 32-unit box.
        private static void DrawMark(DrawingCanvas ctx, float x, float y, float size)
        {
            float u = size / 32f, r = 8f * u;
            var c = new PointF(x + 16f * u, y + 17f * u);
            var pen = new SolidPen(new PenOptions(Ring, 1.7f * u) { StrokeOptions = new StrokeOptions { LineCap = LineCap.Round } });
            IPath Half(float start) => new SixLabors.ImageSharp.Drawing.Path(new ArcLineSegment(c, new SizeF(14f * u, 4.6f * u), -22f, start, 180f));
            ctx.Draw(pen, Half(180f));
            ctx.Fill(new RadialGradientBrush(new PointF(c.X - 0.28f * r, c.Y - 0.40f * r), 1.6f * r, GradientRepetitionMode.None,
                                             new ColorStop(0, Hex("#ffd7a3")), new ColorStop(0.42f, Accent), new ColorStop(1, Hex("#b4560f"))),
                     new EllipsePolygon(c.X, c.Y, r));
            ctx.Draw(pen, Half(0f));
            ctx.Fill(new SolidBrush(Muted), new EllipsePolygon(x + 27f * u, y + 6.5f * u, 2f * u));
        }

        // Eight steps per corner arc is smooth at these sizes.
        private static IPath Rounded(float x, float y, float width, float height, float radius)
        {
            float r = Math.Max(0f, Math.Min(radius, Math.Min(width, height) / 2f));
            if (r <= 0f) return new RectanglePolygon(x, y, width, height);
            var pts = new List<PointF>(36);
            void Arc(float cx, float cy, float from)
            {
                for (int i = 0; i <= 8; i++)
                {
                    float a = (from + 90f * i / 8f) * MathF.PI / 180f;
                    pts.Add(new PointF(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a)));
                }
            }
            Arc(x + width - r, y + r, 270f);
            Arc(x + width - r, y + height - r, 0f);
            Arc(x + r, y + height - r, 90f);
            Arc(x + r, y + r, 180f);
            return new SixLabors.ImageSharp.Drawing.Polygon(new LinearLineSegment(pts.ToArray()));
        }

        private sealed class Faces
        {
            public FontFamily Regular, SemiBold, Bold, ExtraBold;
            public List<FontFamily> Fallback = new List<FontFamily>();
        }

        private static readonly Lazy<Faces> Loaded = new Lazy<Faces>(Load);
        private static readonly Dictionary<(int, float), Font> FontCache = new Dictionary<(int, float), Font>();
        private static readonly Dictionary<Font, float> AscentCache = new Dictionary<Font, float>();
        private static readonly object FontLock = new object();

        private static Faces Load()
        {
            var collection = new FontCollection();
            FontFamily Add(string file)
            {
                using Stream s = typeof(LeaderboardPng).Assembly.GetManifestResourceStream("KMH.Fonts." + file)
                                 ?? throw new InvalidOperationException("embedded font missing: " + file);
                return collection.Add(s);
            }
            var f = new Faces { Regular = Add("Inter-Regular.ttf"), SemiBold = Add("Inter-SemiBold.ttf"), Bold = Add("Inter-Bold.ttf"), ExtraBold = Add("Inter-ExtraBold.ttf") };
            // Names in scripts Inter does not cover draw from whatever the host has; on a bare host they show as boxes.
            foreach (string name in new[] { "Noto Sans CJK SC", "Noto Sans", "DejaVu Sans", "Segoe UI", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic" })
                if (SystemFonts.TryGet(name, out FontFamily sys)) f.Fallback.Add(sys);
            return f;
        }

        internal static Font FontFor(int weight, float size)
        {
            lock (FontLock)
            {
                if (FontCache.TryGetValue((weight, size), out Font cached)) return cached;
                Faces f = Loaded.Value;
                FontFamily family = weight >= 800 ? f.ExtraBold : weight >= 700 ? f.Bold : weight >= 600 ? f.SemiBold : f.Regular;
                // Inter's Bold file is the "Inter" family's Bold style, beside Regular; SemiBold and ExtraBold are families of their own.
                bool bold = weight >= 700 && weight < 800 && System.Linq.Enumerable.Contains(family.GetAvailableStyles().ToArray(), FontStyle.Bold);
                Font font = family.CreateFont(size, bold ? FontStyle.Bold : FontStyle.Regular);
                FontCache[(weight, size)] = font;
                return font;
            }
        }

        // Baselines, as the SVG places text: the distance from the top of a line to its baseline.
        private static float Ascent(Font font)
        {
            lock (FontLock)
            {
                if (AscentCache.TryGetValue(font, out float a)) return a;
                FontRectangle b = TextMeasurer.MeasureBounds("H", new TextOptions(font));
                AscentCache[font] = a = b.Bottom;
                return a;
            }
        }

        internal static float Measure(string s, int size, int weight)
            => string.IsNullOrEmpty(s) ? 0f : TextMeasurer.MeasureAdvance(s, Options(FontFor(weight, size))).Width;

        private static TextOptions Options(Font font) => new TextOptions(font) { FallbackFontFamilies = Loaded.Value.Fallback };

        // Measured in the real font, so a cut lands where the text actually ends.
        internal static string Fit(string s, int maxWidth, int size, int weight)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (Measure(s, size, weight) <= maxWidth) return s;
            var t = new StringInfo(s);
            for (int n = t.LengthInTextElements - 1; n > 0; n--)
            {
                string cut = t.SubstringByTextElements(0, n).TrimEnd() + "…";
                if (Measure(cut, size, weight) <= maxWidth) return cut;
            }
            return "…";
        }

        private static void DrawText(DrawingCanvas ctx, float x, float baseline, string text, float size, Color colour, int weight,
                                     LeaderboardSvg.Anchor anchor, float spacing)
        {
            text = Clean(text);
            if (text.Length == 0) return;
            var fill = new SolidBrush(colour);
            Font font = FontFor(weight, size);
            float top = baseline - Ascent(font);
            if (spacing > 0f)
            {
                // Letter-spaced, a character at a time: the brand line only, which is plain ASCII.
                float total = 0f;
                foreach (char ch in text) total += TextMeasurer.MeasureAdvance(ch.ToString(), Options(font)).Width + spacing;
                total -= spacing;
                float cx = anchor == LeaderboardSvg.Anchor.Start ? x : anchor == LeaderboardSvg.Anchor.Middle ? x - total / 2f : x - total;
                foreach (char ch in text)
                {
                    ctx.DrawText(new RichTextOptions(font) { Origin = new PointF(cx, top), FallbackFontFamilies = Loaded.Value.Fallback }, ch.ToString(), fill, null);
                    cx += TextMeasurer.MeasureAdvance(ch.ToString(), Options(font)).Width + spacing;
                }
                return;
            }
            ctx.DrawText(new RichTextOptions(font)
            {
                Origin = new PointF(x, top),
                HorizontalAlignment = anchor == LeaderboardSvg.Anchor.Start ? HorizontalAlignment.Left
                                    : anchor == LeaderboardSvg.Anchor.Middle ? HorizontalAlignment.Center : HorizontalAlignment.Right,
                FallbackFontFamilies = Loaded.Value.Fallback,
            }, text, fill, null);
        }

        // Names come from players: control and unpaired surrogate characters are dropped, as the SVG drops them.
        private static string Clean(string s)
        {
            s = s ?? "";
            var b = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { b.Append(ch).Append(s[++i]); continue; }
                if (char.IsSurrogate(ch) || ch == (char)0xFFFE || ch == (char)0xFFFF || char.IsControl(ch)) continue;
                b.Append(ch);
            }
            return b.ToString();
        }

        private static Color Hex(string hex) => Color.ParseHex(hex);
    }
}
