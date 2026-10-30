using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Global
{
    // Generated here and never sent anywhere; kept outside the config tree because it proves a heartbeat's origin.
    internal static class KmhServerKey
    {
        private const string FileName = "global-signing.key";
        // Written before Global is asked, so a crash after Global accepts it cannot lose the only key Global then takes.
        private const string StagedFileName = "global-signing.next.key";
        private const string PreviousFileName = "global-signing.previous.key";

        // Its own folder so a "back up my configs" or "paste me your config" never sweeps the private key along.
        private static string Folder => Persistence.KmhDataPaths.IdentityDir;
        private static string Path_ => Path.Combine(Folder, FileName);
        private static string StagedPath => Path.Combine(Folder, StagedFileName);
        private static string PreviousPath => Path.Combine(Folder, PreviousFileName);

        private static readonly object _lock = new object();
        private static ECDsa _cached;
        private static ECDsa _staged;

        public static bool Exists => File.Exists(Path_);

        public static string PublicKey
        {
            get
            {
                ECDsa key = Load();
                return key == null ? "" : KmhGlobalSigning.ExportPublicKey(key);
            }
        }

        public static string Fingerprint => KmhGlobalSigning.Fingerprint(PublicKey);

        public static string Sign(byte[] canonical)
        {
            ECDsa key = Load();
            return key == null ? "" : KmhGlobalSigning.Sign(key, canonical);
        }

        public static ECDsa Load()
        {
            lock (_lock)
            {
                if (_cached != null) return _cached;
                try
                {
                    Directory.CreateDirectory(Folder);
                    if (File.Exists(Path_))
                    {
                        // Every load: a key restored from a backup has lost its access list.
                        Restrict(Path_);
                        _cached = ReadKey(Path_);
                        return _cached;
                    }

                    ECDsa created = KmhGlobalSigning.CreateKey();
                    string pkcs8 = Convert.ToBase64String(created.ExportPkcs8PrivateKey());
                    // Written then tightened; a reader in between still needs the local access this process has.
                    File.WriteAllText(Path_, pkcs8);
                    Restrict(Path_);

                    // Registering a public key whose private half never reached disk strands the server for good.
                    if (!VerifyReloadable(created))
                    {
                        created.Dispose();
                        ServerLog.Error("Global: the new signing key could not be read back from disk, so it was not "
                                      + "kept. This server will not register until KMH-Data/Identity is writable.");
                        try { File.Delete(Path_); } catch { }
                        return null;
                    }

                    _cached = created;
                    ServerLog.Info($"Global: generated this server's signing key ({KmhGlobalSigning.ShortFingerprint(KmhGlobalSigning.Fingerprint(KmhGlobalSigning.ExportPublicKey(created)))}). Back up KMH-Data/Identity with your saves.");
                    return _cached;
                }
                catch (Exception ex)
                {
                    // Never fatal: a server that cannot sign simply does not appear in the Global browser.
                    ServerLog.Warn($"Global: could not load or create the signing key: {ex.Message}");
                    return null;
                }
            }
        }

        public static bool StagedExists => File.Exists(StagedPath);

        public static ECDsa LoadStaged()
        {
            lock (_lock)
            {
                if (_staged != null) return _staged;
                try
                {
                    if (!File.Exists(StagedPath)) return null;
                    return _staged = ReadKey(StagedPath);
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Global: could not read the staged signing key: {ex.Message}");
                    return null;
                }
            }
        }

        // Kept when it already exists: an interrupted change resumes with the key Global may already hold.
        public static ECDsa CreateStaged()
        {
            lock (_lock)
            {
                if (LoadStaged() is ECDsa existing) return existing;
                try
                {
                    Directory.CreateDirectory(Folder);
                    ECDsa created = KmhGlobalSigning.CreateKey();
                    File.WriteAllText(StagedPath, Convert.ToBase64String(created.ExportPkcs8PrivateKey()));
                    Restrict(StagedPath);
                    if (!VerifyReloadable(created, StagedPath))
                    {
                        created.Dispose();
                        try { File.Delete(StagedPath); } catch { }
                        return null;
                    }
                    return _staged = created;
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Global: could not create a new signing key: {ex.Message}");
                    return null;
                }
            }
        }

        public static string StagedPublicKey => LoadStaged() is ECDsa k ? KmhGlobalSigning.ExportPublicKey(k) : "";
        public static string StagedFingerprint => KmhGlobalSigning.Fingerprint(StagedPublicKey);
        public static string SignStaged(byte[] canonical) => LoadStaged() is ECDsa k ? KmhGlobalSigning.Sign(k, canonical) : "";

        // One rename swaps the staged key in and keeps the outgoing one beside it; at no point is there no key file.
        public static bool PromoteStaged()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(StagedPath)) return false;
                    if (File.Exists(Path_)) File.Replace(StagedPath, Path_, PreviousPath, ignoreMetadataErrors: true);
                    else File.Move(StagedPath, Path_);
                    _cached?.Dispose(); _cached = null;
                    _staged?.Dispose(); _staged = null;
                    return true;
                }
                catch (Exception ex)
                {
                    ServerLog.Error($"Global: could not install the new signing key: {ex.Message}");
                    return false;
                }
            }
        }

        public static void DiscardStaged()
        {
            lock (_lock)
            {
                _staged?.Dispose(); _staged = null;
                try { if (File.Exists(StagedPath)) File.Delete(StagedPath); }
                catch (Exception ex) { ServerLog.Warn($"Global: could not remove the staged signing key: {ex.Message}"); }
            }
        }

        private static bool VerifyReloadable(ECDsa created, string path = null)
        {
            try
            {
                using (ECDsa reloaded = ReadKey(path ?? Path_))
                {
                    byte[] probe = System.Text.Encoding.UTF8.GetBytes("kmh.identity.selfcheck");
                    byte[] signature = reloaded.SignData(probe, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                    return created.VerifyData(probe, signature, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Global: signing key verification failed: {ex.Message}");
                return false;
            }
        }

        private static ECDsa ReadKey(string path)
        {
            ECDsa key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(path).Trim()), out _);
            return key;
        }

        internal static bool IsKeyFile(string path) => path.EndsWith(".key", StringComparison.OrdinalIgnoreCase);

        internal static void Restrict(string path)
        {
            try
            {
                // A key file the whole machine can read is not a credential.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) RestrictWindows(path);
                else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) { ServerLog.Warn($"Global: could not restrict permissions on the signing key: {ex.Message}"); }
        }

        // On a shared host the inherited ACL lets other accounts read the key, so inheritance is broken.
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void RestrictWindows(string path)
        {
            var info = new FileInfo(path);
            System.Security.AccessControl.FileSecurity acl = info.GetAccessControl();
            using (System.Security.Principal.WindowsIdentity me = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (System.Security.AccessControl.FileSystemAccessRule existing in
                         acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                    acl.RemoveAccessRuleAll(existing);

                // No SetOwner: under an admin account taking ownership needs a privilege a normal process lacks,
                // which would fail the whole restriction; the access list alone limits the key.
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    me.User, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            info.SetAccessControl(acl);
        }

        internal static void ResetForTest()
        {
            lock (_lock) { _cached?.Dispose(); _cached = null; _staged?.Dispose(); _staged = null; }
        }
    }
}
