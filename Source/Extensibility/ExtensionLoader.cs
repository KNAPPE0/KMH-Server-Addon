using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KMH.Sdk.Server;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Extensibility
{
    // Discovers + loads server-side extensions at startup. Recursively scans kmh-extensions/ (next to the exe) for any
    // *.dll, whether one folder per extension or flat. Every load step is wrapped in try/catch: a broken extension
    // logs + skips, KMH itself continues.
    internal static class ExtensionLoader
    {
        private static readonly List<LoadedExtension> _loaded = new List<LoadedExtension>();

        public static IReadOnlyList<LoadedExtension> Loaded => _loaded;

        public static void DiscoverAndLoad()
        {
            string dir = Path.Combine(KmhDataPaths.AddonDir, "kmh-extensions");
            if (!Directory.Exists(dir))
            {
                // First-time install - create the empty folder + a tiny README so admins see where to drop
                // extensions without hunting through the docs
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(
                        Path.Combine(dir, "README.txt"),
                        ExtensionsReadme.Content);
                }
                catch { /* lazy folder; not a fatal startup blocker */ }
                ServerLog.Info("Extensions: kmh-extensions/ folder created (empty).");
                return;
            }

            string[] dlls;
            try { dlls = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                ServerLog.Warn($"Extensions: scan failed at {dir}: {ex.Message}");
                return;
            }
            if (dlls.Length == 0)
            {
                ServerLog.Info("Extensions: kmh-extensions/ present but empty.");
                return;
            }

            foreach (string dllPath in dlls)
            {
                TryLoadAssembly(dllPath);
            }
            ServerLog.Info($"Extensions: {_loaded.Count} loaded.");
        }

        public static void ShutdownAll()
        {
            foreach (LoadedExtension ext in _loaded)
            {
                try { ext.Instance.Shutdown(); }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Extension '{ext.Name}' Shutdown threw: {ex.Message}");
                }
            }
        }

        private static void TryLoadAssembly(string dllPath)
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(dllPath); }
            catch (Exception ex)
            {
                ServerLog.Warn($"Extensions: could not load '{Path.GetFileName(dllPath)}': {ex.Message}");
                return;
            }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                // Partial type load - surface the loader exceptions so missing-dep diagnostics aren't silently
                // swallowed
                ServerLog.Warn($"Extensions: type load failure in '{Path.GetFileName(dllPath)}':");
                foreach (Exception inner in ex.LoaderExceptions.Take(3))
                {
                    ServerLog.Warn($"  - {inner.Message}");
                }
                types = ex.Types.Where(t => t != null).ToArray();
            }

            foreach (Type t in types)
            {
                if (t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IKmhServerExtension).IsAssignableFrom(t)) continue;
                TryInstantiate(t, dllPath);
            }
        }

        private static void TryInstantiate(Type type, string sourceDllPath)
        {
            IKmhServerExtension instance;
            try { instance = (IKmhServerExtension)Activator.CreateInstance(type); }
            catch (Exception ex)
            {
                ServerLog.Warn($"Extensions: could not instantiate '{type.FullName}' from '{Path.GetFileName(sourceDllPath)}': {ex.Message}");
                return;
            }

            string name    = string.IsNullOrWhiteSpace(instance.Name)    ? type.FullName : instance.Name;
            string version = string.IsNullOrWhiteSpace(instance.Version) ? "0.0.0"       : instance.Version;

            KmhServerHost host = new KmhServerHost(name);
            try { instance.Register(host); }
            catch (Exception ex)
            {
                ServerLog.Error($"Extensions: '{name}' v{version} Register() threw - disabling", ex);
                return;
            }

            _loaded.Add(new LoadedExtension
            {
                Name        = name,
                Version     = version,
                SourceDll   = Path.GetFileName(sourceDllPath),
                Instance    = instance,
                Host        = host,
            });
            ServerLog.Info($"Extensions: loaded '{name}' v{version} (from {Path.GetFileName(sourceDllPath)})");
        }
    }

    internal sealed class LoadedExtension
    {
        public string                Name      { get; set; }
        public string                Version   { get; set; }
        public string                SourceDll { get; set; }
        public IKmhServerExtension   Instance  { get; set; }
        public KmhServerHost         Host      { get; set; }
    }
}
