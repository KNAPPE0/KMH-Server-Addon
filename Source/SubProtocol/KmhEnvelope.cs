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

        // Names the logical action, not this delivery of it, so a re-sent request is refused instead of buying or withdrawing twice.
        [JsonProperty("op", NullValueHandling = NullValueHandling.Ignore)]
        public string OpId { get; set; }

        private JObject _data;
        private object  _raw;
        private bool    _outbound;   // built for sending: _raw holds the payload; _data is materialized only if accessed

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

        // Non-null sentinel so a null payload serializes as "data":{}, never "data":null - the wire contract both sides parse.
        private static readonly object EmptyData = new object();

        public string Serialize()
            => _outbound
                ? JsonConvert.SerializeObject(new Wire { Kind = Kind, Version = Version, OpId = OpId, Data = _raw ?? EmptyData })
                : JsonConvert.SerializeObject(this);

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
