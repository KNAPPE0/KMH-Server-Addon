namespace KMHServerAddon.Results
{
    // Add codes, never repurpose one: a client may localize or dedupe on the code rather than the English text.
    internal enum KmhErrorCode
    {
        None = 0,
        UnsupportedCapability,   // the server can't do what this request needs
        PermissionDenied,        // caller lacks the rank/role
        InvalidState,            // not allowed right now (e.g. maintenance)
        InvalidItem,             // the item/payload is unusable
        InsufficientFunds,       // not enough silver
        LimitExceeded,           // over a configured cap
        CooldownActive,          // must wait before retrying
        MigrationFailed,         // a config/data migration did not complete
        DeliveryFailed,          // goods/silver could not be handed over
        SaveRollbackDetected,    // the colony save is behind the server
        DuplicateRequest,        // already handled (idempotency)
        RecoveryRequired,        // needs a recovery/reconcile pass first
    }

    // The only player-facing wording for a refusal, so a detailed internal reason can never reach the player.
    internal static class KmhErrorText
    {
        public static string SafeMessage(KmhErrorCode code)
        {
            switch (code)
            {
                case KmhErrorCode.None:                 return "";
                case KmhErrorCode.UnsupportedCapability:return "This server doesn't support that yet - the owner may need to update KMH.";
                case KmhErrorCode.PermissionDenied:     return "You don't have permission to do that.";
                case KmhErrorCode.InvalidState:         return "KMH is briefly in maintenance - try that again in a moment.";
                case KmhErrorCode.InvalidItem:          return "That item can't be used here.";
                case KmhErrorCode.InsufficientFunds:    return "You don't have enough silver for that.";
                case KmhErrorCode.LimitExceeded:        return "That's over the server's limit for this action.";
                case KmhErrorCode.CooldownActive:       return "Please wait a bit before trying that again.";
                case KmhErrorCode.MigrationFailed:      return "KMH is finishing an update - try again shortly.";
                case KmhErrorCode.DeliveryFailed:       return "That couldn't be delivered; it's been held for recovery.";
                case KmhErrorCode.SaveRollbackDetected: return "Your colony save is behind the server; that action is paused for safety.";
                case KmhErrorCode.DuplicateRequest:     return "That request was already handled.";
                case KmhErrorCode.RecoveryRequired:     return "KMH needs to reconcile something first - try again shortly.";
                default:                                return "That action couldn't be completed.";
            }
        }
    }
}
