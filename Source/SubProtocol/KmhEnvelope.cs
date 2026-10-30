using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.SubProtocol
{
    // Mirrored on the patch mod side with the same property names, or handler code stops reading the same fields.
    public class KmhEnvelope
    {
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("v")]
        public int Version { get; set; }

        // Names the logical action, not one delivery, so a re-send is refused instead of buying or withdrawing twice.
        [JsonProperty("op", NullValueHandling = NullValueHandling.Ignore)]
        public string OpId { get; set; }

        private JObject _data;
        private object  _raw;
        private bool    _outbound;

        [JsonProperty("data")]
        public JObject Data
        {
            get { if (_data == null && _outbound) _data = _raw == null ? new JObject() : JObject.FromObject(_raw); return _data; }
            set { _data = value; _outbound = false; }
        }

        public KmhEnvelope() { }

        public KmhEnvelope(string kind, object data, int version = -1)
        {
            Kind     = kind;
            Version  = version < 0 ? KmhProtocol.CurrentVersion : version;
            _raw     = data;
            _outbound = true;
        }

        // Inbound-shaped on purpose, so a test can tell an absent key apart from a sent false.
        internal static KmhEnvelope ParseForTest(object payload)
            => new KmhEnvelope { Kind = "test", Version = KmhProtocol.CurrentVersion, Data = JObject.FromObject(payload) };

        // A null payload must serialize as "data":{}, never "data":null: the wire contract both sides parse.
        private static readonly object EmptyData = new object();

        public string Serialize()
            => _outbound
                ? JsonConvert.SerializeObject(new Wire { Kind = Kind, Version = Version, OpId = OpId, Data = _raw ?? EmptyData })
                : JsonConvert.SerializeObject(this);

        // UTF8(Serialize()) in one allocation, with `headroom` zero bytes in front for the frame header.
        [System.ThreadStatic] private static System.Text.StringBuilder _scratchText;
        [System.ThreadStatic] private static System.Text.Encoder _scratchEncoder;
        private static readonly JsonSerializer WireSerializer = JsonSerializer.CreateDefault();
        private static readonly System.Text.UTF8Encoding Utf8 = new System.Text.UTF8Encoding(false);
        // Cap on the buffer one pool thread keeps between frames; larger snapshots allocate their own.
        private const int KeepChars = 128 * 1024;

        internal byte[] SerializeUtf8(int headroom)
        {
            System.Text.StringBuilder sb = _scratchText ?? new System.Text.StringBuilder(8 * 1024);
            _scratchText = null;   // held by this call until it finishes cleanly
            sb.Clear();
            using (var sw = new System.IO.StringWriter(sb, System.Globalization.CultureInfo.InvariantCulture))
            using (var jw = new JsonTextWriter(sw))
                WireSerializer.Serialize(jw, _outbound
                    ? new Wire { Kind = Kind, Version = Version, OpId = OpId, Data = _raw ?? EmptyData }
                    : (object)this);

            // Per-chunk counting reads a pair split across chunks as two 3-byte replacements, not one 4-byte character.
            int count = 0;
            char last = '\0';
            foreach (System.ReadOnlyMemory<char> chunk in sb.GetChunks())
            {
                System.ReadOnlySpan<char> span = chunk.Span;
                if (span.Length == 0) continue;
                count += Utf8.GetByteCount(span);
                if (char.IsHighSurrogate(last) && char.IsLowSurrogate(span[0])) count -= 2;
                last = span[span.Length - 1];
            }

            // One encoder across the chunks, so that split pair is written as the one character it is.
            System.Text.Encoder enc = _scratchEncoder ?? (_scratchEncoder = Utf8.GetEncoder());
            enc.Reset();
            byte[] bytes = new byte[headroom + count];
            int n = headroom;
            foreach (System.ReadOnlyMemory<char> chunk in sb.GetChunks())
                n += enc.GetBytes(chunk.Span, new System.Span<byte>(bytes, n, bytes.Length - n), flush: false);
            n += enc.GetBytes(System.ReadOnlySpan<char>.Empty, new System.Span<byte>(bytes, n, bytes.Length - n), flush: true);
            if (n != bytes.Length) throw new System.InvalidOperationException($"UTF-8 length {n - headroom} differs from its count {count}");

            if (sb.Capacity <= KeepChars) _scratchText = sb;
            return bytes;
        }

        private sealed class Wire
        {
            [JsonProperty("kind")] public string Kind { get; set; }
            [JsonProperty("v")]    public int    Version { get; set; }
            [JsonProperty("op", NullValueHandling = NullValueHandling.Ignore)] public string OpId { get; set; }
            [JsonProperty("data")] public object Data { get; set; }
        }

        public static KmhEnvelope TryParse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<KmhEnvelope>(json);
            }
            catch
            {
                return null;
            }
        }

        public int GetInt(string key, int defaultValue = 0)
        {
            if (Data == null || Data[key] == null) return defaultValue;
            try { return Data.Value<int>(key); } catch { return defaultValue; }
        }

        public long GetLong(string key, long defaultValue = 0)
        {
            if (Data == null || Data[key] == null) return defaultValue;
            try { return Data.Value<long>(key); } catch { return defaultValue; }
        }

        public string GetString(string key, string defaultValue = null)
        {
            if (Data == null || Data[key] == null) return defaultValue;
            try { return Data.Value<string>(key); } catch { return defaultValue; }
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            if (Data == null || Data[key] == null) return defaultValue;
            try { return Data.Value<bool>(key); } catch { return defaultValue; }
        }

        public T DataAs<T>() where T : class
        {
            if (Data == null) return null;
            try { return Data.ToObject<T>(); } catch { return null; }
        }

        // A malformed array must read as empty, never throw: these run on the network thread.
        public int[] GetIntArray(string key)
        {
            if (Data == null || !(Data[key] is JArray arr)) return new int[0];
            try
            {
                var outArr = new int[arr.Count];
                for (int i = 0; i < arr.Count; i++) outArr[i] = arr[i].Value<int>();
                return outArr;
            }
            catch { return new int[0]; }
        }

        public System.Collections.Generic.Dictionary<string, int> GetIntMap(string key)
        {
            var outMap = new System.Collections.Generic.Dictionary<string, int>();
            if (Data == null || !(Data[key] is JObject obj)) return outMap;
            try
            {
                foreach (JProperty p in obj.Properties())
                    if (p.Value != null && p.Value.Type != JTokenType.Null) outMap[p.Name] = p.Value.Value<int>();
            }
            catch { }
            return outMap;
        }

        // Bounded: an edit map arrives from a client, and an unbounded one would be a packet that allocates freely.
        public System.Collections.Generic.Dictionary<string, string> GetStringMap(string key, int maxEntries = 256)
        {
            var outMap = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            if (Data == null || !(Data[key] is JObject obj)) return outMap;
            try
            {
                foreach (JProperty p in obj.Properties())
                {
                    if (outMap.Count >= maxEntries) break;
                    if (p.Value == null || p.Value.Type == JTokenType.Null) continue;
                    outMap[p.Name] = p.Value.Value<string>() ?? "";
                }
            }
            catch { }
            return outMap;
        }

        public System.Collections.Generic.List<string> GetStringList(string key)
        {
            System.Collections.Generic.List<string> outList = new System.Collections.Generic.List<string>();
            if (Data == null || Data[key] == null) return outList;
            try
            {
                foreach (JToken t in Data[key])
                    if (t != null && t.Type != JTokenType.Null) outList.Add(t.Value<string>());
            }
            catch { }
            return outList;
        }
    }
}
