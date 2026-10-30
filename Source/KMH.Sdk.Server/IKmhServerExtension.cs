namespace KMH.Sdk.Server
{
    /// <summary>Implement this in a DLL under <c>kmh-extensions/</c> beside <c>KMHServerAddon.exe</c>; KMH loads it at startup.</summary>
    /// <remarks>Every public implementing class with a parameterless constructor registers independently. A throw from its
    /// constructor or <see cref="Register"/> skips that extension; a throwing event handler stops no other subscriber.</remarks>
    public interface IKmhServerExtension
    {
        /// <summary>Short display name shown in logs and admin diagnostics, e.g. "Auction House".</summary>
        string Name { get; }

        /// <summary>SemVer string of the extension.</summary>
        string Version { get; }

        /// <summary>Called once after KMH's handlers register, before RWT's <c>Main</c>. Use your own Harmony id.</summary>
        /// <param name="host">The stable API surface; everything you can safely use lives here.</param>
        void Register(IKmhServerHost host);

        /// <summary>Called on graceful shutdown only (a process kill skips it); KMH's own stores save on every mutation.</summary>
        void Shutdown();
    }
}
