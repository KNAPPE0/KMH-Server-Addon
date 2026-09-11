using System;

namespace KMHServerAddon.Features.Identity
{
    // Presentation only: a role grants no permission, because a badge players can see is one someone will try to forge.
    internal static class StaffRoles
    {
        public const string None      = "";
        public const string Op        = "op";
        public const string Moderator = "moderator";
        public const string Admin     = "admin";
        public const string Developer = "developer";
        public const string Owner     = "owner";

        // Spaced so an extension can slot a role between two of these without renumbering the set.
        public static int Priority(string role)
        {
            switch (Normalize(role))
            {
                case Owner:     return 50;
                case Developer: return 40;
                case Admin:     return 30;
                case Moderator: return 20;
                case Op:        return 10;
                default:        return 0;
            }
        }

        // Everything reads roles through here, or a hand-edited config could produce a second invisible "Admin".
        public static string Normalize(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return None;
            switch (role.Trim().ToLowerInvariant())
            {
                case "owner":                return Owner;
                case "developer": case "dev": return Developer;
                case "admin":                return Admin;
                case "moderator": case "mod": return Moderator;
                case "op":                   return Op;
                default:                     return None;
            }
        }

        public static bool IsKnown(string role) => Normalize(role) != None;

        // The higher of two roles. Used when a player is both (say) a listed Developer and a live RWT admin.
        public static string Higher(string a, string b)
            => Priority(a) >= Priority(b) ? Normalize(a) : Normalize(b);

        public static string DefaultLabel(string role)
        {
            switch (Normalize(role))
            {
                case Owner:     return "Owner";
                case Developer: return "Dev";
                case Admin:     return "Admin";
                case Moderator: return "Mod";
                case Op:        return "OP";
                default:        return "";
            }
        }

        // Separable by lightness as well as hue, and the label always shows, so colour never carries the role alone.
        public static string DefaultColor(string role)
        {
            switch (Normalize(role))
            {
                case Owner:     return "E2C16B";
                case Developer: return "7FD1FF";
                case Admin:     return "FF8A6B";
                case Moderator: return "8FD98F";
                case Op:        return "C0C6CF";
                default:        return "";
            }
        }

        public static readonly string[] All = { Owner, Developer, Admin, Moderator, Op };
    }
}
