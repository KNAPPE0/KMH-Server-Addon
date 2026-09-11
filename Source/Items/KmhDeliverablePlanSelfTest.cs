using System.Collections.Generic;

namespace KMHServerAddon.Items
{
    // What a fill charges for and what it hands over agree only while both divide stacks the same way.
    internal static class KmhDeliverablePlanSelfTest
    {
        private static KmhThingPayload P(int stack, bool scribed, bool mergeable)
            => new KmhThingPayload
            {
                DefName = "Thing", StackCount = stack,
                ScribeXml = scribed ? "<xml/>" : "", Mergeable = mergeable,
            };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            r.Add(("Payload: a plain stack splits", KmhPayloadEscrow.IsSplittable(P(5, false, false)), ""));
            r.Add(("Payload: a scribed unique stack does NOT split", !KmhPayloadEscrow.IsSplittable(P(5, true, false)), ""));
            r.Add(("Payload: a scribed but fungible stack splits", KmhPayloadEscrow.IsSplittable(P(5, true, true)), ""));
            r.Add(("Payload: null never splits", !KmhPayloadEscrow.IsSplittable(null), ""));

            var splitNotMerge = new KmhThingPayload { StackCount = 75, ScribeXml = "<blob/>", Splittable = true, Mergeable = false };
            r.Add(("Payload: a stack can be splittable without being mergeable",
                   KmhPayloadEscrow.IsSplittable(splitNotMerge) && !splitNotMerge.Mergeable,
                   "one physical x75 stack must not force a 75-unit withdrawal"));

            var oneStack = new List<KmhThingPayload> { splitNotMerge };
            bool partials = KmhPayloadEscrow.DeliverableUnits(oneStack, 1) == 1
                         && KmhPayloadEscrow.DeliverableUnits(oneStack, 74) == 74
                         && KmhPayloadEscrow.DeliverableUnits(oneStack, 75) == 75
                         && KmhPayloadEscrow.DeliverableUnits(oneStack, 76) == 75;
            r.Add(("Payload: a splittable x75 stack delivers 1, 74, 75 and caps at 75", partials, ""));

            var ten = new List<KmhThingPayload>();
            for (int i = 0; i < 10; i++)
                ten.Add(new KmhThingPayload { StackCount = 75, ScribeXml = "<blob/>", Splittable = true, Mergeable = false });
            r.Add(("Payload: ten splittable x75 stacks deliver exact totals and never more than they hold",
                   KmhPayloadEscrow.DeliverableUnits(ten, 149) == 149
                   && KmhPayloadEscrow.DeliverableUnits(ten, 150) == 150
                   && KmhPayloadEscrow.DeliverableUnits(ten, 750) == 750
                   && KmhPayloadEscrow.DeliverableUnits(ten, 800) == 750, ""));

            // Never loosen this: splitting an unvouched blob would duplicate the saved state.
            r.Add(("Payload: an unvouched unique blob is still atomic",
                   !KmhPayloadEscrow.IsSplittable(new KmhThingPayload { StackCount = 5, ScribeXml = "<blob/>" }), ""));

            KmhThingPayload Bone(string blob) => new KmhThingPayload
            { DefName = "Bone", StackCount = 75, ScribeXml = blob, Splittable = true, Fingerprint = "fp" + blob };

            var vault = new List<KmhThingPayload> { Bone("<a/>"), Bone("<b/>"), Bone("<c/>"), Bone("<d/>") };
            List<KmhThingPayload> shown = Features.Treasury.TreasuryStore.GroupForDisplay(vault);
            r.Add(("Treasury: identical-looking stacks with different saved state show as one row",
                   shown.Count == 1 && shown[0].StackCount == 300,
                   $"{vault.Count} stored rows -> {shown.Count} shown, {(shown.Count > 0 ? shown[0].StackCount : 0)} units"));

            r.Add(("Treasury: grouping for display never alters the stored rows",
                   vault.Count == 4 && vault[0].StackCount == 75 && vault[0].ScribeXml == "<a/>",
                   "the blobs are what a modded item's identity lives in"));

            var planks = new List<KmhThingPayload>
            {
                new KmhThingPayload { DefName = "WoodPlank", StackCount = 75, HitPoints = 150, MaxHitPoints = 150, Fingerprint = "a" },
                new KmhThingPayload { DefName = "WoodPlank", StackCount = 75, HitPoints = 149, MaxHitPoints = 150, Fingerprint = "b" },
                new KmhThingPayload { DefName = "WoodPlank", StackCount = 75, HitPoints = 150, MaxHitPoints = 150, Tainted = true, Fingerprint = "c" },
                new KmhThingPayload { DefName = "WoodPlank", StackCount = 75, HitPoints = 150, MaxHitPoints = 150, ScribeXml = "<x/>", Fingerprint = "d" },
            };
            r.Add(("Treasury: damage, taint and kept-state keep otherwise identical items in separate rows",
                   Features.Treasury.TreasuryStore.GroupForDisplay(planks).Count == 4,
                   "a damaged plank must never be shown as the same row as an undamaged one"));

            r.Add(("Treasury: the same item in different stuff or quality stays separate",
                   Features.Treasury.TreasuryStore.GroupForDisplay(new List<KmhThingPayload>
                   {
                       new KmhThingPayload { DefName = "Bed", StuffDefName = "WoodLog", Fingerprint = "a" },
                       new KmhThingPayload { DefName = "Bed", StuffDefName = "Steel",   Fingerprint = "b" },
                       new KmhThingPayload { DefName = "Bed", StuffDefName = "Steel", Quality = 4, Fingerprint = "c" },
                   }).Count == 3, ""));

            var atomic5 = new List<KmhThingPayload> { P(5, true, false) };
            r.Add(("Deliverable: an atomic stack bigger than the need delivers nothing",
                   KmhPayloadEscrow.DeliverableUnits(atomic5, 3) == 0,
                   $"{KmhPayloadEscrow.DeliverableUnits(atomic5, 3)} unit(s)"));
            r.Add(("Deliverable: the same stack delivers whole when fully owed",
                   KmhPayloadEscrow.DeliverableUnits(atomic5, 5) == 5
                   && KmhPayloadEscrow.DeliverableUnits(atomic5, 9) == 5, ""));

            var split5 = new List<KmhThingPayload> { P(5, false, false) };
            r.Add(("Deliverable: a splittable stack fills partially",
                   KmhPayloadEscrow.DeliverableUnits(split5, 3) == 3, ""));

            // Owed 2: the atomic 4 contributes nothing and the plain 2 covers it, so the answer is 2, not 6 and not 0.
            var mixed = new List<KmhThingPayload> { P(4, true, false), P(2, false, false) };
            r.Add(("Deliverable: a later stack still fills what an atomic one cannot",
                   KmhPayloadEscrow.DeliverableUnits(mixed, 2) == 2,
                   $"{KmhPayloadEscrow.DeliverableUnits(mixed, 2)} unit(s)"));
            r.Add(("Deliverable: never reports more than it was asked for",
                   KmhPayloadEscrow.DeliverableUnits(mixed, 1) <= 1
                   && KmhPayloadEscrow.DeliverableUnits(mixed, 5) <= 5, ""));

            // Replayed independently below instead of calling the planner again, or the check would only prove it agrees with itself.
            var shapes = new List<List<KmhThingPayload>>
            {
                new List<KmhThingPayload> { P(1, false, false) },
                new List<KmhThingPayload> { P(5, true, false) },
                new List<KmhThingPayload> { P(3, true, true), P(4, true, false) },
                new List<KmhThingPayload> { P(2, false, false), P(6, true, false), P(1, true, false) },
                new List<KmhThingPayload> { P(4, true, false), P(4, true, false) },
            };
            bool matches = true; string firstBad = "";
            foreach (List<KmhThingPayload> shape in shapes)
                for (int want = 0; want <= 12; want++)
                {
                    int planned = KmhPayloadEscrow.DeliverableUnits(shape, want);
                    int owed = want, handed = 0;
                    foreach (KmhThingPayload p in shape)
                    {
                        if (owed >= p.StackCount) { handed += p.StackCount; owed -= p.StackCount; }
                        else if (owed > 0 && KmhPayloadEscrow.IsSplittable(p)) { handed += owed; owed = 0; }
                    }
                    if (planned != handed && firstBad.Length == 0)
                    { matches = false; firstBad = $"want={want} planned={planned} handed={handed}"; }
                }
            r.Add(("Deliverable: the plan equals what the delivery loop hands over", matches,
                   firstBad.Length == 0 ? $"{shapes.Count} shapes x 13 quantities agree" : firstBad));

            bool conserves = true;
            foreach (List<KmhThingPayload> shape in shapes)
            {
                int total = 0; foreach (KmhThingPayload p in shape) total += p.StackCount;
                for (int want = 0; want <= 12; want++)
                    if (KmhPayloadEscrow.DeliverableUnits(shape, want) > total) conserves = false;
            }
            r.Add(("Deliverable: never promises more units than were withdrawn", conserves, ""));

            return r;
        }
    }
}
