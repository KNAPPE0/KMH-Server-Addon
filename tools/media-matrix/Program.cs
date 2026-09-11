using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

// matrix.exe <serverDll> <clientDll> <probePath...> - the offline suites check the rules, this checks live hosts.
class Program
{
    static string[] _probe = Array.Empty<string>();
    static Assembly _srv, _cli;
    static object _cfg;

    static async Task<int> Main(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("usage: matrix <serverDll> <clientDll> <probe...>"); return 2; }
        _probe = a.Skip(2).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        _srv = Assembly.LoadFrom(a[0]);
        _cli = Assembly.LoadFrom(a[1]);
        Console.WriteLine($"server: {_srv.GetName().Name} v{_srv.GetName().Version}");
        Console.WriteLine($"client: {_cli.GetName().Name} v{_cli.GetName().Version}");
        Console.WriteLine();

        _cfg = BuildConfig();

        foreach (Case c in Cases()) await Run(c);

        await Resolver.Run(_srv, _cli, _cfg);

        Console.WriteLine();
        Console.WriteLine($"rows: {_rows}   fetched: {_fetched}   shown: {_shown}   animated: {_animated}   refused-before-fetch: {_refused}");
        return 0;
    }


    sealed class Case
    {
        public string Name;
        public string Proxy;        // Discord's re-hosted copy, as an embed would carry it
        public string Original;     // the site the link came from
        public string Typed;        // a url a player typed in game instead
        public bool   KnownImage = true;
        public string Expect;       // what this row is here to demonstrate
    }

    static IEnumerable<Case> Cases()
    {
        // Real url shapes taken from relayed chat: an invented fixture passes while real traffic fails.
        yield return new Case {
            Name = "Discord PNG attachment (signature expired - Discord links are time-limited)",
            Proxy = "https://cdn.discordapp.com/attachments/1379170256672522290/1538634661687984138/"
                  + "E5B174A9-1453-493F-A84D-725DA69EF8F8.png",
            Expect = "named HTTP failure, not a decode error" };

        yield return new Case {
            Name = "JPEG source via Discord proxy (asked for png)",
            Proxy = "https://images-ext-1.discordapp.net/external/ezknekXkV55IKfCMfz5hGncwHBuZhngCqkVG9fNC64k/https/"
                  + "i.ytimg.com/vi/mkggXE5e2yk/maxresdefault.jpg",
            Expect = "shown as a still image" };

        yield return new Case {
            Name = "still GIF (giphy preview)",
            Proxy = "https://i.giphy.com/media/joSNxeswxuc74Juo8X/giphy_s.gif",
            Expect = "shown as a gif, animated or not" };

        yield return new Case {
            Name = "direct GIF (giphy media)",
            Proxy = "https://media.giphy.com/media/v1.Y2lkPTc5MGI3NjExYTR3a3o3amc5NHNyYmlhaWpzM3d1MGx3a2pkcmlqNGI2b3Fr"
                  + "enF1bSZlcD12MV9naWZzX3RyZW5kaW5nJmN0PWc/joSNxeswxuc74Juo8X/giphy.gif",
            Expect = "ANIMATED - the known-good case" };

        yield return new Case {
            Name = "Tenor gif via Discord proxy",
            Proxy = "https://images-ext-1.discordapp.net/external/9hQQWo74Xt5awINwXQjXoQyTwVr8XN0SaLymwEnKqFI/https/"
                  + "media1.tenor.com/m/e8s1VZmddzwAAAAC/cry-bozo.gif",
            Original = "https://media1.tenor.com/m/e8s1VZmddzwAAAAC/cry-bozo.gif",
            Expect = "ANIMATED - original gif preferred over the flattening proxy" };

        yield return new Case {
            Name = "Tenor gif, proxy only (no original)",
            Proxy = "https://images-ext-1.discordapp.net/external/eyKzkQYh-GlP98Pg4CjxDbsLlORjnHiT1UmtOsQnIFs/https/"
                  + "media1.tenor.com/m/mrzz_hAH_7cAAAAC/we-are-officially-in-crisis-mode-katherine-hastings.gif",
            Expect = "ANIMATED - proxy passes gif through untouched" };

        yield return new Case {
            Name = "Klipy animated WebP via Discord proxy",
            Proxy = "https://images-ext-1.discordapp.net/external/E9MtZmGUKrzHpoVlM0Zwz46Aa4qUGXTB9sgjmbCNub4/https/"
                  + "static.klipy.com/ii/8ce8357c78ea940b9c2015daf05ce1a5/9b/0e/p5dC8SCQ.webp",
            Original = "https://static.klipy.com/ii/8ce8357c78ea940b9c2015daf05ce1a5/9b/0e/p5dC8SCQ.webp",
            Expect = "STILL png - the animated-webp gap" };

        yield return new Case {
            Name = "Klipy raw animated WebP (original only)",
            Original = "https://static.klipy.com/ii/4493325008d34b7bf8cd6813cd5c1619/67/39/1jF34ftpxOU9fiYj177.webp",
            Expect = "named as animated webp, not a generic failure" };

        yield return new Case {
            Name = "Klipy share PAGE",
            Proxy = "https://images-ext-1.discordapp.net/external/hash/https/klipy.com/gifs/lloyd-frontera-tged",
            Original = "https://klipy.com/gifs/lloyd-frontera-tged",
            Expect = "REFUSED before any fetch - names no media file" };

        yield return new Case {
            Name = "Tenor share PAGE, typed by a player",
            Typed = "https://tenor.com/view/logan-paul-sorry-forgive-lapse-in-judgement-gif-22857221",
            KnownImage = false,
            Expect = "REFUSED before any fetch" };

        yield return new Case {
            Name = "untrusted host",
            Proxy = "https://evil.example/a/b.png",
            Expect = "REFUSED - host not allow-listed" };

        yield return new Case {
            Name = "plain http from an allowed host",
            Proxy = "http://cdn.discordapp.com/a/b.png",
            Expect = "REFUSED - not https" };

        yield return new Case {
            Name = "broken url",
            Proxy = "https://",
            Expect = "REFUSED - unparseable" };

        yield return new Case {
            Name = "expired Discord CDN link (signed url past its window)",
            Proxy = "https://cdn.discordapp.com/attachments/1379170256672522290/1538591956387500082/"
                  + "510A6AC6-BE83-40CF-B061-8485532BC137.png?ex=6a833cec&is=6a81eb6c"
                  + "&hm=83707c6203bee04258d488529b826cf700bc5995ef755b211f9589b49682b914&",
            Expect = "named HTTP failure, not a decode error" };

        yield return new Case {
            Name = "video attachment (mp4)",
            Proxy = "https://cdn.discordapp.com/attachments/1379170256672522290/1538606765346914456/"
                  + "file_example_MP4_480_1_5MG.mp4",
            Expect = "classified as VIDEO - goes to the player, never the image decoder" };

        yield return new Case {
            Name = "oversized media (18MB mp4 as an image)",
            Proxy = "https://cdn.discordapp.com/attachments/1379170256672522290/1538606858275782758/"
                  + "file_example_MP4_1920_18MG.mp4",
            Expect = "VIDEO - size cap does not apply, it is never downloaded" };
    }


    static int _rows, _fetched, _shown, _animated, _refused;

    static async Task Run(Case c)
    {
        _rows++;
        Console.WriteLine("=".PadRight(100, '='));
        Console.WriteLine($"CASE  {c.Name}");
        Console.WriteLine($"      expect: {c.Expect}");

        string selected, why;
        if (c.Typed != null)
        {
            selected = c.Typed; why = "typed by a player";
        }
        else
        {
            object[] args = { _cfg, c.Proxy, c.Original, null };
            selected = (string)Invoke(_srv, "KMHServerAddon.Features.Chat.ChatMediaSelection", "Choose", args);
            why = (string)args[3];
        }
        Console.WriteLine($"  1. select   : {(string.IsNullOrEmpty(selected) ? "(none)" : Short(selected))}");
        Console.WriteLine($"                {why}");

        if (string.IsNullOrEmpty(selected)) { _refused++; Console.WriteLine("  -> NOTHING REACHES CHAT"); Console.WriteLine(); return; }

        object[] vargs = { _cfg, selected, c.Typed != null, c.KnownImage, null };
        string vetted = (string)Invoke(_srv, "KMHServerAddon.Features.Chat.ChatImagePolicy", "VetMedia", vargs);
        bool isVideo = (bool)vargs[4];

        object[] rargs = { _cfg, selected, c.Typed != null, c.KnownImage, null };
        Invoke(_srv, "KMHServerAddon.Features.Chat.ChatImagePolicy", "Vet", rargs, 5);
        string reason = (string)rargs[4];

        Console.WriteLine($"  2. policy   : {(string.IsNullOrEmpty(vetted) ? "REFUSED - " + reason : Short(vetted))}");
        if (string.IsNullOrEmpty(vetted)) { _refused++; Console.WriteLine("  -> NOTHING REACHES CHAT"); Console.WriteLine(); return; }
        Console.WriteLine($"  3. in chat  : image={!isVideo}  video={isVideo}");

        if (isVideo)
        {
            _shown++;
            Console.WriteLine("  -> PRESENTED as a video: click-to-play, streamed, never downloaded to disk");
            Console.WriteLine();
            return;
        }

        _fetched++;
        (HttpStatusCode status, string ctype, byte[] body, string err, string finalUrl) = await Fetch(vetted);
        Console.WriteLine($"  4. fetch    : HTTP {(int)status} {ctype} {body?.Length ?? 0} bytes"
                        + (finalUrl != null && finalUrl != vetted ? $"  (redirected -> {Short(finalUrl)})" : "")
                        + (err != null ? $"  transport error: {err}" : ""));

        if (err != null || (int)status >= 400)
        {
            string msg = (string)Invoke(_cli, "KMHPatch.Features.Chat.ChatImageCache", "HttpFailure",
                                        new object[] { (long)status, err ?? "" });
            Console.WriteLine($"  -> SHOWN AS FAILURE: \"{msg}\"");
            Console.WriteLine();
            return;
        }

        string fmt = (string)Invoke(_cli, "KMHPatch.Features.Chat.ChatImageCache", "DescribeFormat", new object[] { body });
        bool html = (bool)Invoke(_cli, "KMHPatch.Features.Chat.ChatImageCache", "LooksLikeHtml", new object[] { body, ctype });
        bool isGif = (bool)Invoke(_cli, "KMHPatch.Features.Chat.KmhGif", "LooksLikeGif", new object[] { body });
        Console.WriteLine($"  5. detected : {fmt}   html={html}   gif={isGif}");

        if (html) { Console.WriteLine("  -> SHOWN AS FAILURE: \"that link is a web page, not an image file\""); Console.WriteLine(); return; }

        if (isGif)
        {
            object raw = Invoke(_cli, "KMHPatch.Features.Chat.KmhGif", "DecodeFrames", new object[] { body });
            int frames = 0, w = 0, h = 0;
            if (raw != null)
            {
                Type rt = raw.GetType();
                object list = rt.GetField("Frames")?.GetValue(raw) ?? rt.GetProperty("Frames")?.GetValue(raw);
                if (list is System.Collections.ICollection col) frames = col.Count;
                w = (int)(rt.GetField("Width")?.GetValue(raw) ?? rt.GetProperty("Width")?.GetValue(raw) ?? 0);
                h = (int)(rt.GetField("Height")?.GetValue(raw) ?? rt.GetProperty("Height")?.GetValue(raw) ?? 0);
            }
            _shown++;
            if (frames > 1) _animated++;
            Console.WriteLine($"  -> PRESENTED as {(frames > 1 ? "an ANIMATED gif" : "a single-frame gif")}: {w}x{h}, {frames} frame(s)");
            Console.WriteLine();
            return;
        }

        string unsupported = (string)Invoke(_cli, "KMHPatch.Features.Chat.ChatImageCache", "UnsupportedFormat",
                                            new object[] { body }, nonPublic: true);
        if (unsupported != null) { Console.WriteLine($"  -> SHOWN AS FAILURE: \"{unsupported}\""); Console.WriteLine(); return; }

        _shown++;
        Console.WriteLine($"  -> PRESENTED as a still image ({fmt})");
        Console.WriteLine();
    }


    static async Task<(HttpStatusCode, string, byte[], string, string)> Fetch(string url)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        // The same user agent a UnityWebRequest presents, so a host that gates on it behaves the same here.
        http.DefaultRequestHeaders.Add("User-Agent", "UnityPlayer/2019.4.30f1 (UnityWebRequest/1.0, libcurl/7.52.0-DEV)");
        try
        {
            using HttpResponseMessage r = await http.GetAsync(url);
            byte[] body = await r.Content.ReadAsByteArrayAsync();
            return (r.StatusCode, r.Content.Headers.ContentType?.ToString() ?? "", body, null,
                    r.RequestMessage?.RequestUri?.ToString());
        }
        catch (Exception ex) { return (0, "", null, ex.GetBaseException().Message, null); }
    }

    static object BuildConfig()
    {
        Type t = _srv.GetType("KMHServerAddon.Features.Chat.ChatConfig", true);
        object cfg = Activator.CreateInstance(t, nonPublic: true);
        t.GetProperty("AllowImagePreviews").SetValue(cfg, true);
        t.GetProperty("AllowTypedImageUrls").SetValue(cfg, true);
        t.GetProperty("AllowVideoLinks").SetValue(cfg, true);
        // Mirrors the live Chat.json allow-list; without it the resolver only reaches a flattened copy.
        var hosts = (string[])t.GetMethod("DefaultImageHosts", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                               .Invoke(null, null);
        t.GetProperty("ImageHostAllowList").SetValue(cfg, hosts.Concat(new[] { "klipy.com", "static.klipy.com" }).ToArray());
        t.GetMethod("Clamp", BindingFlags.Public | BindingFlags.Instance).Invoke(cfg, null);
        return cfg;
    }

    static object Invoke(Assembly asm, string type, string method, object[] args, int argc = -1, bool nonPublic = false)
    {
        Type t = asm.GetType(type, true);
        var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo m = t.GetMethods(flags)
                        .Where(x => x.Name == method && x.GetParameters().Length == (argc < 0 ? args.Length : argc))
                        .OrderByDescending(x => x.GetParameters().Length)
                        .First();
        return m.Invoke(null, args);
    }

    static string Short(string s) => s == null ? "(null)" : (s.Length <= 96 ? s : s.Substring(0, 93) + "...");

    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        foreach (string dir in _probe)
        {
            string f = Path.Combine(dir, name + ".dll");
            if (File.Exists(f)) { try { return Assembly.LoadFrom(f); } catch { } }
        }
        return null;
    }
}
