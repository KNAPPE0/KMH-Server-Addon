using System.Collections.Generic;

namespace KMHServerAddon.Items
{
    // Clone correctness is item-loss-critical: a dropped field or a shared Warnings list corrupts stored goods.
    internal static class KmhItemServiceSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var src = new KmhThingPayload
            {
                SchemaVersion = 2, DefName = "MeleeWeapon_LongSword", StuffDefName = "Steel", StackCount = 1,
                HitPoints = 90, MaxHitPoints = 100, Quality = 5, Tainted = false, ScribeXml = "<blob/>",
                Fidelity = "full", DisplayLabel = "Steel longsword", MarketValue = 220L, Fingerprint = "fp1",
                Legacy = false, Warnings = new List<string> { "w1" }, Mergeable = false, Splittable = true,
                RotProgressTicks = 0,
            };

            var clone = KmhItemService.ClonePayload(src, 7);
            bool fields = clone.SchemaVersion == src.SchemaVersion
                && clone.DefName == src.DefName && clone.StuffDefName == src.StuffDefName
                && clone.HitPoints == src.HitPoints && clone.MaxHitPoints == src.MaxHitPoints
                && clone.Quality == src.Quality && clone.Tainted == src.Tainted
                && clone.ScribeXml == src.ScribeXml && clone.Fidelity == src.Fidelity
                && clone.DisplayLabel == src.DisplayLabel && clone.MarketValue == src.MarketValue
                && clone.Fingerprint == src.Fingerprint && clone.Legacy == src.Legacy
                && clone.Mergeable == src.Mergeable && clone.Splittable == src.Splittable
                && clone.RotProgressTicks == src.RotProgressTicks;
            r.Add(("Item: clone preserves all state", fields && clone.StackCount == 7,
                fields ? "count set to 7, all 18 fields copied" : $"a field was dropped (splittable={clone.Splittable})"));

            clone.Warnings.Add("w2");
            r.Add(("Item: clone deep-copies Warnings",
                src.Warnings.Count == 1 && clone.Warnings.Count == 2, $"src={src.Warnings.Count} clone={clone.Warnings.Count}"));

            var noWarn = new KmhThingPayload { DefName = "Steel", Warnings = null };
            var nwClone = KmhItemService.ClonePayload(noWarn, 3);
            r.Add(("Item: null Warnings clones to empty", nwClone.Warnings != null && nwClone.Warnings.Count == 0, "no NRE"));

            var stripped = KmhItemService.CloneWithoutBlob(src);
            r.Add(("Item: CloneWithoutBlob drops blob only",
                stripped.ScribeXml == "" && stripped.DefName == src.DefName && stripped.StackCount == src.StackCount,
                "blob gone, rest intact"));

            bool plan =
                KmhItemService.PlanTake(10, 10, false) == KmhItemService.TakeKind.Whole &&
                KmhItemService.PlanTake(10, 20, false) == KmhItemService.TakeKind.Whole &&
                KmhItemService.PlanTake(10, 4, true)  == KmhItemService.TakeKind.Split &&
                KmhItemService.PlanTake(10, 4, false) == KmhItemService.TakeKind.Skip  &&
                KmhItemService.PlanTake(10, 0, true)  == KmhItemService.TakeKind.Skip;
            r.Add(("Item: PlanTake whole/split/skip", plan, "all four cases correct"));

            Split(r);
            Fingerprint(r);
            Merge(r);
            return r;
        }

        private static KmhThingPayload Pay(string def, string stuff = "", int q = 0, int hp = -1, bool tainted = false, string blob = "", bool mergeable = false)
            => new KmhThingPayload { DefName = def, StuffDefName = stuff, Quality = q, HitPoints = hp, MaxHitPoints = 100, Tainted = tainted, ScribeXml = blob, Mergeable = mergeable, StackCount = 1 };

        private static void Split(List<(string, bool, string)> r)
        {
            // Scribed but vouched splittable: the modded-stack shape that divides without being mergeable.
            var vault = new List<KmhThingPayload>
            {
                new KmhThingPayload
                {
                    DefName = "WoodPlank", StackCount = 75, ScribeXml = "<blob/>", Fingerprint = "fp-split",
                    Splittable = true, Mergeable = false,
                },
            };

            List<KmhThingPayload> taken = KmhPayloadEscrow.PopUnits(vault, 20);
            r.Add(("Split: a stack taken off a splittable stack is itself still splittable",
                taken.Count == 1 && taken[0].Splittable && KmhPayloadEscrow.IsSplittable(taken[0]),
                taken.Count == 1 ? $"splittable={taken[0].Splittable}" : $"{taken.Count} stack(s) returned"));

            r.Add(("Split: dividing a stack neither creates nor loses units",
                KmhPayloadEscrow.TotalUnits(taken) == 20 && KmhPayloadEscrow.TotalUnits(vault) == 55
                && vault[0].Splittable && vault[0].ScribeXml == "<blob/>",
                $"took {KmhPayloadEscrow.TotalUnits(taken)}, left {KmhPayloadEscrow.TotalUnits(vault)}"));

            List<KmhThingPayload> again = KmhPayloadEscrow.PopUnits(taken, 5);
            r.Add(("Split: a stack that has already been split does not become all-or-nothing",
                KmhPayloadEscrow.TotalUnits(again) == 5 && KmhPayloadEscrow.TotalUnits(taken) == 15,
                $"{KmhPayloadEscrow.TotalUnits(again)} taken, {KmhPayloadEscrow.TotalUnits(taken)} left"));
        }

        private static void Fingerprint(List<(string, bool, string)> r)
        {
            var a = Pay("Steel", "", 0, -1);
            r.Add(("Fingerprint: stable for identical input",
                KmhItemSafety.GetStateFingerprint(a) == KmhItemSafety.GetStateFingerprint(Pay("Steel", "", 0, -1)), "same in, same out"));

            string baseFp = KmhItemSafety.GetStateFingerprint(Pay("Plasteel", "", 3, 80, false));
            bool distinct =
                baseFp != KmhItemSafety.GetStateFingerprint(Pay("Steel",    "", 3, 80, false))
                && baseFp != KmhItemSafety.GetStateFingerprint(Pay("Plasteel", "", 5, 80, false))
                && baseFp != KmhItemSafety.GetStateFingerprint(Pay("Plasteel", "", 3, 60, false))
                && baseFp != KmhItemSafety.GetStateFingerprint(Pay("Plasteel", "", 3, 80, true));
            r.Add(("Fingerprint: distinct on any identity change", distinct, "def/quality/hp/taint each differ"));
        }

        private static void Merge(List<(string, bool, string)> r)
        {
            bool notSame =
                !KmhItemMerge.Evaluate(Pay("Longsword", "Steel"),   Pay("Longsword", "Plasteel")).CanMerge &&
                !KmhItemMerge.Evaluate(Pay("Longsword", "Steel", 3),Pay("Longsword", "Steel", 5)).CanMerge &&
                !KmhItemMerge.Evaluate(Pay("Shirt", "", 0, 100, false), Pay("Shirt", "", 0, 100, true)).CanMerge;
            r.Add(("Merge: same def but different identity does not merge", notSame, "material/quality/taint block"));

            var v = KmhItemMerge.Evaluate(Pay("Longsword", "Steel", 4, 90, false, blob: "<x/>"), Pay("Longsword", "Steel", 4, 90, false, blob: "<x/>"));
            r.Add(("Merge: saved-state instance stays unique", !v.CanMerge && v.Reason.Contains("unique"), v.Reason));

            var exact = KmhItemMerge.Evaluate(Pay("Steel", "", 0, -1), Pay("Steel", "", 0, -1));
            r.Add(("Merge: identical plain items exact-stack", exact.Kind == KmhMergeKind.ExactStack, "exact stack"));

            var fung = KmhItemMerge.Evaluate(Pay("Meal", "", 0, 90, false, mergeable: true), Pay("Meal", "", 0, 40, false, mergeable: true));
            r.Add(("Merge: fungible merges across wear", fung.Kind == KmhMergeKind.Fungible, "fungible"));

            var pairs = new[]
            {
                (Pay("Steel"), Pay("Steel")),
                (Pay("Steel"), Pay("Plasteel")),
                (Pay("Meal", "", 0, 90, false, mergeable: true), Pay("Meal", "", 0, 40, false, mergeable: true)),
                (Pay("Sword", "Steel", 4, 90, false, blob: "<x/>"), Pay("Sword", "Steel", 4, 90, false, blob: "<x/>")),
            };
            bool agree = true;
            foreach (var (x, y) in pairs)
            {
                bool boolMerge = KmhItemSafety.CanSafelyMerge(x, y) || KmhItemSafety.CanMergeFungible(x, y);
                if (KmhItemMerge.Evaluate(x, y).CanMerge != boolMerge) agree = false;
            }
            r.Add(("Merge: verdict agrees with boolean checks", agree, "no divergence from KmhItemSafety"));

            var t = Pay("Meal", "", 0, 100, false, mergeable: true); t.StackCount = 3; t.RotProgressTicks = 0;
            var inc = Pay("Meal", "", 0, 40, false, mergeable: true); inc.StackCount = 1; inc.RotProgressTicks = 400;
            KmhItemSafety.MergeFungible(t, inc);
            bool avg = t.StackCount == 4 && t.HitPoints == 85 && t.RotProgressTicks == 100;   // (100*3+40*1)/4=85, (0*3+400*1)/4=100
            r.Add(("Merge: fungible weight-averages wear", avg, $"count {t.StackCount}, hp {t.HitPoints}, rot {t.RotProgressTicks}"));
        }
    }
}
