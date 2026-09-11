namespace KMH.Sdk.Server
{
    /// <summary>
    /// Optional companion to <see cref="IKmhServerExtension"/> declaring which SDK contract you compiled against.
    /// </summary>
    /// <remarks>
    /// An extension that does not implement this is assumed to target the baseline contract, so existing extensions
    /// keep loading. Declaring it lets the host refuse an incompatible pairing with a clear message instead of
    /// half-loading and failing later inside a handler.
    /// </remarks>
    public interface IKmhSdkTargeted
    {
        /// <summary>The <see cref="KmhSdkContract.Version"/> this extension was built against.</summary>
        int TargetSdkContract { get; }
    }
}
