using System;
using System.IO;
using KMHServerAddon.Diagnostics;
using SixLabors.ImageSharp;
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
            public bool   PassedThrough;   // already decodable; bytes returned untouched
        }

        internal static Result Convert(byte[] source, MediaConfig cfg)
        {
            var r = new Result();
            if (source == null || source.Length == 0) { r.Error = "empty source"; return r; }
            if (cfg == null) { r.Error = "no config"; return r; }

            // Re-encoding something the client already decodes would only cost fidelity.
            string already = PassThroughMime(source);
            if (already != null)
            {
                if (source.Length > cfg.MaxOutputBytes)
                { r.Error = $"already-decodable source is over the {cfg.MaxOutputBytes} byte output limit"; return r; }
                r.Ok = true; r.Bytes = source; r.Mime = already; r.PassedThrough = true;
                TrySize(source, ref r);
                return r;
            }

            // Identify reads dimensions without materialising pixels, so a 40000x40000 claim is refused before decoding.
            ImageInfo info;
            try { info = Image.Identify(source); }
            catch (Exception ex) { r.Error = "unreadable media: " + ex.GetBaseException().Message; return r; }
            if (info == null) { r.Error = "unrecognised media format"; return r; }

            if (info.Width > cfg.MaxDimension || info.Height > cfg.MaxDimension)
            { r.Error = $"too large: {info.Width}x{info.Height} over the {cfg.MaxDimension}px side limit"; return r; }

            long pixels = (long)info.Width * info.Height;
            if (pixels > cfg.MaxPixels)
            { r.Error = $"too many pixels: {pixels} over the {cfg.MaxPixels} limit"; return r; }

            // Image.Load materialises every frame at once, and MaxPixels alone bounds only one of them.
            int headerFrames = info.FrameMetadataCollection?.Count ?? 1;
            if (headerFrames > cfg.MaxFrames)
            { r.Error = $"too many frames: {headerFrames} over the {cfg.MaxFrames} limit"; return r; }

            long decodedBytes = pixels * Math.Max(1, headerFrames) * 4L;   // Rgba32
            if (decodedBytes > cfg.MaxDecodedBytes)
            {
                r.Error = $"decoding would need {decodedBytes / (1024 * 1024)}MB "
                        + $"({pixels} px x {headerFrames} frame(s)), over the {cfg.MaxDecodedBytes / (1024 * 1024)}MB limit";
                return r;
            }

            Image<Rgba32> image;
            try { image = Image.Load<Rgba32>(source); }
            catch (Exception ex) { r.Error = "could not decode: " + ex.GetBaseException().Message; return r; }

            using (image)
            {
                int frames = image.Frames.Count;
                if (frames > cfg.MaxFrames)
                { r.Error = $"too many frames: {frames} over the {cfg.MaxFrames} limit"; return r; }

                // Identify does not report WebP frames reliably, so the header check above can pass a file it should refuse.
                long realBytes = (long)image.Width * image.Height * Math.Max(1, frames) * 4L;
                if (realBytes > cfg.MaxDecodedBytes)
                {
                    r.Error = $"decoding would need {realBytes / (1024 * 1024)}MB "
                            + $"({image.Width}x{image.Height} x {frames} frame(s)), over the "
                            + $"{cfg.MaxDecodedBytes / (1024 * 1024)}MB limit";
                    return r;
                }

                r.Animated = frames > 1;
                if (r.Animated) ShrinkForChat(image, cfg);
                r.Width = image.Width; r.Height = image.Height; r.Frames = frames;

                try
                {
                    byte[] output = r.Animated ? EncodeGif(image) : EncodePng(image);
                    if (output.Length > cfg.MaxOutputBytes)
                    {
                        // GIF conversion expands animation several-fold, so this is a routine outcome, not a corner case.
                        r.Error = $"converted to {output.Length} bytes, over the {cfg.MaxOutputBytes} output limit";
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

        // GIF writes every frame in full, so height costs bytes AND a client texture per frame. Animations only, downwards only.
        internal static void ShrinkForChat(Image<Rgba32> image, MediaConfig cfg)
        {
            int cap = cfg?.AnimationMaxHeight ?? 0;
            if (cap <= 0 || image == null || image.Height <= cap) return;
            int width = Math.Max(1, (int)Math.Round(image.Width * (cap / (double)image.Height)));
            try { image.Mutate(x => x.Resize(width, cap)); }
            catch (Exception ex) { Diagnostics.ServerLog.Debug($"Media: could not shrink an animation: {ex.Message}"); }
        }

        // Nothing carries WebP timing into GIF metadata, so a conversion would otherwise play at the encoder's default speed.
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

                    // A GIF delay under 2 means "as fast as possible", which viewers clamp to 100ms - the opposite of a fast source.
                    int hundredths = (int)Math.Round(w.FrameDelay / 10.0);
                    g.FrameDelay = hundredths < 2 ? 2 : hundredths;
                    g.DisposalMethod = w.DisposalMethod == WebpDisposalMethod.RestoreToBackground
                        ? GifDisposalMethod.RestoreToBackground
                        : GifDisposalMethod.NotDispose;
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

        private static void TrySize(byte[] bytes, ref Result r)
        {
            try
            {
                ImageInfo info = Image.Identify(bytes);
                if (info == null) return;
                r.Width = info.Width; r.Height = info.Height;
                r.Frames = info.FrameMetadataCollection?.Count ?? 1;
                r.Animated = r.Frames > 1;
            }
            catch { }
        }
    }
}
