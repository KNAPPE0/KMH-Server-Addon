using System.Collections.Generic;
using R = KMHServerAddon.Features.Guilds.Dto.GuildMemberDto;

namespace KMHServerAddon.Features.Guilds
{
    internal static class GuildPermissionsSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            bool settings = GuildPermissions.Can(R.RankOwner, GuildCapability.SaveSettings)
                         && GuildPermissions.Can(R.RankAdmin, GuildCapability.SaveSettings)
                         && !GuildPermissions.Can(R.RankModerator, GuildCapability.SaveSettings);
            r.Add(("Guild: settings need admin+", settings, "owner/admin yes, moderator no"));

            bool invite = GuildPermissions.Can(R.RankModerator, GuildCapability.Invite)
                       && !GuildPermissions.Can(R.RankOfficer, GuildCapability.Invite)
                       && !GuildPermissions.Can(R.RankMember, GuildCapability.Invite);
            r.Add(("Guild: invite needs moderator+", invite, "moderator yes, officer/member no"));

            bool ownerOnly = GuildPermissions.Can(R.RankOwner, GuildCapability.Disband)
                          && !GuildPermissions.Can(R.RankAdmin, GuildCapability.Disband)
                          && GuildPermissions.Can(R.RankOwner, GuildCapability.TransferOwner)
                          && !GuildPermissions.Can(R.RankAdmin, GuildCapability.TransferOwner);
            r.Add(("Guild: disband/transfer owner-only", ownerOnly, "owner yes, admin no"));

            bool actOn = GuildPermissions.CanActOnMember(R.RankAdmin, R.RankMember)      // admin kicks member
                      && GuildPermissions.CanActOnMember(R.RankModerator, R.RankOfficer) // mod kicks officer
                      && !GuildPermissions.CanActOnMember(R.RankAdmin, R.RankAdmin)       // not an equal
                      && !GuildPermissions.CanActOnMember(R.RankAdmin, R.RankOwner)       // never the owner
                      && !GuildPermissions.CanActOnMember(R.RankOfficer, R.RankMember);   // officer lacks the capability
            r.Add(("Guild: manage-member outranks target", actOn, "outrank required, no equals, never owner"));

            bool unknown = !GuildPermissions.Can("bogus", GuildCapability.Invite)
                        && !GuildPermissions.Can("", GuildCapability.SaveSettings)
                        && GuildPermissions.RankOrder("bogus") > GuildPermissions.RankOrder(R.RankMember);
            r.Add(("Guild: unknown rank has no power", unknown, "bad rank sorts below member"));

            // The in-memory tally is empty after a restart, so the persisted mirror is what stops a fresh daily cap.
            const long day = 1000, prevDay = 900;
            bool tallyWins   = GuildStore.WithdrawnToday(day, day, 400, day, 0) == 400;
            bool mirrorAfterRestart = GuildStore.WithdrawnToday(day, 0, 0, day, 700) == 700;
            bool staleBothReset     = GuildStore.WithdrawnToday(day, prevDay, 5000, prevDay, 5000) == 0;
            bool staleTallyUsesMirror = GuildStore.WithdrawnToday(day, prevDay, 5000, day, 250) == 250;
            r.Add(("Guild: withdraw tally survives restart", mirrorAfterRestart, "empty tally falls back to the persisted mirror"));
            r.Add(("Guild: withdraw tally day rollover", staleBothReset && staleTallyUsesMirror && tallyWins,
                   "new day resets; live tally beats the mirror; stale tally defers to it"));

            return r;
        }
    }
}
