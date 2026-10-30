using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // The guard must name the assembly the runtime asked for; blaming another sends owners checking a file that exists.
    internal static class KmhDependencyPreflightSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            // The real shape: the net45 Newtonsoft RWT ships asks for System.Security.Permissions on first use.
            Exception transitive = new FileNotFoundException(
                "Could not load file or assembly 'System.Security.Permissions, Version=0.0.0.0, Culture=neutral, "
                + "PublicKeyToken=cc7b13ffcd2ddd51'. The system cannot find the file specified.",
                "System.Security.Permissions, Version=0.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");

            string named = DependencyPreflight.MissingAssembly(transitive);
            r.Add(("dependency: the assembly the runtime asked for is named, not the one KMH called into",
                   named == "System.Security.Permissions", named.Length == 0 ? "named nothing" : named));

            string described = DependencyPreflight.Describe(transitive);
            r.Add(("dependency: a transitive miss points at the RWT download, which is what ships it",
                   described.Contains("System.Security.Permissions") && described.Contains("RimWorld Together"),
                   described));
            r.Add(("dependency: a transitive miss does not tell the owner to go re-extract KMH",
                   !described.Contains("Newtonsoft"), described));

            // Only when Newtonsoft itself is the miss is re-extracting KMH the right advice.
            Exception direct = new FileNotFoundException(
                "Could not load file or assembly 'Newtonsoft.Json, Version=13.0.0.0'.", "Newtonsoft.Json.dll");
            string directText = DependencyPreflight.Describe(direct);
            r.Add(("dependency: a missing Newtonsoft still points at the KMH package",
                   DependencyPreflight.MissingAssembly(direct) == "Newtonsoft.Json"
                   && directText.Contains("Colony Nexus package"), directText));

            // Nested is the common case: the throw arrives wrapped in a TypeInitializationException.
            Exception nested = new TypeInitializationException("Some.Type", transitive);
            r.Add(("dependency: an assembly named only in an inner exception is still found",
                   DependencyPreflight.MissingAssembly(nested) == "System.Security.Permissions",
                   DependencyPreflight.MissingAssembly(nested)));

            // No assembly in the text at all must not produce a confident lie about which file to copy.
            Exception vague = new InvalidOperationException("serializer exploded");
            string vagueText = DependencyPreflight.Describe(vague);
            r.Add(("dependency: an unattributable failure names no file rather than guessing one",
                   DependencyPreflight.MissingAssembly(vague).Length == 0 && !vagueText.Contains(".dll"), vagueText));

            // The live check has to agree with itself: this build can serialize, or the suite could not have run.
            bool live = DependencyPreflight.Check(out string liveDetail);
            r.Add(("dependency: the boot probe round-trips JSON on this build", live, liveDetail));

            return r;
        }
    }
}
