namespace KMH.Sdk.Server.Apis
{
    /// <summary>A protocol envelope hiding Newtonsoft's <c>JObject</c>, so an extension need not pin a Newtonsoft version.</summary>
    public interface IKmhEnvelope
    {
        /// <summary>The wire kind string (e.g. <c>"kmh.treasury.request"</c>).</summary>
        string Kind { get; }

        /// <summary>Protocol version stamped by the sender.</summary>
        int Version { get; }

        /// <summary>Read an int field. Returns <paramref name="defaultValue"/> if absent or non-numeric.</summary>
        int GetInt(string key, int defaultValue = 0);

        /// <summary>Read a string field. Returns <paramref name="defaultValue"/> if absent.</summary>
        string GetString(string key, string defaultValue = null);

        /// <summary>Read a bool field. Returns <paramref name="defaultValue"/> if absent or non-bool.</summary>
        bool GetBool(string key, bool defaultValue = false);

        /// <summary>Deserialises the whole payload as a DTO, which may use <c>[JsonProperty]</c> to map wire names.</summary>
        /// <typeparam name="T">Reference type with a parameterless ctor.</typeparam>
        T DataAs<T>() where T : class;
    }
}
