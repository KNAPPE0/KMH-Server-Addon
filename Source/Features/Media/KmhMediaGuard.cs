using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace KMHServerAddon.Features.Media
{
    // The fetch target is client-influenced, so this is the SSRF gate: only a provably public address passes.
    internal static class KmhMediaGuard
    {
        internal static bool IsPublicAddress(IPAddress ip, out string why)
        {
            why = "";
            if (ip == null) { why = "no address"; return false; }

            // ::ffff:127.0.0.1 is loopback however it is spelled, so it is judged as the IPv4 address it really is.
            if (ip.IsIPv4MappedToIPv6)
            {
                try { ip = ip.MapToIPv4(); } catch { why = "unmappable v4-in-v6"; return false; }
            }

            if (IPAddress.IsLoopback(ip)) { why = "loopback"; return false; }
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) { why = "unspecified"; return false; }
            if (ip.Equals(IPAddress.Broadcast)) { why = "broadcast"; return false; }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();

                if (b[0] == 0)   { why = "this-network 0.0.0.0/8"; return false; }
                if (b[0] == 10)  { why = "private 10/8"; return false; }
                if (b[0] == 127) { why = "loopback 127/8"; return false; }
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) { why = "private 172.16/12"; return false; }
                if (b[0] == 192 && b[1] == 168)              { why = "private 192.168/16"; return false; }
                if (b[0] == 169 && b[1] == 254)              { why = "link-local 169.254/16 (cloud metadata)"; return false; }
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) { why = "carrier-grade NAT 100.64/10"; return false; }
                if (b[0] == 192 && b[1] == 0 && b[2] == 0)    { why = "IETF protocol assignments 192.0.0/24"; return false; }
                if (b[0] == 192 && b[1] == 0 && b[2] == 2)    { why = "documentation 192.0.2/24"; return false; }
                if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) { why = "benchmarking 198.18/15"; return false; }
                if (b[0] == 198 && b[1] == 51 && b[2] == 100) { why = "documentation 198.51.100/24"; return false; }
                if (b[0] == 203 && b[1] == 0 && b[2] == 113)  { why = "documentation 203.0.113/24"; return false; }
                if (b[0] >= 224) { why = "multicast or reserved (>=224/4)"; return false; }
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal)  { why = "IPv6 link-local fe80::/10"; return false; }
                if (ip.IsIPv6SiteLocal)  { why = "IPv6 site-local fec0::/10"; return false; }
                if (ip.IsIPv6Multicast)  { why = "IPv6 multicast ff00::/8"; return false; }
                if (ip.IsIPv6Teredo || ip.IsIPv6UniqueLocal) { why = ip.IsIPv6Teredo ? "Teredo" : "IPv6 unique-local fc00::/7"; return false; }

                byte[] b = ip.GetAddressBytes();
                bool allZeroButLast = true;
                for (int i = 0; i < 15; i++) if (b[i] != 0) { allZeroButLast = false; break; }
                if (allZeroButLast && (b[15] == 0 || b[15] == 1)) { why = "IPv6 unspecified or loopback"; return false; }

                // 2002::/16 - 6to4, which embeds an IPv4 address that may itself be private.
                if (b[0] == 0x20 && b[1] == 0x02)
                {
                    var embedded = new IPAddress(new[] { b[2], b[3], b[4], b[5] });
                    if (!IsPublicAddress(embedded, out string inner)) { why = "6to4 wrapping " + inner; return false; }
                }
                // 64:ff9b::/96 - NAT64, likewise.
                if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
                {
                    var embedded = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
                    if (!IsPublicAddress(embedded, out string inner)) { why = "NAT64 wrapping " + inner; return false; }
                }
                return true;
            }

            why = "address family " + ip.AddressFamily;
            return false;
        }

        // Checking only the first would let a host returning one public and one private address through.
        internal static bool ResolveAndCheck(string host, out List<IPAddress> addresses, out string why)
        {
            addresses = new List<IPAddress>();
            why = "";
            if (string.IsNullOrWhiteSpace(host)) { why = "no host"; return false; }

            // A literal address in the url skips DNS entirely - and is the most direct way to try to reach 127.0.0.1.
            if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress literal))
            {
                if (!IsPublicAddress(literal, out why)) return false;
                addresses.Add(literal);
                return true;
            }

            IPAddress[] resolved;
            try { resolved = Dns.GetHostAddresses(host); }
            catch (Exception ex) { why = "dns failed: " + ex.GetBaseException().Message; return false; }

            if (resolved == null || resolved.Length == 0) { why = "dns returned nothing"; return false; }

            foreach (IPAddress ip in resolved)
            {
                if (!IsPublicAddress(ip, out string bad)) { why = $"{ip} is {bad}"; return false; }
                addresses.Add(ip);
            }
            return true;
        }
    }
}
