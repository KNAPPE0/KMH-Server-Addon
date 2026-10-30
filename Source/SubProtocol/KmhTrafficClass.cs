using System;
using System.Collections.Generic;

namespace KMHServerAddon.SubProtocol
{
    internal enum TrafficClass { Control, Interactive, Background, Bulk }

    // Separate budgets, so control traffic is never starved by a transfer and a transfer is not capped by message count.
    internal static class KmhTrafficClass
    {
        public const int ControlPerWindow = 60;
        public const int InteractivePerWindow = 120;
        public const int BackgroundPerWindow = 90;
        // Resized by EnforcementPublish at registration so a legal profile's frames fit; this default precedes that.
        public static int BulkPerWindow = 400;
        public const int WindowSeconds = 10;

        private static readonly HashSet<string> Control = new HashSet<string>(StringComparer.Ordinal)
        {
            KmhProtocol.Kind.Ping,
            KmhProtocol.Kind.HelloAck,
            KmhProtocol.Kind.Hello,
        };

        private static readonly HashSet<string> Bulk = new HashSet<string>(StringComparer.Ordinal)
        {
            KmhProtocol.Kind.EnforcementPublishPart,
        };

        private static readonly HashSet<string> Background = new HashSet<string>(StringComparer.Ordinal)
        {
            KmhProtocol.Kind.EnforcementSnapshotRequest,
            KmhProtocol.Kind.EnforcementProfileRequest,
            // A cross-store aggregate any client can ask for, and a display ranking nobody types into.
            KmhProtocol.Kind.GuildLeaderboardRequest,
            KmhProtocol.Kind.LeaderboardsRequest,
        };

        public static TrafficClass Of(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return TrafficClass.Interactive;
            if (Control.Contains(kind)) return TrafficClass.Control;
            if (Bulk.Contains(kind)) return TrafficClass.Bulk;
            if (Background.Contains(kind)) return TrafficClass.Background;
            return TrafficClass.Interactive;
        }

        public static int LimitFor(TrafficClass c) => c switch
        {
            TrafficClass.Control     => ControlPerWindow,
            TrafficClass.Background  => BackgroundPerWindow,
            TrafficClass.Bulk        => BulkPerWindow,
            _                        => InteractivePerWindow,
        };

        // A bulk kind only gets the bulk budget while that user actually holds an open transaction. Without one
        // it is charged as interactive, so a modified client blasting publish parts is throttled like any flood.
        public static Func<string, bool> BulkAuthorized = _ => false;

        public static TrafficClass Effective(string kind, string username)
        {
            TrafficClass c = Of(kind);
            if (c != TrafficClass.Bulk) return c;
            return BulkAuthorized(username) ? TrafficClass.Bulk : TrafficClass.Interactive;
        }
    }
}
