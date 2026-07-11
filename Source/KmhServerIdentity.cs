namespace KMHServerAddon
{
    // Single source of truth for this instance's identity: a stable Id (from KmhDataMeta) + a friendly Name
    // (Config/Maintenance.json ServerName). Surfaced in status.json, the boot log, Discord, and the handshake so
    // multiple KMH servers on one host are easy to tell apart.
    internal static class KmhServerIdentity
    {
        public static string Id => Persistence.KmhDataMeta.InstanceId ?? "";

        public static string Name
        {
            get
            {
                string n = Maintenance.MaintenanceConfig.Current?.ServerName;
                return string.IsNullOrWhiteSpace(n) ? "KMH Server" : n.Trim();
            }
        }
    }
}
