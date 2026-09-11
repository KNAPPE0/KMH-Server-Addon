namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// A handle for a connected client, hiding RWT's ServerClient so its API drift stays out of extension code.
    /// </summary>
    /// <remarks>
    /// The handle is a snapshot from when the event fired, so the client may already be gone; a send to a stale
    /// handle returns false rather than throwing.
    /// </remarks>
    public interface IKmhClient
    {
        /// <summary>In-game username, empty until the handshake completes.</summary>
        string Username { get; }

        /// <summary>The connecting IP address, as a string. Diagnostic-only.</summary>
        string Ip { get; }

        /// <summary>
        /// True once RWT's verification handshake is done. Events see verified clients almost always, but a raw chat
        /// patch can see a pre-verified one, so check this if your handler needs the username.
        /// </summary>
        bool IsVerified { get; }
    }
}
