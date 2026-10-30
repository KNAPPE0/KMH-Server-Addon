using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    internal enum KmhValidationSeverity { Info, Warning, Error }

    internal readonly struct KmhValidationIssue
    {
        public readonly KmhValidationSeverity Severity;
        public readonly string Where;
        public readonly string Message;
        public KmhValidationIssue(KmhValidationSeverity sev, string where, string message) { Severity = sev; Where = where; Message = message; }
        public override string ToString() => $"[{Severity}] {Where}: {Message}";
    }

    // Reads the RAW on-disk values, because loading already clamped them and a silent correction is what an owner needs told.
    internal static class KmhConfigValidator
    {
        public static List<KmhValidationIssue> CheckEconomy(int marketTaxPercent, double depositFeePercent, double withdrawFeePercent, long maxSilverDepositPerTx)
        {
            var issues = new List<KmhValidationIssue>();
            if (marketTaxPercent < 0 || marketTaxPercent > 100)
                issues.Add(Err("Economy.Marketplace.TaxPercent", $"{marketTaxPercent}% is outside 0-100; it will be corrected."));
            else if (marketTaxPercent > 50)
                issues.Add(Warn("Economy.Marketplace.TaxPercent", $"{marketTaxPercent}% is a very high marketplace tax."));

            foreach ((string name, double fee) in new[] { ("Custom.DepositFeePercent", depositFeePercent), ("Custom.WithdrawFeePercent", withdrawFeePercent) })
                if (fee < 0 || fee > 50)
                    issues.Add(Warn($"Economy.{name}", $"{fee}% is outside the intended 0-50 range."));

            if (maxSilverDepositPerTx < 0)
                issues.Add(Err("Economy.Treasury.MaxSilverDepositPerTx", $"{maxSilverDepositPerTx} is negative."));

            double totalTake = marketTaxPercent + Clamp0(depositFeePercent) + Clamp0(withdrawFeePercent);
            if (totalTake > 60)
                issues.Add(Info("Economy", $"combined tax + treasury fees total ~{totalTake:0}% - the economy is heavily extractive."));

            return issues;
        }

        public static List<KmhValidationIssue> CheckTransport(bool apiEnabled, int port, int maxConnections, int maxConnectionsPerIp)
        {
            var issues = new List<KmhValidationIssue>();
            if (port < 1 || port > 65535)
                issues.Add(Err("Transport.KmhApiPort", $"{port} is not a valid port (1-65535); it will reset to the default."));
            if (maxConnections < 1)
                issues.Add(Err("Transport.Advanced.MaxConnections", $"{maxConnections} must be at least 1."));
            if (maxConnectionsPerIp > maxConnections)
                issues.Add(Warn("Transport.Advanced.MaxConnectionsPerIp", $"per-IP cap ({maxConnectionsPerIp}) exceeds the total cap ({maxConnections}); it will be capped."));
            if (maxConnectionsPerIp < 1)
                issues.Add(Warn("Transport.Advanced.MaxConnectionsPerIp", $"{maxConnectionsPerIp} must be at least 1."));
            if (!apiEnabled)
                issues.Add(Info("Transport", "Colony Nexus API transport is off; clients use the chat carrier only."));
            return issues;
        }

        // Files can each be sane yet contradict one another, so the server does work and then throws it away.
        public static List<KmhValidationIssue> CheckCrossFile(
            bool previewsOn, long chatImageBytes, string[] chatHosts,
            bool resolverOn, long mediaSourceBytes, long mediaOutputBytes, string[] mediaHosts,
            bool videoLinksOn, bool videoServerOn)
        {
            var issues = new List<KmhValidationIssue>();

            if (resolverOn && !previewsOn)
                issues.Add(Warn("Media/Chat", "the media resolver is on but Chat.AllowImagePreviews is off - "
                                            + "every image it converts is discarded before a player sees it."));

            if (resolverOn && mediaOutputBytes > chatImageBytes)
                issues.Add(Warn("Media.Images.MaxOutputBytes", $"{Mb(mediaOutputBytes)} is larger than "
                                            + $"Chat.MaxImageBytes ({Mb(chatImageBytes)}), so the resolver can produce "
                                            + "an image clients are told to refuse."));

            if (resolverOn && mediaSourceBytes < mediaOutputBytes)
                issues.Add(Warn("Media.Images.MaxSourceBytes", $"{Mb(mediaSourceBytes)} is smaller than MaxOutputBytes "
                                            + $"({Mb(mediaOutputBytes)}); the output limit can never be reached."));

            if (videoLinksOn && !videoServerOn)
                issues.Add(Info("Chat.AllowVideoLinks", "video links are allowed but Media.Video.ServerEnabled is off - "
                                            + "they post as plain links rather than playing in game."));

            // An empty resolver allowlist is not "allow nothing", it is "allow any https host" - see KmhMediaFetch.
            bool resolverUnrestricted = mediaHosts == null || mediaHosts.Length == 0;
            if (resolverOn && resolverUnrestricted)
                issues.Add(Info("Media.Fetch.SourceHostAllowList", "empty, so the resolver will fetch from any https host "
                                            + "a player links. List the hosts you trust to narrow what the server "
                                            + "fetches on their behalf."));

            // Chat picks which links may be posted, the resolver which it may fetch; a host in only one never resolves.
            if (resolverOn && previewsOn && !resolverUnrestricted)
            {
                List<string> chatOnly = Missing(chatHosts, mediaHosts);
                if (chatOnly.Count > 0)
                    issues.Add(Warn("Media.Fetch.SourceHostAllowList",
                        $"{chatOnly.Count} host(s) chat accepts cannot be fetched by the resolver ({Join(chatOnly)}), "
                        + "so formats clients cannot decode stay broken from those hosts. Add them to Media.json "
                        + "SourceHostAllowList, or drop them from Chat.json ImageHostAllowList. Chat's "
                        + "AutoAddNewImageHosts grows its list on its own; the resolver's never does, on purpose - "
                        + "it fetches server-side."));
            }

            return issues;
        }

        private static string Mb(long bytes) => $"{bytes / (1024 * 1024)}MB";

        private static string Join(List<string> names)
            => names.Count <= 3 ? string.Join(", ", names)
                                : string.Join(", ", names.GetRange(0, 3)) + $" and {names.Count - 3} more";

        private static List<string> Missing(string[] wanted, string[] present)
        {
            var gaps = new List<string>();
            if (wanted == null) return gaps;
            var have = new HashSet<string>(present ?? new string[0], System.StringComparer.OrdinalIgnoreCase);
            foreach (string h in wanted)
                if (!string.IsNullOrWhiteSpace(h) && !have.Contains(h.Trim())) gaps.Add(h.Trim());
            return gaps;
        }

        // A missing key falls back to the config class's own default, never a literal here that could drift from it.
        public static List<KmhValidationIssue> ValidateFiles()
        {
            var all = new List<KmhValidationIssue>();

            var ecoDef = new Features.Economy.EconomyConfig();
            JObject eco = ReadJson(KmhDataPaths.EconomyConfigFile);
            if (eco != null)
                all.AddRange(CheckEconomy(
                    Int(eco,  "Marketplace.TaxPercent",        ecoDef.Marketplace.TaxPercent),
                    Dbl(eco,  "Custom.DepositFeePercent",      ecoDef.Custom.DepositFeePercent),
                    Dbl(eco,  "Custom.WithdrawFeePercent",     ecoDef.Custom.WithdrawFeePercent),
                    Long(eco, "Treasury.MaxSilverDepositPerTx", ecoDef.Treasury.MaxSilverDepositPerTx)));
            if (eco != null && eco["EconomyMode"] is JToken mode && mode.Type == JTokenType.String
                && Array.FindIndex(Features.Economy.EconomyConfig.ValidEconomyModes, k => string.Equals(k, ((string)mode).Trim(), StringComparison.OrdinalIgnoreCase)) < 0)
                all.Add(Warn("Economy.EconomyMode", $"'{(string)mode}' is not a mode (Balanced, Localized, Hardcore, Standard or Custom); the server runs Balanced."));

            var tpDef = new Features.Transport.TransportConfig();
            JObject tp = ReadJson(KmhDataPaths.TransportConfigFile);
            // Read in the current shape even before the boot migration has rewritten an older file.
            if (tp != null) JsonFileStore.ApplyMoves(tp, Features.Transport.TransportConfig.Moves);
            if (tp != null)
                all.AddRange(CheckTransport(
                    Bool(tp, "EnableKmhApiTransport", tpDef.EnableKmhApiTransport),
                    Int(tp,  "KmhApiPort",            tpDef.KmhApiPort),
                    Int(tp,  "Advanced.MaxConnections",      tpDef.Advanced.MaxConnections),
                    Int(tp,  "Advanced.MaxConnectionsPerIp", tpDef.Advanced.MaxConnectionsPerIp)));

            var chatDef  = new Features.Chat.ChatConfig();
            var mediaDef = new Features.Media.MediaConfig();
            JObject chat  = ReadJson(KmhDataPaths.ChatConfigFile);
            JObject media = ReadJson(KmhDataPaths.MediaConfigFile);
            if (chat != null && media != null)
                all.AddRange(CheckCrossFile(
                    Bool(chat,  "AllowImagePreviews",         chatDef.AllowImagePreviews),
                    Long(chat,  "MaxImageBytes",              chatDef.MaxImageBytes),
                    Strings(chat,  "ImageHostAllowList",      chatDef.ImageHostAllowList),
                    Bool(media, "Images.ResolverEnabled",      mediaDef.Images.ResolverEnabled),
                    Long(media, "Images.MaxSourceBytes",       mediaDef.Images.MaxSourceBytes),
                    Long(media, "Images.MaxOutputBytes",       mediaDef.Images.MaxOutputBytes),
                    Strings(media, "Fetch.SourceHostAllowList", mediaDef.Fetch.SourceHostAllowList),
                    Bool(chat,  "AllowVideoLinks",            chatDef.AllowVideoLinks),
                    Bool(media, "Video.ServerEnabled",         mediaDef.Video.ServerEnabled)));

            return all;
        }

        private static KmhValidationIssue Err(string w, string m)  => new KmhValidationIssue(KmhValidationSeverity.Error, w, m);
        private static KmhValidationIssue Warn(string w, string m) => new KmhValidationIssue(KmhValidationSeverity.Warning, w, m);
        private static KmhValidationIssue Info(string w, string m) => new KmhValidationIssue(KmhValidationSeverity.Info, w, m);
        private static double Clamp0(double v) => v < 0 ? 0 : v;

        private static JObject ReadJson(string path)
        {
            try { return System.IO.File.Exists(path) ? JObject.Parse(System.IO.File.ReadAllText(path)) : null; }
            catch { return null; }   // a malformed file is the load-test's job to report, not this layer's
        }

        // Lets a self-test prove each path still resolves; after a regroup this layer would otherwise read nothing and pass.
        internal static string[] KeysReadFrom(string configLabel)
        {
            switch (configLabel)
            {
                case "Economy":   return new[] { "Marketplace.TaxPercent", "Custom.DepositFeePercent",
                                                 "Custom.WithdrawFeePercent", "Treasury.MaxSilverDepositPerTx" };
                case "Transport": return new[] { "EnableKmhApiTransport", "KmhApiPort",
                                                 "Advanced.MaxConnections", "Advanced.MaxConnectionsPerIp" };
                case "Chat":      return new[] { "AllowImagePreviews", "MaxImageBytes",
                                                 "ImageHostAllowList", "AllowVideoLinks" };
                case "Media":     return new[] { "Images.ResolverEnabled", "Images.MaxSourceBytes",
                                                 "Images.MaxOutputBytes", "Fetch.SourceHostAllowList",
                                                 "Video.ServerEnabled" };
                default:          return System.Array.Empty<string>();
            }
        }

        // SelectToken, not o[k]: grouped keys are PATHS, and a plain index returns null, silently reading the default.
        private static JToken At(JObject o, string k) => o?.SelectToken(k);

        private static int  Int(JObject o, string k, int d)  => At(o, k)?.Type == JTokenType.Integer ? At(o, k).Value<int>()  : d;
        private static long Long(JObject o, string k, long d)=> At(o, k)?.Type == JTokenType.Integer ? At(o, k).Value<long>() : d;
        private static double Dbl(JObject o, string k, double d) => (At(o, k)?.Type == JTokenType.Float || At(o, k)?.Type == JTokenType.Integer) ? At(o, k).Value<double>() : d;
        private static bool Bool(JObject o, string k, bool d) => At(o, k)?.Type == JTokenType.Boolean ? At(o, k).Value<bool>() : d;

        private static string[] Strings(JObject o, string k, string[] d)
        {
            if (At(o, k) is not JArray arr) return d ?? new string[0];
            var list = new List<string>(arr.Count);
            foreach (JToken t in arr)
                if (t.Type == JTokenType.String) list.Add(t.Value<string>());
            return list.ToArray();
        }
    }
}
