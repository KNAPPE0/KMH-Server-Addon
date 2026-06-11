namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// SDK-safe handle for a connected client. Hides RWT's ServerClient
    /// type behind a stable interface so RWT-side API drift doesn't
    /// ripple into extension code.
    /// </summary>
    /// <remarks>
    /// The handle is a snapshot at the moment the event fired. If the
    /// client disconnects mid-handler the underlying ServerClient may
    /// already be gone from <c>Network.ServerClients</c>; sends to a
    /// stale handle return false instead of throwing.
    /// </remarks>
    public interface IKmhClient
    {
        /// <summary>
        /// In-game username from the player's RWT user file. May be
        /// empty before the handshake completes; extensions should
        /// guard against null/empty for early-lifecycle events.
        /// </summary>
        string Username { get; }

        /// <summary>The connecting IP address, as a string. Diagnostic-only.</summary>
        string Ip { get; }

        /// <summary>
        /// True once the client has completed RWT's verification
        /// handshake. KMH event subscriptions almost always see
        /// verified clients only, but raw chat patches can see
        /// pre-verified ones - check this if your handler needs the
        /// username.
        /// </summary>
        bool IsVerified { get; }
    }
}
