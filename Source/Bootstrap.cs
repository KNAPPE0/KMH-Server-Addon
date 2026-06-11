using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;

namespace KMHServerAddon
{
    // Entry point + launcher (StartupObject in csproj).
    //
    // KMH does NOT ship or modify RimWorld Together. The server owner drops KMHServerAddon.exe next to the official
    // single-file GameServer.exe and runs the addon. We extract RWT's managed assemblies out of GameServer.exe at
    // runtime, load them into THIS process, Harmony-patch them, then invoke RWT's own Main. We redistribute nothing
    // of RWT's
    //
    // Two things make Harmony work against the official build:
    //   1. We run framework-dependent (on the system .NET 8 runtime), so
    // Harmony/MonoMod can find clrjit normally - it cannot inside a self-contained single-file process
    //   2. RWT's official assemblies are ReadyToRun (precompiled native) images
    // that MonoMod can't patch, so we re-launch ourselves once with DOTNET_ReadyToRun=0, which makes the runtime
    // JIT plain IL instead
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
            // Windows ships GameServer.exe; Linux/macOS ship a GameServer binary.
            string gameServerExe = File.Exists(Path.Combine(dir, "GameServer.exe"))
                ? Path.Combine(dir, "GameServer.exe")
                : Path.Combine(dir, "GameServer");

            // Resolve RWT (and its deps) from the extraction cache when the runtime asks for them. Newtonsoft is
            // intentionally not extracted - we use the copy bundled with us, so there's only one in-process.
            // Single-file hosts don't probe loose DLLs next to the exe, so check both the server folder and the
            // extraction cache
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

            // Hand off to the build matching the server generation found. This exe is compiled against the old API
            // (Shared/TCPNetwork); a new server (RTShared/RTNetwork) runs the embedded payload compiled against the
            // new API instead. Same source either way
            string gen = DetectGeneration(dir, cache);
            if (gen == null) { PrintNeedGameServer(dir); return 1; }
            if (gen == "old")
            {
                Console.WriteLine($"{Constants.LogPrefix} RWT 26.5.x server detected.");
                return Main_.RunAndStartServer(args);
            }
            return RunNewGenerationPayload(args);
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
