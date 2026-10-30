using System;
using System.Collections.Generic;
using System.Text;

namespace KMHServerAddon.Features.Global.Federation
{
    // Mirrors Global's rules for an immediate plain refusal; Global still decides and is never overridden here.
    internal static class FederationRules
    {
        public const int MinName = 3, MaxName = 32, MinTag = 2, MaxTag = 5, MaxDescription = 280;

        public static readonly string[] Colors =
        {
            "#C0392B", "#E67E22", "#F1C40F", "#27AE60", "#16A085", "#2980B9",
            "#8E44AD", "#2C3E50", "#7F8C8D", "#D35400", "#1ABC9C", "#C2185B",
        };

        public static readonly string[] Categories = { "quest", "world", "site", "road", "frontier", "activity" };

        public static bool NeedsPlayer(string category) => category == "quest" || category == "world" || category == "activity";

        public static bool IsCategory(string c) => Array.IndexOf(Categories, c) >= 0;

        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new StringBuilder(name.Length);
            bool space = false;
            foreach (char ch in name.Trim())
            {
                if (char.IsWhiteSpace(ch)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        public static string NormalizeTag(string tag) => (tag ?? "").Trim().ToUpperInvariant();

        public static string NameProblem(string name)
        {
            string n = NormalizeName(name);
            if (n.Length < MinName || n.Length > MaxName) return $"A federation name is {MinName} to {MaxName} characters.";
            foreach (char ch in n)
                if (char.IsControl(ch) || ch == '<' || ch == '>') return "A federation name cannot contain < > or control characters.";
            return null;
        }

        public static string TagProblem(string tag)
        {
            string t = NormalizeTag(tag);
            if (t.Length < MinTag || t.Length > MaxTag) return $"A tag is {MinTag} to {MaxTag} letters or digits.";
            foreach (char ch in t)
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))) return "A tag uses only the letters A-Z and digits.";
            return null;
        }

        public static string DescriptionProblem(string d)
        {
            string s = (d ?? "").Trim();
            if (s.Length > MaxDescription) return $"A description is at most {MaxDescription} characters.";
            foreach (char ch in s)
                if ((char.IsControl(ch) && ch != '\n') || ch == '<' || ch == '>')
                    return "A description cannot contain < > or control characters.";
            return null;
        }

        public static string ColorProblem(string c)
            => string.IsNullOrEmpty(c) || Array.IndexOf(Colors, c.ToUpperInvariant()) >= 0 ? null : "Pick one of the federation colours.";

        public static string ProfileProblem(string name, string tag, string description, string color)
            => NameProblem(name) ?? TagProblem(tag) ?? DescriptionProblem(description) ?? ColorProblem(color);

        // These are ceilings; Federation.json can only lower them.
        public static double SiteOutputPercent(int level) => level >= 9 ? 3 : level >= 6 ? 2 : level >= 3 ? 1 : 0;
        public static double RoadworksPercent(int level)  => level >= 9 ? 3 : level >= 6 ? 2 : 0;

        public static List<(string Key, int Level, double Value)> BenefitTable()
            => new List<(string, int, double)>
            {
                ("tag", 1, 0),
                ("site_output", 3, 1), ("site_output", 6, 2), ("site_output", 9, 3),
                ("extra_objective", 5, 0),
                ("roadworks", 6, 2), ("roadworks", 9, 3),
                ("renowned", 10, 0),
            };
    }
}
