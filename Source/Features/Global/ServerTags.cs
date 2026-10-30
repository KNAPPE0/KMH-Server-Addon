using System;
using System.Collections.Generic;
using System.Text;

namespace KMHServerAddon.Features.Global
{
    // Applied when publishing, never written back to Global.json: a config that edits itself loses the owner's trust.
    internal static class ServerTags
    {
        public const int MaxTags      = 12;
        public const int MaxTagLength = 24;

        // Each is already a listing field, so a tag copy goes stale; "notset" is a leaked enum default.
        private static readonly string[] DerivedPrefixes = { "rwt-", "kmh-", "rimworld-", "slot-" };
        private static readonly HashSet<string> Derived = new HashSet<string>(StringComparer.Ordinal)
        {
            "notset", "unknown", "none",
        };

        // Shown to owners so servers meaning the same thing pick one spelling; free text still works.
        public static readonly string[] Suggested =
        {
            "vanilla", "modded", "pve", "pvp", "roleplay", "hardcore", "casual",
            "public", "whitelist", "18plus", "newbie-friendly", "long-term",
        };

        public static string[] Normalize(string[] raw)
        {
            List<string> outList = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (raw == null) return new string[0];

            foreach (string entry in raw)
            {
                string tag = Clean(entry);
                if (tag.Length == 0 || IsDerived(tag)) continue;
                if (!seen.Add(tag)) continue;
                outList.Add(tag);
                if (outList.Count >= MaxTags) break;
            }
            return outList.ToArray();
        }

        internal static bool IsDerived(string cleaned)
        {
            if (Derived.Contains(cleaned)) return true;
            foreach (string p in DerivedPrefixes)
                if (cleaned.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        // Filter chips match exactly, and markup or commas in a tag would break whatever renders it.
        internal static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            StringBuilder sb = new StringBuilder(raw.Length);
            bool lastHyphen = false;
            foreach (char c in raw.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); lastHyphen = false; continue; }
                if (sb.Length == 0 || lastHyphen) continue;
                sb.Append('-');
                lastHyphen = true;
            }
            string s = sb.ToString().TrimEnd('-');
            return s.Length > MaxTagLength ? s.Substring(0, MaxTagLength).TrimEnd('-') : s;
        }
    }
}
