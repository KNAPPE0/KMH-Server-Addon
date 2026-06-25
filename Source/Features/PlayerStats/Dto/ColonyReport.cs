using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.PlayerStats.Dto
{
    // Client -> server: a player's colony summary + their colonist pawn. Byte-identical to the patch-side DTO.
    // The compact fields drive the leaderboard row; the nested ColonistProfile is stored server-side and served
    // on demand to anyone who opens the colonist modal (so the owner needn't be online).
    public class ColonyReport
    {
        [JsonProperty("colony_name")]       public string ColonyName      { get; set; } = "";
        [JsonProperty("colony_age_days")]   public int    ColonyAgeDays   { get; set; } = 0;
        [JsonProperty("time_played_hours")] public int    TimePlayedHours { get; set; } = 0;
        [JsonProperty("wealth")]            public long   Wealth          { get; set; } = 0;
        [JsonProperty("kills")]             public long   Kills           { get; set; } = 0;
        [JsonProperty("population")]        public int    Population      { get; set; } = 0;
        [JsonProperty("kills_humanlike")]   public long   KillsHumanlike  { get; set; } = 0;
        [JsonProperty("kills_mechanoid")]   public long   KillsMechanoid  { get; set; } = 0;
        [JsonProperty("kills_animal")]      public long   KillsAnimal     { get; set; } = 0;
        [JsonProperty("raids_survived")]    public int    RaidsSurvived   { get; set; } = 0;
        [JsonProperty("pawns_lost")]        public int    PawnsLost       { get; set; } = 0;
        [JsonProperty("development_score")] public int    DevelopmentScore { get; set; } = 0;
        [JsonProperty("defense_score")]     public int    DefenseScore    { get; set; } = 0;
        [JsonProperty("top_colonist_name")]     public string TopColonistName    { get; set; } = "";
        [JsonProperty("top_colonist_title")]    public string TopColonistTitle   { get; set; } = "";
        [JsonProperty("top_colonist_kills")]    public int    TopColonistKills   { get; set; } = 0;
        [JsonProperty("colonist")]          public ColonistProfile Colonist { get; set; }
        [JsonProperty("roster")]            public List<ColonistEntry> Roster { get; set; } = new List<ColonistEntry>();
    }

    // Server -> client: every colony's reported colonists, flattened, for the per-skill Colonist Records boards.
    public class ColonistRosterSnapshot
    {
        [JsonProperty("colonists")] public List<ColonistEntry> Colonists { get; set; } = new List<ColonistEntry>();
    }

    // One compact colonist row. Owner + colony are stamped server-side when the roster snapshot is built.
    public class ColonistEntry
    {
        [JsonProperty("owner")]   public string Owner  { get; set; } = "";
        [JsonProperty("colony")]  public string Colony { get; set; } = "";
        [JsonProperty("name")]    public string Name   { get; set; } = "";
        [JsonProperty("title")]   public string Title  { get; set; } = "";
        [JsonProperty("age")]     public int    Age    { get; set; } = 0;
        [JsonProperty("days")]    public int    Days   { get; set; } = 0;
        [JsonProperty("kills")]   public int    Kills  { get; set; } = 0;
        [JsonProperty("sk_shooting")]     public int SkShooting     { get; set; } = 0;
        [JsonProperty("sk_melee")]        public int SkMelee        { get; set; } = 0;
        [JsonProperty("sk_medicine")]     public int SkMedicine     { get; set; } = 0;
        [JsonProperty("sk_crafting")]     public int SkCrafting     { get; set; } = 0;
        [JsonProperty("sk_construction")] public int SkConstruction { get; set; } = 0;
    }

    // Server -> client: the full colonist profile for one player, fetched on demand (kmh.colonist.profile).
    public class ColonistProfileEnvelope
    {
        [JsonProperty("username")] public string        Username { get; set; } = "";
        [JsonProperty("detail")]   public ColonistProfile Detail  { get; set; }
    }

    // Full colonist pawn profile (Bio / Health / Combat). Sections map to the modal tabs.
    public class ColonistProfile
    {
        // header
        [JsonProperty("name")]           public string Name          { get; set; } = "";
        [JsonProperty("title")]          public string Title         { get; set; } = "";   // role, e.g. "Combat Engineer"
        [JsonProperty("gender_age")]     public string GenderAge     { get; set; } = "";   // "Female, age 44 (131)"
        [JsonProperty("descriptor")]     public string Descriptor    { get; set; } = "";   // "Baseliner • Colony"
        [JsonProperty("days_in_colony")] public int    DaysInColony  { get; set; } = 0;

        // bio
        [JsonProperty("childhood")]      public string Childhood     { get; set; } = "";
        [JsonProperty("adulthood")]      public string Adulthood     { get; set; } = "";
        [JsonProperty("traits")]         public List<string>          Traits    { get; set; } = new List<string>();
        [JsonProperty("skills")]         public List<ColonistSkill>   Skills    { get; set; } = new List<ColonistSkill>();
        [JsonProperty("incapable")]      public List<string>          Incapable { get; set; } = new List<string>();

        // health
        [JsonProperty("health_pct")]     public int    HealthPct     { get; set; } = 0;
        [JsonProperty("pain_pct")]       public int    PainPct       { get; set; } = 0;
        [JsonProperty("capacities")]     public List<ColonistCapacity> Capacities { get; set; } = new List<ColonistCapacity>();
        [JsonProperty("conditions")]     public List<string>           Conditions { get; set; } = new List<string>();

        // combat
        [JsonProperty("total_kills")]    public int    TotalKills     { get; set; } = 0;
        [JsonProperty("humanlike_kills")] public int   HumanlikeKills { get; set; } = 0;
        [JsonProperty("mechanoid_kills")] public int   MechanoidKills { get; set; } = 0;
        [JsonProperty("animal_kills")]   public int    AnimalKills    { get; set; } = 0;
        [JsonProperty("damage_taken")]   public int    DamageTaken    { get; set; } = 0;
        [JsonProperty("weapon")]         public string Weapon         { get; set; } = "";
        [JsonProperty("weapon_quality")] public string WeaponQuality  { get; set; } = "";
        [JsonProperty("recent_combat")]  public List<string> RecentCombat { get; set; } = new List<string>();
    }

    public class ColonistSkill
    {
        [JsonProperty("name")]    public string Name    { get; set; } = "";
        [JsonProperty("level")]   public int    Level   { get; set; } = 0;
        [JsonProperty("passion")] public int    Passion { get; set; } = 0;   // 0 none, 1 minor, 2 major
    }

    public class ColonistCapacity
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("pct")]  public int    Pct  { get; set; } = 0;
    }
}
