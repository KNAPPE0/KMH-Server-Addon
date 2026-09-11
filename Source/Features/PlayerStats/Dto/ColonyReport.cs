using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.PlayerStats.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
    public class ColonyReport
    {
        [JsonProperty("save_id")]           public string SaveId          { get; set; } = "";
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
        [JsonProperty("settlements")]       public List<SettlementReport> Settlements { get; set; } = new List<SettlementReport>();
    }

    // Breakdown of the single Wealth figure above; the sum of these is that figure, so a reader can see where it sits.
    public class SettlementReport
    {
        [JsonProperty("name")]       public string Name       { get; set; } = "";
        [JsonProperty("wealth")]     public long   Wealth     { get; set; } = 0;
        [JsonProperty("population")] public int    Population { get; set; } = 0;
    }

    public class ColonistRosterSnapshot
    {
        [JsonProperty("colonists")] public List<ColonistEntry> Colonists { get; set; } = new List<ColonistEntry>();
    }

    // Owner and colony are stamped server-side, never taken from the client's row.
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

    public class ColonistProfileEnvelope
    {
        [JsonProperty("username")] public string        Username { get; set; } = "";
        [JsonProperty("detail")]   public ColonistProfile Detail  { get; set; }
    }

    public class ColonistProfile
    {
        [JsonProperty("name")]           public string Name          { get; set; } = "";
        [JsonProperty("title")]          public string Title         { get; set; } = "";   // role, e.g. "Combat Engineer"
        [JsonProperty("gender_age")]     public string GenderAge     { get; set; } = "";   // "Female, age 44 (131)"
        [JsonProperty("descriptor")]     public string Descriptor    { get; set; } = "";   // "Baseliner • Colony"
        [JsonProperty("days_in_colony")] public int    DaysInColony  { get; set; } = 0;

        [JsonProperty("childhood")]      public string Childhood     { get; set; } = "";
        [JsonProperty("adulthood")]      public string Adulthood     { get; set; } = "";
        [JsonProperty("traits")]         public List<string>          Traits    { get; set; } = new List<string>();
        [JsonProperty("skills")]         public List<ColonistSkill>   Skills    { get; set; } = new List<ColonistSkill>();
        [JsonProperty("incapable")]      public List<string>          Incapable { get; set; } = new List<string>();

        [JsonProperty("health_pct")]     public int    HealthPct     { get; set; } = 0;
        [JsonProperty("pain_pct")]       public int    PainPct       { get; set; } = 0;
        [JsonProperty("capacities")]     public List<ColonistCapacity> Capacities { get; set; } = new List<ColonistCapacity>();
        [JsonProperty("conditions")]     public List<string>           Conditions { get; set; } = new List<string>();

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
