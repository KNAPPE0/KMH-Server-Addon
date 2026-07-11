using System;
using System.IO;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Persistence
{
    // Coordinated rollback. A restore is queued as a one-line marker file (a backup name, "latest", or
    // "before:<iso|yyyyMMdd-HHmmss>"); it's applied at boot, before any store loads, so KMH lines up with an external
    // RWT rollback. An external tool can drop the marker itself, or `kmh restore` writes it.
    internal static class KmhDataRestore
    {
        public static void ApplyPendingRestore()
        {
            string marker = KmhDataPaths.RestoreRequestFile;
            string spec;
            try
            {
                if (!File.Exists(marker)) return;
                spec = File.ReadAllText(marker).Trim();
            }
            catch (Exception ex) { ServerLog.Warn($"Restore marker unreadable: {ex.Message}"); return; }

            // Consume the marker regardless of outcome, so a bad spec can't loop the restore every boot.
            TryDeleteMarker(marker);

            if (string.IsNullOrWhiteSpace(spec)) { ServerLog.Warn("Restore marker was empty - ignoring."); return; }

            ServerLog.Warn($"Restore requested: '{spec}'. Applying to KMH-Data before load...");
            if (KmhDataBackup.Restore(spec, out string safety, out string err))
                ServerLog.Success($"Restore complete from '{spec}'." + (safety != null ? $" Previous data saved as '{safety}'." : ""));
            else
                ServerLog.Error($"Restore FAILED for '{spec}': {err}. Continuing with existing KMH-Data.");
        }

        // Queue a restore for the next boot. Resolves the spec now and stores the concrete backup name, so the result
        // is deterministic even if new backups appear before the restart.
        public static bool Queue(string spec, out string resolved, out string error)
        {
            resolved = null; error = null;
            string path = KmhDataBackup.ResolvePath(spec);
            if (path == null) { error = $"no backup matches '{spec}'"; return false; }
            resolved = Path.GetFileName(path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KmhDataPaths.RestoreRequestFile));
                File.WriteAllText(KmhDataPaths.RestoreRequestFile, resolved);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static void TryDeleteMarker(string marker)
        {
            try { if (File.Exists(marker)) File.Delete(marker); }
            catch (Exception ex) { ServerLog.Warn($"Could not clear restore marker: {ex.Message}"); }
        }
    }
}
