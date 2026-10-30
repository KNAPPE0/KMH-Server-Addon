using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Mods;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // RWT's server never runs RimWorld: Mods.json RimWorldVersion states it, and RWT 26.10.20.1 and later admit only 1.7.
    internal static class KmhGameVersionSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Game version: a server that says nothing is the version its RWT admits - 1.7 from 26.10.20.1, 1.6 before", () =>
            {
                string expected = ModPolicyConfig.DefaultFor(ModPolicyConfig.RunningRwtVersion());
                var fresh = new ModPolicyConfig();
                var old = Newtonsoft.Json.JsonConvert.DeserializeObject<ModPolicyConfig>(
                    "{\"SchemaVersion\":1,\"ExpansionsOptional\":false,\"Optional\":[],\"Required\":[]}");
                bool ok = fresh.RimWorldVersion == expected && fresh.GameVersion == expected && old.RimWorldVersion == expected
                       && ModPolicyConfig.DefaultFor("26.10.20.1") == "1.7" && ModPolicyConfig.DefaultFor("26.10.15.1") == "1.6"
                       && ModPolicyConfig.DefaultFor("") == "1.6" && ModPolicyConfig.DefaultFor("1.0.0+5f6851fe") == "1.6";
                return (ok, $"RWT '{ModPolicyConfig.RunningRwtVersion()}': fresh={fresh.RimWorldVersion} old file={old.RimWorldVersion}");
            });

            Check("Game version: a setting below what RWT admits is read as RWT's version, since it would refuse every player", () =>
            {
                bool ok = ModPolicyConfig.GameVersionFor("1.6", "26.10.20.1") == "1.7" && ModPolicyConfig.GameVersionFor("", "26.10.20.1") == "1.7"
                       && ModPolicyConfig.GameVersionFor("1.6", "26.10.20.1_(1)") == "1.7" && ModPolicyConfig.GameVersionFor("1.6", "26.11.1.1") == "1.7"
                       && ModPolicyConfig.GameVersionFor("1.6", "27.1.1.1") == "1.7" && ModPolicyConfig.GameVersionFor("1.8", "26.10.20.1") == "1.8"
                       && ModPolicyConfig.GameVersionFor("1.6", "26.10.20.0") == "1.6" && ModPolicyConfig.GameVersionFor("1.6", "26.10.15.1") == "1.6"
                       && ModPolicyConfig.GameVersionFor("1.6", "26.9.99.99") == "1.6" && ModPolicyConfig.GameVersionFor("1.7", "26.10.15.1") == "1.7"
                       && ModPolicyConfig.GameVersionFor("1.6", "") == "1.6" && ModPolicyConfig.GameVersionFor("1.6", "1.0.0+5f6851fe") == "1.6";
                return (ok, $"'1.6' on RWT 26.10.20.1 -> {ModPolicyConfig.GameVersionFor("1.6", "26.10.20.1")}");
            });

            Check("Game version: on RWT 26.10.20.1 a new or stale Mods.json serves 1.7 and the listing says so; on 26.10.15.1 it stays 1.6", () =>
            {
                ModPolicyConfig cfg = ModPolicyConfig.Current;
                string was = cfg.RimWorldVersion;
                try
                {
                    ModPolicyConfig.RwtVersionForTest = "26.10.20.1";
                    cfg.RimWorldVersion = "1.6";
                    string fresh17 = new ModPolicyConfig().RimWorldVersion, stale17 = cfg.GameVersion;
                    string listed17 = Features.Global.GlobalStatusProbe.RimWorldVersion();
                    ModPolicyConfig.RwtVersionForTest = "26.10.15.1";
                    string fresh16 = new ModPolicyConfig().RimWorldVersion, stale16 = cfg.GameVersion;
                    bool ok = fresh17 == "1.7" && stale17 == "1.7" && listed17 == "1.7" && fresh16 == "1.6" && stale16 == "1.6";
                    return (ok, $"26.10.20.1: new {fresh17}, stale 1.6 -> {stale17}, listed {listed17}; 26.10.15.1: new {fresh16}, stale {stale16}");
                }
                finally { ModPolicyConfig.RwtVersionForTest = null; cfg.RimWorldVersion = was; }
            });

            Check("Game version: the owner's value is read as major.minor, and a value that is not a version is the default", () =>
            {
                bool ok = ModPolicyConfig.GameVersionOf("1.7") == "1.7" && ModPolicyConfig.GameVersionOf("1.7.4976 rev1043") == "1.7"
                       && ModPolicyConfig.GameVersionOf(" 1.10 ") == "1.10" && ModPolicyConfig.GameVersionOf("1.6") == "1.6"
                       && ModPolicyConfig.GameVersionOf("") == "1.6" && ModPolicyConfig.GameVersionOf(null) == "1.6"
                       && ModPolicyConfig.GameVersionOf("latest") == "1.6" && ModPolicyConfig.GameVersionOf("17") == "1.6"
                       && ModPolicyConfig.GameVersionOf("1.700000") == "1.6";
                return (ok, $"'1.7.4976 rev1043'->{ModPolicyConfig.GameVersionOf("1.7.4976 rev1043")} 'latest'->{ModPolicyConfig.GameVersionOf("latest")}");
            });

            Check("Handshake: a client on another RimWorld version is refused; the same version or none at all is not", () =>
            {
                bool ok = KmhHandshakeHandler.GameVersionDiffers("1.7.4976 rev1043", "1.6")
                       && KmhHandshakeHandler.GameVersionDiffers("1.6.4871 rev591", "1.7")
                       && !KmhHandshakeHandler.GameVersionDiffers("1.6.4871 rev591", "1.6")
                       && !KmhHandshakeHandler.GameVersionDiffers("1.7", "1.7")
                       && !KmhHandshakeHandler.GameVersionDiffers(null, "1.6")
                       && !KmhHandshakeHandler.GameVersionDiffers("", "1.6")
                       && !KmhHandshakeHandler.GameVersionDiffers("<b>1.7</b>", "1.6");
                return (ok, "");
            });

            Check("Older clients: a KMH that states no version is a 1.6 client - kept by a 1.6 server, refused by any other", () =>
            {
                bool ok = !KmhHandshakeHandler.RefusesGame(null, "1.6") && !KmhHandshakeHandler.RefusesGame("", "1.6")
                       && KmhHandshakeHandler.RefusesGame(null, "1.7") && KmhHandshakeHandler.RefusesGame("", "1.7")
                       && KmhHandshakeHandler.RefusesGame("<b>x</b>", "1.7") && !KmhHandshakeHandler.RefusesGame("<b>x</b>", "1.6")
                       && KmhHandshakeHandler.StatedOrPresumed(null) == "1.6"
                       && KmhHandshakeHandler.StatedOrPresumed("1.7.4976 rev1043") == "1.7.4976 rev1043";
                return (ok, "");
            });

            Check("Older clients: a stated version is judged both ways, and a supported 1.6 client is never refused", () =>
            {
                bool ok = KmhHandshakeHandler.RefusesGame("1.7.4976 rev1043", "1.6") && KmhHandshakeHandler.RefusesGame("1.6.4871 rev591", "1.7")
                       && !KmhHandshakeHandler.RefusesGame("1.6.4871 rev591", "1.6") && !KmhHandshakeHandler.RefusesGame("1.6.4633", "1.6")
                       && !KmhHandshakeHandler.RefusesGame("1.7.4976 rev1043", "1.7");
                return (ok, "");
            });

            Check("Listing: the version Global is told is the configured one, never a constant", () =>
            {
                // Set to something other than the default, or a hard-coded "1.6" would pass too.
                ModPolicyConfig cfg = ModPolicyConfig.Current;
                string was = cfg.RimWorldVersion;
                try
                {
                    cfg.RimWorldVersion = "1.7.4976 rev1043";
                    string listed = Features.Global.GlobalStatusProbe.RimWorldVersion();
                    cfg.RimWorldVersion = "1.8";
                    string higher = Features.Global.GlobalStatusProbe.RimWorldVersion();
                    return (listed == "1.7" && higher == "1.8", $"configured '1.7.4976 rev1043' -> listed {listed}; '1.8' -> {higher}");
                }
                finally { cfg.RimWorldVersion = was; }
            });

            return r;
        }
    }
}
