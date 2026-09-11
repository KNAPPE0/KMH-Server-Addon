namespace KMHServerAddon
{
    internal static class KmhServerIdentity
    {
        public static string Id => Persistence.KmhDataMeta.InstanceId ?? "";

        // The owner's KMH name when set, otherwise RWT's own. An explicit value always wins and is never rewritten.
        public static string Name
        {
            get
            {
                string n = Maintenance.MaintenanceConfig.Current?.ServerName;
                if (!string.IsNullOrWhiteSpace(n)) return n.Trim();

                string rwt = null;
                try { rwt = Master.ServerConfig?.Name; } catch { }
                return string.IsNullOrWhiteSpace(rwt) ? "KMH Server" : rwt.Trim();
            }
        }
    }
}
