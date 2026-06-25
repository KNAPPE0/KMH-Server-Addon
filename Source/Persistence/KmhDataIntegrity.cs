using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Persistence
{
    // Read-only KMH JSON health check for kmh verify; missing is fine, empty or unparseable gets flagged.
    internal static class KmhDataIntegrity
    {
        public enum State { Missing, Ok, Empty, Corrupt }

        public sealed class FileStatus
        {
            public string Label;
            public string Path;
            public State  State;
            public bool   Regenerable;
            public long   Bytes;
            public string Detail = "";
        }

        public sealed class ScanResult
        {
            public List<FileStatus> Files = new List<FileStatus>();
            public List<string> StrayCorruptFiles = new List<string>(); // .corrupt-* recovery leftovers (informational)
            public List<string> StrayTmpFiles      = new List<string>(); // .tmp interrupted writes (safe to remove)
            public int Ok, Missing, Empty, Corrupt;
            public bool CriticalDamage; // a non-regenerable file is empty/corrupt - the loud case
            public string Summary =>
                $"{Ok} ok, {Missing} not-yet-created, {Empty} empty, {Corrupt} corrupt" +
                (StrayCorruptFiles.Count > 0 ? $", {StrayCorruptFiles.Count} salvaged .corrupt file(s)" : "") +
                (StrayTmpFiles.Count > 0 ? $", {StrayTmpFiles.Count} stray .tmp file(s)" : "");
        }

        public static ScanResult Scan()
        {
            ScanResult r = new ScanResult();
            foreach (KmhDataPaths.DataFile f in KmhDataPaths.KnownDataFiles)
            {
                FileStatus fs = new FileStatus { Label = f.Label, Path = f.Path, Regenerable = f.Regenerable };
                try
                {
                    if (!File.Exists(f.Path))
                    {
                        fs.State = State.Missing; fs.Detail = "not created yet"; r.Missing++;
                    }
                    else
                    {
                        fs.Bytes = new FileInfo(f.Path).Length;
                        string text = File.ReadAllText(f.Path);
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            fs.State = State.Empty; fs.Detail = "file is empty"; r.Empty++;
                            if (!f.Regenerable) r.CriticalDamage = true;
                        }
                        else
                        {
                            try { JToken.Parse(text); fs.State = State.Ok; fs.Detail = $"{fs.Bytes} bytes"; r.Ok++; }
                            catch (Exception ex)
                            {
                                fs.State = State.Corrupt; fs.Detail = ex.Message; r.Corrupt++;
                                if (!f.Regenerable) r.CriticalDamage = true;
                            }
                        }
                    }
                }
                catch (Exception ex) { fs.State = State.Corrupt; fs.Detail = $"read error: {ex.Message}"; r.Corrupt++; if (!f.Regenerable) r.CriticalDamage = true; }
                r.Files.Add(fs);
            }

            try
            {
                if (Directory.Exists(KmhDataPaths.Folder))
                {
                    foreach (string p in Directory.GetFiles(KmhDataPaths.Folder, "*", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileName(p);
                        if (name.IndexOf(".corrupt-", StringComparison.OrdinalIgnoreCase) >= 0) r.StrayCorruptFiles.Add(p);
                        else if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))      r.StrayTmpFiles.Add(p);
                    }
                }
            }
            catch { /* stray-file sweep is best-effort */ }

            return r;
        }

        // Boot logging: one summary line, then loud per-file errors only for the damaged irreplaceable files.
        public static void LogScan(ScanResult r)
        {
            if (r.CriticalDamage)
                ServerLog.Error($"Data integrity: {r.Summary} - IRREPLACEABLE DATA IS DAMAGED (see below). " +
                                "Restore from KMH-Data-Backups before players reconnect.");
            else
                ServerLog.Info($"Data integrity: {r.Summary}.");

            foreach (FileStatus fs in r.Files.Where(x => x.State == State.Corrupt || x.State == State.Empty))
            {
                string msg = $"Data integrity: {fs.Label} ({Path.GetFileName(fs.Path)}) is {fs.State} - {fs.Detail}.";
                if (fs.Regenerable) ServerLog.Warn(msg + " It will regenerate from defaults/clients.");
                else                ServerLog.Error(msg + " This is runtime state with no automatic rebuild.");
            }

            if (r.StrayTmpFiles.Count > 0)
                ServerLog.Warn($"Data integrity: {r.StrayTmpFiles.Count} leftover .tmp file(s) from interrupted writes - " +
                               "harmless (canonical files are intact), remove them at your leisure.");
        }
    }
}
