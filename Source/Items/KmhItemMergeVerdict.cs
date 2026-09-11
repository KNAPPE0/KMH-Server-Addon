namespace KMHServerAddon.Items
{
    // Sharing a defName is not sharing an identity: material, quality, taint, durability and any saved blob separate two items.
    internal enum KmhMergeKind
    {
        Fungible,
        ExactStack,
        No,
    }

    internal readonly struct KmhMergeVerdict
    {
        public readonly KmhMergeKind Kind;
        public readonly string Reason;
        public KmhMergeVerdict(KmhMergeKind kind, string reason) { Kind = kind; Reason = reason; }
        public bool CanMerge => Kind != KmhMergeKind.No;
    }

    internal static class KmhItemMerge
    {
        public static KmhMergeVerdict Evaluate(KmhThingPayload a, KmhThingPayload b)
        {
            if (a == null || b == null) return No("missing payload");

            if (!Eq(a.DefName, b.DefName))           return No("different item");
            if (!Eq(a.StuffDefName, b.StuffDefName))  return No("different material");
            if (a.Quality != b.Quality)               return No("different quality");
            if (a.Tainted != b.Tainted)               return No("different tainted state");

            // Checked before durability: MergeFungible averages wear, so wear is not part of fungible identity.
            if (KmhItemSafety.CanMergeFungible(a, b))
                return new KmhMergeVerdict(KmhMergeKind.Fungible, "");

            bool hasBlob = !string.IsNullOrEmpty(a.ScribeXml) || !string.IsNullOrEmpty(b.ScribeXml);
            if (hasBlob)                              return No("unique instance (has saved per-item state)");
            if (a.HitPoints != b.HitPoints || a.MaxHitPoints != b.MaxHitPoints)
                return No("different durability");

            return new KmhMergeVerdict(KmhMergeKind.ExactStack, "");
        }

        private static KmhMergeVerdict No(string why) => new KmhMergeVerdict(KmhMergeKind.No, why);
        private static bool Eq(string x, string y) => string.Equals(x ?? "", y ?? "", System.StringComparison.OrdinalIgnoreCase);
    }
}
