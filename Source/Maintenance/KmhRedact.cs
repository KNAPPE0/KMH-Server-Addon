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

        // Substring match, case-insensitive - a field is sensitive if its NAME says so.
        private static readonly string[] SensitiveKeyParts =
        {
            "token", "secret", "password", "passwd", "apikey", "api_key", "authorization",
            "bearer", "webhook", "credential", "privatekey", "private_key", "clientsecret",
            "client_secret", "connectionstring", "connection_string", "auth",
        };

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

        // Redacts by key name, recursively. Structure is preserved so a reader can still see the shape.
        internal static string Json(string json)
        {
            try
            {
                JToken root = JToken.Parse(json);
                Walk(root);
                return Text(root.ToString(Newtonsoft.Json.Formatting.Indented));
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

        // Free text: known secret values, then anything shaped like a credential after a labelled field.
        internal static string Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string outp = text;

            string[] secrets;
            lock (_knownSecrets) { secrets = new string[_knownSecrets.Count]; _knownSecrets.CopyTo(secrets); }
            foreach (string s in secrets)
                outp = Regex.Replace(outp, Regex.Escape(s), Mask, RegexOptions.IgnoreCase);

            outp = Regex.Replace(outp,
                @"(?i)\b(token|secret|password|api[_-]?key|authorization|bearer|webhook)\b\s*[:=]\s*[""']?([^\s""',}]{8,})",
                m => m.Groups[1].Value + "=" + Mask);

            // "Authorization: Bearer <token>" separates with a space, not with = or :.
            outp = Regex.Replace(outp, @"(?i)\b(bearer|basic)\s+([A-Za-z0-9._~+/=-]{8,})", m => m.Groups[1].Value + " " + Mask);

            // Discord bot tokens are recognisable on their own: three dot-separated base64url runs.
            outp = Regex.Replace(outp, @"\b[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{20,}\b", Mask);

            // A signed url's `hm=` is a read capability, and a support bundle is meant to be handed to a stranger.
            outp = Regex.Replace(outp, @"(?i)([?&]hm=)[A-Za-z0-9%._-]+", m => m.Groups[1].Value + Mask);

            return outp;
        }
    }
}
