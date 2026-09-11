using System.Collections.Generic;
using KMHServerAddon.Features.Mail;
using KMHServerAddon.Features.Mail.Dto;
using Newtonsoft.Json;

namespace KMHServerAddon.Maintenance
{
    // Uses the same serializer settings JsonFileStore does, or a field that fails to round-trip strands real goods.
    internal static class KmhMailPersistenceSelfTest
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting        = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var sent = new MailMessage
            {
                Id = 42, FromUsername = "alice", ToUsername = "bob",
                Subject = "gear", Body = "line1\nline2", SentUtcTicks = 1234567, ReadUtcTicks = 0,
                AttachedSilver = 500,
                AttachedItems  = new Dictionary<string, int> { { "Steel", 40 }, { "WoodLog", 7 } },
                AttachedPayloads = new List<Items.KmhThingPayload>
                {
                    new Items.KmhThingPayload
                    {
                        DefName = "MeleeWeapon_LongSword", StuffDefName = "Plasteel", StackCount = 1,
                        Quality = 5, HitPoints = 90, MaxHitPoints = 100, Tainted = false,
                        Fingerprint = "fp-longsword-plasteel-q5", MarketValue = 1234,
                    },
                },
                AttachState = MailStore.AttachEscrowed,
            };

            MailMessage back = JsonConvert.DeserializeObject<MailMessage>(JsonConvert.SerializeObject(sent, Settings), Settings);

            bool core = back != null && back.Id == 42 && back.FromUsername == "alice" && back.ToUsername == "bob"
                     && back.Body == "line1\nline2" && back.AttachState == MailStore.AttachEscrowed;
            r.Add(("Mail persist: message core round-trips", core, core ? "ok" : "core fields lost"));

            bool silver = back != null && back.AttachedSilver == 500;
            r.Add(("Mail persist: attached silver round-trips", silver, $"{back?.AttachedSilver}"));

            bool items = back?.AttachedItems != null && back.AttachedItems.Count == 2
                      && back.AttachedItems.TryGetValue("Steel", out int st) && st == 40
                      && back.AttachedItems.TryGetValue("WoodLog", out int wl) && wl == 7;
            r.Add(("Mail persist: attached items round-trip", items, $"{back?.AttachedItems?.Count ?? -1} entries"));

            Items.KmhThingPayload p = back?.AttachedPayloads != null && back.AttachedPayloads.Count == 1 ? back.AttachedPayloads[0] : null;
            bool gear = p != null && p.DefName == "MeleeWeapon_LongSword" && p.StuffDefName == "Plasteel"
                     && p.Quality == 5 && p.HitPoints == 90 && p.MaxHitPoints == 100
                     && p.Fingerprint == "fp-longsword-plasteel-q5" && p.StackCount == 1;
            r.Add(("Mail persist: gear payload keeps quality/HP/stuff/fingerprint", gear,
                p == null ? "payload lost" : $"{p.DefName} q{p.Quality} {p.HitPoints}/{p.MaxHitPoints}"));

            // Escrow accounting must agree after a reload, or the sweep and recall disagree with what is held.
            bool stillOpen = MailStore.HasAttachment(back);
            r.Add(("Mail persist: reloaded attachment still reads as held", stillOpen, ""));

            // A message with no attachment must not resurrect one from nulls.
            var plain = new MailMessage { Id = 7, FromUsername = "a", ToUsername = "b", Subject = "hi", AttachState = MailStore.AttachNone };
            MailMessage plainBack = JsonConvert.DeserializeObject<MailMessage>(JsonConvert.SerializeObject(plain, Settings), Settings);
            r.Add(("Mail persist: plain message stays attachment-free", !MailStore.HasAttachment(plainBack), ""));

            return r;
        }
    }
}
