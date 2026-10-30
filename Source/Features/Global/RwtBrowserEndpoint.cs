using System;
using System.Net;
using System.Reflection;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Global
{
    // RWT's browser lists the IP the server looks up for itself; this makes it list PublicHost instead.
    internal static class RwtBrowserEndpoint
    {
        private const string TelemetryType = "RTNetwork.Packets.ServerBrowser.PKT_ServerTelemetry";
        private static string _lastLogged;

        internal static MethodInfo Target()
        {
            Type pkt = AccessTools.TypeByName(TelemetryType);
            MethodInfo set = pkt == null ? null : AccessTools.PropertySetter(pkt, "Endpoint");
            ParameterInfo[] ps = set?.GetParameters();
            return ps != null && ps.Length == 1 && ps[0].ParameterType == typeof(string) ? set : null;
        }

        internal static void Install(Harmony harmony)
        {
            try
            {
                MethodInfo set = Target();
                if (set == null) { ServerLog.Diag("RWT server browser: this RWT build has no listing endpoint - nothing to rename."); return; }
                harmony.Patch(set, prefix: new HarmonyMethod(typeof(RwtBrowserEndpoint), nameof(UsePublicHost)));
            }
            catch (Exception ex) { ServerLog.Warn($"RWT server browser: endpoint rename setup threw: {ex.Message}"); }
        }

        // Not named Prefix: Harmony treats that name as a patch by convention, and this one is applied by hand.
        private static void UsePublicHost(ref string __0)
        {
            try
            {
                string listed = Announced(__0, GlobalConfig.Current?.PublicHost);
                if (listed == __0) return;
                if (listed != _lastLogged)
                {
                    _lastLogged = listed;
                    ServerLog.Info($"RWT server browser: listed as {listed} (PublicHost), not the looked-up address.");
                }
                __0 = listed;
            }
            catch { }
        }

        // Only a looked-up IP (or a failed lookup) is replaced, and only by a host players outside can reach.
        internal static string Announced(string lookedUp, string publicHost)
        {
            string host = (publicHost ?? "").Trim();
            if (host.Length == 0 || GlobalConfigCheck.IsPrivateHost(host)) return lookedUp;
            string ip = (lookedUp ?? "").Trim();
            return ip.Length == 0 || IPAddress.TryParse(ip, out _) ? host : lookedUp;
        }
    }
}
