using System;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>KMH's server logger, prefixing every line with the extension name so admins can tell who is talking.</summary>
    public interface IServerLog
    {
        void Info(string message);
        void Verbose(string message);
        void Warn(string message);
        void Error(string message);
        void Error(string message, Exception ex);
    }
}
