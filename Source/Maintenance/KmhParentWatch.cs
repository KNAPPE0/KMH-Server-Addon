using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KMHServerAddon.Maintenance
{
    // The launcher re-runs itself and waits; stopping only the launcher must stop that copy, the real server, too.
    internal static class KmhParentWatch
    {
        internal const string ParentVar = "KMH_REEXEC_PARENT";

        public static void MarkChild(ProcessStartInfo psi) => psi.Environment[ParentVar] = Environment.ProcessId.ToString();

        public static bool Start(string parentPidText, Action onGone, int pollMs = 1000)
        {
            if (onGone == null || !int.TryParse(parentPidText, out int pid) || pid <= 0) return false;
            Process parent;
            try { parent = Process.GetProcessById(pid); }
            catch (ArgumentException) { Run(onGone); return true; }   // already gone: the stop came first
            catch (Exception) { return false; }

            var t = new Thread(() =>
            {
                try { while (!parent.HasExited) Thread.Sleep(pollMs); }
                catch (Exception) { }
                Run(onGone);
            }) { IsBackground = true, Name = "KMH launcher watch" };
            t.Start();
            return true;
        }

        // An exception escaping a background thread ends the process without the shutdown it is meant to start.
        private static void Run(Action onGone)
        {
            try { onGone(); } catch (Exception) { }
        }

        // SIGTERM, then wait, so the server finishes saving before the launcher exits.
        public static bool ForwardStop(Process child, int waitMs = 25000)
        {
            try
            {
                if (child == null || child.HasExited) return false;
                // Windows sends Ctrl+C and window close to both processes; the watch covers a hard kill of the launcher.
                if (OperatingSystem.IsWindows()) return false;
                if (SysKill(child.Id, 15) != 0) return false;
                child.WaitForExit(waitMs);
                return true;
            }
            catch (Exception) { return false; }
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SysKill(int pid, int signal);
    }
}
