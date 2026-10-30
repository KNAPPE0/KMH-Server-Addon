using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // The harness decides what "all pass" means; a check that did not run must never count as a pass.
    internal static class KmhSelfTestHarnessSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("harness: a skipped check does not look like a pass", () =>
            {
                (string name, bool ok, string detail) = KmhSelfTestResult.Skipped("probe", "fixture absent");
                // ok=false is the point: a runner that does not know the marker must fail, not count a pass.
                bool right = !ok && KmhSelfTestResult.IsSkip(detail) && detail.Contains("fixture absent");
                return (right, right ? "reported as not-run, and loud to an unaware harness" : $"ok={ok} detail={detail}");
            });

            Check("harness: an ordinary pass is never mistaken for a skip", () =>
            {
                bool clean = !KmhSelfTestResult.IsSkip("verified") && !KmhSelfTestResult.IsSkip("")
                          && !KmhSelfTestResult.IsSkip(null);
                return (clean, clean ? "only the marker counts" : "a normal detail was read as a skip");
            });

            // A suite with a second Run overload makes GetMethod throw and aborts the whole run.
            Check("harness: every suite exposes exactly one callable entry point", () =>
            {
                var bad = new List<string>();
                foreach (Type t in typeof(KmhSelfTestHarnessSelfTest).Assembly.GetTypes())
                {
                    if (!t.IsClass || !t.IsAbstract || !t.IsSealed) continue;          // static classes only
                    if (!t.Name.EndsWith("SelfTest", StringComparison.Ordinal)) continue;

                    MethodInfo[] runs = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                         .Where(m => m.Name == "Run").ToArray();
                    if (runs.Length == 0) { bad.Add(t.Name + ": no Run"); continue; }
                    if (runs.Length > 1 && !runs.Any(m => m.GetParameters().Length == 0))
                        bad.Add(t.Name + ": several Run overloads, none parameterless");
                }
                return (bad.Count == 0, bad.Count == 0 ? "every suite is callable" : string.Join("; ", bad));
            });

            // A skip marker the runner cannot read means skips silently become passes again.
            Check("harness: the skip marker is discoverable the way the runner finds it", () =>
            {
                FieldInfo f = typeof(KmhSelfTestResult).GetField("SkipPrefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                bool ok = f != null && f.IsLiteral && (string)f.GetRawConstantValue() == KmhSelfTestResult.SkipPrefix;
                return (ok, ok ? "a const the runner can read without running code" : "SkipPrefix is not a readable const");
            });

            return r;
        }
    }
}
