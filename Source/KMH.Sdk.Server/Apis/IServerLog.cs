using System;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// Pipes extension messages through KMH's standard server logger.
    /// Output is automatically prefixed with the extension name so
    /// admins can tell which extension is talking.
    /// </summary>
    public interface IServerLog
    {
        void Info(string message);
        void Verbose(string message);
        void Warn(string message);
        void Error(string message);
        void Error(string message, Exception ex);
    }
}
