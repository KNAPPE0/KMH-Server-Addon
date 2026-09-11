namespace KMH.Sdk.Server
{
    /// <summary>
    /// Version of the extension SDK contract: the shape of the interfaces and records an extension compiles against.
    /// </summary>
    /// <remarks>
    /// Bumped only by a change that would break an extension built against the previous SDK, such as a method added
    /// to an interface extensions implement or a record field renamed. Purely additive changes do not bump it.
    /// </remarks>
    public static class KmhSdkContract
    {
        /// <summary>The SDK contract version this build publishes.</summary>
        public const int Version = 1;
    }
}
