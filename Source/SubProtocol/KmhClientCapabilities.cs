namespace KMHServerAddon.SubProtocol
{
    // What a CLIENT declares, sent in the hello ack. Not KmhCapabilities: the contract asserts those are advertised.
    internal static class KmhClientCapabilities
    {
        // The client stores queued notices and acknowledges them, so the server can hold them until it hears back.
        public const string DurableNotifications = "durable_notifications";
    }
}
