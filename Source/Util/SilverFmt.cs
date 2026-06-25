namespace KMHServerAddon.Util
{
    internal static class SilverFmt
    {
        public static string Format(int amount)  => Format((long)amount);
        public static string Format(long amount) => amount < 0
            ? "-$" + (-amount).ToString("N0")
            :  "$" +   amount.ToString("N0");
    }
}
