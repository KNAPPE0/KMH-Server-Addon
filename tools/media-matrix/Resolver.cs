using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// The real server pipeline against live hosts, through the real client decoder: only the TCP hop is missing.
static class Resolver
{
    static Assembly _srv, _cli;
    static object _chatCfg, _mediaCfg;

    public static async Task Run(Assembly srv, Assembly cli, object chatCfg)
    {
        _srv = srv; _cli = cli; _chatCfg = chatCfg;
        _mediaCfg = NewMediaConfig();

        Console.WriteLine();
        Console.WriteLine("#".PadRight(100, '#'));
        Console.WriteLine("# SERVER MEDIA RESOLVER");
        Console.WriteLine("#".PadRight(100, '#'));

        await Routing();
        await Security();
        await EndToEnd();
    }


    static Task Routing()
    {
        Console.WriteLine();
        Console.WriteLine("=== routing: only what the client cannot decode takes the server path ===");
        foreach (var (label, url) in new[]
        {
            ("PNG",            "https://cdn.discordapp.com/attachments/1/2/shot.png"),
            ("JPEG",           "https://cdn.discordapp.com/attachments/1/2/photo.jpg"),
            ("GIF",            "https://media.tenor.com/abc/thing.gif"),
            ("BMP",            "https://cdn.discordapp.com/attachments/1/2/old.bmp"),
            ("WebP",           "https://cdn.discordapp.com/attachments/1/2/pic.webp"),
            ("proxied WebP",   "https://images-ext-1.discordapp.net/external/h/https/static.klipy.com/ii/a/b/c.webp?format=png"),
            ("MP4 (video)",    "https://cdn.discordapp.com/attachments/1/2/clip.mp4"),
        })
        {
            bool needs = (bool)Call(_srv, "KMHServerAddon.Features.Chat.ChatMediaUrl", "NeedsResolver", new[] { _chatCfg, url });
            string from = needs ? (string)Call(_srv, "KMHServerAddon.Features.Chat.ChatMediaUrl", "ResolverSourceFor", new[] { _chatCfg, url }) : "";
            Console.WriteLine($"  {label,-14} -> {(needs ? "SERVER RESOLVER" : "direct client fetch")}"
                            + (needs ? $"   fetches {Short(from)}" : ""));
        }
        return Task.CompletedTask;
    }


    static async Task Security()
    {
        Console.WriteLine();
        Console.WriteLine("=== security: the fetcher refuses before it connects ===");

        // Real public DNS names that answer with loopback/RFC1918 - exactly the shape an SSRF attempt takes.
        foreach (var (label, url) in new[]
        {
            ("loopback by name",     "https://localhost/a.webp"),
            ("loopback by literal",  "https://127.0.0.1/a.webp"),
            ("private by literal",   "https://10.0.0.1/a.webp"),
            ("cloud metadata",       "https://169.254.169.254/latest/meta-data/a.webp"),
            ("IPv6 loopback",        "https://[::1]/a.webp"),
            ("plain http",           "http://cdn.discordapp.com/a.webp"),
            ("host not allow-listed","https://evil.example/a.webp"),
        })
        {
            var res = await Fetch(url);
            Console.WriteLine($"  {label,-22} -> {(Ok(res) ? "*** FETCHED - THIS IS A BUG ***" : Err(res))}");
        }

        Console.WriteLine();
        Console.WriteLine("  with the allow-list deliberately opened to private hosts, the address guard still refuses:");
        object openCfg = OpenConfig(new[] { "localhost", "127.0.0.1", "10.0.0.1", "169.254.169.254" });
        object saved = _chatCfg;
        _chatCfg = openCfg;
        foreach (var (label, url) in new[]
        {
            ("allow-listed localhost",   "https://localhost/a.webp"),
            ("allow-listed 127.0.0.1",   "https://127.0.0.1/a.webp"),
            ("allow-listed 10.0.0.1",    "https://10.0.0.1/a.webp"),
            ("allow-listed metadata IP",  "https://169.254.169.254/a.webp"),
        })
        {
            var res2 = await Fetch(url);
            Console.WriteLine($"    {label,-24} -> {(Ok(res2) ? "*** FETCHED - THIS IS A BUG ***" : Err(res2))}");
        }
        _chatCfg = saved;
        Console.WriteLine();

        // A redirect is re-vetted by the full policy, so a redirect off the allow-list dies at the hop.
        var redir = await Fetch("https://httpbin.org/redirect-to?url=https%3A%2F%2F127.0.0.1%2Fa.webp");
        Console.WriteLine($"  {"redirect off-list",-22} -> {(Ok(redir) ? "*** FETCHED - THIS IS A BUG ***" : Err(redir))}");
    }


    static async Task EndToEnd()
    {
        Console.WriteLine();
        Console.WriteLine("=== end to end: real bytes, real conversion, real client reassembly and decode ===");

        await One("Klipy ANIMATED WebP (the case this was built for)",
                  "https://static.klipy.com/ii/4493325008d34b7bf8cd6813cd5c1619/67/39/1jF34ftpxOU9fiYj177.webp");

        await One("Klipy WebP via Discord's proxy (flattened by the proxy)",
                  "https://images-ext-1.discordapp.net/external/E9MtZmGUKrzHpoVlM0Zwz46Aa4qUGXTB9sgjmbCNub4/https/"
                  + "static.klipy.com/ii/8ce8357c78ea940b9c2015daf05ce1a5/9b/0e/p5dC8SCQ.webp");

        await One("a GIF fed to the resolver (should pass through untouched)",
                  "https://media.giphy.com/media/v1.Y2lkPTc5MGI3NjExYTR3a3o3amc5NHNyYmlhaWpzM3d1MGx3a2pkcmlqNGI2b3FrenF1bSZlcD12MV9naWZzX3RyZW5kaW5nJmN0PWc/joSNxeswxuc74Juo8X/giphy.gif");

        await One("an HTML page fed to the resolver", "https://tenor.com/view/logan-paul-sorry-gif-22857221");
    }

    static async Task One(string label, string url)
    {
        Console.WriteLine();
        Console.WriteLine("  " + label);
        Console.WriteLine("    source: " + Short(url));

        var fetched = await Fetch(url);
        if (!Ok(fetched)) { Console.WriteLine("    -> refused at fetch: " + Err(fetched)); return; }

        byte[] raw = (byte[])Prop(fetched, "Bytes");
        Console.WriteLine($"    fetch : {raw.Length} bytes, type={Prop(fetched, "ContentType")}, hops={Prop(fetched, "Hops")}");

        object conv = Call(_srv, "KMHServerAddon.Features.Media.KmhMediaTranscode", "Convert", new object[] { raw, _mediaCfg });
        if (!(bool)Prop(conv, "Ok")) { Console.WriteLine("    -> refused at convert: " + Prop(conv, "Error")); return; }

        byte[] outBytes = (byte[])Prop(conv, "Bytes");
        Console.WriteLine($"    convert: {Prop(conv, "Mime")} {Prop(conv, "Width")}x{Prop(conv, "Height")} "
                        + $"{Prop(conv, "Frames")} frame(s), {outBytes.Length} bytes"
                        + ((bool)Prop(conv, "PassedThrough") ? "  (passed through unchanged)" : ""));

        int chunkBytes = (int)Field(_srv, "KMHServerAddon.Features.Media.KmhMediaHandler", "ChunkBytes");
        string hash = (string)Call(_srv, "KMHServerAddon.Features.Media.KmhMediaCache", "HashOf", new object[] { outBytes });
        int chunks = (outBytes.Length + chunkBytes - 1) / chunkBytes;

        object asm = CallStatic(_cli, "KMHPatch.Features.Chat.ChatMediaClient+Assembly", "Begin",
                                new object[] { (string)Prop(conv, "Mime"), outBytes.Length, chunks, hash, 32 * 1024 * 1024, null });
        if (asm == null) { Console.WriteLine("    -> client refused the meta"); return; }

        MethodInfo add = asm.GetType().GetMethod("Add");
        for (int i = 0; i < chunks; i++)
        {
            int at = i * chunkBytes, len = Math.Min(chunkBytes, outBytes.Length - at);
            byte[] part = new byte[len];
            Buffer.BlockCopy(outBytes, at, part, 0, len);
            add.Invoke(asm, new object[] { i, chunks, part });
        }
        byte[] rebuilt = (byte[])asm.GetType().GetMethod("Take").Invoke(asm, null);
        if (rebuilt == null)
        {
            Console.WriteLine("    -> client rejected the stream: " + asm.GetType().GetField("Error").GetValue(asm));
            return;
        }
        Console.WriteLine($"    client : reassembled {chunks} chunk(s), {rebuilt.Length} bytes, hash verified");

        bool isGif = (bool)Call(_cli, "KMHPatch.Features.Chat.KmhGif", "LooksLikeGif", new object[] { rebuilt });
        if (isGif)
        {
            object anim = Call(_cli, "KMHPatch.Features.Chat.KmhGif", "DecodeFrames", new object[] { rebuilt });
            int n = 0, w = 0, h = 0;
            if (anim != null)
            {
                Type t = anim.GetType();
                object list = t.GetField("Frames")?.GetValue(anim) ?? t.GetProperty("Frames")?.GetValue(anim);
                if (list is ICollection col) n = col.Count;
                w = (int)(t.GetField("Width")?.GetValue(anim) ?? t.GetProperty("Width")?.GetValue(anim) ?? 0);
                h = (int)(t.GetField("Height")?.GetValue(anim) ?? t.GetProperty("Height")?.GetValue(anim) ?? 0);
            }
            Console.WriteLine($"    DECODED: {w}x{h}, {n} frame(s)  ->  {(n > 1 ? "ANIMATED IN GAME" : "single frame")}");
        }
        else
        {
            string fmt = (string)Call(_cli, "KMHPatch.Features.Chat.ChatImageCache", "DescribeFormat", new object[] { rebuilt });
            Console.WriteLine($"    DECODED: {fmt} still image");
        }
    }


    static async Task<object> Fetch(string url)
    {
        Type t = _srv.GetType("KMHServerAddon.Features.Media.KmhMediaFetch", true);
        MethodInfo m = t.GetMethod("GetAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var task = (Task)m.Invoke(null, new object[] { url, _mediaCfg, _chatCfg, CancellationToken.None });
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result").GetValue(task);
    }

    static bool Ok(object result) => (bool)Prop(result, "Ok");
    static string Err(object result) => (string)Prop(result, "Error");

    // Deliberately permits hosts it never should, proving the address guard is a separate gate from the allow-list.
    static object OpenConfig(string[] hosts)
    {
        Type t = _srv.GetType("KMHServerAddon.Features.Chat.ChatConfig", true);
        object cfg = Activator.CreateInstance(t, nonPublic: true);
        t.GetProperty("AllowImagePreviews").SetValue(cfg, true);
        t.GetProperty("AllowTypedImageUrls").SetValue(cfg, true);
        t.GetMethod("Clamp").Invoke(cfg, null);
        t.GetProperty("ImageHostAllowList").SetValue(cfg, hosts);
        return cfg;
    }

    static object NewMediaConfig()
    {
        Type t = _srv.GetType("KMHServerAddon.Features.Media.MediaConfig", true);
        object cfg = Activator.CreateInstance(t, nonPublic: true);
        t.GetMethod("Clamp").Invoke(cfg, null);
        return cfg;
    }

    static object Prop(object o, string name)
        => o.GetType().GetField(name)?.GetValue(o) ?? o.GetType().GetProperty(name)?.GetValue(o);

    static object Field(Assembly asm, string type, string name)
        => asm.GetType(type, true).GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetRawConstantValue();

    static object Call(Assembly asm, string type, string method, object[] args)
    {
        Type t = asm.GetType(type, true);
        MethodInfo m = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .First(x => x.Name == method && x.GetParameters().Length == args.Length);
        return m.Invoke(null, args);
    }

    static object CallStatic(Assembly asm, string type, string method, object[] args)
    {
        Type t = asm.GetType(type.Replace('+', '+'), true);
        MethodInfo m = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .First(x => x.Name == method);
        return m.Invoke(null, args);
    }

    static string Short(string s) => s == null ? "(null)" : (s.Length <= 92 ? s : s.Substring(0, 89) + "...");
}
