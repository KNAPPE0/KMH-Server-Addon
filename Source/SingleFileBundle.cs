using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace KMHServerAddon
{
    // Extracts the managed assemblies from a .NET single-file bundle. We use it to unpack KMH's OWN payload (this
    // exe bundles our hook + Harmony + Discord.Net) onto disk next to GameServer.exe, so GameServer's runtime can
    // load our startup hook. We ship nothing of RWT's; we never read RWT here
    //
    // Bundle format (Microsoft.NET.HostModel): a 16-byte signature sits near the end; the int64 just before it is
    // the manifest offset. The manifest is major/minor/fileCount/bundleId, then (major>=2) five int64s, then per
    // file: offset, size, (major>=6) compressedSize, type byte, 7-bit-len path. Type 1 = managed assembly;
    // compressedSize>0 means deflate-compressed
    internal static class SingleFileBundle
    {
        private static readonly byte[] Signature =
        {
            0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
            0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        };

        // Extract the bundle's managed assemblies into targetDir (once - a marker keyed to the exe's size skips
        // re-extraction). Returns how many DLLs are available afterward
        public static int Extract(string exePath, string targetDir, string sentinelDll)
        {
            Directory.CreateDirectory(targetDir);
            long len = new FileInfo(exePath).Length;
            string marker = Path.Combine(targetDir, ".source");
            if (File.Exists(marker) && File.ReadAllText(marker) == len.ToString()
                && File.Exists(Path.Combine(targetDir, sentinelDll)))
            {
                return Count(targetDir);
            }

            byte[] data = File.ReadAllBytes(exePath);
            // The 16-byte signature can occur by chance inside a bundled assembly, so we can't just take the last
            // match - find the one whose preceding int64 points to a header that actually parses as a valid bundle
            // manifest
            long pos = FindValidHeader(data);
            if (pos < 0) throw new InvalidDataException("not a .NET single-file bundle (no valid manifest)");

            int major = ReadI32(data, ref pos);
            ReadI32(data, ref pos);                       // minor
            int fileCount = ReadI32(data, ref pos);
            ReadStr(data, ref pos);                       // bundleId
            if (major >= 2) pos += 40;                    // deps/runtimeconfig/flags

            int n = 0;
            for (int i = 0; i < fileCount; i++)
            {
                long offset = ReadI64(data, ref pos);
                long size   = ReadI64(data, ref pos);
                long comp   = major >= 6 ? ReadI64(data, ref pos) : 0;
                byte type   = data[pos++];
                string rel  = ReadStr(data, ref pos);
                if (type != 1) continue;                  // managed assemblies only

                string name = Path.GetFileName(rel);
                if (string.IsNullOrEmpty(name)) continue;

                // R2R composite native images bake in dependency identity - skip them so the runtime JITs the plain
                // IL instead
                if (name.EndsWith(".r2r.dll", StringComparison.OrdinalIgnoreCase)) continue;
                // Don't ship our Newtonsoft - RWT's GameServer process already has one (13.x); a second copy with a
                // different identity makes Harmony's JIT hook crash. Our code binds to RWT's at runtime
                if (name.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)) continue;

                string outPath = Path.Combine(targetDir, name);
                if (comp > 0)
                {
                    using var src = new MemoryStream(data, (int)offset, (int)comp, false);
                    using var inflate = new DeflateStream(src, CompressionMode.Decompress);
                    using var outFs = File.Create(outPath);
                    inflate.CopyTo(outFs);
                }
                else
                {
                    using var outFs = File.Create(outPath);
                    outFs.Write(data, (int)offset, (int)size);
                }
                n++;
            }

            File.WriteAllText(marker, len.ToString());
            return n;
        }

        private static int Count(string dir)
        {
            try { return Directory.GetFiles(dir, "*.dll").Length; } catch { return 0; }
        }

        // Scan from the end for a signature whose preceding int64 points to a header that parses as a sane manifest
        // (version + file count in range). Returns the manifest offset, or -1
        private static long FindValidHeader(byte[] data)
        {
            for (long i = data.Length - Signature.Length; i >= 8; i--)
            {
                if (!MatchesAt(data, i)) continue;
                long ho = BitConverter.ToInt64(data, (int)(i - 8));
                if (ho < 0 || ho + 12 > data.Length) continue;
                int major = BitConverter.ToInt32(data, (int)ho);
                int minor = BitConverter.ToInt32(data, (int)ho + 4);
                int fc    = BitConverter.ToInt32(data, (int)ho + 8);
                if (major >= 1 && major <= 6 && minor >= 0 && minor <= 9 && fc >= 1 && fc <= 100000)
                    return ho;
            }
            return -1;
        }

        private static bool MatchesAt(byte[] data, long i)
        {
            for (int j = 0; j < Signature.Length; j++)
                if (data[i + j] != Signature[j]) return false;
            return true;
        }

        private static int  ReadI32(byte[] d, ref long p) { int v = BitConverter.ToInt32(d, (int)p); p += 4; return v; }
        private static long ReadI64(byte[] d, ref long p) { long v = BitConverter.ToInt64(d, (int)p); p += 8; return v; }

        private static string ReadStr(byte[] d, ref long p)
        {
            int len = 0, shift = 0;
            while (true)
            {
                byte b = d[p++];
                len |= (b & 0x7f) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            string s = Encoding.UTF8.GetString(d, (int)p, len);
            p += len;
            return s;
        }
    }
}
