using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

class P
{
    static string[] _probe;

    // A mutated build can stay staged (another flavour's build does not refresh it), so a stale payload is refused.
    static bool PayloadIsCurrent(string payload, out string why)
    {
        why = "";
        try
        {
            if (!File.Exists(payload)) { why = "the payload does not exist: " + payload; return false; }

            DirectoryInfo dir = new DirectoryInfo(Path.GetDirectoryName(payload));
            DirectoryInfo root = dir;
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Source"))) root = root.Parent;
            if (root == null) return true;   // running from a packaged layout with no source tree to compare against

            DateTime built = File.GetLastWriteTimeUtc(payload);
            string newest = null;
            DateTime newestAt = DateTime.MinValue;
            foreach (string cs in Directory.EnumerateFiles(Path.Combine(root.FullName, "Source"), "*.cs", SearchOption.AllDirectories))
            {
                if (cs.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                 || cs.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                DateTime at = File.GetLastWriteTimeUtc(cs);
                if (at > newestAt) { newestAt = at; newest = cs; }
            }

            // Two seconds of slack: a build writes the payload and its inputs within the same moment.
            if (newest != null && newestAt > built.AddSeconds(2))
            {
                why = $"{Path.GetFileName(newest)} was edited at {newestAt:HH:mm:ss}Z but the payload was built at {built:HH:mm:ss}Z";
                return false;
            }
            return true;
        }
        catch (Exception ex) { why = "could not check payload freshness: " + ex.Message; return false; }
    }

    static int Main(string[] a)
    {
        // Absolute first: the working directory moves below and a relative argument would stop resolving.
        string target = Path.GetFullPath(a[0]);
        _probe = a.Skip(1).Select(Path.GetFullPath).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        if (!PayloadIsCurrent(target, out string staleWhy))
        {
            Console.WriteLine("STALE PAYLOAD - refusing to run: " + staleWhy);
            Console.WriteLine("  The suite would report on code that is no longer what the source says.");
            Console.WriteLine("  Rebuild before testing:  dotnet build Source\\KMHServerAddon.csproj -c Release -t:Rebuild -p:RwtFlavor=New");
            Console.WriteLine("                           dotnet build Source\\KMHServerAddon.csproj -c Release -t:Rebuild -p:RwtFlavor=RT");
            return 2;
        }

        // No Master.MainPath here, so KMH-Data falls back to the cwd; pinned, or suites litter the launch folder.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Console.WriteLine("data root: " + AppContext.BaseDirectory);

        Assembly asm = Assembly.LoadFrom(target);
        Console.WriteLine("assembly: " + asm.GetName().Name + " v" + asm.GetName().Version);
        Console.WriteLine();

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

        // Nothing here boots the server; fake a finished boot or every admission-gated check reads "still starting up".
        Type readiness = types.FirstOrDefault(t => t.Name == "KmhReadiness");
        readiness?.GetMethod("Ready", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                 ?.Invoke(null, null);

        var suites = types.Where(t => t.Name.EndsWith("SelfTest")).OrderBy(t => t.Name).ToList();
        // A dev loop only: a filtered run can never pass the gate, which still demands the full suite and check counts.
        string only = Environment.GetEnvironmentVariable("KMH_SUITE");
        if (!string.IsNullOrEmpty(only)) suites = suites.Where(t => t.Name.Contains(only)).ToList();
        string SkipMarker = ResolveSkipMarker(asm);
        int pass = 0, fail = 0, suitesRun = 0, suitesSkipped = 0, checksSkipped = 0;
        var skippedNames = new List<string>();

        foreach (Type t in suites)
        {
            // Not GetMethod("Run"), which throws on a Run helper overload; prefer no-arg but accept a lone overload.
            MethodInfo[] runs = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                 .Where(m => m.Name == "Run").ToArray();
            MethodInfo run = runs.FirstOrDefault(m => m.GetParameters().Length == 0)
                          ?? (runs.Length == 1 ? runs[0] : null);
            if (run == null && runs.Length > 1)
                Console.WriteLine("  SKIP  " + t.Name + " (several Run overloads, none of them parameterless)");
            if (run == null) { Console.WriteLine("  SKIP  " + t.Name + " (no static Run)"); suitesSkipped++; continue; }
            ParameterInfo[] ps = run.GetParameters();

            try
            {
                if (ps.Length == 0)
                {
                    object result = run.Invoke(null, null);
                    int n = 0;
                    foreach (object row in (IEnumerable)result)
                    {
                        Type rt = row.GetType();
                        string name = (string)rt.GetField("Item1").GetValue(row);
                        bool ok = (bool)rt.GetField("Item2").GetValue(row);
                        string det = (string)rt.GetField("Item3").GetValue(row);
                        // A skip is neither pass nor fail; its ok=false only makes marker-unaware harnesses fail loudly.
                        bool skipped = det != null && det.StartsWith(SkipMarker, StringComparison.Ordinal);
                        string verdict = skipped ? "SKIP  " : ok ? "PASS  " : "FAIL  ";

                        // Passing details too: that is where a measurement lives.
                        if (Environment.GetEnvironmentVariable("KMH_LIST") == "1")
                            Console.WriteLine("    " + verdict + name
                                              + (!string.IsNullOrEmpty(det) && (ok || skipped) ? "  -  " + det : ""));

                        if (skipped)
                        {
                            checksSkipped++;
                            skippedNames.Add(t.Name + ": " + name + " (" + det.Substring(SkipMarker.Length) + ")");
                        }
                        else if (ok) pass++;
                        else { fail++; Console.WriteLine("  FAIL  [" + t.Name + "] " + name + " - " + det); }
                        n++;
                    }
                    Console.WriteLine("  " + (n > 0 ? "ok  " : "??  ") + t.Name.PadRight(38) + n + " check(s)");
                    suitesRun++;
                }
                else if (ps.Length == 2 && ps[1].IsOut)
                {
                    Action<string> log = s => { };
                    object[] args = new object[] { log, null };
                    bool ok = (bool)run.Invoke(null, args);
                    if (ok) pass++; else fail++;
                    Console.WriteLine("  " + (ok ? "ok  " : "FAIL") + t.Name.PadRight(38) + args[1]);
                    suitesRun++;
                }
                else { Console.WriteLine("  SKIP  " + t.Name + " (unsupported signature)"); suitesSkipped++; }
            }
            catch (Exception ex)
            {
                Exception e = ex.InnerException ?? ex;
                fail++;
                Console.WriteLine("  THREW " + t.Name + " -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine("suites: " + suitesRun + " run, " + suitesSkipped + " skipped   |   checks: "
                          + pass + " pass, " + fail + " fail, " + checksSkipped + " skipped");

        // Named rather than counted only: a release run has to be able to see WHICH coverage it did not get.
        foreach (string s2 in skippedNames) Console.WriteLine("  SKIP  " + s2);

        // A release gate sets this; a developer running the suite on a shipped layout does not.
        bool skipsForbidden = Environment.GetEnvironmentVariable("KMH_REQUIRE_NO_SKIPS") == "1";
        if (skipsForbidden && (checksSkipped > 0 || suitesSkipped > 0))
        {
            Console.WriteLine("  REQUIRED COVERAGE MISSING - KMH_REQUIRE_NO_SKIPS=1 and "
                              + checksSkipped + " check(s) / " + suitesSkipped + " suite(s) did not run.");
            fail++;
        }
        if (!string.IsNullOrEmpty(only))
            Console.WriteLine(fail == 0 ? $"RESULT: FILTERED PASS (KMH_SUITE={only}) - not a gate run" : "RESULT: FAILURES");
        else
            Console.WriteLine(fail == 0 ? "RESULT: ALL PASS" : "RESULT: FAILURES");
        return fail == 0 ? 0 : 1;
    }

    // From the assembly under test, so runner and suites agree on a skip; without it, skips could count as passes.
    static string ResolveSkipMarker(Assembly asm)
    {
        foreach (Type t in asm.GetTypes())
        {
            if (t.Name != "KmhSelfTestResult") continue;
            FieldInfo f = t.GetField("SkipPrefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null) return (string)f.GetRawConstantValue();
        }
        Console.WriteLine("  FAIL  the assembly has no KmhSelfTestResult.SkipPrefix - skips cannot be told from passes.");
        Environment.Exit(2);
        return "";
    }

    static Assembly Resolve(object s, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name + ".dll";
        foreach (string dir in _probe)
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { } }
        }
        return null;
    }
}
