using KMHServerAddon.Results;

namespace KMHServerAddon.Transactions
{
    // Before implementing: boot recovery cannot refund a Reserved move, so a crash mid-settle strands escrowed value.
    internal interface IKmhValueMove
    {
        string    System { get; }   // a KmhPolicyKeys.System key
        KmhTxType Type   { get; }
        string    Describe();

        // Nothing has reached the destination yet, so returning false here needs no refund.
        bool Reserve(out KmhErrorCode error);

        bool Deliver(out KmhErrorCode error);

        // Called only after a successful Reserve, and must be safe to call more than once.
        void Refund();
    }
}
