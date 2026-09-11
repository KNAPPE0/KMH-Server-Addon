using System;

namespace KMHServerAddon.Maintenance
{
    // Unknown kinds classify as mutating, so the next feature added is refused rather than forgotten.
    internal static class KmhMutationClass
    {
        // Everything absent from this list writes something; reads stay open so players can look while frozen.
        public static bool IsRead(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return true;
            if (kind.EndsWith(".request", StringComparison.Ordinal)) return true;
            if (SubProtocol.KmhRouter.IsTransportControlKind(kind)) return true;

            switch (kind)
            {
                // Resolve a link for display; no authoritative state is written.
                case SubProtocol.KmhProtocol.Kind.VideoResolve:
                case SubProtocol.KmhProtocol.Kind.ChatMediaRefresh:
                    return true;

                // The client latches these on the send, not on our accept, so refusing one loses the session's data with no retry.
                case SubProtocol.KmhProtocol.Kind.ItemLabels:
                case SubProtocol.KmhProtocol.Kind.ItemValues:
                case SubProtocol.KmhProtocol.Kind.SiteMetaPush:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsMutation(string kind) => !IsRead(kind);
    }
}
