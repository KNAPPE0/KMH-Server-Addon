using System;
using System.Collections.Generic;
using System.IO;
using KMH.Sdk.Server.Apis;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Extensibility
{
    // Per-extension storage rooted at kmh-data/extensions/<name>/. Sanitises the extension name so a malformed Name
    // property can't escape the storage scope (no slashes, no ..)
    internal sealed class ExtensionStorageImpl : IExtensionStorage
    {
        public ExtensionStorageImpl(string extensionName)
        {
            string safe = Sanitize(extensionName);
            RootPath    = Path.Combine(KmhDataPaths.Folder, "extensions", safe);
            try
            {
                if (!Directory.Exists(RootPath)) Directory.CreateDirectory(RootPath);
            }
            catch { /* lazy create on first Save attempt */ }
        }

        public string RootPath { get; }

        public bool Save<T>(string filename, T value) where T : class
        {
            if (string.IsNullOrEmpty(filename) || value == null) return false;
            return JsonFileStore.Save(Path.Combine(RootPath, filename), value);
        }

        public bool TryLoad<T>(string filename, out T value) where T : class
        {
            value = null;
            if (string.IsNullOrEmpty(filename)) return false;
            return JsonFileStore.TryLoad(Path.Combine(RootPath, filename), out value);
        }

        public IReadOnlyList<string> ListFiles()
        {
            try
            {
                if (!Directory.Exists(RootPath)) return Array.Empty<string>();
                string[] entries = Directory.GetFiles(RootPath, "*.json");
                List<string> names = new List<string>(entries.Length);
                foreach (string e in entries) names.Add(Path.GetFileName(e));
                return names;
            }
            catch { return Array.Empty<string>(); }
        }

        public bool Delete(string filename)
        {
            try
            {
                string path = Path.Combine(RootPath, filename);
                if (!File.Exists(path)) return true;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unnamed";
            char[] buf = new char[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                buf[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_';
            }
            string result = new string(buf);

            // '.' is allowed inside a name (e.g. "com.author.ext") but a name that is ONLY dots ("." / ".." /
            // "...") would resolve the storage root to kmh-data/ itself - one level above extensions/ - and let a
            // malformed extension overwrite core files like treasury.json. Strip leading/trailing dots; if
            // nothing's left, fall back
            result = result.Trim('.');
            return result.Length == 0 ? "unnamed" : result;
        }
    }
}
