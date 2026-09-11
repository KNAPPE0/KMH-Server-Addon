using System;
using System.Collections.Generic;
using System.Reflection;
using KMHServerAddon.Features;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // KmhRouter lets a kind through when FeatureForKind returns null, so an unmapped feature is quietly un-toggleable.
    internal static class KmhFeatureGateSelfTest
    {
        private static readonly string[] IntentionallyUngated =
        {
            "kmh.hello", "kmh.ping", "kmh.pong", "kmh.notice", "kmh.notify", "kmh.op.result",   // lifecycle
            "kmh.delivery",                                                     // receipt for value already debited
            "kmh.item_labels", "kmh.item_values", "kmh.condition_defs",         // catalog intake
            "kmh.colony.report", "kmh.debug",                                   // telemetry
            "kmh.enforcement",                                                  // admin config, must always work
            "kmh.link", "kmh.linked_accounts",                                  // Discord linking
            "kmh.reputation", "kmh.site",                                       // no owner toggle exists for these
            // Both toggle in the store rather than the router, so turning one off still allows a cancel and a fetch.
            "kmh.roadworks",
            "kmh.frontier",
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            var unmapped = new List<string>();
            int total = 0;

            foreach (FieldInfo f in typeof(KmhProtocol.Kind).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType != typeof(string)) continue;
                string kind = f.GetValue(null) as string;
                if (string.IsNullOrEmpty(kind) || !kind.StartsWith("kmh.", StringComparison.Ordinal)) continue;
                total++;
                if (FeaturesConfig.FeatureForKind(kind) != null) continue;
                if (!IsExempt(kind)) unmapped.Add(kind);
            }

            r.Add(("Feature gate: every kind is gated or explicitly exempt", unmapped.Count == 0,
                unmapped.Count == 0 ? $"{total} kinds" : $"UNGATED: {string.Join(", ", unmapped)}"));

            // The gate must actually resolve the features it claims to, or a toggle silently does nothing.
            bool resolves = FeaturesConfig.FeatureForKind(KmhProtocol.Kind.ColonistRosterRequest) == "standings"
                         && FeaturesConfig.FeatureForKind(KmhProtocol.Kind.SeasonArchiveRequest)  == "standings"
                         && FeaturesConfig.FeatureForKind(KmhProtocol.Kind.MailRequest)           == "mail"
                         && FeaturesConfig.FeatureForKind(KmhProtocol.Kind.ChatRequest)           == "chat";
            r.Add(("Feature gate: records/archive gate with standings", resolves, ""));

            // The exemption is only honest while the store enforces the toggle. Prove it, do not assume it.
            bool wasOn = Features.Sites.SitesConfig.Current.AllowRoadworks;
            try
            {
                Features.Sites.SitesConfig.Current.AllowRoadworks = false;
                bool started = Features.Roadworks.RoadworksStore.TryStartProject(
                    "__kmh_gate_probe__diag", -1, "trail",
                    new List<Features.Roadworks.Dto.RoadTile>
                    {
                        new Features.Roadworks.Dto.RoadTile { TileId = 1 },
                        new Features.Roadworks.Dto.RoadTile { TileId = 2 },
                    },
                    out _, out string why);
                // The probe has no treasury, so "not enough silver" would pass with the toggle deleted.
                bool refusedForBeingOff = !started && (why ?? "").IndexOf("turned off", StringComparison.OrdinalIgnoreCase) >= 0;
                r.Add(("Feature gate: Roadworks off refuses new projects in the store", refusedForBeingOff,
                       started ? "a project was accepted with Roadworks disabled" : $"refused with: {why}"));
            }
            finally { Features.Sites.SitesConfig.Current.AllowRoadworks = wasOn; }

            // The Frontier exemption is only honest while a proposal with no open question is refused. Prove it.
            bool recorded = Features.Frontier.KmhWorldDirector.TryRecordProposal(
                "__kmh_gate_probe__diag", "no-such-token", 1, 0, "fp", out string whyProp);
            r.Add(("Feature gate: a placement proposal with no open question is refused", !recorded,
                   recorded ? "a proposal was accepted with nothing asked" : $"refused with: {whyProp}"));

            return r;
        }

        private static bool IsExempt(string kind)
        {
            foreach (string p in IntentionallyUngated)
                if (kind.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
