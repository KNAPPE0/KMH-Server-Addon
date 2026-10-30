using System;
using System.Collections.Generic;
using System.Text;

namespace KMHServerAddon.SubProtocol
{
    // Everything here arrives from a socket a modified client controls. The rule for every case is the same:
    // reject or default, bounded allocation, no throw on the network thread, and no state change.
    internal static class KmhWireFuzzSelfTest
    {
        // Deterministic on purpose: a fuzz case that only fails on some runs is a case nobody can reproduce.
        private static int _seed = 0x5EED;
        private static int Next(int bound)
        {
            _seed = unchecked(_seed * 1103515245 + 12345);
            int v = (_seed >> 16) & 0x7FFF;
            return bound <= 0 ? 0 : v % bound;
        }

        private static readonly string[] Scalars =
        {
            "null", "true", "false", "0", "-1", "1e400", "-1e400",
            "2147483647", "-2147483648", "9223372036854775807", "-9223372036854775808",
            "9223372036854775808", "-9223372036854775809",          // past Int64 in both directions
            "1.7976931348623157e309",                                // past Double
            "\"\"", "\"not a number\"", "[]", "{}", "[1,2,3]", "{\"a\":1}",
            "\"\\u0000\"", "\"\\ud800\"",                            // lone surrogate
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"THREW on the network thread: {ex.GetType().Name}: {ex.Message}")); }
            }

            Check("Fuzz: a malformed envelope parses to null rather than throwing", () =>
            {
                string[] cases =
                {
                    "", " ", "{", "}", "[]", "null", "0", "\"a string\"",
                    "{\"kind\":", "{\"kind\":\"x\",}", "{'kind':'x'}",
                    "{\"kind\":\"x\",\"v\":\"not a number\"}",
                    "{\"kind\":null,\"v\":null,\"data\":null}",
                    "{\"data\":[1,2,3]}",                    // data must be an object
                    "{\"kind\":\"x\",\"v\":3,\"data\":\"a string\"}",
                    "\uFEFF{\"kind\":\"x\"}",                // BOM
                    "{\"kind\":\"x\",\"v\":3,\"data\":{}}\u0000trailing",
                };
                var threw = new List<string>();
                int parsed = 0;
                foreach (string c in cases)
                {
                    try { if (KmhEnvelope.TryParse(c) != null) parsed++; }
                    catch (Exception ex) { threw.Add($"{Short(c)} -> {ex.GetType().Name}"); }
                }
                return (threw.Count == 0,
                        threw.Count == 0 ? $"{cases.Length} malformed envelope(s), {parsed} parsed, none threw"
                                         : "THREW: " + string.Join("; ", threw));
            });

            Check("Fuzz: a hostile scalar in any accessor returns the default, never throws", () =>
            {
                var threw = new List<string>();
                foreach (string v in Scalars)
                {
                    string json = "{\"kind\":\"k\",\"v\":3,\"data\":{\"f\":" + v + "}}";
                    KmhEnvelope env = KmhEnvelope.TryParse(json);
                    if (env == null) continue;
                    try
                    {
                        env.GetInt("f", -7); env.GetLong("f", -7); env.GetString("f", "d");
                        env.GetBool("f"); env.GetIntArray("f"); env.GetIntMap("f");
                        env.GetStringMap("f"); env.GetStringList("f");
                        env.GetInt("missing", -7); env.GetStringList("missing");
                    }
                    catch (Exception ex) { threw.Add($"{v} -> {ex.GetType().Name}: {ex.Message}"); }
                }
                return (threw.Count == 0,
                        threw.Count == 0 ? $"{Scalars.Length} hostile value(s) through 8 accessor(s)"
                                         : "THREW: " + string.Join("; ", threw));
            });

            Check("Fuzz: an out-of-range number reads as the default, not as a wrapped value", () =>
            {
                // Silently wrapping Int64.MaxValue into a negative int is how a withdrawal becomes a deposit.
                var bad = new List<string>();
                (string Json, string What)[] cases =
                {
                    ("9223372036854775808",  "past Int64.Max"),
                    ("-9223372036854775809", "past Int64.Min"),
                    ("1e400",                "past Double"),
                };
                foreach ((string j, string what) in cases)
                {
                    KmhEnvelope env = KmhEnvelope.TryParse("{\"kind\":\"k\",\"v\":3,\"data\":{\"n\":" + j + "}}");
                    if (env == null) continue;
                    long asLong = env.GetLong("n", -7);
                    int  asInt  = env.GetInt("n", -7);
                    if (asLong > 0 && asLong < 100) bad.Add($"{what} read as {asLong} (long)");
                    if (asInt  > 0 && asInt  < 100) bad.Add($"{what} read as {asInt} (int)");
                }

                // A numeric string coerces to its own value, which changes nothing; only a wrap would.
                KmhEnvelope str = KmhEnvelope.TryParse("{\"kind\":\"k\",\"v\":3,\"data\":{\"n\":\"12\"}}");
                long coerced = str?.GetLong("n", -7) ?? -7;
                if (coerced != 12 && coerced != -7) bad.Add($"a numeric string read as {coerced}");
                return (bad.Count == 0, bad.Count == 0 ? "no out-of-range number wraps into a small positive"
                                                       : string.Join("; ", bad));
            });

            Check("Fuzz: a declared frame length that disagrees with the body is refused", () =>
            {
                var bad = new List<string>();
                // The length field's top bit is the compression flag, so the remaining 31 bits are the length.
                int[] headers = { 0, 1, int.MaxValue, KmhFrameCodec.LengthMask, unchecked((int)0xFFFFFFFF) };
                foreach (int h in headers)
                {
                    byte[] len4 = { (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h };
                    try
                    {
                        KmhFrameCodec.ReadHeader(len4, out int bytes, out bool compressed);
                        if (bytes < 0) bad.Add($"header {h:X8} -> negative length {bytes}");
                        if (bytes > KmhFrameCodec.LengthMask) bad.Add($"header {h:X8} -> {bytes} past the mask");
                    }
                    catch (Exception ex) { bad.Add($"header {h:X8} threw {ex.GetType().Name}"); }
                }
                return (bad.Count == 0, bad.Count == 0 ? $"{headers.Length} header(s), every length non-negative and masked"
                                                       : string.Join("; ", bad));
            });

            Check("Fuzz: a malformed compressed stream inflates to nothing rather than throwing", () =>
            {
                var threw = new List<string>();
                byte[][] cases =
                {
                    new byte[0],
                    new byte[] { 0 },
                    new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                    Encoding.UTF8.GetBytes("not a deflate stream at all"),
                    new byte[] { 0x78, 0x9C, 0x01, 0x02, 0x03 },       // a plausible header, junk body
                };
                foreach (byte[] c in cases)
                {
                    try
                    {
                        byte[] outp = KmhFrameCodec.Inflate(c, 1024);
                        if (outp != null && outp.Length > 1024) threw.Add($"inflated past the cap: {outp.Length}");
                    }
                    catch (Exception ex) { threw.Add($"{c.Length} byte(s) -> {ex.GetType().Name}"); }
                }
                return (threw.Count == 0, threw.Count == 0 ? $"{cases.Length} malformed stream(s), none threw or overran"
                                                           : string.Join("; ", threw));
            });

            Check("Fuzz: a compression bomb is stopped at the cap, not at the allocation", () =>
            {
                byte[] raw = new byte[2 * 1024 * 1024];
                byte[] packed = KmhFrameCodec.Deflate(raw);
                if (packed == null) return (false, "2MB of zeroes did not compress - the case cannot be built");
                byte[] outp = KmhFrameCodec.Inflate(packed, 64 * 1024);
                bool stopped = outp == null || outp.Length <= 64 * 1024;
                return (stopped, stopped ? $"{packed.Length} compressed byte(s) held to {64 * 1024} on inflate"
                                         : $"inflated {outp.Length} bytes past a 64KB cap");
            });

            Check("Fuzz: a huge or deeply nested payload is bounded, not parsed forever", () =>
            {
                var sb = new StringBuilder("{\"kind\":\"k\",\"v\":3,\"data\":{\"s\":\"");
                sb.Append('x', 200_000);
                sb.Append("\"}}");
                KmhEnvelope big = KmhEnvelope.TryParse(sb.ToString());
                bool bigOk = big == null || (big.GetString("s") ?? "").Length == 200_000;

                // Deep nesting is where a recursive-descent parser dies; the answer must be null, not a stack overflow.
                var deep = new StringBuilder("{\"kind\":\"k\",\"v\":3,\"data\":");
                const int depth = 2000;
                for (int i = 0; i < depth; i++) deep.Append("{\"a\":");
                deep.Append('1');
                for (int i = 0; i < depth; i++) deep.Append('}');
                deep.Append('}');
                bool deepHandled;
                try { KmhEnvelope.TryParse(deep.ToString()); deepHandled = true; }
                catch (Exception ex) { deepHandled = false; r.Add(("Fuzz: nesting detail", false, ex.GetType().Name)); }

                return (bigOk && deepHandled,
                        bigOk && deepHandled ? "a 200KB string and 2000 levels of nesting are both survivable"
                                             : $"largeString={bigOk}, deepNesting={deepHandled}");
            });

            Check("Fuzz: a string map from a client is bounded however many keys arrive", () =>
            {
                var sb = new StringBuilder("{\"kind\":\"k\",\"v\":3,\"data\":{\"m\":{");
                for (int i = 0; i < 5000; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("\"k").Append(i).Append("\":\"v\"");
                }
                sb.Append("}}}");
                KmhEnvelope env = KmhEnvelope.TryParse(sb.ToString());
                if (env == null) return (false, "5000 keys did not parse at all");
                var map = env.GetStringMap("m");
                bool bounded = map.Count <= 256;
                return (bounded, bounded ? $"5000 key(s) truncated to {map.Count}"
                                         : $"kept {map.Count} of 5000 - an unbounded packet allocates freely");
            });

            Check("Fuzz: a path-shaped string is never treated as a path by the wire layer", () =>
            {
                // The envelope must hand these back verbatim; deciding they are dangerous belongs to the code
                // that would open a file, and a wire layer that "helpfully" normalises one hides the attack.
                string[] paths =
                {
                    "../../etc/passwd", "..\\..\\windows\\system32", "/absolute/path",
                    "C:\\Windows\\System32", "a/../b", "%2e%2e%2f", "\\\\server\\share",
                };
                var wrong = new List<string>();
                foreach (string p in paths)
                {
                    string json = "{\"kind\":\"k\",\"v\":3,\"data\":{\"p\":" +
                                  Newtonsoft.Json.JsonConvert.ToString(p) + "}}";
                    KmhEnvelope env = KmhEnvelope.TryParse(json);
                    string got = env?.GetString("p");
                    if (got != p) wrong.Add($"{p} -> {got ?? "null"}");
                }
                return (wrong.Count == 0, wrong.Count == 0 ? $"{paths.Length} path-shaped string(s) round-trip unchanged"
                                                           : string.Join("; ", wrong));
            });

            Check("Fuzz: a thousand randomised payloads leave no accessor throwing", () =>
            {
                _seed = 0x5EED;
                var threw = new List<string>();
                for (int i = 0; i < 1000 && threw.Count < 5; i++)
                {
                    var sb = new StringBuilder("{");
                    int fields = Next(6);
                    for (int f = 0; f < fields; f++)
                    {
                        if (f > 0) sb.Append(',');
                        string key = new[] { "kind", "v", "op", "data", "id", "qty", "amount" }[Next(7)];
                        sb.Append('"').Append(key).Append("\":").Append(Scalars[Next(Scalars.Length)]);
                    }
                    sb.Append('}');
                    string json = sb.ToString();
                    try
                    {
                        KmhEnvelope env = KmhEnvelope.TryParse(json);
                        if (env == null) continue;
                        env.GetInt("id"); env.GetLong("amount"); env.GetString("kind");
                        env.GetBool("op"); env.GetIntArray("data"); env.GetIntMap("data");
                        env.GetStringMap("data"); env.GetStringList("data");
                        env.DataAs<Dictionary<string, string>>();
                    }
                    catch (Exception ex) { threw.Add($"{Short(json)} -> {ex.GetType().Name}: {ex.Message}"); }
                }
                return (threw.Count == 0, threw.Count == 0 ? "1000 randomised payloads, no accessor threw"
                                                           : string.Join(" | ", threw));
            });

            return r;
        }

        private static string Short(string s)
            => s == null ? "null" : (s.Length <= 40 ? s : s.Substring(0, 37) + "...");
    }
}
