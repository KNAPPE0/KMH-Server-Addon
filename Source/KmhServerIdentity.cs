namespace KMHServerAddon
{
    internal static class KmhServerIdentity
    {
        public static string Id => Persistence.KmhDataMeta.InstanceId ?? "";

        // Derived from the signing key's fingerprint, so learning the id early is not enough to register it first.
        public static string DeriveId(string publicKey)
        {
            string fp = Features.Global.KmhGlobalSigning.Fingerprint(publicKey ?? "");
            return fp.Length >= 32 ? fp.Substring(0, 32).ToLowerInvariant() : "";
        }

        // The owner's KMH name when set, otherwise RWT's own. An explicit value always wins and is never rewritten.
        public static string Name
        {
            get
            {
                string n = Maintenance.MaintenanceConfig.Current?.ServerName;
                if (!string.IsNullOrWhiteSpace(n)) return n.Trim();

                string rwt = null;
                try { rwt = Master.ServerConfig?.Name; } catch { }
                if (string.IsNullOrWhiteSpace(rwt)) rwt = RwtNameOnDisk();
                return string.IsNullOrWhiteSpace(rwt) ? FallbackName : rwt.Trim();
            }
        }

        // Neutral, as Global's own placeholder is: a server with no name must not read as Colony Nexus's own.
        internal const string FallbackName = "RimWorld Together server";

        // Colony Nexus starts before RWT loads its configs, so early boot reads RWT's own file rather than a placeholder.
        private static string _rwtNameOnDisk;
        private static string RwtNameOnDisk()
        {
            if (_rwtNameOnDisk != null) return _rwtNameOnDisk;
            try
            {
                string path = System.IO.Path.Combine(Master.MainPath ?? System.IO.Directory.GetCurrentDirectory(), "Configs", "ServerConfig.json");
                _rwtNameOnDisk = System.IO.File.Exists(path)
                    ? (string)Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path))["Name"] ?? ""
                    : "";
            }
            catch { _rwtNameOnDisk = ""; }
            return _rwtNameOnDisk;
        }
    }
}
