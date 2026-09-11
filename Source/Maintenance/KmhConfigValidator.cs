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
                issues.Add(Err("Economy.MarketplaceTaxPercent", $"{marketTaxPercent}% is outside 0-100; it will be corrected."));
            else if (marketTaxPercent > 50)
                issues.Add(Warn("Economy.MarketplaceTaxPercent", $"{marketTaxPercent}% is a very high marketplace tax."));

            foreach ((string name, double fee) in new[] { ("TreasuryDepositFeePercent", depositFeePercent), ("TreasuryWithdrawFeePercent", withdrawFeePercent) })
                if (fee < 0 || fee > 50)
                    issues.Add(Warn($"Economy.{name}", $"{fee}% is outside the intended 0-50 range."));

            if (maxSilverDepositPerTx < 0)
                issues.Add(Err("Economy.MaxSilverDepositPerTx", $"{maxSilverDepositPerTx} is negative."));

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
                issues.Add(Err("Transport.MaxConnections", $"{maxConnections} must be at least 1."));
            if (maxConnectionsPerIp > maxConnections)
                issues.Add(Warn("Transport.MaxConnectionsPerIp", $"per-IP cap ({maxConnectionsPerIp}) exceeds the total cap ({maxConnections}); it will be capped."));
            if (maxConnectionsPerIp < 1)
                issues.Add(Warn("Transport.MaxConnectionsPerIp", $"{maxConnectionsPerIp} must be at least 1."));
            if (!apiEnabled)
                issues.Add(Info("Transport", "KMH API transport is off; clients use the chat carrier only."));
            return issues;
        }

        // A missing key falls back to the config class's own default, never a literal here that could drift from it.
        public static List<KmhValidationIssue> ValidateFiles()
        {
            var all = new List<KmhValidationIssue>();

            var ecoDef = new Features.Economy.EconomyConfig();
            JObject eco = ReadJson(KmhDataPaths.EconomyConfigFile);
            if (eco != null)
                all.AddRange(CheckEconomy(
                    Int(eco,  "MarketplaceTaxPercent",      ecoDef.MarketplaceTaxPercent),
                    Dbl(eco,  "TreasuryDepositFeePercent",  ecoDef.TreasuryDepositFeePercent),
                    Dbl(eco,  "TreasuryWithdrawFeePercent", ecoDef.TreasuryWithdrawFeePercent),
                    Long(eco, "MaxSilverDepositPerTx",      ecoDef.MaxSilverDepositPerTx)));

            var tpDef = new Features.Transport.TransportConfig();
            JObject tp = ReadJson(KmhDataPaths.TransportConfigFile);
            if (tp != null)
                all.AddRange(CheckTransport(
                    Bool(tp, "EnableKmhApiTransport", tpDef.EnableKmhApiTransport),
                    Int(tp,  "KmhApiPort",            tpDef.KmhApiPort),
                    Int(tp,  "MaxConnections",        tpDef.MaxConnections),
                    Int(tp,  "MaxConnectionsPerIp",   tpDef.MaxConnectionsPerIp)));

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
        private static int  Int(JObject o, string k, int d)  => o[k]?.Type == JTokenType.Integer ? o[k].Value<int>()  : d;
        private static long Long(JObject o, string k, long d)=> o[k]?.Type == JTokenType.Integer ? o[k].Value<long>() : d;
        private static double Dbl(JObject o, string k, double d) => (o[k]?.Type == JTokenType.Float || o[k]?.Type == JTokenType.Integer) ? o[k].Value<double>() : d;
        private static bool Bool(JObject o, string k, bool d) => o[k]?.Type == JTokenType.Boolean ? o[k].Value<bool>() : d;
    }
}
