namespace KMHServerAddon.Policy
{
    // Declaration order is load-bearing: later origins win during resolution.
    internal enum ValueOrigin
    {
        Default,    // KMH's built-in baseline for the system
        Profile,    // the selected server profile (Balanced/Casual/Hardcore/Legacy)
        Owner,      // an explicit owner override
        Migration,  // carried across from an older config during migration
        Safety,     // forced by KMH over any owner value because the owner's value was unsafe
        Extension,  // supplied by a mod extension
    }
}
