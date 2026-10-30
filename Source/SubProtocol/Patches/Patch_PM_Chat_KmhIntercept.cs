using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol.Patches
{
    // Returning false skips RWT's chat processing entirely, so KMH JSON never reaches a visible surface.
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_KmhIntercept
    {
        // Scanned before deserializing, so ordinary chat never pays for a parse RWT is about to repeat anyway.
        private static readonly byte[] Marker = System.Text.Encoding.UTF8.GetBytes("[KMH-CLI]");

        [HarmonyPrefix]
        internal static bool Prefix(ServerClient client, byte[] bytes)
        {
            // RWT would relay it under that account's name, which clients take for the server's own.
            if (Features.Moderation.BanGate.IsReservedName(client?.GetData<UserFile>()?.Username))
            {
                ServerLog.Warn("Dropped chat from an account named like Colony Nexus' own system name.");
                return false;
            }
            if (bytes == null || IndexOf(bytes, Marker) < 0) return true;

            PKT_Chat pkt;
            try
            {
                pkt = Serializer.ConvertBytesToObject<PKT_Chat>(bytes);
            }
            catch
            {
                return true;
            }

            if (pkt == null || pkt.Username != KmhProtocol.ClientUsername)
            {
                return true;
            }

            // Before login a connection has no identity, and every limiter and handler keys on one.
            if (string.IsNullOrEmpty(client?.GetData<UserFile>()?.Username))
            {
                ServerLog.Verbose("Dropped Colony Nexus traffic from a connection that has not logged in.");
                return false;
            }

            try
            {
                KmhRouter.HandleInbound(client, pkt);
            }
            catch (System.Exception ex)
            {
                ServerLog.Error("HandleInbound threw unexpectedly", ex);
            }

            return false;
        }

        // Naive search is enough: the needle is tiny and the haystack is one chat packet.
        private static int IndexOf(byte[] hay, byte[] needle)
        {
            int end = hay.Length - needle.Length;
            for (int i = 0; i <= end; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
