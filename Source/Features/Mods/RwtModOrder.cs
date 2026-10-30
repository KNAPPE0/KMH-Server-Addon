using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Mods
{
    // DataString is RimWorld's ModsConfig.xml verbatim and Hash its SHA-256 as uppercase hex (RWT 26.8.x); null on
    // older RWT or before a world publishes one, which means no order to enforce, never an empty order.
    internal static class RwtModOrder
    {
        // A load order is a list of package ids, not a document: anything beyond this is not an order.
        internal const int MaxEntries = 512;
        internal const int MaxIdLength = 190;
        internal const int MaxDataStringBytes = 256 * 1024;

        internal sealed class Parsed
        {
            public List<string> Order = new List<string>();
            public string Hash = "";
            public bool Trusted;        // the stated hash matched the bytes it claims to cover
            public string Failure = "";
        }

        // Fails safe: any answer other than a verified parse yields an empty order, which enforces nothing.
        public static Parsed Read(string dataString, string statedHash)
        {
            Parsed p = new Parsed();
            if (string.IsNullOrEmpty(dataString)) { p.Failure = "no order published"; return p; }

            byte[] bytes = Encoding.UTF8.GetBytes(dataString);
            if (bytes.Length > MaxDataStringBytes)
            {
                p.Failure = $"order document is {bytes.Length} bytes, over the {MaxDataStringBytes} cap";
                return p;
            }

            string actual = Sha256Upper(bytes);
            // An order whose hash does not cover it is not this server's order, whoever wrote it.
            if (!string.IsNullOrEmpty(statedHash)
                && !string.Equals(actual, statedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                p.Failure = "the published order hash does not match the order it describes";
                return p;
            }

            List<string> ids = ActiveMods(dataString, out string parseError);
            if (parseError != null) { p.Failure = parseError; return p; }

            p.Order = ids;
            p.Hash = actual;
            p.Trusted = true;
            return p;
        }

        // The reader resolves nothing external, since this string comes from a config file an owner edits by hand.
        private static List<string> ActiveMods(string xml, out string error)
        {
            error = null;
            List<string> ids = new List<string>();
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                };
                using (XmlReader reader = XmlReader.Create(new System.IO.StringReader(xml), settings))
                {
                    bool inActive = false;
                    // ReadElementContentAsString already moves to the next node; advancing again skips every other <li>.
                    bool advance = true;
                    while (true)
                    {
                        if (advance && !reader.Read()) break;
                        advance = true;

                        if (reader.NodeType == XmlNodeType.Element && reader.Name == "activeMods") inActive = true;
                        else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "activeMods") break;
                        else if (inActive && reader.NodeType == XmlNodeType.Element && reader.Name == "li")
                        {
                            string id = (reader.ReadElementContentAsString() ?? "").Trim();
                            advance = false;
                            if (id.Length == 0 || id.Length > MaxIdLength) continue;
                            // A repeated id is one position, not two: RimWorld loads it once.
                            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
                            if (ids.Count > MaxEntries) { error = $"order lists more than {MaxEntries} mods"; return new List<string>(); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"order document could not be read ({ex.GetType().Name})";
                return new List<string>();
            }
            return ids;
        }

        internal static string Sha256Upper(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(bytes);
                StringBuilder hex = new StringBuilder(h.Length * 2);
                foreach (byte b in h) hex.Append(b.ToString("X2"));
                return hex.ToString();
            }
        }

        public static void WarnIfUntrusted(Parsed p)
        {
            if (p == null || p.Trusted || string.IsNullOrEmpty(p.Failure)) return;
            ServerLog.Warn($"Mod order: {p.Failure} - Colony Nexus will publish no load order for this server, so clients "
                         + "are not asked to reorder anything.");
        }
    }
}
