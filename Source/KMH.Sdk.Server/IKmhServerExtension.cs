namespace KMH.Sdk.Server
{
    /// <summary>
    /// Implement this and KMH discovers and instantiates it at server startup.
    /// </summary>
    /// <remarks>
    /// Discovery: a DLL in <c>kmh-extensions/</c> beside <c>KMHServerAddon.exe</c>, holding at least one
    /// public class implementing this interface with a parameterless constructor. Several per DLL are
    /// registered independently.
    /// <para>
    /// Failure isolation: a throw from your constructor or <see cref="Register"/> is logged and your
    /// extension is skipped, and a throw from an event handler does not stop the other subscribers.
    /// </para>
    /// </remarks>
    public interface IKmhServerExtension
    {
        /// <summary>Short display name shown in logs and admin diagnostics, e.g. "Auction House".</summary>
        string Name { get; }

        /// <summary>SemVer string of the extension.</summary>
        string Version { get; }

        /// <summary>
        /// Called once after KMH's own handlers are registered but before RWT's <c>Main</c> runs. Any
        /// Harmony patches must use your own instance with an id distinct from KMH's.
        /// </summary>
        /// <param name="host">The stable API surface; everything you can safely use lives here.</param>
        void Register(IKmhServerHost host);

        /// <summary>
        /// Called on graceful shutdown only, so a process kill skips it. KMH's own stores save on every
        /// mutation and need nothing here.
        /// </summary>
        void Shutdown();
    }
}
