namespace KMH.Sdk.Server.Apis
{
    /// <summary>A connected-client handle hiding RWT's ServerClient, so its API drift stays out of extension code.</summary>
    /// <remarks>A snapshot from when the event fired: the client may be gone, and a send to it returns false, never throws.</remarks>
    public interface IKmhClient
    {
        /// <summary>In-game username, empty until the handshake completes.</summary>
        string Username { get; }

        /// <summary>The connecting IP address, as a string. Diagnostic-only.</summary>
        string Ip { get; }

        /// <summary>True once RWT's verification is done; check it if you need the username, as raw chat can arrive earlier.</summary>
        bool IsVerified { get; }
    }
}
