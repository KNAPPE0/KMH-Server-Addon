using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// configgen <KMHServerAddon.dll> <probe-dir>... - a fresh install is the one config nobody tests by hand.
class P
{
    static string[] _probe;

    static int Main(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("usage: configgen <addon.dll> <probe-dir>..."); return 2; }
        string target = a[0];
        _probe = a.Skip(1).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        Assembly asm = Assembly.LoadFrom(target);

        string root = Path.Combine(Path.GetTempPath(), "kmh-configgen-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);

        // Pointing Master.MainPath at a temp folder is what makes this a real first boot, not a read of my own server.
        if (!TrySetMainPath(root, out string how)) { Console.Error.WriteLine("could not set Master.MainPath: " + how); return 2; }
        Console.WriteLine("data root: " + root + "  (" + how + ")");
        Console.WriteLine();

        var generators = asm.GetTypes()
            .Select(t => new { T = t, M = t.GetMethod("EnsureGenerated", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) })
            .Where(x => x.M != null && x.M.GetParameters().Length == 0)
            .OrderBy(x => x.T.Name)
            .ToList();

        int made = 0, failed = 0;
        foreach (var g in generators)
        {
            try { g.M.Invoke(null, null); made++; }
            catch (Exception ex) { failed++; Console.WriteLine("  GENERATE FAILED  " + g.T.Name + " -> " + ex.GetBaseException().Message); }
        }
        Console.WriteLine($"generators: {made} ran, {failed} failed");
        Console.WriteLine();

        string cfgDir = Path.Combine(root, "KMH-Data", "Config");
        if (!Directory.Exists(cfgDir)) { Console.Error.WriteLine("no Config directory was created"); return 1; }

        string[] files = Directory.GetFiles(cfgDir, "*.json", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        int fields = 0;
        foreach (string f in files)
        {
            string rel = f.Substring(cfgDir.Length).TrimStart(Path.DirectorySeparatorChar);
            string[] lines = File.ReadAllLines(f);
            int n = lines.Count(l => l.TrimStart().StartsWith("\""));
            fields += n;
            Console.WriteLine($"=== {rel}  ({n} field(s), {new FileInfo(f).Length} bytes) ===");
            foreach (string l in lines) Console.WriteLine(l);
            Console.WriteLine();
        }
        Console.WriteLine($"TOTAL: {files.Length} config file(s), {fields} field line(s)");

        if (Environment.GetEnvironmentVariable("KMH_KEEP") != "1")
        {
            try { Directory.Delete(root, true); } catch { }
        }
        else Console.WriteLine("kept: " + root);

        return failed == 0 ? 0 : 1;
    }

    static bool TrySetMainPath(string root, out string how)
    {
        how = "";
        Type master = null;
        foreach (string dir in _probe)
        {
            foreach (string dll in new[] { "GameServer.dll", "RTServer.dll" })
            {
                string p = Path.Combine(dir, dll);
                if (!File.Exists(p)) continue;
                try { master = Assembly.LoadFrom(p).GetTypes().FirstOrDefault(t => t.Name == "Master"); } catch { }
                if (master != null) break;
            }
            if (master != null) break;
        }
        if (master == null) { how = "no Master type on the probe path"; return false; }

        PropertyInfo prop = master.GetProperty("MainPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (prop != null && prop.CanWrite) { prop.SetValue(null, root); how = "Master.MainPath property"; return true; }

        FieldInfo field = master.GetField("MainPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field != null) { field.SetValue(null, root); how = "Master.MainPath field"; return true; }

        FieldInfo backing = master.GetField("<MainPath>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);
        if (backing != null) { backing.SetValue(null, root); how = "Master.MainPath backing field"; return true; }

        how = "MainPath is not settable";
        return false;
    }

    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name + ".dll";
        foreach (string dir in _probe)
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return Assembly.LoadFrom(p);
        }
        return null;
    }
}
