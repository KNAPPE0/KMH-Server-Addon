using System;
using System.IO;
using KMHServerAddon.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace KMHServerAddon.Features.Media
{
    // Animation is decided from the decoded frame count, never from the url.
    internal static class KmhMediaTranscode
    {
        internal sealed class Result
        {
            public bool   Ok;
            public string Error = "";
            public byte[] Bytes;
            public string Mime = "";
            public int    Width, Height, Frames;
            public bool   Animated;
            public bool   PassedThrough;
        }

        // Only the formats chat allows (ChatConfig.ImageFileExtensions), and no metadata: TIFF is refused at the door
        // and ICC, EXIF and XMP are never parsed or converted. ImageSharp's advisories have sat in exactly those.
        private static readonly Configuration Formats = new Configuration(
            new PngConfigurationModule(), new SixLabors.ImageSharp.Formats.Jpeg.JpegConfigurationModule(),
            new GifConfigurationModule(), new WebpConfigurationModule(), new SixLabors.ImageSharp.Formats.Bmp.BmpConfigurationModule());

        internal static DecoderOptions Decoding(uint maxFrames = uint.MaxValue)
            => new DecoderOptions { Configuration = Formats, SkipMetadata = true, ColorProfileHandling = ColorProfileHandling.Preserve, MaxFrames = maxFrames };

        internal static Result Convert(byte[] source, MediaConfig cfg)
        {
            var r = new Result();
            if (source == null || source.Length == 0) { r.Error = "empty source"; return r; }
            if (cfg == null) { r.Error = "no config"; return r; }

            // Re-encoding something the client already decodes would only cost fidelity.
            string already = PassThroughMime(source);
            if (already != null)
            {
                if (source.Length > cfg.Images.MaxOutputBytes)
                { r.Error = $"already-decodable source is over the {cfg.Images.MaxOutputBytes} byte output limit"; return r; }
                // Never decoded here, so every player's game decodes it: its header is all that stops a small file
                // declaring a 16384-square picture. Same bounds as the client's own still-image check.
                if (!TrySize(source, ref r)) { r.Error = "unreadable image: its size could not be read"; return r; }
                if (r.Width > MaxPassThroughSide || r.Height > MaxPassThroughSide || (long)r.Width * r.Height > MaxPassThroughPixels)
                {
                    r.Error = $"too large for players to show: {r.Width}x{r.Height}, over the {MaxPassThroughSide}px side and "
                            + $"{MaxPassThroughPixels} pixel limit";
                    return r;
                }
                r.Ok = true; r.Bytes = source; r.Mime = already; r.PassedThrough = true;
                return r;
            }

            // Identify reads dimensions without materialising pixels, so a 40000x40000 claim is refused before decoding.
            ImageInfo info;
            try { info = Image.Identify(Decoding(), source); }
            catch (Exception ex) { r.Error = "unreadable media: " + ex.GetBaseException().Message; return r; }
            if (info == null) { r.Error = "unrecognised media format"; return r; }

            if (info.Width > cfg.Images.MaxDimension || info.Height > cfg.Images.MaxDimension)
            { r.Error = $"too large: {info.Width}x{info.Height} over the {cfg.Images.MaxDimension}px side limit"; return r; }

            long pixels = (long)info.Width * info.Height;
            if (pixels > cfg.Images.MaxPixels)
            { r.Error = $"too many pixels: {pixels} over the {cfg.Images.MaxPixels} limit"; return r; }

            // Image.Load materialises every frame at once, and MaxPixels alone bounds only one of them. Identify
            // under-reports animated WebP, so its frames are counted from the container itself.
            int headerFrames = Math.Max(info.FrameMetadataCollection?.Count ?? 1, WebpFrameCount(source));
            if (headerFrames > cfg.Images.MaxFrames)
            { r.Error = $"too many frames: {headerFrames} over the {cfg.Images.MaxFrames} limit"; return r; }

            long decodedBytes = pixels * Math.Max(1, headerFrames) * 4L;   // Rgba32
            if (decodedBytes > cfg.Images.MaxDecodedBytes)
            {
                r.Error = $"decoding would need {decodedBytes / (1024 * 1024)}MB "
                        + $"({pixels} px x {headerFrames} frame(s)), over the {cfg.Images.MaxDecodedBytes / (1024 * 1024)}MB limit";
                return r;
            }

            // A handful of decodes at a time, each capped at the frame limit, whatever the header claimed.
            if (!_decodeSlots.Wait(DecodeWait)) { r.Error = "the media decoder is busy - try again shortly"; return r; }
            Image<Rgba32> image;
            try { image = Image.Load<Rgba32>(Decoding(FrameCap(pixels, cfg)), source); }
            catch (Exception ex) { _decodeSlots.Release(); r.Error = "could not decode: " + ex.GetBaseException().Message; return r; }
            try { return Finish(image, r, cfg); }
            finally { _decodeSlots.Release(); }
        }

        internal const int MaxConcurrentDecodes = 2;
        private static readonly System.Threading.SemaphoreSlim _decodeSlots = new System.Threading.SemaphoreSlim(MaxConcurrentDecodes);
        internal static TimeSpan DecodeWait = TimeSpan.FromSeconds(30);

        internal static IDisposable HoldDecodeSlotsForTest(int n)
        {
            int held = 0;
            for (int i = 0; i < n; i++) if (_decodeSlots.Wait(0)) held++;
            return new SlotRelease(held);
        }

        private sealed class SlotRelease : IDisposable
        {
            private int _n;
            public SlotRelease(int n) => _n = n;
            public void Dispose() { if (_n > 0) _decodeSlots.Release(_n); _n = 0; }
        }

        // RIFF "WEBP", then chunks of <fourcc><u32 little-endian size><data padded to even>; each ANMF is one frame.
        internal static int WebpFrameCount(byte[] b)
        {
            if (b == null || b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F'
                || b[8] != 'W' || b[9] != 'E' || b[10] != 'B' || b[11] != 'P') return 0;
            int frames = 0;
            long pos = 12;
            while (pos + 8 <= b.Length)
            {
                int p = (int)pos;
                if (b[p] == 'A' && b[p + 1] == 'N' && b[p + 2] == 'M' && b[p + 3] == 'F') frames++;
                long size = (uint)(b[p + 4] | b[p + 5] << 8 | b[p + 6] << 16 | b[p + 7] << 24);
                pos += 8 + size + (size & 1);
            }
            return frames;
        }

        // The byte budget caps the decoder too, so a header that under-counts frames cannot allocate MaxFrames canvases.
        // One frame past either limit, so Finish still sees the file is over it and refuses rather than truncating.
        internal static uint FrameCap(long pixels, MediaConfig cfg)
        {
            long budgetFrames = cfg.Images.MaxDecodedBytes / Math.Max(1L, pixels * 4L);
            return (uint)Math.Max(1L, Math.Min(cfg.Images.MaxFrames, budgetFrames) + 1);
        }

        private static Result Finish(Image<Rgba32> image, Result r, MediaConfig cfg)
        {
            using (image)
            {
                int frames = image.Frames.Count;
                if (frames > cfg.Images.MaxFrames)
                { r.Error = $"too many frames: {frames} over the {cfg.Images.MaxFrames} limit"; return r; }

                // Identify does not report WebP frames reliably, so the header check above can pass a file it should refuse.
                long realBytes = (long)image.Width * image.Height * Math.Max(1, frames) * 4L;
                if (realBytes > cfg.Images.MaxDecodedBytes)
                {
                    r.Error = $"decoding would need {realBytes / (1024 * 1024)}MB "
                            + $"({image.Width}x{image.Height} x {frames} frame(s)), over the "
                            + $"{cfg.Images.MaxDecodedBytes / (1024 * 1024)}MB limit";
                    return r;
                }

                // BMP's decoder keeps an embedded ICC profile despite SkipMetadata; no source metadata is passed on.
                StripProfiles(image);
                r.Animated = frames > 1;
                if (r.Animated) ShrinkForChat(image, cfg);
                r.Width = image.Width; r.Height = image.Height; r.Frames = frames;

                try
                {
                    byte[] output = r.Animated ? EncodeGif(image) : EncodePng(image);
                    if (output.Length > cfg.Images.MaxOutputBytes)
                    {
                        // GIF conversion expands animation several-fold, so this is a routine outcome, not a corner case.
                        r.Error = $"converted to {output.Length} bytes, over the {cfg.Images.MaxOutputBytes} output limit";
                        return r;
                    }
                    r.Ok = true;
                    r.Bytes = output;
                    r.Mime = r.Animated ? "image/gif" : "image/png";
                    return r;
                }
                catch (Exception ex) { r.Error = "conversion failed: " + ex.GetBaseException().Message; return r; }
            }
        }

        internal static void StripProfiles(Image<Rgba32> image)
        {
            image.Metadata.ExifProfile = null; image.Metadata.IccProfile = null;
            image.Metadata.IptcProfile = null; image.Metadata.XmpProfile = null;
            foreach (ImageFrame<Rgba32> f in image.Frames)
            {
                f.Metadata.ExifProfile = null; f.Metadata.IccProfile = null;
                f.Metadata.IptcProfile = null; f.Metadata.XmpProfile = null;
            }
        }

        // GIF stores every frame in full, so height costs bytes and a texture per frame; animations only, downward only.
        internal static void ShrinkForChat(Image<Rgba32> image, MediaConfig cfg)
        {
            int cap = cfg?.Images.AnimationMaxHeight ?? 0;
            if (cap <= 0 || image == null || image.Height <= cap) return;
            int width = Math.Max(1, (int)Math.Round(image.Width * (cap / (double)image.Height)));
            try { image.Mutate(x => x.Resize(width, cap)); }
            catch (Exception ex) { Diagnostics.ServerLog.Debug($"Media: could not shrink an animation: {ex.Message}"); }
        }

        // Nothing copies WebP timing into GIF metadata; without this a conversion plays at the encoder's default speed.
        private static byte[] EncodeGif(Image<Rgba32> image)
        {
            try
            {
                WebpMetadata src = image.Metadata.GetWebpMetadata();
                GifMetadata dst = image.Metadata.GetGifMetadata();
                if (src != null && dst != null) dst.RepeatCount = src.RepeatCount;   // 0 = forever, as in both formats
            }
            catch { }

            foreach (ImageFrame<Rgba32> frame in image.Frames)
            {
                try
                {
                    WebpFrameMetadata w = frame.Metadata.GetWebpMetadata();
                    GifFrameMetadata g = frame.Metadata.GetGifMetadata();
                    if (w == null || g == null) continue;

                    // A GIF delay under 2 means "as fast as possible", which viewers clamp to 100ms - the opposite of fast.
                    int hundredths = (int)Math.Round(w.FrameDelay / 10.0);
                    g.FrameDelay = hundredths < 2 ? 2 : hundredths;
                    g.DisposalMode = w.DisposalMode == FrameDisposalMode.RestoreToBackground
                        ? FrameDisposalMode.RestoreToBackground
                        : FrameDisposalMode.DoNotDispose;
                }
                catch { }
            }

            using var ms = new MemoryStream();
            image.Save(ms, new GifEncoder());
            return ms.ToArray();
        }

        private static byte[] EncodePng(Image<Rgba32> image)
        {
            using var ms = new MemoryStream();
            image.Save(ms, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, CompressionLevel = PngCompressionLevel.DefaultCompression });
            return ms.ToArray();
        }

        // Detected by magic number, because the extension and the content-type both lie routinely.
        internal static string PassThroughMime(byte[] b)
        {
            if (b == null || b.Length < 12) return null;
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)                 return "image/jpeg";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return "image/gif";
            return null;
        }

        internal const int  MaxPassThroughSide   = 8192;
        internal const long MaxPassThroughPixels = 4096L * 4096;

        private static bool TrySize(byte[] bytes, ref Result r)
        {
            try
            {
                ImageInfo info = Image.Identify(Decoding(), bytes);
                if (info == null || info.Width <= 0 || info.Height <= 0) return false;
                r.Width = info.Width; r.Height = info.Height;
                r.Frames = info.FrameMetadataCollection?.Count ?? 1;
                r.Animated = r.Frames > 1;
                return true;
            }
            catch { return false; }
        }
    }
}
