using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

class P
{
    static string[] _probe;

    static int Main(string[] a)
    {
        // Absolute first: the working directory moves below and a relative argument would stop resolving.
        string target = Path.GetFullPath(a[0]);
        _probe = a.Skip(1).Select(Path.GetFullPath).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        // Nothing sets RWT's Master.MainPath here, so KMH-Data falls back to the working directory. Pin it, or the
        // suites write a scratch KMH-Data into whatever folder the run was started from.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Console.WriteLine("data root: " + AppContext.BaseDirectory);

        Assembly asm = Assembly.LoadFrom(target);
        Console.WriteLine("assembly: " + asm.GetName().Name + " v" + asm.GetName().Version);
        Console.WriteLine();

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

        // Nothing here boots the server, so fake the finished boot or every admission-gated check reads as "still starting up".
        Type readiness = types.FirstOrDefault(t => t.Name == "KmhReadiness");
        readiness?.GetMethod("Ready", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                 ?.Invoke(null, null);

        var suites = types.Where(t => t.Name.EndsWith("SelfTest")).OrderBy(t => t.Name).ToList();
        int pass = 0, fail = 0, suitesRun = 0, suitesSkipped = 0;

        foreach (Type t in suites)
        {
            MethodInfo run = t.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
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
                        if (Environment.GetEnvironmentVariable("KMH_LIST") == "1") Console.WriteLine("    " + (ok ? "PASS  " : "FAIL  ") + name);
            if (ok) pass++; else { fail++; Console.WriteLine("  FAIL  [" + t.Name + "] " + name + " - " + det); }
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
        Console.WriteLine("suites: " + suitesRun + " run, " + suitesSkipped + " skipped   |   checks: " + pass + " pass, " + fail + " fail");
        Console.WriteLine(fail == 0 ? "RESULT: ALL PASS" : "RESULT: FAILURES");
        return fail == 0 ? 0 : 1;
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
