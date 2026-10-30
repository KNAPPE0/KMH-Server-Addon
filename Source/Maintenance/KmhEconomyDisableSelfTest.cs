using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Economy;

namespace KMHServerAddon.Maintenance
{
    // The guild panel and the access check must both read the resolved policy, or a preset disagrees with its UI.
    internal static class KmhEconomyDisableSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            foreach (string mode in new[] { "Standard", "Balanced", "Localized", "Hardcore", "Custom" })
            {
                string captured = mode;
                Check($"Economy: Disabled guild treasury holds under EconomyMode={captured}", () =>
                {
                    EconomyPolicy p = new EconomyConfig
                    {
                        EconomyMode = captured,
                        DisableGuildTreasury = true,
                    }.ResolvePolicy();
                    bool ok = string.Equals(p.GuildAccess, "Disabled", StringComparison.OrdinalIgnoreCase);
                    return (ok, ok ? "the kill switch wins over the preset" : $"preset re-enabled it as '{p.GuildAccess}'");
                });

                Check($"Economy: Disabled personal treasury holds under EconomyMode={captured}", () =>
                {
                    EconomyPolicy p = new EconomyConfig
                    {
                        EconomyMode = captured,
                        DisablePersonalTreasury = true,
                    }.ResolvePolicy();
                    bool ok = string.Equals(p.PersonalAccess, "Disabled", StringComparison.OrdinalIgnoreCase);
                    return (ok, ok ? "the kill switch wins over the preset" : $"preset re-enabled it as '{p.PersonalAccess}'");
                });
            }

            Check("Economy: a preset still sets access when nothing is disabled", () =>
            {
                EconomyPolicy loc = new EconomyConfig { EconomyMode = "Localized" }.ResolvePolicy();
                EconomyPolicy std = new EconomyConfig { EconomyMode = "Standard" }.ResolvePolicy();
                // The override must not flatten every mode into one; only an explicit Disabled changes anything.
                bool ok = loc.GuildAccess == "GuildHallRequired" && loc.PersonalAccess == "ColonyOnly"
                       && std.GuildAccess == "Remote" && std.PersonalAccess == "Remote";
                return (ok, ok ? "presets still decide access" : $"localized={loc.PersonalAccess}/{loc.GuildAccess} standard={std.PersonalAccess}/{std.GuildAccess}");
            });

            Check("Economy: a misspelled EconomyMode runs the Balanced default, not the permissive Standard", () =>
            {
                EconomyConfig typo = new EconomyConfig { EconomyMode = "Balancd" };
                EconomyPolicy raw = typo.ResolvePolicy();
                EconomyConfig.ClampForTest(typo);
                EconomyPolicy loaded = typo.ResolvePolicy();
                bool ok = raw.Mode == "Balanced" && loaded.Mode == "Balanced" && typo.EconomyMode == "Balanced";
                return (ok, $"unloaded {raw.Mode}, loaded {loaded.Mode}, stored as {typo.EconomyMode}");
            });

            Check("Economy: a preset does not destroy remembered Custom tuning", () =>
            {
                // A preset decides effective policy; switching back to Custom must return the owner's own numbers.
                EconomyConfig cfg = new EconomyConfig
                {
                    EconomyMode = "Custom",
                    Custom = new CustomEconomyRules
                    {
                        DepositFeePercent = 7.5, MaxGuildSilver = 123456, WithdrawCooldownSeconds = 42,
                    },
                };
                EconomyPolicy custom = cfg.ResolvePolicy();

                cfg.EconomyMode = "Balanced";
                EconomyPolicy balanced = cfg.ResolvePolicy();

                cfg.EconomyMode = "Custom";
                EconomyPolicy back = cfg.ResolvePolicy();

                bool presetApplied = Math.Abs(balanced.DepositFeePct - 1.0) < 0.001 && balanced.MaxGuildSilver == 0;
                bool restored = Math.Abs(back.DepositFeePct - 7.5) < 0.001 && back.MaxGuildSilver == 123456
                             && back.WithdrawCooldownSec == 42;
                bool fieldsIntact = Math.Abs(cfg.Custom.DepositFeePercent - 7.5) < 0.001;
                bool ok = presetApplied && restored && fieldsIntact
                       && Math.Abs(custom.DepositFeePct - 7.5) < 0.001;
                return (ok, ok
                    ? "the preset decided policy without touching the stored Custom values"
                    : $"presetApplied={presetApplied} restored={restored} fieldsIntact={fieldsIntact}");
            });

            Check("Economy: gameplay and the guild panel read one effective answer", () =>
            {
                EconomyConfig prev = EconomyConfig.ApplyForTest(new EconomyConfig
                {
                    EconomyMode = "Hardcore",
                    DisableGuildTreasury = true,
                });
                try
                {
                    string effective = Features.Economy.EconomyAccess.Policy.GuildAccess;
                    bool ok = string.Equals(effective, "Disabled", StringComparison.OrdinalIgnoreCase);
                    return (ok, ok ? "one authority for the effective access rule" : $"effective='{effective}'");
                }
                finally { EconomyConfig.ApplyForTest(prev); }
            });

            Check("Economy: disabling one treasury leaves the other alone", () =>
            {
                EconomyPolicy p = new EconomyConfig
                {
                    EconomyMode = "Localized",
                    DisableGuildTreasury = true,
                }.ResolvePolicy();
                bool ok = p.GuildAccess == "Disabled" && p.PersonalAccess == "ColonyOnly";
                return (ok, ok ? "guild off, personal untouched" : $"personal={p.PersonalAccess} guild={p.GuildAccess}");
            });

            return r;
        }
    }
}
