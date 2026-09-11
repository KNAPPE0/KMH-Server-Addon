using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace KMHServerAddon.Maintenance
{
    // A client-influenced outbound request is the shape of a Server-Side Request Forgery, so the address checks matter most.
    internal static class KmhMediaSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            // Deny-by-default: an address not provably public fails closed.
            void Refuses(string label, string address)
            {
                bool ok = !Features.Media.KmhMediaGuard.IsPublicAddress(IPAddress.Parse(address), out string why);
                r.Add(($"media: refuses {label} ({address})", ok, ok ? why : "ACCEPTED"));
            }
            Refuses("loopback",              "127.0.0.1");
            Refuses("loopback, high in /8",  "127.255.255.254");
            Refuses("private 10/8",          "10.0.0.1");
            Refuses("private 172.16/12",     "172.16.5.4");
            Refuses("private 172.31 edge",   "172.31.255.255");
            Refuses("private 192.168/16",    "192.168.1.1");
            Refuses("cloud metadata",        "169.254.169.254");
            Refuses("carrier-grade NAT",     "100.64.0.1");
            Refuses("this-network 0/8",      "0.0.0.0");
            Refuses("multicast",             "239.255.255.250");
            Refuses("broadcast",             "255.255.255.255");
            Refuses("IPv6 loopback",         "::1");
            Refuses("IPv6 link-local",       "fe80::1");
            Refuses("IPv6 unique-local",     "fd00::1");
            Refuses("IPv4-mapped loopback",  "::ffff:127.0.0.1");
            Refuses("IPv4-mapped private",   "::ffff:10.1.2.3");
            Refuses("6to4 wrapping private", "2002:0a00:0001::1");     // 10.0.0.1 inside 2002::/16
            Refuses("NAT64 wrapping private","64:ff9b::c0a8:0101");    // 192.168.1.1 inside 64:ff9b::/96

            // ...and the ones it must still allow, or the resolver could never fetch anything at all.
            void Allows(string label, string address)
            {
                bool ok = Features.Media.KmhMediaGuard.IsPublicAddress(IPAddress.Parse(address), out string why);
                r.Add(($"media: allows {label} ({address})", ok, ok ? "" : "refused: " + why));
            }
            Allows("a public v4", "93.184.216.34");
            Allows("a public v6", "2606:2800:220:1:248:1893:25c8:1946");
            Allows("172.15 just below the private block", "172.15.0.1");
            Allows("172.32 just above the private block", "172.32.0.1");

            // A literal address in the url skips DNS entirely, which is the most direct way to aim at 127.0.0.1.
            r.Add(("media: a literal private address in a url is refused before any connection",
                   !Features.Media.KmhMediaGuard.ResolveAndCheck("127.0.0.1", out _, out string litWhy), litWhy));
            // A name, not a literal: checking one returned address and connecting to another is how a rebind bypass works.
            r.Add(("media: a hostname that resolves into private space is refused",
                   !Features.Media.KmhMediaGuard.ResolveAndCheck("localhost", out _, out string dnsWhy), dnsWhy));
            r.Add(("media: a literal public address resolves to itself",
                   Features.Media.KmhMediaGuard.ResolveAndCheck("93.184.216.34", out List<IPAddress> lit, out _)
                   && lit.Count == 1, ""));

            // Policy and decoder have to agree, or the server converts what the client could already draw.
            var cfg = new Features.Chat.ChatConfig
            {
                AllowImagePreviews = true, AllowTypedImageUrls = true,
                ImageHostAllowList = Features.Chat.ChatConfig.DefaultImageHosts(),
            };
            r.Add(("media: png, jpeg and gif stay on the direct client path",
                   !Features.Chat.ChatMediaUrl.NeedsResolver(cfg, "https://cdn.discordapp.com/a/b.png")
                   && !Features.Chat.ChatMediaUrl.NeedsResolver(cfg, "https://cdn.discordapp.com/a/b.jpg")
                   && !Features.Chat.ChatMediaUrl.NeedsResolver(cfg, "https://media.tenor.com/a/b.gif"), ""));
            r.Add(("media: webp needs the resolver",
                   Features.Chat.ChatMediaUrl.NeedsResolver(cfg, "https://cdn.discordapp.com/a/b.webp"), ""));
            // The extension allow-list carries .bmp, which nothing on the client can draw.
            r.Add(("media: bmp is allowed by policy and therefore must take the resolver path",
                   Features.Chat.ChatImagePolicy.LooksLikeImage(cfg, "/a/b.bmp")
                   && Features.Chat.ChatMediaUrl.NeedsResolver(cfg, "https://cdn.discordapp.com/a/b.bmp"), ""));
            r.Add(("media: a video never takes the resolver path",
                   !Features.Chat.ChatMediaUrl.NeedsResolver(cfg, "https://cdn.discordapp.com/a/b.mp4"), ""));

            // Reading the ?format=png hint here would answer "no conversion needed" for the media that needs it most.
            const string wrappedWebp = "https://images-ext-1.discordapp.net/external/h/https/"
                                     + "static.klipy.com/ii/a/b/c.webp?format=png";
            r.Add(("media: a ?format=png fallback does not hide that the source still needs converting",
                   Features.Chat.ChatMediaUrl.NeedsResolver(cfg, wrappedWebp), ""));
            r.Add(("media: the format hint is stripped without disturbing the rest of the query",
                   Features.Chat.ChatMediaUrl.StripFormatParam("https://h/a?width=1&format=png&x=2")
                       == "https://h/a?width=1&x=2"
                   && Features.Chat.ChatMediaUrl.StripFormatParam("https://h/a?format=png") == "https://h/a"
                   && Features.Chat.ChatMediaUrl.StripFormatParam("https://h/a") == "https://h/a",
                   Features.Chat.ChatMediaUrl.StripFormatParam("https://h/a?width=1&format=png&x=2")));

            // The proxy serves a flattened copy, so converting it would produce a correct and useless one-frame gif.
            var withKlipy = new Features.Chat.ChatConfig
            {
                AllowImagePreviews = true,
                ImageHostAllowList = new[] { "discordapp.net", "static.klipy.com" },
            };
            r.Add(("media: the resolver fetches the unflattened original when its host is allow-listed",
                   Features.Chat.ChatMediaUrl.ResolverSourceFor(withKlipy, wrappedWebp)
                       == "https://static.klipy.com/ii/a/b/c.webp",
                   Features.Chat.ChatMediaUrl.ResolverSourceFor(withKlipy, wrappedWebp)));
            // The hint is what makes Discord return the flattened still, so the fetch url must never carry it.
            r.Add(("media: the resolver never fetches the flattened ?format= copy",
                   !Features.Chat.ChatMediaUrl.ResolverSourceFor(withKlipy, wrappedWebp).Contains("format=")
                   && !Features.Chat.ChatMediaUrl.ResolverSourceFor(cfg, wrappedWebp).Contains("format="),
                   Features.Chat.ChatMediaUrl.ResolverSourceFor(cfg, wrappedWebp)));
            r.Add(("media: the original is fetched even off the player-facing allow-list, because no player fetches it",
                   Features.Chat.ChatMediaUrl.ResolverSourceFor(cfg, wrappedWebp)
                       == "https://static.klipy.com/ii/a/b/c.webp",
                   Features.Chat.ChatMediaUrl.ResolverSourceFor(cfg, wrappedWebp)));

            // ...and the owner can still shut that door, which is the half that has to keep working.
            var openMedia = new Features.Media.MediaConfig(); openMedia.Clamp();
            var lockedMedia = new Features.Media.MediaConfig
            { SourceHostAllowList = new[] { "cdn.discordapp.com" } };
            lockedMedia.Clamp();
            r.Add(("media: by default the server may fetch any https host",
                   Features.Media.KmhMediaFetch.AllowedForTest("https://static.klipy.com/x.webp", openMedia, out _),
                   ""));
            r.Add(("media: an owner-set source list refuses a host outside it",
                   !Features.Media.KmhMediaFetch.AllowedForTest("https://static.klipy.com/x.webp", lockedMedia, out string lockedWhy)
                   && Features.Media.KmhMediaFetch.AllowedForTest("https://cdn.discordapp.com/x.webp", lockedMedia, out _),
                   lockedWhy));
            r.Add(("media: plain http is refused whatever the list says",
                   !Features.Media.KmhMediaFetch.AllowedForTest("http://static.klipy.com/x.webp", openMedia, out string schemeWhy),
                   schemeWhy));

            // Fixtures are built by ImageSharp itself, so no network is involved.
            var mcfg = new Features.Media.MediaConfig();
            mcfg.Clamp();

            byte[] stillWebp = MakeWebp(frames: 1, w: 40, h: 30);
            var still = Features.Media.KmhMediaTranscode.Convert(stillWebp, mcfg);
            r.Add(("media: a static webp becomes a png",
                   still.Ok && still.Mime == "image/png" && !still.Animated && still.Frames == 1,
                   still.Ok ? still.Mime : still.Error));

            byte[] animWebp = MakeWebp(frames: 5, w: 40, h: 30);
            var anim = Features.Media.KmhMediaTranscode.Convert(animWebp, mcfg);
            r.Add(("media: an animated webp becomes an animated gif",
                   anim.Ok && anim.Mime == "image/gif" && anim.Animated && anim.Frames == 5,
                   anim.Ok ? $"{anim.Mime}, {anim.Frames} frame(s)" : anim.Error));

            // A one-frame result would be a silent flattening, the exact failure the resolver exists to fix.
            if (anim.Ok)
            {
                int reread = 0;
                try { using (Image img = Image.Load(anim.Bytes)) reread = img.Frames.Count; } catch { }
                r.Add(("media: the converted gif really holds every frame", reread == 5, reread + " frame(s)"));
                r.Add(("media: dimensions survive the conversion", anim.Width == 40 && anim.Height == 30,
                       $"{anim.Width}x{anim.Height}"));
            }

            // Expiring the id -> url registration alongside the byte cache made an older gif fall back to a flattened still.
            const long old = 100L, fresh = 900L, cut = 500L;
            r.Add(("media: the cache TTL releases bytes and never a registration",
                   Features.Media.KmhMediaCache.ExpiresOnTtl(true, old, cut)
                   && !Features.Media.KmhMediaCache.ExpiresOnTtl(false, old, cut)
                   && !Features.Media.KmhMediaCache.ExpiresOnTtl(true, fresh, cut)
                   && !Features.Media.KmhMediaCache.ExpiresOnTtl(false, fresh, cut), ""));
            r.Add(("media: registrations are still bounded, far above anything a chat ring can hold",
                   Features.Media.KmhMediaCache.MaxRegistrations >= 10_000, ""));

            // Measured on a real 498x498 klipy source: 5.4MB of gif became 2.5MB at 320px, with all 42 frames intact.
            var capped = new Features.Media.MediaConfig { AnimationMaxHeight = 64 }; capped.Clamp();
            byte[] tallAnim = MakeWebp(frames: 4, w: 200, h: 200);
            var shrunk = Features.Media.KmhMediaTranscode.Convert(tallAnim, capped);
            r.Add(("media: a tall animation is sent at chat size, keeping every frame",
                   shrunk.Ok && shrunk.Animated && shrunk.Frames == 4 && shrunk.Height == 64 && shrunk.Width == 64,
                   shrunk.Ok ? $"{shrunk.Width}x{shrunk.Height}, {shrunk.Frames} frame(s), {shrunk.Bytes.Length} bytes" : shrunk.Error));
            // Only downwards, and only animations: a still and a short animation must come back untouched.
            var shortAnim = Features.Media.KmhMediaTranscode.Convert(MakeWebp(frames: 4, w: 40, h: 30), capped);
            var stillTall = Features.Media.KmhMediaTranscode.Convert(MakeWebp(frames: 1, w: 200, h: 200), capped);
            r.Add(("media: an animation already small enough, and any still, are left at their own size",
                   shortAnim.Ok && shortAnim.Width == 40 && shortAnim.Height == 30
                   && stillTall.Ok && stillTall.Width == 200 && stillTall.Height == 200,
                   $"short={shortAnim.Width}x{shortAnim.Height} still={stillTall.Width}x{stillTall.Height}"));
            r.Add(("media: the size cap is owner-editable and can be turned off",
                   new Features.Media.MediaConfig().AnimationMaxHeight > 0
                   && Features.Media.KmhMediaTranscode.Convert(
                          tallAnim, Clamped(new Features.Media.MediaConfig { AnimationMaxHeight = 0 })).Height == 200, ""));

            // Formats the client already reads are handed back untouched rather than re-encoded.
            byte[] png = Features.Media.KmhMediaTranscode.Convert(stillWebp, mcfg).Bytes;
            var passed = Features.Media.KmhMediaTranscode.Convert(png, mcfg);
            r.Add(("media: an already-decodable format passes through without re-encoding",
                   passed.Ok && passed.PassedThrough && ReferenceEquals(passed.Bytes, png), ""));

            // Each limit is the only thing between a hostile file and the server.
            var tiny = new Features.Media.MediaConfig { MaxFrames = 2 }; tiny.Clamp();
            var tooMany = Features.Media.KmhMediaTranscode.Convert(animWebp, tiny);
            r.Add(("media: too many frames is refused", !tooMany.Ok && tooMany.Error.Contains("frames"), tooMany.Error));

            // Bigger than the config's clamp floors, or Clamp raises the limit past the fixture and the check proves nothing.
            byte[] bigWebp  = MakeWebp(frames: 1, w: 300, h: 300);
            byte[] bigAnim  = MakeWebp(frames: 8, w: 300, h: 300);

            var narrow = new Features.Media.MediaConfig { MaxDimension = 64 }; narrow.Clamp();
            var tooWide = Features.Media.KmhMediaTranscode.Convert(bigWebp, narrow);
            r.Add(("media: an over-sized image is refused on its header, before decoding",
                   !tooWide.Ok && tooWide.Error.Contains("side limit"), tooWide.Error));

            var fewPixels = new Features.Media.MediaConfig { MaxPixels = 4096, MaxDimension = 4096 }; fewPixels.Clamp();
            var tooBig = Features.Media.KmhMediaTranscode.Convert(bigWebp, fewPixels);
            r.Add(("media: a pixel-count bomb is refused", !tooBig.Ok && tooBig.Error.Contains("pixels"), tooBig.Error));

            // ImageSharp materialises every frame in one Load, so the fixture must exceed the clamp floor to fire the bound.
            byte[] memBomb = MakeWebp(frames: 12, w: 400, h: 400);
            var tightMem = new Features.Media.MediaConfig { MaxDecodedMegabytes = 4, MaxFrames = 500 }; tightMem.Clamp();
            var bomb = Features.Media.KmhMediaTranscode.Convert(memBomb, tightMem);
            r.Add(("media: total decoded animation cost is bounded, not just per-frame pixels",
                   !bomb.Ok && bomb.Error.Contains("decoding would need"),
                   bomb.Ok ? $"NOT REFUSED: {bomb.Width}x{bomb.Height} {bomb.Frames}f, cap {tightMem.MaxDecodedBytes}B, src {memBomb.Length}B"
                           : bomb.Error));
            r.Add(("media: the decoded-memory bound is its own number, not derived from the others",
                   new Features.Media.MediaConfig().MaxDecodedBytes > 0
                   && new Features.Media.MediaConfig { MaxDecodedMegabytes = 1 }.MaxDecodedMegabytes >= 1, ""));

            var smallOut = new Features.Media.MediaConfig { MaxOutputBytes = 16 * 1024 }; smallOut.Clamp();
            var tooFat = Features.Media.KmhMediaTranscode.Convert(bigAnim, smallOut);
            r.Add(("media: output has its own cap, separate from the source cap",
                   !tooFat.Ok && tooFat.Error.Contains("output limit"), tooFat.Error));
            r.Add(("media: source and output caps are genuinely separate numbers",
                   new Features.Media.MediaConfig().MaxSourceBytes != new Features.Media.MediaConfig().MaxOutputBytes, ""));

            bool threw = false;
            Features.Media.KmhMediaTranscode.Result junk = null, empty = null, html = null;
            try
            {
                junk  = Features.Media.KmhMediaTranscode.Convert(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50, 9, 9 }, mcfg);
                empty = Features.Media.KmhMediaTranscode.Convert(new byte[0], mcfg);
                html  = Features.Media.KmhMediaTranscode.Convert(System.Text.Encoding.ASCII.GetBytes("<!DOCTYPE html><html>no</html>"), mcfg);
                Features.Media.KmhMediaTranscode.Convert(null, mcfg);
            }
            catch { threw = true; }
            r.Add(("media: corrupt, empty and html input fail closed and never throw",
                   !threw && junk != null && !junk.Ok && !empty.Ok && !html.Ok, ""));

            r.Add(("media: an id is stable for a url and differs between urls",
                   Features.Media.KmhMediaCache.IdFor("https://a/b.webp") == Features.Media.KmhMediaCache.IdFor("https://a/b.webp")
                   && Features.Media.KmhMediaCache.IdFor("https://a/b.webp") != Features.Media.KmhMediaCache.IdFor("https://a/c.webp"), ""));
            r.Add(("media: an id carries no url in it",
                   !Features.Media.KmhMediaCache.IdFor("https://secret.example/x.webp").Contains("secret"), ""));
            r.Add(("media: the content hash is a full sha-256 and changes with the bytes",
                   Features.Media.KmhMediaCache.HashOf(new byte[] { 1 }).Length == 64
                   && Features.Media.KmhMediaCache.HashOf(new byte[] { 1 }) != Features.Media.KmhMediaCache.HashOf(new byte[] { 2 }), ""));
            r.Add(("media: an unknown id resolves to nothing, which is what 'expired' is made of",
                   Features.Media.KmhMediaCache.SourceUrlFor("nosuchid") == ""
                   && Features.Media.KmhMediaCache.Get("nosuchid") == null, ""));

            string regId = Features.Media.KmhMediaCache.Register("https://cdn.discordapp.com/a/registered.webp");
            r.Add(("media: registering an id records what it refers to without fetching anything",
                   Features.Media.KmhMediaCache.SourceUrlFor(regId) == "https://cdn.discordapp.com/a/registered.webp"
                   && Features.Media.KmhMediaCache.Get(regId) == null, ""));

            // The client rejects a stream whose shape disagrees with its meta.
            int chunk = Features.Media.KmhMediaHandler.ChunkBytes;
            r.Add(("media: chunk size stays well inside the transport frame limit",
                   chunk > 0 && chunk <= 32 * 1024, chunk + " bytes"));
            r.Add(("media: chunk counts cover the last partial chunk",
                   (chunk + chunk - 1 + chunk - 1) / chunk == 2
                   && (1 + chunk - 1) / chunk == 1
                   && (chunk * 3 + chunk - 1) / chunk == 3, ""));

            r.Add(("media: the resolver ships on, and can be turned off",
                   new Features.Media.MediaConfig().ServerMediaResolverEnabled, ""));
            var wild = new Features.Media.MediaConfig
            {
                MaxSourceBytes = -5, MaxOutputBytes = int.MaxValue, MaxPixels = 0,
                MaxFrames = -1, TimeoutSeconds = 0, MaxRedirects = 99, CacheTtlMinutes = 0, MaxVideoSeconds = 1,
            };
            wild.Clamp();
            r.Add(("media: a hand-edited config cannot disable a guard",
                   wild.MaxSourceBytes >= 16 * 1024 && wild.MaxOutputBytes <= 64 * 1024 * 1024
                   && wild.MaxPixels > 0 && wild.MaxFrames >= 1 && wild.TimeoutSeconds >= 2
                   && wild.MaxRedirects <= 10 && wild.CacheTtlMinutes >= 1 && wild.MaxVideoSeconds >= 5, ""));
            r.Add(("media: a zero video limit means no limit, not a limit of zero",
                   new Features.Media.MediaConfig { MaxVideoSeconds = 0 }.MaxVideoSeconds == 0, ""));

            var watch = new Features.Media.MediaConfig { WatchMaxHeight = 4000, WatchMaxSeconds = 2, VideoCacheMaxMegabytes = 1 };
            watch.Clamp();
            r.Add(("media: a hand-edited watch config keeps a usable height, length and cache size",
                   watch.WatchMaxHeight <= 1080 && watch.WatchMaxSeconds >= 5
                   && watch.VideoCacheMaxMegabytes >= 64, ""));
            r.Add(("media: watch playback ships on, with no length limit of its own",
                   new Features.Media.MediaConfig().WatchPagePlayback
                   && new Features.Media.MediaConfig().WatchMaxSeconds == 0, ""));

            // It listens publicly, so what it will hand out is the whole of its safety.
            r.Add(("video: only files inside this server's own video cache can ever be served",
                   Features.Media.KmhVideoRelay.InCache(Features.Media.KmhVideoCache.PathFor("abc123defgh", 480))
                   && !Features.Media.KmhVideoRelay.InCache(@"C:\Windows\System32\config\SAM")
                   && !Features.Media.KmhVideoRelay.InCache(System.IO.Path.Combine(Features.Media.KmhVideoCache.Folder, "..", "Config", "Staff.json"))
                   && !Features.Media.KmhVideoRelay.InCache(""), ""));

            r.Add(("video: a cache file is named after the video and its height, and never anything else",
                   Features.Media.KmhVideoCache.PathFor("abc123defgh", 480).EndsWith("abc123defgh_480.mp4", StringComparison.Ordinal)
                   && Features.Media.KmhVideoCache.Safe("../../etc/passwd") == "______etc_passwd"
                   && Features.Media.KmhVideoCache.Safe("") == "unknown",
                   Features.Media.KmhVideoCache.Safe("../../etc/passwd")));

            r.Add(("video: nothing is published while the server is not listening",
                   !Features.Media.KmhVideoRelay.Running
                   && Features.Media.KmhVideoRelay.Publish(Features.Media.KmhVideoCache.PathFor("abc123defgh", 480), "video/mp4").Length == 0, ""));

            r.Add(("video: a request path yields only the token part",
                   Features.Media.KmhVideoRelay.TokenOf("/abcdef123456.mp4?seek=9") == "abcdef123456"
                   && Features.Media.KmhVideoRelay.TokenOf("/") == ""
                   && Features.Media.KmhVideoRelay.TokenOf("/../../secret") == "", ""));

            r.Add(("video: only GET-shaped absolute paths are answered",
                   Features.Media.KmhVideoRelay.ParseRequest("GET /abc.mp4 HTTP/1.1\r\n\r\n", out string vm, out string vp)
                   && vm == "GET" && vp == "/abc.mp4"
                   && !Features.Media.KmhVideoRelay.ParseRequest("GET abc.mp4 HTTP/1.1\r\n", out _, out _)
                   && !Features.Media.KmhVideoRelay.ParseRequest("", out _, out _), ""));

            bool Range(string spec, long len, out long a, out long b)
                => Features.Media.KmhVideoRelay.ParseRange(spec, len, out a, out b);
            r.Add(("video: a player's range is honoured, and a nonsense one is not",
                   Range("bytes=0-", 1000, out long ra, out long rb) && ra == 0 && rb == 999
                   && Range("bytes=100-199", 1000, out ra, out rb) && ra == 100 && rb == 199
                   && Range("bytes=-500", 1000, out ra, out _) && ra == 500
                   && !Range("bytes=5000-", 1000, out _, out _)
                   && !Range("pages=1-2", 1000, out _, out _)
                   && !Range("", 1000, out _, out _), ""));

            r.Add(("video: a partial answer declares the span it is actually sending",
                   Features.Media.KmhVideoRelay.ResponseHead(true, 100, 199, 1000, "video/mp4")
                       .Contains("Content-Range: bytes 100-199/1000")
                   && Features.Media.KmhVideoRelay.ResponseHead(true, 100, 199, 1000, "video/mp4")
                       .Contains("Content-Length: 100")
                   && Features.Media.KmhVideoRelay.ResponseHead(false, 0, 999, 1000, "video/mp4")
                       .StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal), ""));

            r.Add(("video: a watch link is recognised, and a lookalike host is not",
                   Features.Media.KmhVideoHandler.VideoIdOf("https://youtu.be/abc123defgh").Length == 11
                   && Features.Media.KmhVideoHandler.VideoIdOf("https://www.youtube.com/watch?v=abc123defgh&t=9").Length == 11
                   && Features.Media.KmhVideoHandler.VideoIdOf("https://youtube.com.attacker.net/watch?v=abc123defgh").Length == 0
                   && Features.Media.KmhVideoHandler.VideoIdOf("not a url").Length == 0, ""));

            // Picture and sound are served separately, so every choice must bring its own sound with it.
            r.Add(("video: every fetch format asks for H.264 and brings sound with it",
                   Features.Media.KmhVideoHelper.Format(480).Contains("vcodec^=avc1")
                   && Features.Media.KmhVideoHelper.Format(480).Contains("height<=480")
                   && Features.Media.KmhVideoHelper.Format(480).Contains("+ba")
                   && !Features.Media.KmhVideoHelper.Format(480).Contains("av01"),
                   Features.Media.KmhVideoHelper.Format(480)));

            string fetchArgs = Features.Media.KmhVideoHelper.FetchArguments(
                "https://youtu.be/abc123defgh", 480, @"C:\tmp\x.mp4", @"C:\tools\ffmpeg.exe", 2048);
            r.Add(("video: an animated png is not mistaken for a still by the image policy",
                   Features.Chat.ChatImagePolicy.LooksLikeImage(cfg, "/stickers/1234.png")
                   && !Features.Chat.ChatMediaUrl.NeedsResolver(cfg, "https://media.discordapp.net/stickers/1234.png"), ""));

            r.Add(("video: a fetch merges into one mp4 with its index at the front",
                   fetchArgs.Contains("--merge-output-format mp4")
                   && fetchArgs.Contains("+faststart")
                   && fetchArgs.Contains("--ffmpeg-location")
                   && fetchArgs.Contains("--no-playlist"), fetchArgs));

            // 60fps is twice the decoding work for a difference nobody sees in a chat panel.
            r.Add(("video: at the same resolution the lighter frame rate is preferred",
                   fetchArgs.Contains("-S \"res,+fps\""), fetchArgs));

            r.Add(("video: a served range keeps the connection open for the next one",
                   Features.Media.KmhVideoRelay.ResponseHead(true, 0, 99, 1000, "video/mp4").Contains("keep-alive")
                   && !Features.Media.KmhVideoRelay.ResponseHead(true, 0, 99, 1000, "video/mp4").Contains("Connection: close"), ""));

            r.Add(("video: with no tools installed the server names what is missing instead of hanging",
                   Features.Media.KmhVideoHelper.Available
                   || Features.Media.KmhVideoHelper.MissingTools().Length > 0,
                   Features.Media.KmhVideoHelper.MissingTools()));

            // A fetch still running leaves a .part beside the target, and half a file plays as a truncated video.
            bool cacheRules;
            string probe = Features.Media.KmhVideoCache.PathFor("selftest0001", 144);
            try
            {
                Features.Media.KmhVideoCache.EnsureFolder();
                File.WriteAllText(probe, "x");
                bool whole = Features.Media.KmhVideoCache.Has(probe);
                File.WriteAllText(probe + ".part", "x");
                bool partial = Features.Media.KmhVideoCache.Has(probe);
                cacheRules = whole && !partial && !Features.Media.KmhVideoCache.Has(probe + "-missing");
            }
            catch { cacheRules = false; }
            finally
            {
                try { File.Delete(probe); } catch { }
                try { File.Delete(probe + ".part"); } catch { }
            }
            r.Add(("video: a file still being fetched is not offered as one that is ready", cacheRules, ""));

            // Run against its own folder, or the sweep evicts what players are currently watching.
            bool sweepRules;
            int keptHours = Features.Media.MediaConfig.Current.VideoCacheHours;
            string sandbox = Path.Combine(Path.GetTempPath(), "KMH-SelfTest-VideoCache");
            try
            {
                Features.Media.KmhVideoCache.FolderOverride = sandbox;
                Features.Media.KmhVideoCache.EnsureFolder();
                string oldFile = Features.Media.KmhVideoCache.PathFor("selftestold1", 144);
                string newFile = Features.Media.KmhVideoCache.PathFor("selftestnew1", 144);
                File.WriteAllText(oldFile, "x");
                File.WriteAllText(newFile, "x");
                File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddHours(-(Math.Max(1, keptHours) + 24)));
                Features.Media.KmhVideoCache.Touch(newFile);
                Features.Media.KmhVideoCache.Sweep();
                sweepRules = keptHours <= 0 || (!File.Exists(oldFile) && File.Exists(newFile));
            }
            catch { sweepRules = false; }
            finally
            {
                Features.Media.KmhVideoCache.FolderOverride = null;
                try { Directory.Delete(sandbox, true); } catch { }
            }
            r.Add(("video: the sweep drops a stale file and keeps the one in use", sweepRules, $"kept {keptHours}h"));

            // The title is only learned while fetching, so a cache hit an hour later has nothing else to show.
            bool titleRules;
            string titleBox = Path.Combine(Path.GetTempPath(), "KMH-SelfTest-VideoTitle");
            try
            {
                Features.Media.KmhVideoCache.FolderOverride = titleBox;
                Features.Media.KmhVideoCache.EnsureFolder();
                string clip = Features.Media.KmhVideoCache.PathFor("selftesttitl", 144);
                File.WriteAllText(clip, "x");
                Features.Media.KmhVideoCache.RememberTitle(clip, "A Video Someone Posted");
                titleRules = Features.Media.KmhVideoCache.TitleOf(clip) == "A Video Someone Posted"
                          && Features.Media.KmhVideoCache.TitleOf(clip + "-none") == "";
                Features.Media.KmhVideoCache.RememberTitle(clip, "   ");
                titleRules = titleRules && Features.Media.KmhVideoCache.TitleOf(clip) == "A Video Someone Posted";
            }
            catch { titleRules = false; }
            finally
            {
                Features.Media.KmhVideoCache.FolderOverride = null;
                try { Directory.Delete(titleBox, true); } catch { }
            }
            r.Add(("video: a cached video remembers its title, and an empty one never overwrites it", titleRules, ""));

            // Every fetch is a process, a download and a merge, so a hand-edited number cannot ask for a hundred.
            var wildFetches = new Features.Media.MediaConfig { VideoMaxConcurrentFetches = 999 };
            var noFetches   = new Features.Media.MediaConfig { VideoMaxConcurrentFetches = 0 };
            wildFetches.Clamp();
            noFetches.Clamp();
            r.Add(("video: concurrent fetches are held between one and a number a server can survive",
                   wildFetches.VideoMaxConcurrentFetches <= 8 && noFetches.VideoMaxConcurrentFetches >= 1
                   && new Features.Media.MediaConfig().VideoMaxConcurrentFetches == 2,
                   $"{wildFetches.VideoMaxConcurrentFetches}/{noFetches.VideoMaxConcurrentFetches}"));

            var wildAge = new Features.Media.MediaConfig { VideoCacheHours = 999_999, VideoMaxFileMegabytes = 999_999 };
            var noAge   = new Features.Media.MediaConfig { VideoCacheHours = 0 };
            wildAge.Clamp();
            noAge.Clamp();
            r.Add(("video: a hand-edited cache age is held in a sane range, and zero still means no age limit",
                   wildAge.VideoCacheHours > 0 && wildAge.VideoCacheHours <= 8_760
                   && wildAge.VideoMaxFileMegabytes <= 32_768
                   && noAge.VideoCacheHours == 0,
                   wildAge.VideoCacheHours + "h"));

            // Without this an owner has to know to flatten the ffmpeg archive they unzipped into Tools.
            string toolRoot = Path.Combine(Path.GetTempPath(), "kmh-toolscan-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string nested = Path.Combine(toolRoot, "Tools", "ffmpeg-master-latest-win64-lgpl", "bin");
                Directory.CreateDirectory(nested);
                string[] dirs = Features.Media.KmhVideoHelper.SearchDirsForTest(toolRoot);
                bool loose  = Array.IndexOf(dirs, Path.Combine(toolRoot, "Tools")) >= 0;
                bool deep   = Array.IndexOf(dirs, nested) >= 0;
                bool oneSub = Array.IndexOf(dirs, Path.Combine(toolRoot, "Tools", "ffmpeg-master-latest-win64-lgpl")) >= 0;
                r.Add(("video: tools are found loose in Tools, and inside an archive folder unzipped there",
                       loose && deep && oneSub,
                       $"loose={loose} sub={oneSub} sub/bin={deep}"));
            }
            finally { try { Directory.Delete(toolRoot, true); } catch { } }

            return r;
        }

        // A genuine WebP, so the decoder is fed the real format with no network and no checked-in binary.
        private static Features.Media.MediaConfig Clamped(Features.Media.MediaConfig c) { c.Clamp(); return c; }

        private static byte[] MakeWebp(int frames, int w, int h)
        {
            // Deliberately noisy: a flat-colour fixture compresses so far that the output-cap check proves nothing.
            using var image = new Image<Rgba32>(w, h);
            Paint(image.Frames.RootFrame, 0);
            for (int f = 1; f < frames; f++)
            {
                using var frame = new Image<Rgba32>(w, h);
                Paint(frame.Frames.RootFrame, f);
                image.Frames.AddFrame(frame.Frames.RootFrame);
            }
            foreach (ImageFrame<Rgba32> fr in image.Frames)
            {
                try { fr.Metadata.GetWebpMetadata().FrameDelay = 80; } catch { }
            }
            using var ms = new MemoryStream();
            image.Save(ms, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
            return ms.ToArray();
        }

        // A deterministic scatter - same bytes every run, but nothing an encoder can collapse.
        private static void Paint(ImageFrame<Rgba32> frame, int seed)
        {
            uint state = (uint)(seed * 2654435761u + 12345u);
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                {
                    state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                    frame[x, y] = new Rgba32((byte)state, (byte)(state >> 8), (byte)(state >> 16), 255);
                }
        }
    }
}
