using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace KMHServerAddon
{
    // Finds the owner's RWT server and its generation. Identification is by CONTENT - we read each candidate's
    // bundle manifest for a known server assembly - because names are unreliable: RWT renamed GameServer -> RTServer,
    // and Bisect layouts rename KMH itself to GameServer.
    internal static class RwtDiscovery
    {
        public const string ServerDllRt  = "RTServer.dll";      // 26.7.x+
        public const string ServerDllOld = "GameServer.dll";    // before the rename
        private const string SelfDll     = "KMHServerAddon.dll";

        public static readonly string[] KnownServerDlls = { ServerDllRt, ServerDllOld };

        // Stock names first; the renamed variants are what a Bisect layout falls through to.
        private static readonly string[] WindowsNames =
        {
            "GameServer.exe", "RTServer.exe", "GameServer.rwt.exe", "RTServer.rwt.exe",
            "GameServer.real.exe", "RwtServer.exe",
        };

        private static readonly string[] UnixNames =
        {
            "GameServer", "RTServer", "GameServer.rwt", "RTServer.rwt", "GameServer.real", "RwtServer",
        };

        public sealed class Candidate
        {
            public string Path;
            public string Rejection;              // null = this one was selected
            public override string ToString()
                => System.IO.Path.GetFileName(Path) + (Rejection == null ? "  <- selected" : "  - " + Rejection);
        }

        public sealed class Result
        {
            public string Directory;
            public string CacheDir;
            public string SelectedExe;            // null when nothing usable was found
            public string ServerDll;              // which server assembly the selection carries
            public string Generation;             // "old" | "new" | "rt" | null
            public string Fingerprint;
            public bool   LooseLayout;            // assemblies already sit beside us; no extraction needed
            public string LoadFailure;            // exe found, but its managed assembly could not be made usable
            public string RuntimeProblem;         // the server needs a .NET this process isn't running
            public readonly List<Candidate> Checked = new List<Candidate>();
            public readonly List<string> Notes = new List<string>();
        }

        // Both lists are probed, native first, so an odd layout (extensionless server on Windows) still resolves.
        public static IEnumerable<string> CandidateNames()
        {
            bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in win ? WindowsNames : UnixNames) if (seen.Add(n)) yield return n;
            foreach (string n in win ? UnixNames : WindowsNames) if (seen.Add(n)) yield return n;
        }

        public static string SelfPath()
        {
            try { return Path.GetFullPath(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? ""); }
            catch { return ""; }
        }

        // .rwt-runtime cache key. Covers the assembly name too, so a rename alone still forces re-extraction.
        public static string Fingerprint(string exePath, string serverDll)
        {
            try
            {
                FileInfo fi = new FileInfo(exePath);
                List<string> parts = new List<string>
                {
                    fi.Name, fi.Length.ToString(), fi.LastWriteTimeUtc.Ticks.ToString(), serverDll ?? "?",
                };
                string dir = fi.DirectoryName ?? "";
                string stem = Path.GetFileNameWithoutExtension(serverDll ?? fi.Name);
                foreach (string ext in new[] { ".deps.json", ".runtimeconfig.json" })
                {
                    FileInfo side = new FileInfo(Path.Combine(dir, stem + ext));
                    parts.Add(side.Exists ? $"{stem}{ext}:{side.Length}:{side.LastWriteTimeUtc.Ticks}" : $"{stem}{ext}:-");
                }
                return string.Join("|", parts);
            }
            catch { return exePath + "|?"; }
        }

        // Slim RWT runs on whatever .NET is installed - here, the runtime KMH is already on. A newer requirement
        // fails later in a way that looks nothing like the cause, so name it up front.
        public static string CheckRuntimeRequirement(string dir, string serverDll)
        {
            if (serverDll == null) return null;
            string cfg = Path.Combine(dir, Path.GetFileNameWithoutExtension(serverDll) + ".runtimeconfig.json");
            if (!File.Exists(cfg)) return null;                 // bundled: carries its own runtime
            try
            {
                string text = File.ReadAllText(cfg);
                Match m = Regex.Match(text, "\"version\"\\s*:\\s*\"(\\d+)\\.(\\d+)");
                if (!m.Success) return null;
                int needMajor = int.Parse(m.Groups[1].Value);
                int haveMajor = Environment.Version.Major;
                if (needMajor <= haveMajor) return null;
                return $"this RimWorld Together build needs the .NET {needMajor} runtime, but KMH is running on "
                     + $".NET {Environment.Version} - install .NET {needMajor} (or use a KMH -selfcontained build "
                     + $"matching it). Read from {Path.GetFileName(cfg)}.";
            }
            catch { return null; }
        }

        public static Result Locate(string dir, string cacheDir)
        {
            Result r = new Result { Directory = dir, CacheDir = cacheDir };

            // Loose/slim install: assemblies are already on disk, nothing to extract.
            string loose = FirstExisting(dir, KnownServerDlls);
            if (loose != null)
            {
                r.LooseLayout    = true;
                r.ServerDll      = loose;
                r.Generation     = DetectGeneration(dir, cacheDir, loose);
                r.RuntimeProblem = CheckRuntimeRequirement(dir, loose);
                r.Notes.Add($"{loose} is already present in the server folder; no extraction needed.");
                NoteDependencyFiles(r, dir, loose);
                return r;
            }

            string self = SelfPath();
            foreach (string name in CandidateNames())
            {
                string p = Path.Combine(dir, name);
                if (!File.Exists(p)) continue;

                Candidate c = new Candidate { Path = p };
                r.Checked.Add(c);

                string full = SafeFull(p);
                if (!string.IsNullOrEmpty(self) && string.Equals(full, self, StringComparison.OrdinalIgnoreCase))
                {
                    c.Rejection = "this is the running KMH launcher itself";
                    continue;
                }

                List<string> inside = SingleFileBundle.ListManagedNames(p);
                if (inside.Count == 0)
                {
                    c.Rejection = "not a .NET single-file bundle (or unreadable)";
                    continue;
                }

                string carried = FirstIn(inside, KnownServerDlls);
                if (carried == null)
                {
                    // Catches a KMH copy under any name, not just the path we run from - and never by file size.
                    c.Rejection = Contains(inside, SelfDll)
                        ? "a KMH Server Addon build (carries " + SelfDll + "), not RimWorld Together"
                        : "no RimWorld Together server assembly inside (" + inside.Count + " assemblies)";
                    continue;
                }

                r.SelectedExe    = p;
                r.ServerDll      = carried;
                r.Fingerprint    = Fingerprint(p, carried);
                r.RuntimeProblem = CheckRuntimeRequirement(dir, carried);
                return r;
            }

            return r;
        }

        // old = Shared/TCPNetwork (26.5.x), new = RTShared + GameServer.dll (26.6.x), rt = RTShared + RTServer.dll.
        public static string DetectGeneration(string dir, string cache, string serverDll)
        {
            bool Has(string dll) => File.Exists(Path.Combine(dir, dll))
                                 || (cache != null && File.Exists(Path.Combine(cache, dll)));

            if (serverDll == null) serverDll = FirstExistingIn(dir, cache, KnownServerDlls);
            if (serverDll == null || !Has(serverDll)) return null;

            if (Has("RTShared.dll") && Has("RTNetwork.dll"))
                return serverDll.Equals(ServerDllRt, StringComparison.OrdinalIgnoreCase) ? "rt" : "new";
            if (Has("Shared.dll") && Has("TCPNetwork.dll"))
                return "old";
            return null;
        }

        // Matches deps against the SELECTED server's name, not whatever else is lying around. Loose installs only.
        public static void NoteDependencyFiles(Result r, string dir, string serverDll)
        {
            string stem = Path.GetFileNameWithoutExtension(serverDll);
            foreach (string ext in new[] { ".deps.json", ".runtimeconfig.json" })
            {
                string p = Path.Combine(dir, stem + ext);
                if (File.Exists(p)) r.Notes.Add($"matched {stem}{ext}");
            }
        }

        // Guards against a same-named file that isn't the real assembly.
        public static string VerifyAssemblyIdentity(string dir, string cache, string serverDll)
        {
            string path = Path.Combine(dir, serverDll);
            if (!File.Exists(path) && cache != null) path = Path.Combine(cache, serverDll);
            if (!File.Exists(path)) return $"{serverDll} was not produced by extraction";
            try
            {
                string expected = Path.GetFileNameWithoutExtension(serverDll);
                string actual = AssemblyName.GetAssemblyName(path).Name;
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return $"{serverDll} declares assembly name '{actual}', expected '{expected}'";
                return null;
            }
            catch (Exception ex) { return $"{serverDll} is not a readable managed assembly: {ex.Message}"; }
        }

        private static string FirstExisting(string dir, string[] names)
        {
            foreach (string n in names) if (File.Exists(Path.Combine(dir, n))) return n;
            return null;
        }

        private static string FirstExistingIn(string dir, string cache, string[] names)
        {
            foreach (string n in names)
            {
                if (File.Exists(Path.Combine(dir, n))) return n;
                if (cache != null && File.Exists(Path.Combine(cache, n))) return n;
            }
            return null;
        }

        private static string FirstIn(List<string> haystack, string[] names)
        {
            foreach (string n in names) if (Contains(haystack, n)) return n;
            return null;
        }

        private static bool Contains(List<string> haystack, string name)
        {
            foreach (string h in haystack)
                if (string.Equals(h, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string SafeFull(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p; }
        }
    }
}
