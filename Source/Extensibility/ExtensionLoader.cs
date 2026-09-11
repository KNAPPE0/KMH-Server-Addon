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
    // Every load step is caught separately, so a broken extension is skipped rather than taking KMH down with it.
    internal static class ExtensionLoader
    {
        private static readonly List<LoadedExtension> _loaded = new List<LoadedExtension>();

        public static IReadOnlyList<LoadedExtension> Loaded => _loaded;

        public static void DiscoverAndLoad()
        {
            string dir = Path.Combine(KmhDataPaths.AddonDir, "kmh-extensions");
            if (!Directory.Exists(dir))
            {
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
                // The loader exceptions name the missing dependency, which the outer message alone never does.
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

            // Refused up front, because an incompatible extension otherwise half-loads and fails later as a reflection error.
            int contract = instance is KMH.Sdk.Server.IKmhSdkTargeted targeted ? targeted.TargetSdkContract : KmhExtensionCompat.Baseline;
            if (!KmhExtensionCompat.IsCompatible(contract))
            {
                ServerLog.Warn($"Extensions: skipping '{name}' v{version} - {KmhExtensionCompat.Explain(contract)} " +
                               $"(this server provides SDK contract {KmhExtensionCompat.Current}).");
                return;
            }

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
                SdkContract = contract,
                SourceDll   = Path.GetFileName(sourceDllPath),
                Instance    = instance,
                Host        = host,
            });
            ServerLog.Info($"Extensions: loaded '{name}' v{version} (SDK contract {contract}, from {Path.GetFileName(sourceDllPath)})");
        }
    }

    internal sealed class LoadedExtension
    {
        public string                Name        { get; set; }
        public string                Version     { get; set; }
        public int                   SdkContract { get; set; }
        public string                SourceDll   { get; set; }
        public IKmhServerExtension   Instance    { get; set; }
        public KmhServerHost         Host        { get; set; }
    }
}
