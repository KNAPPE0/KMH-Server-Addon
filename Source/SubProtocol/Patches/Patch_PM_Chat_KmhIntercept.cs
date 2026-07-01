using GameServer.PacketManager;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol.Patches
{
    // Harmony Prefix on RWT's PM_Chat.Receive - when the inbound packet's Username matches
    // KmhProtocol.ClientUsername (zero-width-space prefix + [KMH-CLI]), we hand it to KmhRouter and return false to
    // skip RWT's normal chat processing entirely (no broadcast, no rate-limit check, no chat-log render - the JSON
    // never reaches any visible surface)
    //
    // For all other chat we return true and RWT proceeds as normal.
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_KmhIntercept
    {
        // ASCII tail of ClientUsername, embedded verbatim in a KMH packet's serialized username. Scan for it first so
        // normal chat skips the deserialize (RWT deserializes the same bytes again right after us).
        private static readonly byte[] Marker = System.Text.Encoding.UTF8.GetBytes("[KMH-CLI]");

        [HarmonyPrefix]
        private static bool Prefix(ServerClient client, byte[] bytes)
        {
            if (bytes == null || IndexOf(bytes, Marker) < 0) return true; // not a KMH message - let RWT handle it

            PKT_Chat pkt;
            try
            {
                pkt = Serializer.ConvertBytesToObject<PKT_Chat>(bytes);
            }
            catch
            {
                // Let RWT handle deserialization failures.
                return true;
            }

            if (pkt == null || pkt.Username != KmhProtocol.ClientUsername)
            {
                return true; // regular chat - defer to RWT
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

        // Naive byte-subsequence search; needle is tiny and the haystack is one chat packet, so this is plenty fast.
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
