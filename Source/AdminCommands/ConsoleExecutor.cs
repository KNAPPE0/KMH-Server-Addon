using System;
using System.Linq;
using System.Text;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.AdminCommands
{
    internal static class ConsoleExecutor
    {
        // Printer is a global singleton, so two concurrent runs would tangle each other's capture.
        private static readonly object _lock = new object();

        public static string Run(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return "(empty command)";

            StringBuilder buf = new StringBuilder();

            lock (_lock)
            {
                Printer p = Printer.Instance;
                if (p == null) return "(no console available)";

                Action<object, Printer.Verbosity> oMsg = p.OnMessage, oWarn = p.OnWarning, oErr = p.OnError, oTitle = p.OnTitle;
                void Cap(object o) { if (o != null) buf.AppendLine(o.ToString()); }

                try
                {
                    p.OnMessage = (o, m) => { Cap(o); oMsg?.Invoke(o, m); };
                    p.OnWarning = (o, m) => { Cap(o); oWarn?.Invoke(o, m); };
                    p.OnError   = (o, m) => { Cap(o); oErr?.Invoke(o, m); };
                    p.OnTitle   = (o, m) => { Cap(o); oTitle?.Invoke(o, m); };

                    Dispatch(commandLine.Trim());
                }
                catch (Exception ex)
                {
                    buf.AppendLine($"[command threw] {ex.Message}");
                    ServerLog.Warn($"Console-run '{commandLine}' threw: {ex.Message}");
                }
                finally
                {
                    p.OnMessage = oMsg; p.OnWarning = oWarn; p.OnError = oErr; p.OnTitle = oTitle;
                }
            }

            string outp = buf.ToString().TrimEnd();
            return string.IsNullOrEmpty(outp) ? "(command produced no output)" : outp;
        }

        // Mirrors RWT's CMD_Base.ParseCommand, which is private and so cannot be called.
        private static void Dispatch(string input)
        {
            int sp = input.IndexOf(' ');
            string prefix = (sp < 0 ? input : input.Substring(0, sp)).ToLowerInvariant();
            string rest   = sp < 0 ? string.Empty : input.Substring(sp + 1);

            CMD_Base.CommandParameters = string.IsNullOrEmpty(rest)
                ? Array.Empty<string>()
                : rest.Split(' ');

            CMD_Base cmd = CMD_Base.Commands?.FirstOrDefault(c =>
                string.Equals(c?.Prefix, prefix, StringComparison.OrdinalIgnoreCase));

            if (cmd == null)
            {
                Printer.Warning($"Command '{prefix}' was not found. Type 'help' for the list.");
                return;
            }

            int paramCount = CMD_Base.CommandParameters.Length;
            if (cmd.ParameterCount == paramCount || cmd.ParameterCount == 0 || cmd.ParameterCount == -1)
                cmd.Action();
            else
                Printer.Warning($"Wrong parameter count for '{cmd.Prefix}' (expected {cmd.ParameterCount}, got {paramCount}).");
        }
    }
}
