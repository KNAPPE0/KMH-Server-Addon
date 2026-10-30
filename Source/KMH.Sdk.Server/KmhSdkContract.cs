namespace KMH.Sdk.Server
{
    /// <summary>Version of the extension SDK contract: the interface and record shapes an extension compiles against.</summary>
    /// <remarks>Bumped only by a change that breaks extensions built on the previous SDK; additive changes never bump it.</remarks>
    public static class KmhSdkContract
    {
        /// <summary>The SDK contract version this build publishes.</summary>
        public const int Version = 1;
    }
}
