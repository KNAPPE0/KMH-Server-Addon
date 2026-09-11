using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.SubProtocol
{
    internal static class KmhEnvelopeSelfTest
    {
        // A deliberate second implementation: it is the reference the real one is compared against, not dead code.
        private sealed class Ref
        {
            [JsonProperty("kind")] public string  Kind    { get; set; }
            [JsonProperty("v")]    public int     Version { get; set; }
            [JsonProperty("data")] public JObject Data    { get; set; }
        }

        private sealed class Sample
        {
            [JsonProperty("a_str")] public string    A { get; set; }
            [JsonProperty("b_num")] public long      B { get; set; }
            [JsonProperty("c_opt", NullValueHandling = NullValueHandling.Ignore)] public string C { get; set; }
            [JsonProperty("d_list")] public List<int> D { get; set; }
        }

        private static string OldWay(string kind, object data, int version)
            => JsonConvert.SerializeObject(new Ref
            {
                Kind = kind, Version = version, Data = data == null ? new JObject() : JObject.FromObject(data)
            });

        private static string NewWay(string kind, object data, int version)
            => new KmhEnvelope(kind, data, version).Serialize();

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            var cases = new List<(string name, object data)>
            {
                ("null payload",        null),
                ("empty object",        new { }),
                ("notice shape",        new { level = "positive", text = "done" }),
                ("nested + specials",   new { a = new { b = 1L, c = true }, list = new[] { 1, 2, 3 }, s = "q\"u\neé" }),
                ("raw dictionary",      new Dictionary<string, int> { { "Steel", 5 }, { "Gold", 2 } }),
                ("dto attrs + null",    new Sample { A = "x", B = 99, C = null, D = new List<int> { 7, 8 } }),
                ("mixed primitives",    new { i = 42, big = 9999999999L, f = 3.5, neg = -7, on = true, off = false }),
            };

            foreach ((string name, object data) in cases)
            {
                string oldStr = OldWay("kmh.x", data, 2);
                string newStr = NewWay("kmh.x", data, 2);
                r.Add(($"Envelope byte-identical: {name}", oldStr == newStr, newStr == oldStr ? "" : $"NEW={newStr} OLD={oldStr}"));
            }

            string nullOut = NewWay("kmh.x", null, 2);
            r.Add(("Envelope null payload -> data:{}", nullOut.Contains("\"data\":{}"), nullOut));

            string sent = NewWay("kmh.note", new { level = "negative", n = 7 }, 2);
            KmhEnvelope parsed = KmhEnvelope.TryParse(sent);
            bool roundTrips = parsed != null && parsed.Kind == "kmh.note" && parsed.Version == 2
                              && parsed.GetString("level") == "negative" && parsed.GetInt("n") == 7;
            r.Add(("Envelope outbound round-trips through parse", roundTrips, sent));

            return r;
        }
    }
}
