using System;
using System.Collections.Generic;
using System.Text;

namespace KMHServerAddon.SubProtocol
{
    internal static class KmhFrameCodecSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Frame codec: a JSON payload round-trips byte for byte", () =>
            {
                byte[] raw = Encoding.UTF8.GetBytes(SampleJson(400));
                byte[] packed = KmhFrameCodec.Deflate(raw);
                if (packed == null) return (false, "a repetitive JSON payload did not compress");
                byte[] back = KmhFrameCodec.Inflate(packed, 8 * 1024 * 1024);
                bool ok = back != null && back.Length == raw.Length;
                if (ok) for (int i = 0; i < raw.Length; i++) if (raw[i] != back[i]) { ok = false; break; }
                int saved = 100 - packed.Length * 100 / raw.Length;
                return (ok, ok ? $"{raw.Length} -> {packed.Length} bytes ({saved}% smaller)" : "bytes differ");
            });

            Check("Frame codec: the header carries the flag and the wire length", () =>
            {
                byte[] body = Encoding.UTF8.GetBytes("hello");
                byte[] plain = KmhFrameCodec.Frame(body, false);
                byte[] zipped = KmhFrameCodec.Frame(body, true);
                KmhFrameCodec.ReadHeader(plain, out int n1, out bool c1);
                KmhFrameCodec.ReadHeader(zipped, out int n2, out bool c2);
                bool ok = n1 == body.Length && !c1 && n2 == body.Length && c2;
                return (ok, ok ? "length and flag both survive" : $"{n1}/{c1} vs {n2}/{c2}");
            });

            // An old peer's frames have a clear top bit, so they must keep reading as uncompressed.
            Check("Frame codec: a frame from a peer that never compresses still reads raw", () =>
            {
                byte[] body = Encoding.UTF8.GetBytes(SampleJson(10));
                byte[] frame = new byte[4 + body.Length];
                frame[0] = (byte)(body.Length >> 24); frame[1] = (byte)(body.Length >> 16);
                frame[2] = (byte)(body.Length >> 8);  frame[3] = (byte)body.Length;
                KmhFrameCodec.ReadHeader(frame, out int n, out bool compressed);
                bool ok = !compressed && n == body.Length;
                return (ok, ok ? "unchanged for an older peer" : $"read as {n} bytes, compressed={compressed}");
            });

            Check("Frame codec: a payload too small to be worth it is left alone", () =>
            {
                byte[] tiny = Encoding.UTF8.GetBytes(new string('a', KmhFrameCodec.CompressAboveBytes - 1));
                bool ok = KmhFrameCodec.Deflate(tiny) == null;
                return (ok, ok ? "sent raw below the floor" : "compressed a payload not worth compressing");
            });

            Check("Frame codec: incompressible data is never sent bigger than it arrived", () =>
            {
                byte[] noise = new byte[8192];
                new Random(1234).NextBytes(noise);
                bool ok = KmhFrameCodec.Deflate(noise) == null;
                return (ok, ok ? "falls back to raw" : "would have sent MORE bytes than the original");
            });

            Check("Frame codec: a decompression bomb is refused, not inflated", () =>
            {
                byte[] bomb = KmhFrameCodec.Deflate(new byte[4 * 1024 * 1024]);   // 4MB of zeroes, a few KB packed
                if (bomb == null) return (false, "the fixture did not compress, so nothing was proven");
                bool refused = KmhFrameCodec.Inflate(bomb, 64 * 1024) == null;
                bool allowed = KmhFrameCodec.Inflate(bomb, 8 * 1024 * 1024) != null;
                return (refused && allowed,
                    refused && allowed ? $"{bomb.Length} packed bytes refused at a 64KB cap, accepted at 8MB"
                  : !refused           ? "a 4MB expansion passed a 64KB cap"
                                       : "the cap refused a payload that fits");
            });

            Check("Frame codec: corrupt compressed bytes fail closed", () =>
            {
                byte[] packed = KmhFrameCodec.Deflate(Encoding.UTF8.GetBytes(SampleJson(200)));
                packed[packed.Length / 2] ^= 0xFF;
                bool ok = KmhFrameCodec.Inflate(packed, 1024 * 1024) == null
                       || KmhEnvelope.TryParse(Encoding.UTF8.GetString(KmhFrameCodec.Inflate(packed, 1024 * 1024) ?? new byte[0])) == null;
                return (ok, ok ? "refused rather than dispatched as a half-message" : "corrupt bytes were accepted");
            });

            return r;
        }

        // Shaped like a real snapshot - repeated keys, short values - because that is what actually travels.
        private static string SampleJson(int rows)
        {
            var sb = new StringBuilder("{\"kind\":\"kmh.snapshot\",\"data\":{\"items\":[");
            for (int i = 0; i < rows; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(i)
                  .Append(",\"defName\":\"Steel\",\"stuff\":null,\"qty\":").Append(i % 97)
                  .Append(",\"seller\":\"player_").Append(i % 13)
                  .Append("\",\"price\":\"12.50\",\"quality\":\"Normal\"}");
            }
            return sb.Append("]}}").ToString();
        }
    }
}
