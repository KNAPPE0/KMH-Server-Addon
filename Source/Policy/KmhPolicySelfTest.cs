using System.Collections.Generic;
using K = KMHServerAddon.Policy.KmhPolicyKeys;

namespace KMHServerAddon.Policy
{
    internal static class KmhPolicySelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var hardcore = KmhPolicyBook.Resolve(K.System.Marketplace, KmhProfiles.Hardcore);
            r.Add(("Policy: profile overrides default",
                (double)hardcore.Get(K.FeePercent) == 12.0 && hardcore.OriginOf(K.FeePercent) == ValueOrigin.Profile,
                $"marketplace fee {hardcore.Get(K.FeePercent)} from {hardcore.OriginOf(K.FeePercent)}"));

            var balanced = KmhPolicyBook.Resolve(K.System.Marketplace, KmhProfiles.Balanced);
            r.Add(("Policy: balanced uses default fee",
                (double)balanced.Get(K.FeePercent) == 5.0 && balanced.OriginOf(K.FeePercent) == ValueOrigin.Default,
                $"fee {balanced.Get(K.FeePercent)} from {balanced.OriginOf(K.FeePercent)}"));

            var owner = new Dictionary<string, object> { [K.FeePercent] = 2.0 };
            var withOwner = KmhPolicyBook.Resolve(K.System.Marketplace, KmhProfiles.Hardcore, owner);
            r.Add(("Policy: owner overrides profile",
                (double)withOwner.Get(K.FeePercent) == 2.0 && withOwner.OriginOf(K.FeePercent) == ValueOrigin.Owner,
                $"fee {withOwner.Get(K.FeePercent)} from {withOwner.OriginOf(K.FeePercent)}"));

            var badOwner = new Dictionary<string, object> { [K.FeePercent] = 500.0 };
            var clamped = KmhPolicyBook.Resolve(K.System.Marketplace, KmhProfiles.Balanced, badOwner);
            r.Add(("Policy: safety clamps unsafe owner value",
                (double)clamped.Get(K.FeePercent) == 50.0 && clamped.OriginOf(K.FeePercent) == ValueOrigin.Safety,
                $"fee {clamped.Get(K.FeePercent)} from {clamped.OriginOf(K.FeePercent)}"));

            var okOwner = new Dictionary<string, object> { [K.FeePercent] = 10.0 };
            var notClamped = KmhPolicyBook.Resolve(K.System.Marketplace, KmhProfiles.Balanced, okOwner);
            r.Add(("Policy: safety leaves in-range owner value alone",
                (double)notClamped.Get(K.FeePercent) == 10.0 && notClamped.OriginOf(K.FeePercent) == ValueOrigin.Owner,
                $"fee {notClamped.Get(K.FeePercent)} from {notClamped.OriginOf(K.FeePercent)}"));

            var mig = new Dictionary<string, object> { [K.CooldownSeconds] = 120 };
            var own = new Dictionary<string, object> { [K.CooldownSeconds] = 30 };
            var both = KmhPolicyBook.Resolve(K.System.Marketplace, KmhProfiles.Balanced, own, mig);
            r.Add(("Policy: owner beats migration",
                (int)both.Get(K.CooldownSeconds) == 30 && both.OriginOf(K.CooldownSeconds) == ValueOrigin.Owner,
                $"cooldown {both.Get(K.CooldownSeconds)} from {both.OriginOf(K.CooldownSeconds)}"));

            var migOnly = KmhPolicyBook.Resolve(K.System.Marketplace, KmhProfiles.Balanced, null, mig);
            r.Add(("Policy: migration kept when owner silent",
                (int)migOnly.Get(K.CooldownSeconds) == 120 && migOnly.OriginOf(K.CooldownSeconds) == ValueOrigin.Migration,
                $"cooldown {migOnly.Get(K.CooldownSeconds)} from {migOnly.OriginOf(K.CooldownSeconds)}"));

            var guild = KmhPolicyBook.Resolve(K.System.GuildTreasury, KmhProfiles.Balanced);
            r.Add(("Policy: system-specific default (guild access)",
                (string)guild.Get(K.Access) == KmhAccessMode.GuildOnly,
                $"access {guild.Get(K.Access)}"));

            var unknown = KmhPolicyBook.Resolve(K.System.Marketplace, "Nonsense");
            r.Add(("Policy: unknown profile falls back to Balanced",
                (double)unknown.Get(K.FeePercent) == 5.0,
                $"fee {unknown.Get(K.FeePercent)}"));

            var report = KmhPolicyReport.Describe(KmhProfiles.Hardcore);
            int systemLines = report.FindAll(l => l.StartsWith("  ") && l.EndsWith(":")).Count;
            bool reportOk = systemLines == 10
                && report.Exists(l => l.Contains(K.FeePercent) && l.Contains("[Profile]"))
                && report.Exists(l => l.Contains("[Default]"));
            r.Add(("Policy: report shows 10 systems with origins", reportOk, $"{systemLines} systems listed"));

            bool coerceFee  = KmhPolicyStore.TryCoerce(K.System.Marketplace, K.FeePercent, "3.5", out object fv, out _) && fv is double d35 && d35 == 3.5;
            bool coerceBool = KmhPolicyStore.TryCoerce(K.System.Marketplace, K.AllowRemote, "no", out object bv, out _) && bv is bool bb && bb == false;
            bool coerceRoundTrip = KmhPolicyStore.TryCoerce(K.System.Marketplace, K.AllowRemote, "True", out object rv, out _) && rv is bool rb && rb == true;
            bool rejectKey  = !KmhPolicyStore.TryCoerce(K.System.Marketplace, "bogusKey", "1", out _, out _);
            bool rejectVal  = !KmhPolicyStore.TryCoerce(K.System.Marketplace, K.FeePercent, "notanumber", out _, out _);
            r.Add(("Policy: store coerces + validates types", coerceFee && coerceBool && coerceRoundTrip && rejectKey && rejectVal, "double/bool/roundtrip ok, bad key+value rejected"));

            KmhPolicyStore.ClearSystem(K.System.Auctions);
            KmhPolicyStore.Set(K.System.Auctions, K.FeePercent, "1.5", out _);
            var withStore = KmhPolicyBook.Resolve(K.System.Auctions, KmhProfiles.Balanced, KmhPolicyStore.Overrides(K.System.Auctions) as Dictionary<string, object>);
            bool setOk = (double)withStore.Get(K.FeePercent) == 1.5 && withStore.OriginOf(K.FeePercent) == ValueOrigin.Owner;
            KmhPolicyStore.Clear(K.System.Auctions, K.FeePercent);
            var afterClear = KmhPolicyBook.Resolve(K.System.Auctions, KmhProfiles.Balanced, KmhPolicyStore.Overrides(K.System.Auctions) as Dictionary<string, object>);
            bool clearOk = (double)afterClear.Get(K.FeePercent) == 5.0 && afterClear.OriginOf(K.FeePercent) == ValueOrigin.Default;
            r.Add(("Policy: store override sets then clears", setOk && clearOk, "owner override applied then reverted"));

            return r;
        }
    }
}
