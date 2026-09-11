namespace KMHServerAddon.Extensibility
{
    internal static class KmhExtensionCompat
    {
        public const int Baseline = 1;                                 // assumed target when an extension declares none
        public const int Current  = KMH.Sdk.Server.KmhSdkContract.Version;
        public const int Minimum  = 1;                                 // oldest target this addon still accepts

        public static bool IsCompatible(int target) => target >= Minimum && target <= Current;

        public static string Explain(int target)
            => target > Current ? $"built for a newer KMH SDK (contract {target} > {Current}) - update the server"
             : target < Minimum ? $"built for an unsupported older KMH SDK (contract {target} < {Minimum}) - rebuild against the current SDK"
             : "compatible";
    }
}
