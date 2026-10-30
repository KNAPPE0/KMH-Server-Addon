using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KMHServerAddon.Features.Moderation
{
    // Names and labels only Colony Nexus itself may wear: one carrying them would pass for Global or its staff.
    internal static class KmhBrandWords
    {
        // "C0l0ny-N3xus", full-width, Cyrillic and I/l/1/| read plainly; loose also folds a dotted i, too wide for names ("Kai" is not "Kal").
        internal static string Skeleton(string s, bool loose = true)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            char last = '\0';
            foreach (char raw in s.Normalize(NormalizationForm.FormKD))
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(raw);
                if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.Format) continue;
                char c = raw == 'I' ? 'l' : Plain(char.ToLowerInvariant(raw), loose);
                if (!char.IsLetterOrDigit(c) || c == last) continue;
                sb.Append(c);
                last = c;
            }
            return sb.ToString();
        }

        private static char Plain(char c, bool loose) => c switch
        {
            '0' => 'o', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '8' => 'b',
            '1' or '|' or 'ӏ' => 'l',
            'i' or 'і' or 'ι' => loose ? 'l' : 'i',
            'а' => 'a', 'е' => 'e', 'о' => 'o', 'р' => 'p', 'с' => 'c', 'у' => 'y',
            'х' => 'x', 'ј' => 'j', 'ѕ' => 's', 'ο' => 'o', 'α' => 'a',
            'ε' => 'e', 'κ' => 'k', 'τ' => 't',
            _ => c,
        };

        private static readonly string[] Authority = Stems(
            "colony nexus", "nexus", "global", "official", "verified", "kmh", "system", "worldweave", "world splice",
            "knappe0", "cn staff", "cn dev", "cn team");

        // The product itself, or its staff, inside any account or guild name.
        private static readonly string[] Product = Stems("colony nexus", "world splice", "worldweave",
            "kmh staff", "cn staff", "nexus staff", "global staff", "kmh team", "cn team", "kmh dev", "cn dev",
            "global admin", "global mod");

        // Whole names that read as the platform speaking.
        private static readonly HashSet<string> PlatformNames = new HashSet<string>(Stems("global", "system", "console"), StringComparer.Ordinal);

        private static string[] Stems(params string[] words) => Array.ConvertAll(words, w => Skeleton(w));

        // A staff badge names this server's roles, never Colony Nexus's.
        internal static bool ClaimsAuthority(string text) => ContainsAny(Skeleton(text), Authority);

        // An account or guild that would read as the product or the platform itself.
        internal static bool PosesAsPlatform(string name)
        {
            string k = Skeleton(name);
            return k.Length > 0 && (PlatformNames.Contains(k) || ContainsAny(k, Product));
        }

        // Spelled like a staff member (case, digits for letters, look-alike letters) without being that account.
        internal static bool LooksLike(string name, string staffName)
        {
            string a = Skeleton(name, loose: false), b = Skeleton(staffName, loose: false);
            return a.Length > 0 && a == b;
        }

        private static bool ContainsAny(string k, string[] words)
        {
            if (k.Length == 0) return false;
            foreach (string w in words) if (w.Length > 0 && k.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
