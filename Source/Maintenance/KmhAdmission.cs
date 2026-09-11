using System;

namespace KMHServerAddon.Maintenance
{
    // Alternate ingresses reach the stores without passing the router - that is how Discord kept trading through a freeze.
    internal enum KmhIngress { ClientApi, ClientChat, Discord, Sdk, Scheduler, Admin, Recovery }

    // "May authoritative state change right now" - kept separate from feature enablement and from who is asking.
    internal static class KmhAdmission
    {
        public static bool AllowsValueMutation(KmhIngress from, out string refusal)
        {
            refusal = null;
            switch (from)
            {
                // Recovery and repair are how the freeze ends; gating them behind it deadlocks the system.
                case KmhIngress.Recovery:
                case KmhIngress.Admin:
                    return true;
            }
            // Before the stores are loaded there is nothing authoritative to mutate, only an empty copy of it.
            if (!KmhReadiness.IsReady)
            {
                refusal = $"KMH is not accepting changes yet - {KmhReadiness.Describe()}.";
                return false;
            }
            if (!KmhMaintenanceGate.BlocksValueMoves) return true;
            refusal = $"KMH is paused right now ({KmhMaintenanceGate.Describe()}) - value moves are refused until it finishes.";
            return false;
        }

        // Player-facing ingress must also respect the owner's feature toggle; admin repair deliberately may not.
        public static bool AllowsPlayerFeature(KmhIngress from, string feature, out string refusal)
        {
            if (!AllowsValueMutation(from, out refusal)) return false;
            if (string.IsNullOrEmpty(feature) || Features.FeaturesConfig.Current.IsEnabled(feature)) return true;
            refusal = $"{feature} is turned off on this server.";
            return false;
        }
    }
}
