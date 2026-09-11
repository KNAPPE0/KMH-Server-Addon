namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// Per-extension persistence rooted at <c>kmh-data/extensions/&lt;extension-name&gt;/</c>, created on first use.
    /// Writes are atomic, so a save interrupted mid-write leaves the previous file intact.
    /// </summary>
    public interface IExtensionStorage
    {
        /// <summary>Absolute path to this extension's folder.</summary>
        string RootPath { get; }

        /// <summary>Save as JSON at <c>{RootPath}/{filename}</c>. Returns false on a disk failure, which is logged.</summary>
        bool Save<T>(string filename, T value) where T : class;

        /// <summary>Load and deserialise. Returns false with a null out-value for a missing file or malformed JSON.</summary>
        bool TryLoad<T>(string filename, out T value) where T : class;

        /// <summary>List the json files in the extension's folder. Empty when the folder doesn't exist yet.</summary>
        System.Collections.Generic.IReadOnlyList<string> ListFiles();

        /// <summary>Delete a file in the extension's folder. Returns false on disk failure.</summary>
        bool Delete(string filename);
    }
}
