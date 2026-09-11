using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace KMHServerAddon
{
    // Parses the Microsoft.NET.HostModel bundle layout; the field order below follows that format, not our choosing.
    internal static class SingleFileBundle
    {
        private static readonly byte[] Signature =
        {
            0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
            0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        };

        private struct Entry
        {
            public string Name;
            public long   Offset;
            public long   Size;
            public long   Comp;
        }

        // Identifies what an executable IS without writing anything. Empty = unreadable or not a bundle.
        public static List<string> ListManagedNames(string exePath)
        {
            List<string> names = new List<string>();
            try
            {
                byte[] data = File.ReadAllBytes(exePath);
                foreach (Entry e in Walk(data)) names.Add(e.Name);
            }
            catch { /* caller treats it as "not a server" */ }
            return names;
        }

        // Skips re-extraction only when the fingerprint matches AND the expected server assembly is present.
        public static int Extract(string exePath, string targetDir, string fingerprint, string sentinelDll)
        {
            Directory.CreateDirectory(targetDir);
            string marker = Path.Combine(targetDir, ".source");
            if (File.Exists(marker) && SafeRead(marker) == fingerprint
                && (string.IsNullOrEmpty(sentinelDll) || File.Exists(Path.Combine(targetDir, sentinelDll))))
            {
                return Count(targetDir);
            }

            // Purged, not merged: a stale server assembly would make generation detection key off a file RWT no longer ships.
            PurgeStaleCache(targetDir, marker);

            byte[] data = File.ReadAllBytes(exePath);
            int n = 0;
            foreach (Entry e in Walk(data))
            {
                // R2R images bake in dependency identity; skip so the runtime JITs plain IL.
                if (e.Name.EndsWith(".r2r.dll", StringComparison.OrdinalIgnoreCase)) continue;
                // A second Newtonsoft with a different identity crashes Harmony's JIT hook; bind to RWT's.
                if (e.Name.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)) continue;

                string outPath = Path.Combine(targetDir, e.Name);
                if (e.Comp > 0)
                {
                    using var src = new MemoryStream(data, (int)e.Offset, (int)e.Comp, false);
                    using var inflate = new DeflateStream(src, CompressionMode.Decompress);
                    using var outFs = File.Create(outPath);
                    inflate.CopyTo(outFs);
                }
                else
                {
                    using var outFs = File.Create(outPath);
                    outFs.Write(data, (int)e.Offset, (int)e.Size);
                }
                n++;
            }

            File.WriteAllText(marker, fingerprint);
            return n;
        }

        private static IEnumerable<Entry> Walk(byte[] data)
        {
            long pos = FindValidHeader(data);
            if (pos < 0) throw new InvalidDataException("not a .NET single-file bundle (no valid manifest)");

            int major = ReadI32(data, ref pos);
            ReadI32(data, ref pos);                       // minor
            int fileCount = ReadI32(data, ref pos);
            ReadStr(data, ref pos);                       // bundleId
            if (major >= 2) pos += 40;                    // deps/runtimeconfig/flags

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

                yield return new Entry { Name = name, Offset = offset, Size = size, Comp = comp };
            }
        }

        // Removes only what a previous extraction wrote.
        private static void PurgeStaleCache(string dir, string marker)
        {
            try
            {
                if (File.Exists(marker)) File.Delete(marker);
                foreach (string f in Directory.GetFiles(dir, "*.dll"))
                {
                    try { File.Delete(f); } catch { /* locked by a running process - extraction will overwrite */ }
                }
            }
            catch { }
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return null; }
        }

        private static int Count(string dir)
        {
            try { return Directory.GetFiles(dir, "*.dll").Length; } catch { return 0; }
        }

        // The signature can occur by chance inside a bundled assembly, so a candidate counts only if its header parses.
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
