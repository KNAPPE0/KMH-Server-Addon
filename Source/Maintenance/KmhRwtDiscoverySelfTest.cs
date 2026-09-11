using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KMHServerAddon.Maintenance
{
    // The next RWT rename should fail here rather than in an owner's console.
    internal static class KmhRwtDiscoverySelfTest
    {
        public static bool Run(Action<string> log, out string summary)
        {
            int pass = 0, fail = 0;
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try
                {
                    (bool ok, string detail) = probe();
                    if (ok) { pass++; log?.Invoke($"    PASS  {name}{(string.IsNullOrEmpty(detail) ? "" : " - " + detail)}"); }
                    else    { fail++; log?.Invoke($"    FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : " - " + detail)}"); }
                }
                catch (Exception ex) { fail++; log?.Invoke($"    FAIL  {name} - threw: {ex.Message}"); }
            }

            string root = Path.Combine(Path.GetTempPath(), "kmh-discovery-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Check("Generation: 26.5.x (GameServer + Shared/TCPNetwork)", () =>
                {
                    string d = Scratch(root, "gen-old", "GameServer.dll", "Shared.dll", "TCPNetwork.dll");
                    string g = RwtDiscovery.DetectGeneration(d, null, "GameServer.dll");
                    return (g == "old", g ?? "null");
                });
                Check("Generation: 26.6.x (GameServer + RTShared/RTNetwork)", () =>
                {
                    string d = Scratch(root, "gen-new", "GameServer.dll", "RTShared.dll", "RTNetwork.dll");
                    string g = RwtDiscovery.DetectGeneration(d, null, "GameServer.dll");
                    return (g == "new", g ?? "null");
                });
                Check("Generation: renamed build (RTServer + RTShared/RTNetwork)", () =>
                {
                    string d = Scratch(root, "gen-rt", "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    string g = RwtDiscovery.DetectGeneration(d, null, "RTServer.dll");
                    return (g == "rt", g ?? "null");
                });
                Check("Generation: server assembly alone is not enough", () =>
                {
                    string d = Scratch(root, "gen-partial", "RTServer.dll");
                    string g = RwtDiscovery.DetectGeneration(d, null, null);
                    return (g == null, g ?? "null");
                });
                Check("Generation: detected without being told which server dll", () =>
                {
                    string d = Scratch(root, "gen-auto", "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    string g = RwtDiscovery.DetectGeneration(d, null, null);
                    return (g == "rt", g ?? "null");
                });

                Check("Candidates cover both the old and renamed server names", () =>
                {
                    List<string> names = new List<string>(RwtDiscovery.CandidateNames());
                    string[] must = { "GameServer.exe", "RTServer.exe", "GameServer.rwt.exe", "GameServer", "RTServer", "GameServer.rwt", "GameServer.real", "RwtServer" };
                    List<string> missing = new List<string>();
                    foreach (string m in must) if (!names.Contains(m)) missing.Add(m);
                    return (missing.Count == 0, missing.Count == 0 ? $"{names.Count} candidates" : "missing " + string.Join(", ", missing));
                });

                Check("Finds a renamed-assembly server (RTServer.exe)", () =>
                {
                    string d = Scratch(root, "find-rt");
                    WriteBundle(Path.Combine(d, "RTServer.exe"), "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    return (r.SelectedExe != null && r.ServerDll == "RTServer.dll",
                            r.SelectedExe == null ? "nothing selected" : Path.GetFileName(r.SelectedExe) + " / " + r.ServerDll);
                });
                Check("Finds a legacy server (GameServer.exe)", () =>
                {
                    string d = Scratch(root, "find-old");
                    WriteBundle(Path.Combine(d, "GameServer.exe"), "GameServer.dll", "Shared.dll", "TCPNetwork.dll");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    return (r.SelectedExe != null && r.ServerDll == "GameServer.dll",
                            r.SelectedExe == null ? "nothing selected" : Path.GetFileName(r.SelectedExe) + " / " + r.ServerDll);
                });
                Check("Renamed RTServer.exe -> GameServer.exe still identified by content", () =>
                {
                    string d = Scratch(root, "find-renamed");
                    WriteBundle(Path.Combine(d, "GameServer.exe"), "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    return (r.ServerDll == "RTServer.dll", r.ServerDll ?? "null");
                });

                // Picking ourselves here would be an infinite re-exec loop, so this one matters most.
                Check("Bisect layout: KMH named GameServer.exe rejects itself, picks GameServer.rwt.exe", () =>
                {
                    string d = Scratch(root, "bisect");
                    WriteBundle(Path.Combine(d, "GameServer.exe"), "KMHServerAddon.dll", "0Harmony.dll");
                    WriteBundle(Path.Combine(d, "GameServer.rwt.exe"), "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    bool picked = r.SelectedExe != null
                               && Path.GetFileName(r.SelectedExe).Equals("GameServer.rwt.exe", StringComparison.OrdinalIgnoreCase);
                    return (picked && r.ServerDll == "RTServer.dll",
                            r.SelectedExe == null ? "nothing selected" : Path.GetFileName(r.SelectedExe));
                });
                Check("A KMH build is never selected, whatever it is named", () =>
                {
                    string d = Scratch(root, "self-only");
                    WriteBundle(Path.Combine(d, "GameServer.exe"), "KMHServerAddon.dll", "0Harmony.dll");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    bool named = r.Checked.Count > 0 && r.Checked[0].Rejection != null
                              && r.Checked[0].Rejection.Contains("KMH");
                    return (r.SelectedExe == null && named,
                            r.SelectedExe != null ? "WRONGLY selected " + Path.GetFileName(r.SelectedExe)
                                                  : (r.Checked.Count > 0 ? r.Checked[0].Rejection : "no candidates"));
                });
                Check("A bigger KMH build still loses to a smaller real server (size is not identity)", () =>
                {
                    string d = Scratch(root, "size-trap");
                    WriteBundle(Path.Combine(d, "GameServer.exe"), 512 * 1024, "KMHServerAddon.dll");
                    WriteBundle(Path.Combine(d, "RTServer.exe"), 1, "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    return (r.SelectedExe != null && Path.GetFileName(r.SelectedExe) == "RTServer.exe",
                            r.SelectedExe == null ? "nothing selected" : Path.GetFileName(r.SelectedExe));
                });
                Check("Nothing usable present -> no selection, candidates recorded", () =>
                {
                    string d = Scratch(root, "empty");
                    File.WriteAllText(Path.Combine(d, "GameServer.exe"), "not a bundle at all");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    return (r.SelectedExe == null && r.Generation == null && r.Checked.Count == 1,
                            r.Checked.Count > 0 ? r.Checked[0].Rejection : "no candidate recorded");
                });
                Check("Loose install is used directly, without extraction", () =>
                {
                    string d = Scratch(root, "loose", "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    File.WriteAllText(Path.Combine(d, "RTServer.deps.json"), "{}");
                    File.WriteAllText(Path.Combine(d, "RTServer.runtimeconfig.json"), "{}");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    bool deps = r.Notes.Exists(n => n.Contains("RTServer.deps.json"))
                             && r.Notes.Exists(n => n.Contains("RTServer.runtimeconfig.json"));
                    return (r.LooseLayout && r.Generation == "rt" && deps, r.Generation + ", deps matched: " + deps);
                });

                Check("Slim layout: loose server dll + apphost, no extraction", () =>
                {
                    string d = Scratch(root, "slim", "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    File.WriteAllText(Path.Combine(d, "RTServer.exe"), "apphost stub, not a bundle");
                    File.WriteAllText(Path.Combine(d, "RTServer.deps.json"), "{}");
                    WriteRuntimeConfig(Path.Combine(d, "RTServer.runtimeconfig.json"), Environment.Version.Major);
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    return (r.LooseLayout && r.Generation == "rt" && r.RuntimeProblem == null,
                            $"loose={r.LooseLayout} gen={r.Generation ?? "null"} runtime={r.RuntimeProblem ?? "ok"}");
                });
                Check("Slim layout needing a newer .NET is reported as a runtime problem, not a missing server", () =>
                {
                    string d = Scratch(root, "slim-newer", "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    WriteRuntimeConfig(Path.Combine(d, "RTServer.runtimeconfig.json"), Environment.Version.Major + 2);
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    bool named = r.RuntimeProblem != null && r.RuntimeProblem.Contains("needs the .NET");
                    return (named && r.ServerDll == "RTServer.dll", r.RuntimeProblem ?? "no runtime problem reported");
                });
                Check("Self-contained server (no runtimeconfig) reports no runtime problem", () =>
                {
                    string d = Scratch(root, "selfcontained", "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, Path.Combine(d, ".rwt-runtime"));
                    return (r.RuntimeProblem == null, r.RuntimeProblem ?? "ok");
                });

                Check("Fingerprint changes when the server assembly is renamed", () =>
                {
                    string d = Scratch(root, "fp");
                    string p = Path.Combine(d, "GameServer.exe");
                    WriteBundle(p, "GameServer.dll");
                    string a = RwtDiscovery.Fingerprint(p, "GameServer.dll");
                    string b = RwtDiscovery.Fingerprint(p, "RTServer.dll");
                    return (a != b, "distinct");
                });
                Check("Fingerprint changes when the server exe changes", () =>
                {
                    string d = Scratch(root, "fp2");
                    string p = Path.Combine(d, "GameServer.exe");
                    WriteBundle(p, "GameServer.dll");
                    string a = RwtDiscovery.Fingerprint(p, "GameServer.dll");
                    WriteBundle(p, "GameServer.dll", "RTShared.dll", "RTNetwork.dll");
                    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(5));
                    string b = RwtDiscovery.Fingerprint(p, "GameServer.dll");
                    return (a != b, "distinct");
                });

                Check("Fingerprint changes when a sibling deps.json changes", () =>
                {
                    string d = Scratch(root, "fp-deps");
                    string p = Path.Combine(d, "RTServer.exe");
                    WriteBundle(p, "RTServer.dll");
                    string deps = Path.Combine(d, "RTServer.deps.json");
                    string a = RwtDiscovery.Fingerprint(p, "RTServer.dll");   // absent
                    File.WriteAllText(deps, "{}");
                    string b = RwtDiscovery.Fingerprint(p, "RTServer.dll");   // present
                    File.WriteAllText(deps, "{\"changed\":true}");
                    File.SetLastWriteTimeUtc(deps, DateTime.UtcNow.AddMinutes(5));
                    string c = RwtDiscovery.Fingerprint(p, "RTServer.dll");   // changed
                    return (a != b && b != c, "three distinct keys");
                });

                // A leftover old server assembly would make generation detection key off a file RWT no longer ships.
                Check("Stale .rwt-runtime is purged when RWT updates", () =>
                {
                    string d = Scratch(root, "stale");
                    string cache = Path.Combine(d, ".rwt-runtime");
                    string p = Path.Combine(d, "GameServer.exe");

                    WriteBundle(p, "GameServer.dll", "RTShared.dll", "RTNetwork.dll");
                    RwtDiscovery.Result r1 = RwtDiscovery.Locate(d, cache);
                    SingleFileBundle.Extract(r1.SelectedExe, cache, r1.Fingerprint, r1.ServerDll);
                    bool before = File.Exists(Path.Combine(cache, "GameServer.dll"))
                               && RwtDiscovery.DetectGeneration(d, cache, r1.ServerDll) == "new";

                    // Owner drops in the newer release under the same file name.
                    WriteBundle(p, "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(5));
                    RwtDiscovery.Result r2 = RwtDiscovery.Locate(d, cache);
                    SingleFileBundle.Extract(r2.SelectedExe, cache, r2.Fingerprint, r2.ServerDll);

                    bool goneOld = !File.Exists(Path.Combine(cache, "GameServer.dll"));
                    bool hasNew  = File.Exists(Path.Combine(cache, "RTServer.dll"));
                    string gen   = RwtDiscovery.DetectGeneration(d, cache, r2.ServerDll);
                    return (before && goneOld && hasNew && gen == "rt",
                            $"before={before} staleGone={goneOld} rtPresent={hasNew} gen={gen ?? "null"}");
                });
                Check("Unchanged server exe reuses the cache", () =>
                {
                    string d = Scratch(root, "reuse");
                    string cache = Path.Combine(d, ".rwt-runtime");
                    string p = Path.Combine(d, "RTServer.exe");
                    WriteBundle(p, "RTServer.dll", "RTShared.dll", "RTNetwork.dll");
                    RwtDiscovery.Result r = RwtDiscovery.Locate(d, cache);
                    SingleFileBundle.Extract(r.SelectedExe, cache, r.Fingerprint, r.ServerDll);
                    string marker = Path.Combine(cache, "RTServer.dll");
                    DateTime first = File.GetLastWriteTimeUtc(marker);
                    File.SetLastWriteTimeUtc(marker, first.AddDays(-1));
                    SingleFileBundle.Extract(r.SelectedExe, cache, r.Fingerprint, r.ServerDll);
                    return (File.GetLastWriteTimeUtc(marker) != first, "not re-extracted");
                });

                Check("Assembly identity check rejects a non-assembly", () =>
                {
                    string d = Scratch(root, "identity");
                    File.WriteAllText(Path.Combine(d, "RTServer.dll"), "definitely not IL");
                    string err = RwtDiscovery.VerifyAssemblyIdentity(d, null, "RTServer.dll");
                    return (err != null, err ?? "wrongly accepted");
                });
                Check("Assembly identity check reports a missing assembly", () =>
                {
                    string d = Scratch(root, "identity-missing");
                    string err = RwtDiscovery.VerifyAssemblyIdentity(d, null, "RTServer.dll");
                    return (err != null, err ?? "wrongly accepted");
                });
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }

            summary = $"{pass} passed, {fail} failed";
            return fail == 0;
        }

        private static void WriteRuntimeConfig(string path, int majorVersion)
            => File.WriteAllText(path,
                "{\"runtimeOptions\":{\"tfm\":\"net" + majorVersion + ".0\",\"framework\":{"
              + "\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + majorVersion + ".0.0\"}}}");

        private static string Scratch(string root, string name, params string[] files)
        {
            string d = Path.Combine(root, name);
            Directory.CreateDirectory(d);
            foreach (string f in files) File.WriteAllText(Path.Combine(d, f), "stub");
            return d;
        }

        private static void WriteBundle(string path, params string[] managedNames)
            => WriteBundle(path, 1, managedNames);

        // Minimal HostModel bundle; `pad` inflates the host stub so size can be decoupled from identity.
        private static void WriteBundle(string path, int pad, params string[] managedNames)
        {
            const int Major = 6, Minor = 0;
            using MemoryStream ms = new MemoryStream();
            using BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8, true);

            w.Write(new byte[Math.Max(1, pad)]);                       // stand-in for the native host

            List<(long off, long len, string name)> entries = new List<(long, long, string)>();
            foreach (string n in managedNames)
            {
                byte[] body = Encoding.UTF8.GetBytes("stub:" + n);
                long off = ms.Position;
                w.Write(body);
                entries.Add((off, body.Length, n));
            }

            long manifest = ms.Position;
            w.Write(Major);
            w.Write(Minor);
            w.Write(entries.Count);
            WriteStr(w, Guid.NewGuid().ToString("N"));
            w.Write(new byte[40]);                                     // deps/runtimeconfig/flags
            foreach ((long off, long len, string name) in entries)
            {
                w.Write(off);
                w.Write(len);
                w.Write(0L);                                           // uncompressed
                w.Write((byte)1);                                      // managed assembly
                WriteStr(w, name);
            }

            w.Write(manifest);
            w.Write(new byte[]
            {
                0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
                0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
            });
            w.Flush();
            File.WriteAllBytes(path, ms.ToArray());
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            int len = b.Length;
            while (len >= 0x80) { w.Write((byte)(len | 0x80)); len >>= 7; }
            w.Write((byte)len);
            w.Write(b);
        }
    }
}
