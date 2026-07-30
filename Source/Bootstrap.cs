using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;

namespace KMHServerAddon
{
    // Entry point + launcher (StartupObject). KMH ships nothing of RWT: it extracts the owner's server assemblies at
    // runtime, Harmony-patches them, then calls RWT's own Main. Two things make Harmony work on the official build:
    // run framework-dependent so MonoMod can find clrjit, and re-launch once with DOTNET_ReadyToRun=0 so the runtime
    // JITs plain IL instead of RWT's un-patchable ReadyToRun images. RwtDiscovery decides which server we load.
    internal static class Bootstrap
    {
        private const string ReExecMarker = "KMH_REEXEC";

        // One payload per generation, compiled against that RWT build; "old" runs in this launcher itself.
        private const string PayloadNew = "KMHAddon.RTShared.dll";    // 26.6.x, GameServer.dll
        private const string PayloadRt  = "KMHAddon.RTServer.dll";    // 26.7.x, RTServer.dll

        public static int Main(string[] args)
        {
            // Re-launch once with ReadyToRun off, forwarding console, args and exit code.
            if (Environment.GetEnvironmentVariable(ReExecMarker) != "1")
                return ReExecWithR2RDisabled(args);

            string dir = AppContext.BaseDirectory;
            string cache = Path.Combine(dir, ".rwt-runtime");

            // Resolve RWT/deps from the server folder or the extraction cache.
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

            RwtDiscovery.Result found = RwtDiscovery.Locate(dir, cache);
            if (Environment.GetEnvironmentVariable("KMH_DEBUG_DISCOVERY") == "1") PrintDiscovery(found);

            // Loose DLLs are usable as-is; a bundled server has to be extracted first.
            if (!found.LooseLayout && found.SelectedExe != null)
            {
                try
                {
                    int n = SingleFileBundle.Extract(found.SelectedExe, cache, found.Fingerprint, found.ServerDll);
                    Console.WriteLine($"{Constants.LogPrefix} Loaded RimWorld Together from "
                                    + $"{Path.GetFileName(found.SelectedExe)} ({n} assemblies, {found.ServerDll}).");
                }
                catch (Exception ex)
                {
                    found.LoadFailure = $"could not read RWT out of {Path.GetFileName(found.SelectedExe)}: {ex.Message}";
                    Console.Error.WriteLine($"{Constants.LogPrefix} {found.LoadFailure}");
                }
            }

            if (found.SelectedExe != null && found.LoadFailure == null)
            {
                string bad = RwtDiscovery.VerifyAssemblyIdentity(dir, cache, found.ServerDll);
                if (bad != null) found.LoadFailure = bad;
            }

            if (found.Generation == null)
                found.Generation = RwtDiscovery.DetectGeneration(dir, cache, found.ServerDll);

            // Not a missing server - saying so would send the owner looking for the wrong thing.
            if (found.RuntimeProblem != null) { PrintRuntimeProblem(found); return 1; }

            if (found.Generation == null) { PrintNeedServer(found); return 1; }

            switch (found.Generation)
            {
                case "old":
                    Console.WriteLine($"{Constants.LogPrefix} RWT 26.5.x server detected.");
                    return Main_.RunAndStartServer(args);
                case "rt":
                    return RunPayload(args, PayloadRt, "RWT server with the renamed RTServer assembly detected");
                default:
                    return RunPayload(args, PayloadNew, "RWT 26.6.x server detected");
            }
        }

        private static int RunPayload(string[] args, string resource, string what)
        {
            Console.WriteLine($"{Constants.LogPrefix} {what} - using the {Path.GetFileNameWithoutExtension(resource)} payload.");
            try
            {
                using Stream s = typeof(Bootstrap).Assembly.GetManifestResourceStream(resource);
                if (s == null)
                {
                    Console.Error.WriteLine($"{Constants.LogPrefix} No {resource} payload embedded - build the "
                                          + "payload flavors first, then the launcher.");
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
                Console.Error.WriteLine($"{Constants.LogPrefix} {resource} payload failed to start: {ex}");
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

        // Opt-in; successful startup otherwise stays at one line.
        private static void PrintDiscovery(RwtDiscovery.Result found)
        {
            Console.WriteLine($"{Constants.LogPrefix} discovery: folder {found.Directory}");
            foreach (RwtDiscovery.Candidate c in found.Checked)
                Console.WriteLine($"{Constants.LogPrefix} discovery:   {c}");
            foreach (string n in found.Notes)
                Console.WriteLine($"{Constants.LogPrefix} discovery:   {n}");
            Console.WriteLine($"{Constants.LogPrefix} discovery: selected="
                + $"{(found.SelectedExe == null ? "(none)" : Path.GetFileName(found.SelectedExe))} "
                + $"serverDll={found.ServerDll ?? "(none)"} loose={found.LooseLayout} "
                + $"generation={found.Generation ?? "(undetermined)"}");
            if (found.Fingerprint != null)
                Console.WriteLine($"{Constants.LogPrefix} discovery: cache key {found.Fingerprint}");
        }

        private static void PrintRuntimeProblem(RwtDiscovery.Result found)
        {
            string line = new string('=', 70);
            var w = Console.Error;
            w.WriteLine();
            w.WriteLine(line);
            w.WriteLine("  KMH Server Addon - RimWorld Together found, but its .NET runtime is missing");
            w.WriteLine(line);
            w.WriteLine();
            w.WriteLine("The RimWorld Together server IS present - this is not a missing-server problem.");
            w.WriteLine();
            w.WriteLine($"  folder: {found.Directory}");
            w.WriteLine($"  server: {(found.SelectedExe == null ? found.ServerDll : Path.GetFileName(found.SelectedExe))}");
            w.WriteLine();
            w.WriteLine($"  {found.RuntimeProblem}");
            w.WriteLine();
            w.WriteLine("Slim RimWorld Together downloads do not bundle .NET - the runtime has to be");
            w.WriteLine("installed on the host: https://dotnet.microsoft.com/download");
            w.WriteLine();
            w.WriteLine("Press Enter to exit...");
            try { Console.ReadLine(); } catch { }
        }

        // Separates "no server here" from "server here but unusable" - the two have different fixes.
        private static void PrintNeedServer(RwtDiscovery.Result found)
        {
            string line = new string('=', 70);
            var w = Console.Error;
            w.WriteLine();
            w.WriteLine(line);
            w.WriteLine(found.SelectedExe == null
                ? "  KMH Server Addon - no RimWorld Together server found"
                : "  KMH Server Addon - found the server, but could not start it");
            w.WriteLine(line);
            w.WriteLine();
            w.WriteLine("Folder searched:");
            w.WriteLine($"  {found.Directory}");
            w.WriteLine();

            w.WriteLine("Executables checked (in order):");
            List<string> names = new List<string>(RwtDiscovery.CandidateNames());
            foreach (string n in names)
            {
                RwtDiscovery.Candidate c = found.Checked.Find(
                    x => string.Equals(Path.GetFileName(x.Path), n, StringComparison.OrdinalIgnoreCase));
                if (c == null) w.WriteLine($"  {n,-22} not present");
                else if (c.Rejection != null) w.WriteLine($"  {n,-22} rejected: {c.Rejection}");
                else w.WriteLine($"  {n,-22} SELECTED");
            }
            w.WriteLine();

            foreach (string note in found.Notes) w.WriteLine($"  {note}");

            if (found.SelectedExe != null)
            {
                w.WriteLine($"Selected: {Path.GetFileName(found.SelectedExe)} (carries {found.ServerDll})");
                if (found.LoadFailure != null)
                {
                    w.WriteLine($"But its managed assembly could not be used:");
                    w.WriteLine($"  {found.LoadFailure}");
                }
                else
                {
                    w.WriteLine("Its assemblies loaded, but the set doesn't match any RWT generation KMH knows:");
                    w.WriteLine($"  expected {RwtDiscovery.ServerDllOld} or {RwtDiscovery.ServerDllRt}, plus either");
                    w.WriteLine("  RTShared.dll + RTNetwork.dll (26.6.x) or Shared.dll + TCPNetwork.dll (26.5.x).");
                    w.WriteLine($"  Present in {Path.GetFileName(found.CacheDir)}: {DescribeCache(found.CacheDir)}");
                    w.WriteLine();
                    w.WriteLine("  This usually means a newer RWT release than this KMH build supports.");
                }
                w.WriteLine();
                w.WriteLine("Deleting the .rwt-runtime folder and re-running forces a clean re-extraction.");
            }
            else
            {
                w.WriteLine("KMH runs on top of RimWorld Together - it does not include it");
                w.WriteLine("(RWT's license forbids redistributing it). Put the official RimWorld");
                w.WriteLine("Together server executable in the folder above, next to KMHServerAddon.exe,");
                w.WriteLine("then run KMHServerAddon.exe (NOT the RWT server). Needs the .NET 8 runtime.");
                w.WriteLine();
                w.WriteLine("Hosting panel locked to ./GameServer? Rename KMH to GameServer and the real");
                w.WriteLine("RWT server to GameServer.rwt - KMH identifies the server by its contents, so");
                w.WriteLine("it will not pick itself.");
            }

            w.WriteLine();
            w.WriteLine("Press Enter to exit...");
            try { Console.ReadLine(); } catch { }
        }

        private static string DescribeCache(string cache)
        {
            try
            {
                List<string> interesting = new List<string>();
                foreach (string f in Directory.GetFiles(cache, "*.dll"))
                {
                    string n = Path.GetFileName(f);
                    if (n.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) continue;
                    interesting.Add(n);
                }
                return interesting.Count == 0 ? "(nothing)" : string.Join(", ", interesting);
            }
            catch { return "(cache folder missing)"; }
        }
    }
}
