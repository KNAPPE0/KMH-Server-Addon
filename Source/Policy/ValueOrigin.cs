namespace KMHServerAddon.Policy
{
    // Precedence is PolicyResolver's call order, not this declaration order.
    internal enum ValueOrigin
    {
        Default,
        Profile,
        Owner,
        Migration,
        Safety,
        Extension,
    }
}
