namespace KMH.Sdk.Server.Hooks
{
    /// <summary>
    /// A hook's verdict on a pending KMH action: allow it, or deny it with a player-facing reason.
    /// </summary>
    public readonly struct KmhHookVerdict
    {
        /// <summary>True if the action should be blocked.</summary>
        public bool Denied { get; }

        /// <summary>Player-facing reason shown when <see cref="Denied"/> is true; null when allowed.</summary>
        public string Reason { get; }

        private KmhHookVerdict(bool denied, string reason) { Denied = denied; Reason = reason; }

        /// <summary>Permit the action - the default when a hook has no objection.</summary>
        public static KmhHookVerdict Allow { get; } = new KmhHookVerdict(false, null);

        /// <summary>Block the action; <paramref name="reason"/> is shown to the player (a safe default is used if blank).</summary>
        public static KmhHookVerdict Deny(string reason)
            => new KmhHookVerdict(true, string.IsNullOrWhiteSpace(reason) ? "Not allowed on this server." : reason);
    }
}
