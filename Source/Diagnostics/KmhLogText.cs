using System;
using System.Text;

namespace KMHServerAddon.Diagnostics
{
    // Names, item labels, guild names and chat all reach diagnostics, and all of them are player-controlled.
    internal static class KmhLogText
    {
        // A newline or ANSI escape in a username would otherwise forge log lines or drive the reader's terminal.
        public static string OneLine(string raw, int maxChars)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(Math.Min(raw.Length, maxChars) + 1);
            foreach (char c in raw)
            {
                if (sb.Length >= maxChars) break;
                if (c == '\r' || c == '\n' || c == '\t') { sb.Append(' '); continue; }
                if (char.IsControl(c)) { sb.Append('�'); continue; }
                sb.Append(c);
            }
            // A pair split by the cap would leave a lone surrogate, which is not valid UTF-8 to write out.
            if (sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;
            if (sb.Length < raw.Length) sb.Append('…');
            return sb.ToString();
        }

        // For limits that are genuinely in bytes. Cuts on a character boundary, so the result is always valid UTF-8.
        public static string TrimToUtf8Bytes(string s, int maxBytes)
        {
            if (string.IsNullOrEmpty(s) || maxBytes <= 0) return "";
            if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;

            // Keep the search on plain indices - nudging mid to a codepoint boundary can stop it advancing and spin forever.
            char[] chars = s.ToCharArray();
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Encoding.UTF8.GetByteCount(chars, 0, mid) <= maxBytes) lo = mid; else hi = mid - 1;
            }
            if (lo > 0 && char.IsHighSurrogate(s[lo - 1])) lo--;
            return s.Substring(0, lo);
        }

        public static int Utf8Bytes(string s) => string.IsNullOrEmpty(s) ? 0 : Encoding.UTF8.GetByteCount(s);
    }
}
