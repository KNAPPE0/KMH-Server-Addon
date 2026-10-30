using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Mods
{
    // RWT derives roles from whichever client last synced. This only adjusts mods RWT already lists: one it has
    // never heard of cannot be checked against a joining client and would refuse everyone.
    internal sealed class ModPolicyConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // Off by default: a save that uses Royalty or Biotech content needs them, and demoting them breaks the colony.
        public bool ExpansionsOptional { get; set; } = false;

        // By the name RimWorld shows.
        public string[] Optional { get; set; } = Array.Empty<string>();

        // Only mods RWT lists: unknown names are reported, never added, since RWT refuses any client with an unlisted mod.
        public string[] Required { get; set; } = Array.Empty<string>();

        // RWT's server never runs RimWorld, so the owner states major.minor; a client on another version stays off.
        public string RimWorldVersion { get; set; } = DefaultFor(RunningRwtVersion());

        public const string DefaultRimWorldVersion = "1.6";

        // RWT 26.10.20.1 admits only RimWorld 1.7 clients; every RWT before it served 1.6.
        private static readonly int[] FirstRwtFor17 = { 26, 10, 20, 1 };

        internal static string DefaultFor(string rwtVersion) => AdmitsOnly17(rwtVersion) ? "1.7" : DefaultRimWorldVersion;

        // "1.7", "1.7.4976" or "1.7.4976 rev1043" all mean 1.7; anything else is the default, never a guess.
        public static string GameVersionOf(string configured)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(configured ?? "", @"^\s*(\d{1,2})\.(\d{1,3})(?![\d])");
            return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value : DefaultRimWorldVersion;
        }

        // A setting below what RWT admits would refuse every player; a higher one is kept for an RWT newer than this build.
        public static string GameVersionFor(string configured, string rwtVersion)
        {
            string stated = GameVersionOf(configured);
            return AdmitsOnly17(rwtVersion) && BelowOneSeven(stated) ? "1.7" : stated;
        }

        internal static bool AdmitsOnly17(string rwtVersion)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(rwtVersion ?? "", @"^\s*(\d{1,4})\.(\d{1,4})\.(\d{1,4})\.(\d{1,4})");
            if (!m.Success) return false;
            for (int i = 0; i < FirstRwtFor17.Length; i++)
            {
                int part = int.Parse(m.Groups[i + 1].Value);
                if (part != FirstRwtFor17[i]) return part > FirstRwtFor17[i];
            }
            return true;
        }

        private static bool BelowOneSeven(string gameVersion)
        {
            string[] p = gameVersion.Split('.');
            int major = int.Parse(p[0]), minor = int.Parse(p[1]);
            return major < 1 || (major == 1 && minor < 7);
        }

        internal static string RwtVersionForTest;

        // Its own frame, so a host without RWT's assemblies fails here and reads as no version rather than throwing.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RunningRwtVersion()
        {
            if (RwtVersionForTest != null) return RwtVersionForTest;
            try { return RwtCompat.ExecutableVersion; }
            catch { return ""; }
        }

        [Newtonsoft.Json.JsonIgnore]
        public string GameVersion => GameVersionFor(RimWorldVersion, RunningRwtVersion());

        private static ModPolicyConfig _current;
        public static ModPolicyConfig Current => _current ?? (_current = LoadOrDefault());
        public static void Reload() { _current = null; }

        public static ModPolicyConfig LoadOrDefault()
        {
            ModPolicyConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.ModPolicyConfigFile, out ModPolicyConfig loaded)
                                  && loaded != null ? loaded : new ModPolicyConfig();
            cfg.Optional = cfg.Optional ?? Array.Empty<string>();
            cfg.Required = cfg.Required ?? Array.Empty<string>();
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.ModPolicyConfigFile))
                JsonFileStore.Save(KmhDataPaths.ModPolicyConfigFile, new ModPolicyConfig());
        }

        // Required wins a contradiction: demanding the mod is the answer that cannot let a mismatched client in.
        public string RoleFor(ModEntry entry, string current)
        {
            if (entry == null) return current;
            if (Names(Required).Contains(entry.Name)) return ModRole.Required;
            if (Names(Optional).Contains(entry.Name)) return ModRole.Optional;
            if (ExpansionsOptional && entry.Kind == ModKind.Expansion) return ModRole.Optional;
            return current;
        }

        private static HashSet<string> Names(string[] list)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (list != null)
                foreach (string s in list) if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
            return set;
        }

        // Reported, not applied: KMH cannot add a mod to RWT's check, so claiming to would advertise an unenforced rule.
        public List<string> UnmatchedNames(ModProfileSnapshot snap)
        {
            List<string> unknown = new List<string>();
            if (snap == null) return unknown;

            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry e in snap.Entries) if (!string.IsNullOrEmpty(e.Name)) known.Add(e.Name);

            foreach (string s in Names(Required)) if (!known.Contains(s)) unknown.Add(s);
            foreach (string s in Names(Optional)) if (!known.Contains(s)) unknown.Add(s);
            unknown.Sort(StringComparer.Ordinal);
            return unknown;
        }
    }
}
