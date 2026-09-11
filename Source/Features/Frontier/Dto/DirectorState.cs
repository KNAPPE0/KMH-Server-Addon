using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Frontier.Dto
{
    // Coordination only: Sites and server quests stay the truth, so nothing here may become a second copy of either.
    public class DirectorState
    {
        // Unwritten today on purpose; it is what a future migration keys on rather than dead state.
        [JsonProperty("schema")]   public int  Schema   { get; set; } = 1;
        [JsonProperty("revision")] public long Revision { get; set; } = 0;

        [JsonProperty("next_eligible_utc")] public long NextEligibleUtcTicks { get; set; } = 0;

        // tile -> earliest utc this location may be acted on again.
        [JsonProperty("target_cooldowns")] public Dictionary<string, long> TargetCooldowns { get; set; }
            = new Dictionary<string, long>(StringComparer.Ordinal);

        [JsonProperty("active_operation_ids")] public List<long> ActiveOperationIds { get; set; } = new List<long>();
        [JsonProperty("outpost_tiles")]        public List<int>  OutpostTiles       { get; set; } = new List<int>();

        [JsonProperty("spawn_budget")]          public int  SpawnBudget         { get; set; } = 0;
        [JsonProperty("budget_refilled_utc")]   public long BudgetRefilledUtcTicks { get; set; } = 0;

        [JsonProperty("resolutions")] public List<ResolutionRecord> Resolutions { get; set; }
            = new List<ResolutionRecord>();

        // Persisted, so a restart mid-question does not re-ask it as a fresh token a client could answer twice.
        [JsonProperty("pending_placement")] public PlacementToken PendingPlacement { get; set; } = null;

        // A reader outside the lock must not hold the live collections, which the tick adds to mid-enumeration.
        public DirectorState CopyForRead()
        {
            DirectorState c = (DirectorState)MemberwiseClone();
            c.TargetCooldowns    = new Dictionary<string, long>(TargetCooldowns, StringComparer.Ordinal);
            c.ActiveOperationIds = new List<long>(ActiveOperationIds);
            c.OutpostTiles       = new List<int>(OutpostTiles);
            // Deep, because TryAdvanceResolution edits Phase in place and would change a shared record under the reader.
            c.Resolutions = new List<ResolutionRecord>(Resolutions.Count);
            foreach (ResolutionRecord r in Resolutions) if (r != null) c.Resolutions.Add(r.Clone());
            if (PendingPlacement != null) c.PendingPlacement = PendingPlacement.CloneForRead();
            return c;
        }
    }

    // Written and flushed before the consequence applies, so a crash leaves evidence rather than inviting a repeat.
    public class ResolutionRecord
    {
        [JsonProperty("operation_id")] public long   OperationId { get; set; } = 0;
        [JsonProperty("phase")]        public string Phase       { get; set; } = FrontierResolution.PhaseResolving;
        [JsonProperty("consequence")]  public string Consequence { get; set; } = "";
        [JsonProperty("target_tile")]  public int    TargetTile  { get; set; } = -1;
        [JsonProperty("started_utc")]  public long   StartedUtcTicks { get; set; } = 0;
        [JsonProperty("updated_utc")]  public long   UpdatedUtcTicks { get; set; } = 0;

        public ResolutionRecord Clone() => (ResolutionRecord)MemberwiseClone();
    }
}
