// Real RWT assemblies, not look-alike source: proves the presence patch target exists and takes a Harmony prefix.
// One load context per folder keeps two builds of one version apart; --rpc supplies a DiscordRPC.dll a drop omits.
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using HarmonyLib;

static class Program
{
    static readonly string[] ManagerNames =
    {
        "RTClient.Managers.DiscordManager", "GameClient.Managers.DiscordManager", "DiscordManager",
    };

    static string _rpcFallback = "";

    static int Main(string[] args)
    {
        var folders = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--rpc" && i + 1 < args.Length) _rpcFallback = args[++i];
            else folders.Add(args[i]);
        }
        if (folders.Count == 0) { Console.WriteLine("usage: rwt-discord-matrix [--rpc <folder>] <assembly folder> ..."); return 2; }

        int rows = 0, installed = 0, absent = 0, failed = 0;
        foreach (string folder in folders)
        {
            rows++;
            Console.WriteLine(new string('-', 100));
            Console.WriteLine(folder);
            if (!Directory.Exists(folder)) { Console.WriteLine("  MISSING FOLDER"); failed++; continue; }

            string? client = Directory.GetFiles(folder, "*.dll")
                .FirstOrDefault(f => Path.GetFileName(f) is "RTClient.dll" or "GameClient.dll");
            if (client == null) { Console.WriteLine("  no RTClient.dll / GameClient.dll here"); failed++; continue; }

            Console.WriteLine($"  client assembly : {Path.GetFileName(client)}");
            Console.WriteLine($"  sha-256         : {Sha(client)}");
            Console.WriteLine($"  file size       : {new FileInfo(client).Length:n0} bytes");

            string? rpc = Path.Combine(folder, "DiscordRPC.dll");
            Console.WriteLine($"  DiscordRPC.dll  : {(File.Exists(rpc) ? Sha(rpc) : "ABSENT")}");

            switch (Inspect(folder, client))
            {
                case Outcome.Installed: installed++; break;
                case Outcome.NoManager: absent++; break;
                default: failed++; break;
            }
        }

        Console.WriteLine(new string('=', 100));
        Console.WriteLine($"sets: {rows}   patch installs: {installed}   no DiscordManager: {absent}   failures: {failed}");
        // A set that has the manager but refuses the patch is the failure this tool exists to catch.
        return failed == 0 ? 0 : 1;
    }

    enum Outcome { Installed, NoManager, Failed }

    static Outcome Inspect(string folder, string clientPath)
    {
        var ctx = new AssemblyLoadContext("probe-" + folder.GetHashCode(), isCollectible: false);
        ctx.Resolving += (c, name) =>
        {
            string p = Path.Combine(folder, name.Name + ".dll");
            if (File.Exists(p)) return c.LoadFromAssemblyPath(p);
            if (_rpcFallback.Length > 0)
            {
                string f = Path.Combine(_rpcFallback, name.Name + ".dll");
                if (File.Exists(f)) return c.LoadFromAssemblyPath(f);
            }
            return null;
        };

        Assembly asm;
        try { asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(clientPath)); }
        catch (Exception ex) { Console.WriteLine($"  LOAD FAILED    : {ex.GetType().Name}: {ex.Message}"); return Outcome.Failed; }

        AssemblyName an = asm.GetName();
        Console.WriteLine($"  assembly name   : {an.Name}");
        Console.WriteLine($"  assembly version: {an.Version}");
        Console.WriteLine($"  informational   : {asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(none)"}");

        Type? manager = null;
        foreach (string n in ManagerNames)
        {
            try { manager = asm.GetType(n, throwOnError: false); } catch { }
            if (manager != null) break;
        }
        if (manager == null)
        {
            // A generation with no manager is a real answer: there is nothing to suppress and nothing to patch.
            Console.WriteLine("  DiscordManager  : ABSENT - nothing for KMH to suppress on this generation");
            return Outcome.NoManager;
        }

        Console.WriteLine($"  DiscordManager  : {manager.FullName}");
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        foreach (string m in new[] { "StartPresence", "StopPresence", "SetPresence" })
        {
            MethodInfo? mi = null;
            try { mi = manager.GetMethod(m, Any); } catch { }
            Console.WriteLine($"    {m,-14}: {(mi == null ? "absent" : Describe(mi))}");
        }
        try
        {
            PropertyInfo? rp = manager.GetProperty("RPClient", Any);
            Console.WriteLine($"    RPClient      : {(rp == null ? "absent" : rp.PropertyType.Name + (rp.GetGetMethod(true)?.IsStatic == true ? " (static)" : ""))}");
        }
        catch (Exception ex) { Console.WriteLine($"    RPClient      : unreadable ({ex.GetType().Name})"); }

        MethodInfo? target = manager.GetMethod("StartPresence", Any);
        if (target == null) { Console.WriteLine("  PATCH          : NO TARGET - KMH would suppress nothing here"); return Outcome.Failed; }

        try
        {
            var harmony = new Harmony("kmh.matrix." + an.Name + "." + Math.Abs(folder.GetHashCode()));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(NoOpPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
            bool present = Harmony.GetPatchInfo(target)?.Prefixes.Count > 0;
            harmony.UnpatchAll(harmony.Id);
            Console.WriteLine($"  PATCH          : {(present ? "INSTALLED and removed cleanly" : "PATCH REPORTED NO PREFIX")}");
            return present ? Outcome.Installed : Outcome.Failed;
        }
        catch (Exception ex) { Console.WriteLine($"  PATCH          : FAILED {ex.GetType().Name}: {ex.Message}"); return Outcome.Failed; }
    }

    // Never allowed to run: the real method opens a Discord connection.
    static bool NoOpPrefix() => false;

    static string Describe(MethodInfo mi)
        => (mi.IsStatic ? "static " : "instance ") + mi.ReturnType.Name + "("
           + string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name)) + ")";

    static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
