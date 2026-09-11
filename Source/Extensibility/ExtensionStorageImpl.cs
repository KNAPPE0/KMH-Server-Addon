using System;
using System.Collections.Generic;
using System.IO;
using KMH.Sdk.Server.Apis;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Extensibility
{
    // The extension's own Name reaches the filesystem here, so it is sanitised before it can widen the storage scope.
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
            if (value == null) return false;
            string path = ResolveInScope(filename);
            return path != null && JsonFileStore.Save(path, value);
        }

        // Tidiness rather than a security boundary, since an extension runs in-process and could write anywhere.
        private string ResolveInScope(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            try
            {
                string root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(RootPath, filename));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch { return null; }
        }

        public bool TryLoad<T>(string filename, out T value) where T : class
        {
            value = null;
            string path = ResolveInScope(filename);
            return path != null && JsonFileStore.TryLoad(path, out value);
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
                string path = ResolveInScope(filename);
                if (path == null) return false;
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

            // Dots are legal inside a name, but an all-dots name would resolve the root a level up into kmh-data itself.
            result = result.Trim('.');
            return result.Length == 0 ? "unnamed" : result;
        }
    }
}
