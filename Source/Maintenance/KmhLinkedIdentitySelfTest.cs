using System;
using System.Collections.Generic;
using KMHServerAddon.Features.LinkedAccounts;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // A Discord snowflake authenticates a mutation, so it has to name exactly one KMH account.
    internal static class KmhLinkedIdentitySelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { LinkedAccountsStore.ResetForTest(); JsonFileStore.ClearFailureStateForTest(); }
            }

            const ulong Snowflake = 1234567890123456789UL;

            Check("Linked accounts: one snowflake cannot claim a second username", () =>
            {
                LinkedAccountsStore.ResetForTest();
                bool first  = LinkedAccountsStore.SetLink("alice", "alice#1", Snowflake, out _);
                bool second = LinkedAccountsStore.SetLink("bob", "alice#1", Snowflake, out string why);
                bool named  = !string.IsNullOrEmpty(why) && why.IndexOf("alice", StringComparison.OrdinalIgnoreCase) >= 0;
                bool bobFree = !LinkedAccountsStore.IsLinked("bob");
                bool ok = first && !second && named && bobFree;
                return (ok, ok ? "the second binding is refused and names who holds it"
                              : $"first={first}, secondRefused={!second}, saysWho={named}, bobUnlinked={bobFree}");
            });

            Check("Linked accounts: relinking the same account is still allowed", () =>
            {
                LinkedAccountsStore.ResetForTest();
                LinkedAccountsStore.SetLink("alice", "alice#1", Snowflake, out _);
                // A Discord rename must not read as somebody else trying to take the account.
                bool again = LinkedAccountsStore.SetLink("alice", "alice#2", Snowflake, out string why);
                bool display = LinkedAccountsStore.TryGetLink("alice", out string shown) && shown == "alice#2";
                bool ok = again && display;
                return (ok, ok ? "the same identity may update its own display name" : $"accepted={again} ({why}), display={display}");
            });

            Check("Linked accounts: a duplicate already on disk resolves to nobody, not to whoever loads first", () =>
            {
                LinkedAccountsStore.ResetForTest();
                LinkedAccountsStore.SeedDuplicateForTest("alice", "bob", Snowflake);
                string who = LinkedAccountsStore.FindUsernameByDiscordId(Snowflake);
                // Deterministic: repeating it must not start naming one of them.
                string again = LinkedAccountsStore.FindUsernameByDiscordId(Snowflake);
                bool ok = who == null && again == null;
                return (ok, ok ? "an ambiguous identity is refused rather than resolved by dictionary order"
                              : $"resolved to '{who}' then '{again}'");
            });

            Check("Linked accounts: a link the disk refused is not reported as linked", () =>
            {
                LinkedAccountsStore.ResetForTest();
                bool claimed;
                using (JsonFileStore.FailWritesForTest(_ => "injected: disk full"))
                    claimed = LinkedAccountsStore.SetLink("alice", "alice#1", Snowflake, out _);
                // And nothing may be left behind in memory pretending the link exists.
                bool leftBehind = LinkedAccountsStore.IsLinked("alice")
                               || LinkedAccountsStore.DiscordIdFor("alice") != 0;
                bool ok = !claimed && !leftBehind;
                return (ok, ok ? "a failed write refuses the link and rolls it back"
                              : $"claimedSuccess={claimed}, leftInMemory={leftBehind}");
            });

            Check("Discord link: a used code only asks the player to confirm in game; nothing is linked until they do, then the Discord that used it is", () =>
            {
                LinkedAccountsStore.ResetForTest();
                string code = Features.Discord.DiscordLinkFlow.IssueCodeFor("selftest_link_a");
                bool used = Features.Discord.DiscordLinkFlow.Redeem(code, "lurker#1", Snowflake, out string owner);
                bool linkedEarly = LinkedAccountsStore.IsLinked("selftest_link_a") || LinkedAccountsStore.FindUsernameByDiscordId(Snowflake) != null;
                bool usedTwice = Features.Discord.DiscordLinkFlow.Redeem(code, "other#2", Snowflake + 1, out _);
                bool confirmed = Features.Discord.DiscordBridge.ConfirmLink("selftest_link_a", out string reply);
                bool linked = LinkedAccountsStore.DiscordIdFor("selftest_link_a") == Snowflake;
                bool ok = used && owner == "selftest_link_a" && !linkedEarly && !usedTwice && confirmed && linked;
                return (ok, $"used {used} for '{owner}', linked before confirm {linkedEarly}, second use {usedTwice}, confirmed {confirmed} ({reply}), linked to the user of the code {linked}");
            });

            Check("Discord link: another player cannot confirm it, and a new /kmh link or ten minutes cancels an unconfirmed request", () =>
            {
                LinkedAccountsStore.ResetForTest();
                Features.Discord.DiscordLinkFlow.Redeem(Features.Discord.DiscordLinkFlow.IssueCodeFor("selftest_link_a"), "lurker#1", Snowflake, out _);
                bool otherPlayer = Features.Discord.DiscordBridge.ConfirmLink("selftest_link_b", out _);
                Features.Discord.DiscordLinkFlow.IssueCodeFor("selftest_link_a");
                bool afterNewCode = Features.Discord.DiscordBridge.ConfirmLink("selftest_link_a", out _);
                Features.Discord.DiscordLinkFlow.Redeem(Features.Discord.DiscordLinkFlow.IssueCodeFor("selftest_link_a"), "lurker#1", Snowflake, out _);
                Features.Discord.DiscordLinkFlow.AgeConfirmationForTest("selftest_link_a", TimeSpan.FromMinutes(11));
                bool afterExpiry = Features.Discord.DiscordBridge.ConfirmLink("selftest_link_a", out _);
                bool ok = !otherPlayer && !afterNewCode && !afterExpiry && !LinkedAccountsStore.IsLinked("selftest_link_a") && !LinkedAccountsStore.IsLinked("selftest_link_b");
                return (ok, $"confirmed by another player {otherPlayer}, after a new code {afterNewCode}, after 11 minutes {afterExpiry}");
            });

            return r;
        }
    }
}
