using KMHServerAddon.Features.Global;

namespace KMHServerAddon.Features.Discord
{
    // The Global server id is the durable one; the instance id only separates two processes claiming it.
    internal static class KmhDiscordIdentity
    {
        public static string ServerId
        {
            get
            {
                string registered = GlobalConfig.Current?.RegisteredServerId;
                return string.IsNullOrWhiteSpace(registered) ? KmhServerIdentity.Id : registered.Trim();
            }
        }

        public static string InstanceId => KmhServerIdentity.Id;
        public static string DisplayName => KmhServerIdentity.Name;
    }
}
