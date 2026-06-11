namespace KMH.Sdk.Server
{
    /// <summary>
    /// Implement this interface in your extension assembly and KMH will
    /// discover + instantiate it at server startup.
    /// </summary>
    /// <remarks>
    /// Discovery rules:
    ///   - DLL must be placed in the <c>kmh-extensions/</c> folder
    ///     alongside <c>KMHServerAddon.exe</c>.
    ///   - DLL must contain at least one concrete public class
    ///     implementing this interface with a parameterless constructor.
    ///   - Multiple extensions per DLL are allowed; each is
    ///     instantiated + registered independently.
    ///
    /// Lifecycle:
    ///   1. <see cref="Register"/> called once after KMH's own handlers
    ///      are wired but before RWT's <c>Main</c> starts. Use the
    ///      <see cref="IKmhServerHost"/> to subscribe to events,
    ///      register custom wire kinds, allocate per-extension storage.
    ///   2. RWT server runs normally. KMH fires events through
    ///      <see cref="IKmhServerHost.Events"/> as marketplace / quest
    ///      / guild / treasury activity happens.
    ///   3. <see cref="Shutdown"/> called when the host process exits
    ///      gracefully. Best-effort - process kills / hard crashes
    ///      don't call it. Persist any state via
    ///      <see cref="IKmhServerHost.StorageFor"/> before this point if
    ///      you care about durability.
    ///
    /// Failure isolation:
    ///   - Throwing from your ctor or from <see cref="Register"/> is
    ///     caught by the loader and logged; KMH continues bootstrap
    ///     without your extension active.
    ///   - Throwing from a subscribed event handler is logged but does
    ///     not abort the event dispatch chain - other subscribers
    ///     still fire.
    /// </remarks>
    public interface IKmhServerExtension
    {
        /// <summary>
        /// Display name shown in logs, <c>/kmh server status</c>
        /// output, and admin-facing diagnostics. Should be short and
        /// human-readable (e.g. "Auction House", "Bank Loans").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// SemVer string of the extension. Used in log lines and any
        /// future "extensions on this server" Discord command.
        /// </summary>
        string Version { get; }

        /// <summary>
        /// Called once at server startup, after KMH's own handlers are
        /// registered but before RWT's <c>Main</c> runs. Wire your
        /// event subscriptions, register custom wire kinds, allocate
        /// storage, install Harmony patches (using your own Harmony
        /// instance with an id distinct from KMH's).
        /// </summary>
        /// <param name="host">
        /// Stable API surface - everything you can safely use lives
        /// here.
        /// </param>
        void Register(IKmhServerHost host);

        /// <summary>
        /// Called on graceful host shutdown. Best-effort. Use for
        /// flushing buffers, closing async clients, etc. KMH's own
        /// persisted stores save themselves on every mutation, so you
        /// don't need to nudge them here.
        /// </summary>
        void Shutdown();
    }
}
