using System;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global.Treasury
{
    internal static class ConnectedOpKind
    {
        public const string Deposit  = "Deposit";
        public const string Withdraw = "Withdraw";
    }

    // Personal and guild share one phase machine, file and reconciler on purpose: one set of crash-recovery rules.
    internal static class ConnectedOpScope
    {
        public const string Personal = "Personal";
        public const string Guild    = "Guild";
    }

    // Each phase is persisted before its action is attempted, so a crash leaves a record of what may have happened.
    internal static class ConnectedOpPhase
    {
        // Deposit. Opening: no silver touched yet, so abandoning it costs nothing.
        public const string Opening  = "Opening";
        // The only phase a deposit may be sent from: Global must never credit silver a colony still holds.
        public const string Escrowed = "Escrowed";
        public const string Credited = "Credited";
        // Only after a refusal proving nothing landed; durable so the refund finishes across a restart.
        public const string Refunding = "Refunding";

        // Withdrawal. Reserving: Global may or may not already hold silver for this operation.
        public const string Reserving  = "Reserving";
        public const string Reserved   = "Reserved";
        // Whether it physically landed is DeliveryStore's answer, never a guess.
        public const string Delivering = "Delivering";
        // Durably proven landed; only from here may a finalize be sent.
        public const string Delivered  = "Delivered";

        public const string Settled  = "Settled";
        public const string Cancelled = "Cancelled";

        public static bool IsTerminal(string phase)
            => phase == Credited || phase == Settled || phase == Cancelled;
    }

    internal sealed class ConnectedOp
    {
        [JsonProperty("id")]       public string Id { get; set; } = "";
        [JsonProperty("player")]   public string Player { get; set; } = "";
        // Absent in files from before guild federation, so it reads back as Personal.
        [JsonProperty("scope")]    public string Scope { get; set; } = ConnectedOpScope.Personal;
        // The link id identifies the federation; the guild name only locates the local vault again.
        [JsonProperty("link")]     public string LinkId { get; set; } = "";
        [JsonProperty("guild")]    public string Guild { get; set; } = "";
        // Bound into every signature so a server cannot move one player's silver under another's name.
        [JsonProperty("user")]     public string UserId { get; set; } = "";
        [JsonProperty("kind")]     public string Kind { get; set; } = "";
        [JsonProperty("amount")]   public long   Amount { get; set; }
        [JsonProperty("phase")]    public string Phase { get; set; } = "";
        [JsonProperty("res")]      public string ReservationId { get; set; } = "";
        [JsonProperty("delivery")] public string DeliveryId { get; set; } = "";
        // Persisted so a retried reserve still carries consent; never copied into the player's snapshot.
        [JsonProperty("grant")]    public string Grant { get; set; } = "";
        // Last refusal, so a stuck operation can say why.
        [JsonProperty("detail")]   public string Detail { get; set; } = "";
        [JsonProperty("created")]  public long CreatedUtcTicks { get; set; }
        [JsonProperty("updated")]  public long UpdatedUtcTicks { get; set; }

        [JsonIgnore] public bool IsDeposit  => Kind == ConnectedOpKind.Deposit;
        [JsonIgnore] public bool IsWithdraw => Kind == ConnectedOpKind.Withdraw;
        [JsonIgnore] public bool IsOpen     => !ConnectedOpPhase.IsTerminal(Phase);
        [JsonIgnore] public bool IsGuild    => Scope == ConnectedOpScope.Guild;
    }
}
