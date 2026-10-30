using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    // Two passes: key-name matching misses a pasted token, value matching misses a secret this build never loaded.
    internal static class KmhRedact
    {
        internal const string Mask = "***REDACTED***";

        private static readonly string[] SensitiveKeyParts =
        {
            "token", "secret", "password", "passwd", "apikey", "api_key", "authorization",
            "bearer", "webhook", "credential", "privatekey", "private_key", "clientsecret",
            "client_secret", "connectionstring", "connection_string", "auth",
        };

        private static readonly string LabelledSecret =
            @"([A-Za-z0-9_-]*(?:" + string.Join("|", Array.ConvertAll(SensitiveKeyParts, Regex.Escape))
            + @")[A-Za-z0-9_-]*)""?\s*[:=]\s*[""']?([^\s""',}]{8,})";

        private static readonly HashSet<string> _knownSecrets = new HashSet<string>(StringComparer.Ordinal);

        // Values this build has actually loaded, so they can be scrubbed from free text that never had a key name.
        internal static void RegisterSecret(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 8) return;
            lock (_knownSecrets) _knownSecrets.Add(value.Trim());
        }

        internal static void ClearSecrets() { lock (_knownSecrets) _knownSecrets.Clear(); }

        internal static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            string k = key.ToLowerInvariant();
            foreach (string part in SensitiveKeyParts) if (k.Contains(part)) return true;
            return false;
        }

        internal static string Json(string json) => Json(json, Newtonsoft.Json.Formatting.Indented);

        internal static string Json(string json, Newtonsoft.Json.Formatting formatting)
        {
            try
            {
                JToken root = JToken.Parse(json);
                Walk(root);
                return Text(root.ToString(formatting));
            }
            catch { return Text(json); }
        }

        private static void Walk(JToken node)
        {
            if (node is JObject obj)
            {
                foreach (JProperty p in obj.Properties())
                {
                    if (IsSensitiveKey(p.Name) && p.Value.Type == JTokenType.String)
                    {
                        string v = p.Value.Value<string>() ?? "";
                        if (v.Length > 0) { RegisterSecret(v); p.Value = Mask; }
                    }
                    else Walk(p.Value);
                }
            }
            else if (node is JArray arr) foreach (JToken child in arr) Walk(child);
        }

        internal static string Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string outp = text;

            string[] secrets;
            lock (_knownSecrets) { secrets = new string[_knownSecrets.Count]; _knownSecrets.CopyTo(secrets); }
            foreach (string s in secrets)
                outp = Regex.Replace(outp, Regex.Escape(s), Mask, RegexOptions.IgnoreCase);

            // Derived from the JSON walker's list so the two cannot drift; names may carry affixes ("client_secret")
            // and values may be quoted, because a JSON fragment logged as text is still text.
            outp = Regex.Replace(outp, LabelledSecret,
                m => m.Groups[1].Value + "=" + Mask, RegexOptions.IgnoreCase);

            // "Authorization: Bearer <token>" separates with a space, not with = or :.
            outp = Regex.Replace(outp, @"(?i)\b(bearer|basic)\s+([A-Za-z0-9._~+/=-]{8,})", m => m.Groups[1].Value + " " + Mask);

            // Discord bot tokens are recognisable on their own: three dot-separated base64url runs.
            outp = Regex.Replace(outp, @"\b[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{20,}\b", Mask);

            // A signed url's `hm=` is a read capability, and a support bundle is meant to be handed to a stranger.
            outp = Regex.Replace(outp, @"(?i)([?&]hm=)[A-Za-z0-9%._-]+", m => m.Groups[1].Value + Mask);

            return outp;
        }

        internal const string AddressMask = "[address]";

        private static readonly Regex Ipv4 = new Regex(
            @"(?<![\w.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?!\w)(?!\.\d)", RegexOptions.CultureInvariant);

        private static readonly Regex Ipv6Candidate = new Regex(
            @"(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}(?:(?:\d{1,3}\.){3}\d{1,3}|[0-9A-Fa-f]{0,4})(?![\w:])", RegexOptions.CultureInvariant);

        // Global's disclosure promises no player IPs; an address-shaped version is masked too, but timestamps,
        // "v1.3.1.0" and MAC-like strings are not.
        internal static string Addresses(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string outp = Ipv4.Replace(text, AddressMask);
            return Ipv6Candidate.Replace(outp, m =>
            {
                string v = m.Value;
                int colons = 0;
                foreach (char c in v) if (c == ':') colons++;
                bool shaped = v.Contains("::") || colons == 7;
                return shaped && System.Net.IPAddress.TryParse(v, out System.Net.IPAddress a)
                       && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? AddressMask : v;
            });
        }
    }
}
