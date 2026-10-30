namespace KMH.Sdk.Server
{
    /// <summary>Optional companion to <see cref="IKmhServerExtension"/> declaring which SDK contract you compiled against.</summary>
    /// <remarks>Without it the baseline contract is assumed; with it the host refuses an incompatible pairing up front
    /// instead of half-loading and failing later inside a handler.</remarks>
    public interface IKmhSdkTargeted
    {
        /// <summary>The <see cref="KmhSdkContract.Version"/> this extension was built against.</summary>
        int TargetSdkContract { get; }
    }
}
