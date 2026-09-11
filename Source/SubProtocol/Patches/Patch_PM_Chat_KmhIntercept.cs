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
