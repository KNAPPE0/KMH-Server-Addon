using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Mail;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Items;

namespace KMHServerAddon.Maintenance
{
    // A headless server cannot rebuild a Thing, so the deep blob is the only faithful copy and must survive untouched.
    internal static class KmhMailItemStateSelfTest
    {
        private static string User(string tag) => $"selftest_mi{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // Every field a payload carries, all set to something distinguishable from its default.
        private static KmhThingPayload Rich(string fingerprint) => new KmhThingPayload
        {
            SchemaVersion   = KmhThingPayload.CurrentSchema,
            DefName         = "Apparel_PowerArmor",
            StuffDefName    = "Plasteel",
            StackCount      = 1,
            HitPoints       = 137,
            MaxHitPoints    = 200,
            Quality         = 6,
            Tainted         = true,
            ScribeXml       = "<thing Class=\"Apparel\"><def>Apparel_PowerArmor</def><id>Apparel_PowerArmor123</id>"
                            + "<stuff>Plasteel</stuff><health>137</health><quality>Masterwork</quality>"
                            + "<wornByCorpse>True</wornByCorpse><comps><li Class=\"CompBiocodable\">"
                            + "<codedPawnLabel>Rusty</codedPawnLabel></li><li Class=\"CompQuality\" /></comps></thing>",
            Fidelity        = KmhThingPayload.FidelityFull,
            DisplayLabel    = "masterwork plasteel power armor (tainted)",
            MarketValue     = 4211,
            Fingerprint     = fingerprint,
            Legacy          = false,
            Mergeable       = false,
            Splittable      = false,
            RotProgressTicks = 12345,
            Warnings        = new List<string> { "biocoded to Rusty" },
        };

        private static KmhThingPayload Stored(string owner, string fingerprint)
        {
            KmhThingPayload got = null;
            foreach (KmhThingPayload p in TreasuryStore.StoredPayloadsForTest(owner))
                if (p.Fingerprint == fingerprint) got = p;
            return got;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            // Compares every field, so a payload field added without being carried is caught here, not by a player.
            static List<string> Differences(KmhThingPayload a, KmhThingPayload b)
            {
                var diff = new List<string>();
                if (a == null || b == null) { diff.Add("one side is null"); return diff; }
                foreach (System.Reflection.PropertyInfo pi in typeof(KmhThingPayload)
                         .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                    object x = pi.GetValue(a), y = pi.GetValue(b);
                    if (x is List<string> lx && y is List<string> ly)
                    {
                        if (lx.Count != ly.Count) { diff.Add(pi.Name); continue; }
                        for (int i = 0; i < lx.Count; i++) if (lx[i] != ly[i]) { diff.Add(pi.Name); break; }
                        continue;
                    }
                    if (!Equals(x, y)) diff.Add($"{pi.Name}: {x} vs {y}");
                }
                return diff;
            }

            Check("Mail: every field of a full-state item survives escrow and claim untouched", () =>
            {
                string from = User("s"), to = User("r");
                KmhThingPayload original = Rich("fp-armour-1");
                if (!TreasuryStore.DepositPayload(from, Rich("fp-armour-1"), "selftest seed")) return (false, "seed");

                long id = MailStore.Send(from, to, "subject", "body", 0, null,
                    new Dictionary<string, int> { { "fp-armour-1", 1 } }, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);

                if (!MailStore.Claim(to, id, out MailStore.AttachTotals t, out _, out string cwhy))
                    return (false, "claim: " + cwhy);

                KmhThingPayload got = Stored(to, "fp-armour-1");

                List<string> diff = got == null ? new List<string> { "not delivered" } : Differences(original, got);
                TreasuryStore.PurgeOwnerForTest(from); TreasuryStore.PurgeOwnerForTest(to);
                bool ok = t.GearCount == 1 && diff.Count == 0;
                return (ok, ok ? "def, stuff, quality, hit points, taint, rot, blob, warnings - all identical"
                              : "changed in transit: " + string.Join("; ", diff));
            });

            Check("Mail: the deep blob is carried byte for byte, not re-serialised", () =>
            {
                // A server that re-emitted the XML would silently drop any comp shape it does not model.
                string from = User("s"), to = User("r");
                KmhThingPayload original = Rich("fp-armour-2");
                if (!TreasuryStore.DepositPayload(from, Rich("fp-armour-2"), "selftest seed")) return (false, "seed");

                long id = MailStore.Send(from, to, "s", "b", 0, null,
                    new Dictionary<string, int> { { "fp-armour-2", 1 } }, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);
                if (!MailStore.Claim(to, id, out _, out _, out string cwhy)) return (false, "claim: " + cwhy);

                string blob = Stored(to, "fp-armour-2")?.ScribeXml;

                TreasuryStore.PurgeOwnerForTest(from); TreasuryStore.PurgeOwnerForTest(to);
                bool ok = blob == original.ScribeXml;
                return (ok, ok ? $"{blob.Length} character(s) identical, comps included"
                              : $"blob changed: {(blob ?? "(none)").Length} vs {original.ScribeXml.Length} characters");
            });

            Check("Mail: a recalled item comes back with the same state it left with", () =>
            {
                string from = User("s"), to = User("r");
                KmhThingPayload original = Rich("fp-armour-3");
                if (!TreasuryStore.DepositPayload(from, Rich("fp-armour-3"), "selftest seed")) return (false, "seed");

                long id = MailStore.Send(from, to, "s", "b", 0, null,
                    new Dictionary<string, int> { { "fp-armour-3", 1 } }, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);
                if (!MailStore.Recall(from, id, out _, out _, out string rwhy)) return (false, "recall: " + rwhy);

                KmhThingPayload got = Stored(from, "fp-armour-3");

                List<string> diff = got == null ? new List<string> { "not returned" } : Differences(original, got);
                // And the recipient cannot then claim what went home.
                bool claimAfter = MailStore.Claim(to, id, out _, out _, out _);

                TreasuryStore.PurgeOwnerForTest(from); TreasuryStore.PurgeOwnerForTest(to);
                bool ok = diff.Count == 0 && !claimAfter;
                return (ok, ok ? "returned identical, and no longer claimable"
                              : $"differences: {string.Join("; ", diff)}, claimableAfterRecall={claimAfter}");
            });

            Check("Mail: an item whose mod is gone is still carried verbatim, never rewritten", () =>
            {
                // The server has no def database; normalising an unknown def turns a modded item into something else.
                string from = User("s"), to = User("r");
                KmhThingPayload exotic = Rich("fp-modded");
                exotic.DefName = "SomeMod_ThingThatThisServerHasNeverHeardOf";
                exotic.StuffDefName = "SomeMod_ExoticAlloy";
                exotic.Warnings = new List<string> { "def not present on this server" };

                KmhThingPayload copy = Rich("fp-modded");
                copy.DefName = exotic.DefName; copy.StuffDefName = exotic.StuffDefName;
                copy.Warnings = new List<string> { "def not present on this server" };
                if (!TreasuryStore.DepositPayload(from, copy, "selftest seed")) return (false, "seed");

                long id = MailStore.Send(from, to, "s", "b", 0, null,
                    new Dictionary<string, int> { { "fp-modded", 1 } }, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);
                if (!MailStore.Claim(to, id, out _, out _, out string cwhy)) return (false, "claim: " + cwhy);

                KmhThingPayload got = Stored(to, "fp-modded");

                List<string> diff = got == null ? new List<string> { "not delivered" } : Differences(exotic, got);
                TreasuryStore.PurgeOwnerForTest(from); TreasuryStore.PurgeOwnerForTest(to);
                bool ok = diff.Count == 0;
                return (ok, ok ? "an unknown def and stuff pass through unchanged, warnings included"
                              : "the server altered an item it cannot identify: " + string.Join("; ", diff));
            });

            Check("Mail: a payload the server refuses is never escrowed as if it had been taken", () =>
            {
                // Anything ValidatePayload refuses must leave the sender's vault untouched, not half-escrowed.
                string from = User("s"), to = User("r");
                var broken = new KmhThingPayload { DefName = "", StackCount = 1, Fingerprint = "fp-broken" };
                bool refused = !TreasuryStore.DepositPayload(from, broken, "selftest");

                int before = TreasuryStore.StoredPayloadsForTest(from).Count;
                long id = MailStore.Send(from, to, "s", "b", 0, null,
                    new Dictionary<string, int> { { "fp-broken", 1 } }, out _, out _, out string why);
                int after = TreasuryStore.StoredPayloadsForTest(from).Count;

                TreasuryStore.PurgeOwnerForTest(from); TreasuryStore.PurgeOwnerForTest(to);
                bool ok = refused && id == 0 && before == after;
                return (ok, ok ? $"the deposit was refused and the send found nothing to attach ({why})"
                              : $"depositRefused={refused}, sentId={id}, vaultBefore={before}, after={after}");
            });

            Check("Mail: two items that differ only in their blob never merge into one", () =>
            {
                // Merging them would hand one colony two copies of one history and lose the other outright.
                string from = User("s"), to = User("r");
                KmhThingPayload a = Rich("fp-twin-a");
                KmhThingPayload b = Rich("fp-twin-b");
                b.ScribeXml = a.ScribeXml.Replace("Rusty", "Mara");
                if (!TreasuryStore.DepositPayload(from, a, "selftest seed")) return (false, "seed a");
                if (!TreasuryStore.DepositPayload(from, b, "selftest seed")) return (false, "seed b");

                long id = MailStore.Send(from, to, "s", "b", 0, null,
                    new Dictionary<string, int> { { "fp-twin-a", 1 }, { "fp-twin-b", 1 } },
                    out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);
                if (!MailStore.Claim(to, id, out MailStore.AttachTotals t, out _, out string cwhy))
                    return (false, "claim: " + cwhy);

                var blobs = new List<string>();
                foreach (KmhThingPayload p in TreasuryStore.StoredPayloadsForTest(to))
                    if (p.DefName == a.DefName) blobs.Add(p.ScribeXml);

                TreasuryStore.PurgeOwnerForTest(from); TreasuryStore.PurgeOwnerForTest(to);
                bool ok = t.GearCount == 2 && blobs.Count == 2
                       && blobs.Contains(a.ScribeXml) && blobs.Contains(b.ScribeXml);
                return (ok, ok ? "both histories arrived, neither overwritten"
                              : $"gearClaimed={t.GearCount}, distinctBlobs={blobs.Count}");
            });

            Check("Mail: one stack named twice in different case is one stack, so a send and recall cannot double it", () =>
            {
                string from = User("s"), to = User("r");
                int SteelOf(string who)
                {
                    int n = 0;
                    Features.Treasury.Dto.TreasurySnapshot v = TreasuryStore.GetSnapshotFor(who);
                    if (v?.Items != null) foreach (KeyValuePair<string, int> kv in v.Items) if (string.Equals(kv.Key, "Steel", StringComparison.OrdinalIgnoreCase)) n += kv.Value;
                    return n;
                }
                try
                {
                    if (!TreasuryStore.DepositItem(from, "Steel", 10, "selftest seed") || !TreasuryStore.DepositSilver(from, 1000, "selftest seed"))
                        return (false, "seed");
                    long forged = MailStore.Send(from, to, "s", "b", 0, new Dictionary<string, int> { { "Steel", 9 }, { "sTEEL", 9 } }, null,
                                                 out _, out _, out string forgedWhy);
                    long honest = MailStore.Send(from, to, "s", "b", 0, new Dictionary<string, int> { { "Steel", 5 }, { "sTEEL", 4 } }, null,
                                                 out _, out _, out string honestWhy);
                    int whileSent = SteelOf(from);
                    bool recalled = honest != 0 && MailStore.Recall(from, honest, out _, out _, out _);
                    int afterRecall = SteelOf(from);
                    bool bundleRefused = !TreasuryStore.WithdrawItemsForTxn(from, new List<KeyValuePair<string, int>>
                        { new KeyValuePair<string, int>("Steel", 6), new KeyValuePair<string, int>("steel", 6) }, "selftest-case-" + Guid.NewGuid().ToString("N"));
                    Dictionary<string, int> merged = MailStore.SanitizeItems(new Dictionary<string, int> { { "Steel", 4 }, { "sTEEL", 5 } });
                    bool ok = forged == 0 && honest != 0 && whileSent == 1 && recalled && afterRecall == 10 && bundleRefused
                           && merged.Count == 1 && merged["steel"] == 9;
                    return (ok, $"9+9 of 10: {(forged == 0 ? "refused (" + forgedWhy + ")" : "SENT")}; 5+4: {(honest != 0 ? "sent" : honestWhy)}, "
                              + $"left {whileSent}, after recall {afterRecall}; 6+6 of 10 withdraw refused {bundleRefused}; merged to {merged.Count} key(s)");
                }
                finally { TreasuryStore.PurgeOwnerForTest(from); TreasuryStore.PurgeOwnerForTest(to); }
            });

            return r;
        }
    }
}
