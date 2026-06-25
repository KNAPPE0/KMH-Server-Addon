using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;

namespace KMHServerAddon
{
    // Entry point + launcher (StartupObject). KMH ships nothing of RimWorld Together: the owner drops
    // KMHServerAddon.exe next to the official GameServer.exe; we extract RWT's assemblies at runtime, Harmony-patch
    // them, then call RWT's own Main. Two requirements make Harmony work on the official build: (1) run
    // framework-dependent so MonoMod can find clrjit (it can't inside a self-contained exe), and (2) re-launch once
    // with DOTNET_ReadyToRun=0 so the runtime JITs plain IL instead of RWT's un-patchable ReadyToRun images.
    internal static class Bootstrap
    {
        private const string ReExecMarker = "KMH_REEXEC";

        public static int Main(string[] args)
        {
            // Step 1: ensure we're running with ReadyToRun disabled. If not, re-launch ourselves once with it off +
            // a marker, and forward the console, args, and exit code
            if (Environment.GetEnvironmentVariable(ReExecMarker) != "1")
                return ReExecWithR2RDisabled(args);

            string dir = AppContext.BaseDirectory;
            string cache = Path.Combine(dir, ".rwt-runtime");
            // Real RWT binary to load assemblies from. If this launcher is named GameServer, skip self and use GameServer.real / GameServer.rwt / RwtServer instead.
            string gameServerExe = FindRwtServerFile(dir);
            // Resolve RWT/deps from the server folder or extraction cache. Keep Newtonsoft bundled with us to avoid duplicate in-process copies.
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                try
                {
                    foreach (string root in new[] { dir, cache })
                    {
                        string p = Path.Combine(root, name.Name + ".dll");
                        if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
                    }
                    return null;
                }
                catch { return null; }
            };

            // Make RWT loadable: loose DLLs already next to us, or extract them from the official GameServer.exe
            if (DetectGeneration(dir, cache) == null)
            {
                if (File.Exists(gameServerExe))
                {
                    try
                    {
                        int n = SingleFileBundle.Extract(gameServerExe, cache, sentinelDll: "GameServer.dll");
                        Console.WriteLine($"{Constants.LogPrefix} Loaded RimWorld Together from GameServer.exe ({n} assemblies).");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"{Constants.LogPrefix} Could not read RWT out of GameServer.exe: {ex.Message}");
                    }
                }
            }
            string gen = DetectGeneration(dir, cache);
            if (gen == null) { PrintNeedGameServer(dir); return 1; }
            if (gen == "old")
            {
                Console.WriteLine($"{Constants.LogPrefix} RWT 26.5.x server detected.");
                return Main_.RunAndStartServer(args);
            }
            return RunNewGenerationPayload(args);
        }
        private static string FindRwtServerFile(string dir)
        {
            string self = "";
            try { self = Path.GetFullPath(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? ""); }
            catch { /* best effort */ }

            foreach (string name in new[] { "GameServer.exe", "GameServer", "GameServer.real", "GameServer.rwt", "RwtServer" })
            {
                string p = Path.Combine(dir, name);
                if (!File.Exists(p)) continue;
                if (!string.IsNullOrEmpty(self) && string.Equals(Path.GetFullPath(p), self, StringComparison.OrdinalIgnoreCase))
                    continue; // that's us, not RWT
                return p;
            }
            return Path.Combine(dir, "GameServer.exe"); // doesn't exist - surfaces the "need GameServer" message
        }

        // "old" = Shared/TCPNetwork (26.5.24.1), "new" = RTShared/RTNetwork (26.6.9.1+), null = no usable server
        // found
        private static string DetectGeneration(string dir, string cache)
        {
            bool Has(string dll) => File.Exists(Path.Combine(dir, dll)) || File.Exists(Path.Combine(cache, dll));
            if (!Has("GameServer.dll")) return null;
            if (Has("RTShared.dll") && Has("RTNetwork.dll")) return "new";
            if (Has("Shared.dll") && Has("TCPNetwork.dll")) return "old";
            return null;
        }

        private static int RunNewGenerationPayload(string[] args)
        {
            Console.WriteLine($"{Constants.LogPrefix} RWT 26.6.x server detected - using the RT payload.");
            try
            {
                using Stream s = typeof(Bootstrap).Assembly.GetManifestResourceStream("KMHAddon.RTShared.dll");
                if (s == null)
                {
                    Console.Error.WriteLine($"{Constants.LogPrefix} No RT payload embedded - rebuild with RwtFlavor=New first, then the launcher.");
                    return 1;
                }
                byte[] bytes = new byte[s.Length];
                s.ReadExactly(bytes, 0, bytes.Length);
                var payload = System.Reflection.Assembly.Load(bytes);
                object result = payload.GetType("KMHServerAddon.Main_")
                                       .GetMethod("RunAndStartServer")
                                       .Invoke(null, new object[] { args });
                return (int)result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Constants.LogPrefix} RT payload failed to start: {ex}");
                return 1;
            }
        }

        private static int ReExecWithR2RDisabled(string[] args)
        {
            try
            {
                string self = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo(self) { UseShellExecute = false };
                foreach (string a in args) psi.ArgumentList.Add(a);
                psi.Environment[ReExecMarker]      = "1";
                psi.Environment["DOTNET_ReadyToRun"] = "0";   // force IL JIT so MonoMod can patch RWT
                psi.Environment["DOTNET_TieredPGO"]  = "0";
                using Process proc = Process.Start(psi);
                proc.WaitForExit();
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Constants.LogPrefix} Re-launch failed: {ex.Message}");
                return 1;
            }
        }

        private static void PrintNeedGameServer(string dir)
        {
            string line = new string('=', 70);
            Console.Error.WriteLine();
            Console.Error.WriteLine(line);
            Console.Error.WriteLine("  KMH Server Addon - GameServer.exe not found");
            Console.Error.WriteLine(line);
            Console.Error.WriteLine();
            Console.Error.WriteLine("KMH runs on top of RimWorld Together - it does not include it");
            Console.Error.WriteLine("(RWT's license forbids redistributing it). Put the official");
            Console.Error.WriteLine("RimWorld Together GameServer.exe in this folder, next to");
            Console.Error.WriteLine("KMHServerAddon.exe:");
            Console.Error.WriteLine($"  {dir}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Then run KMHServerAddon.exe (NOT GameServer.exe). Needs the");
            Console.Error.WriteLine(".NET 8 runtime installed.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Press Enter to exit...");
            try { Console.ReadLine(); } catch { }
        }
    }
}
