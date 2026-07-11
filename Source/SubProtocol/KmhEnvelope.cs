using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.SubProtocol
{
    // Wire envelope shared by every KMH message. Mirror of the patch mod's KMHPatch.SubProtocol.KmhEnvelope and the
    // server-fork's prior Shared.SubProtocol.KmhEnvelope. Same JSON property names, same semantics, same typed
    // accessors
    public class KmhEnvelope
    {
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("v")]
        public int Version { get; set; }

        [JsonProperty("data")]
        public JObject Data { get; set; }

        public KmhEnvelope() { }

        public KmhEnvelope(string kind, object data, int version = -1)
        {
            Kind    = kind;
            Version = version < 0 ? KmhProtocol.CurrentVersion : version;
            Data    = data == null ? new JObject() : JObject.FromObject(data);
        }

        public string Serialize() => JsonConvert.SerializeObject(this);

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

        // Typed accessors - same contract as the patch-mod side so handler code looks identical on either end of
        // the wire
        public int GetInt(string key, int defaultValue = 0)
        {
            if (Data == null || Data[key] == null) return defaultValue;
            try { return Data.Value<int>(key); } catch { return defaultValue; }
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

        // Reads a JSON string array field into a list (empty list when absent/malformed).
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
