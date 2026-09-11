using System.Collections.Generic;

namespace KMHServerAddon.Extensibility
{
    internal static class KmhExtensionCompatSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            r.Add(("Compat: baseline target loads",
                   KmhExtensionCompat.IsCompatible(KmhExtensionCompat.Baseline), $"baseline {KmhExtensionCompat.Baseline}"));
            r.Add(("Compat: current target loads",
                   KmhExtensionCompat.IsCompatible(KmhExtensionCompat.Current), $"current {KmhExtensionCompat.Current}"));
            r.Add(("Compat: newer-than-server refused",
                   !KmhExtensionCompat.IsCompatible(KmhExtensionCompat.Current + 1), "target Current+1"));
            r.Add(("Compat: older-than-minimum refused",
                   !KmhExtensionCompat.IsCompatible(KmhExtensionCompat.Minimum - 1), "target Minimum-1"));

            bool explainAgrees = KmhExtensionCompat.Explain(KmhExtensionCompat.Current) == "compatible"
                              && KmhExtensionCompat.Explain(KmhExtensionCompat.Current + 1) != "compatible"
                              && KmhExtensionCompat.Explain(KmhExtensionCompat.Minimum - 1) != "compatible";
            r.Add(("Compat: Explain matches the gate", explainAgrees, ""));

            r.Add(("Compat: addon current == SDK version",
                   KmhExtensionCompat.Current == KMH.Sdk.Server.KmhSdkContract.Version,
                   $"addon {KmhExtensionCompat.Current} vs sdk {KMH.Sdk.Server.KmhSdkContract.Version}"));

            return r;
        }
    }
}
