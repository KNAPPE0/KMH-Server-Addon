using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Sites.Dto
{
    // Facts only - a client that could send a family or a skill would rewrite the Site economy.
    internal sealed class SiteOutputMetadata
    {
        [JsonProperty("def")]        public string DefName { get; set; } = "";
        [JsonProperty("label")]      public string Label   { get; set; } = "";

        // Parents included, most specific first - the classifier relies on that order.
        [JsonProperty("cats")]       public List<string> Categories { get; set; } = new List<string>();

        // stuffProps categories, decisive for materials whose ThingCategory is too generic to classify.
        [JsonProperty("stuff")]      public List<string> StuffCategories { get; set; } = new List<string>();

        // tradeTags + thingSetMakerTags, because modded content often tags accurately when its categories are custom.
        [JsonProperty("tags")]       public List<string> Tags { get; set; } = new List<string>();

        [JsonProperty("ingestible")] public bool IsIngestible { get; set; }
        [JsonProperty("food_type")]  public string FoodType   { get; set; } = "";   // FoodTypeFlags, e.g. "Meat, AnimalProduct"

        // Butchered from a corpse, or milk/wool/egg - the client walks butcherProducts and the animal comps for this.
        [JsonProperty("animal")]     public bool IsAnimalProduct { get; set; }

        [JsonProperty("harvested")]  public bool IsHarvestedFromPlant { get; set; }

        // Narrower than harvested: the source plant is a tree, or cannot be sown and so is gathered where it grows.
        [JsonProperty("tree")]       public bool IsTreeHarvest { get; set; }
        [JsonProperty("wild")]       public bool IsWildHarvest { get; set; }

        [JsonProperty("mineable")]   public bool IsMineable { get; set; }

        // At least one RecipeDef produces it, so it is manufactured rather than gathered.
        [JsonProperty("crafted")]    public bool IsCraftedProduct { get; set; }

        [JsonProperty("mv")]         public float MarketValue { get; set; }

        // Set by the SERVER after classification, never accepted from the wire.
        [JsonIgnore] public string Family { get; set; } = SiteOutputFamilies.Unknown;
        [JsonIgnore] public string Skill  { get; set; } = "";
        [JsonIgnore] public string Source { get; set; } = "";   // classifier | override | extension
    }
}
