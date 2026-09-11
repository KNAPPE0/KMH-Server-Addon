using System.Collections.Generic;

namespace KMHServerAddon.Features.Economy
{
    // These rules enforce nothing unless the guild snapshot actually carries the hall the client is answering about.
    internal static class KmhGuildHallAccessSelfTest
    {
        private static EconomyContext Ctx(bool nearHall, bool caravanNear, bool caravanNearKnown, bool hasCaravan = true)
            => new EconomyContext(true, true, hasCaravan, false, false, nearHall, false, caravanNear, caravanNearKnown);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            r.Add(("GuildHall: Remote ignores proximity entirely",
                   EconomyAccess.CheckGuildForTest("Remote", Ctx(false, false, true), out _), ""));
            r.Add(("GuildHall: Disabled refuses whatever the client reports",
                   !EconomyAccess.CheckGuildForTest("Disabled", Ctx(true, true, true), out _), ""));

            r.Add(("GuildHall: GuildHallRequired refuses a player away from the hall",
                   !EconomyAccess.CheckGuildForTest("GuildHallRequired", Ctx(false, false, true), out string whyAway),
                   whyAway));
            r.Add(("GuildHall: GuildHallRequired allows a player at the hall",
                   EconomyAccess.CheckGuildForTest("GuildHallRequired", Ctx(true, false, true), out _),
                   "a colony near the hall counts for this mode"));

            r.Add(("GuildHall: CaravanNearGuildHall refuses a distant caravan beside a near colony",
                   !EconomyAccess.CheckGuildForTest("CaravanNearGuildHall", Ctx(true, false, true), out string whyFar),
                   whyFar));
            r.Add(("GuildHall: CaravanNearGuildHall allows a caravan at the hall",
                   EconomyAccess.CheckGuildForTest("CaravanNearGuildHall", Ctx(true, true, true), out _), ""));
            r.Add(("GuildHall: CaravanNearGuildHall still needs a caravan at all",
                   !EconomyAccess.CheckGuildForTest("CaravanNearGuildHall", Ctx(true, true, true, hasCaravan: false), out _), ""));

            r.Add(("GuildHall: a client without the caravan flag falls back, not shut out",
                   EconomyAccess.CheckGuildForTest("CaravanNearGuildHall", Ctx(true, false, false), out _)
                   && !EconomyAccess.CheckGuildForTest("CaravanNearGuildHall", Ctx(false, false, false), out _),
                   "absent means unknown, which is the old general answer - not false"));

            // Through FromEnvelope, since the cases above skip the wire parse the presence detection lives in.
            var newClient = SubProtocol.KmhEnvelope.ParseForTest(new
            {
                ctx_known = true, ctx_has_colony = true, ctx_has_caravan = true,
                ctx_near_guild_hall = true, ctx_caravan_near_hall = false,
            });
            var oldClient = SubProtocol.KmhEnvelope.ParseForTest(new
            {
                ctx_known = true, ctx_has_colony = true, ctx_has_caravan = true,
                ctx_near_guild_hall = true,
            });
            EconomyContext newCtx = EconomyContext.FromEnvelope(newClient);
            EconomyContext oldCtx = EconomyContext.FromEnvelope(oldClient);
            r.Add(("GuildHall: the wire tells a sent 'false' apart from an absent flag",
                   newCtx.CaravanNearHallKnown && !newCtx.CaravanIsNearHall
                   && !oldCtx.CaravanNearHallKnown && oldCtx.CaravanIsNearHall,
                   "both look like false on a bare GetBool, and they mean opposite things"));
            r.Add(("GuildHall: a new client's distant caravan is refused, an old client's is not",
                   !EconomyAccess.CheckGuildForTest("CaravanNearGuildHall", newCtx, out _)
                   && EconomyAccess.CheckGuildForTest("CaravanNearGuildHall", oldCtx, out _), ""));

            var unknown = default(EconomyContext);
            r.Add(("GuildHall: an unknown context stays permissive",
                   EconomyAccess.CheckGuildForTest("GuildHallRequired", unknown, out _)
                   && EconomyAccess.CheckGuildForTest("CaravanNearGuildHall", unknown, out _),
                   "the server cannot see the game; refusing on silence would strand players"));
            return r;
        }
    }
}
