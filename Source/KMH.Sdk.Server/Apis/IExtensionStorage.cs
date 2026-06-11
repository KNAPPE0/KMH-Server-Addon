namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// Per-extension persistence scope rooted at
    /// <c>kmh-data/extensions/&lt;extension-name&gt;/</c>. Files saved
    /// here are atomic (write-temp-then-rename, like KMH's own stores)
    /// and survive server restarts.
    /// </summary>
    /// <remarks>
    /// The extension folder is created on first use. Multiple
    /// extensions get isolated folders so an uninstall is a single
    /// "delete this folder" operation and backups can target specific
    /// extensions if needed.
    /// </remarks>
    public interface IExtensionStorage
    {
        /// <summary>Absolute path to this extension's folder.</summary>
        string RootPath { get; }

        /// <summary>
        /// Serialise + save as JSON at <c>{RootPath}/{filename}</c>.
        /// Returns false on disk failure (logged via KMH's logger).
        /// </summary>
        bool Save<T>(string filename, T value) where T : class;

        /// <summary>
        /// Try to load + deserialise. Returns false for missing file
        /// or malformed JSON. Out-value is null on miss.
        /// </summary>
        bool TryLoad<T>(string filename, out T value) where T : class;

        /// <summary>List the json files in the extension's folder. Empty when the folder doesn't exist yet.</summary>
        System.Collections.Generic.IReadOnlyList<string> ListFiles();

        /// <summary>Delete a file in the extension's folder. Returns false on disk failure.</summary>
        bool Delete(string filename);
    }
}
