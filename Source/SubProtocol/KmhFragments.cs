using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace KMHServerAddon.SubProtocol
{
    // Lifts the LOGICAL message ceiling; the physical frame stays a DoS guard, so raising MaxFrameKb only moves the cliff.
    internal static class KmhFragments
    {
        public const string Kind = "kmh.fragment";

        // Base64 costs ~33%, so 32 KB of payload lands near 44 KB of envelope, well inside a 64 KB frame.
        public const int RawChunkBytes = 32 * 1024;

        // A peer that can make us hold memory is a peer that can exhaust us.
        public const int  MaxChunks               = 512;
        public const long MaxTransferBytes        = 8L * 1024 * 1024;
        public const int  MaxConcurrentPerPeer    = 4;
        public const int  AssemblyTimeoutSeconds  = 30;

        public static bool IsFragment(string kind) => string.Equals(kind, Kind, StringComparison.Ordinal);

        public static List<KmhEnvelope> Split(string kind, string serialized, int safeFrameBytes)
        {
            if (string.IsNullOrEmpty(serialized)) return null;
            byte[] raw = Encoding.UTF8.GetBytes(serialized);
            if (raw.LongLength > MaxTransferBytes)
            {
                Diagnostics.ServerLog.Error($"Transport: '{kind}' is {raw.LongLength} bytes, past the {MaxTransferBytes} transfer ceiling - not sent.");
                return null;
            }

            int chunk = Math.Min(RawChunkBytes, Math.Max(1024, safeFrameBytes / 2));
            int total = (int)((raw.LongLength + chunk - 1) / chunk);
            if (total <= 0 || total > MaxChunks)
            {
                Diagnostics.ServerLog.Error($"Transport: '{kind}' needs {total} fragments, past the {MaxChunks} limit - not sent.");
                return null;
            }

            string tid = Guid.NewGuid().ToString("N");
            string hash = HashOf(raw);
            var outp = new List<KmhEnvelope>(total);
            for (int i = 0; i < total; i++)
            {
                int off = i * chunk;
                int len = Math.Min(chunk, raw.Length - off);
                outp.Add(new KmhEnvelope(Kind, new
                {
                    tid, kind, i, n = total, len = raw.Length, hash,
                    data = Convert.ToBase64String(raw, off, len),
                }));
            }
            return outp;
        }

        public static string HashOf(byte[] raw)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(raw);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        internal sealed class Assembler
        {
            private sealed class Transfer
            {
                public string Kind;
                public byte[][] Parts;
                public int Have;
                public int TotalBytes;
                public string Hash;
                public DateTime StartedUtc;
            }

            private readonly Dictionary<string, Transfer> _open = new Dictionary<string, Transfer>(StringComparer.Ordinal);

            // `why` is set only on rejection, so a caller can log a real reason instead of silence.
            public KmhEnvelope Accept(KmhEnvelope frag, out string why)
            {
                why = null;
                if (frag == null) { why = "no envelope"; return null; }

                string tid  = frag.GetString("tid", "") ?? "";
                string kind = frag.GetString("kind", "") ?? "";
                int i       = frag.GetInt("i", -1);
                int n       = frag.GetInt("n", -1);
                int len     = frag.GetInt("len", -1);
                string hash = frag.GetString("hash", "") ?? "";
                string data = frag.GetString("data", "") ?? "";

                if (tid.Length == 0 || tid.Length > 64 || kind.Length == 0) { why = "bad transfer header"; return null; }
                if (n <= 0 || n > MaxChunks) { why = $"bad chunk count {n}"; return null; }
                if (i < 0 || i >= n) { why = $"chunk index {i} outside 0..{n - 1}"; return null; }
                if (len <= 0 || len > MaxTransferBytes) { why = $"declared size {len} out of range"; return null; }

                Prune();

                if (!_open.TryGetValue(tid, out Transfer t))
                {
                    if (_open.Count >= MaxConcurrentPerPeer) { why = "too many transfers in flight"; return null; }
                    t = new Transfer
                    {
                        Kind = kind, Parts = new byte[n][], TotalBytes = len, Hash = hash,
                        StartedUtc = DateTime.UtcNow,
                    };
                    _open[tid] = t;
                }
                // A transfer's own header must not change halfway through.
                else if (t.Parts.Length != n || t.TotalBytes != len
                         || !string.Equals(t.Kind, kind, StringComparison.Ordinal)
                         || !string.Equals(t.Hash, hash, StringComparison.Ordinal))
                { _open.Remove(tid); why = "fragment header changed mid-transfer"; return null; }

                if (t.Parts[i] != null) { why = null; return null; }   // duplicate: ignore, not an error

                byte[] piece;
                try { piece = Convert.FromBase64String(data); }
                catch { _open.Remove(tid); why = "undecodable chunk"; return null; }

                long soFar = piece.LongLength;
                foreach (byte[] p in t.Parts) if (p != null) soFar += p.LongLength;
                if (soFar > t.TotalBytes) { _open.Remove(tid); why = "chunks exceed the declared size"; return null; }

                t.Parts[i] = piece;
                t.Have++;
                if (t.Have < t.Parts.Length) return null;

                _open.Remove(tid);
                var full = new byte[t.TotalBytes];
                int off = 0;
                foreach (byte[] p in t.Parts)
                {
                    if (off + p.Length > full.Length) { why = "reassembled size overflow"; return null; }
                    Buffer.BlockCopy(p, 0, full, off, p.Length);
                    off += p.Length;
                }
                if (off != full.Length) { why = "reassembled size mismatch"; return null; }
                if (!string.Equals(HashOf(full), t.Hash, StringComparison.Ordinal)) { why = "hash mismatch"; return null; }

                KmhEnvelope env = KmhEnvelope.TryParse(Encoding.UTF8.GetString(full));
                if (env == null) { why = "reassembled payload did not parse"; return null; }
                return env;
            }

            // An abandoned transfer must not hold its buffers for the life of the connection.
            private void Prune()
            {
                if (_open.Count == 0) return;
                DateTime cutoff = DateTime.UtcNow.AddSeconds(-AssemblyTimeoutSeconds);
                List<string> dead = null;
                foreach (KeyValuePair<string, Transfer> kv in _open)
                    if (kv.Value.StartedUtc < cutoff) (dead = dead ?? new List<string>()).Add(kv.Key);
                if (dead == null) return;
                foreach (string k in dead) _open.Remove(k);
            }

            public void Clear() => _open.Clear();
        }
    }
}
