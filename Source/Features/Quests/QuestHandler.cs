using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Quests
{
    internal static class QuestHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestClaim,   OnClaim);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestSubmit,  OnSubmit);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestCancel,  OnCancel);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestPost,    OnPost);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestApprove, OnApprove);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestAbandon,     OnAbandon);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestSubmitProof, OnSubmitProof);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestReview,      OnReview);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.QuestVerify,      OnVerify);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            SendSnapshotTo(client);
        }

        private static void OnPost(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (env == null) return;

            // The whole draft is deserialised so per-kind fields flow through without being named here.
            Dto.QuestEntry draft = env.DataAs<Dto.QuestEntry>() ?? new Dto.QuestEntry();
            int expiresInHours   = env.GetInt("expires_hours", 0);

            var op = new Security.KmhOpClaim("quest.post", username, env);
            if (!op.Begin()) { SendSnapshotTo(client); SendTreasurySnapshotTo(client); return; }

            long id = QuestStore.PostDraft(username, draft, expiresInHours, out string reason);
            if (id == 0)
            {
                op.Release();
                ServerLog.Verbose($"Quest post rejected for {username}: {reason}");
                KmhRouter.Notify(client, "negative",
                    string.IsNullOrEmpty(reason) ? "Couldn't post that quest - check the fields and your post limit." : reason);
                SendTreasurySnapshotTo(client);   // a failed escrow attempt may already have moved the client's view
                return;
            }

            ServerLog.Info($"Quest: {username} posted quest #{id} '{draft.Title}' (bounty {draft.BountySilver}s, kind {draft.Kind})");
            PlayerStats.PlayerStatsStore.BumpQuestsPosted(username);
            KmhRouter.Notify(client, "positive", $"Quest posted: {draft.Title}");

            BroadcastSnapshot();
            SendTreasurySnapshotTo(client);
        }

        private static bool TryQuestId(ServerClient client, KmhEnvelope env, out string username, out long questId)
        {
            username = client?.GetData<UserFile>()?.Username;
            questId  = (long)(env?.GetInt("quest_id", 0) ?? 0);
            return questId > 0;
        }

        private static void OnAbandon(ServerClient client, KmhEnvelope env)
        {
            if (!TryQuestId(client, env, out string username, out long questId)) return;

            if (QuestStore.Abandon(username, questId, out string posterAffected))
            {
                ServerLog.Info($"Quest: {username} abandoned quest #{questId}");
                BroadcastSnapshot();
            }
            else
            {
                KmhRouter.Notify(client, "negative", "Couldn't abandon that quest.");
                SendSnapshotTo(client);
            }
        }

        private static void OnVerify(ServerClient client, KmhEnvelope env)
        {
            if (!TryQuestId(client, env, out string username, out long questId)) return;

            if (QuestStore.VerifyComplete(username, questId, out string posterAffected))
            {
                ServerLog.Info($"Quest: {username} auto-verified quest #{questId}");
                if (QuestStore.PartiesOf(questId).State == Dto.QuestEntry.StatePendingReview)
                {
                    KmhRouter.Notify(client, "neutral", "Reported complete - the poster signs off before this bounty is paid.");
                    NoticeAwaitingReview(questId, username, posterAffected);
                }
                BroadcastSnapshot();
                SendTreasurySnapshotTo(client);
                SendTreasurySnapshotToUsername(posterAffected);
            }
            else SendSnapshotTo(client);
        }

        private static void OnSubmitProof(ServerClient client, KmhEnvelope env)
        {
            if (!TryQuestId(client, env, out string username, out long questId)) return;

            string proofText = env?.GetString("proof_text", "") ?? "";
            string proofUrl  = env?.GetString("proof_image_url", "") ?? "";

            if (QuestStore.SubmitProof(username, questId, proofText, proofUrl, out string posterAffected))
            {
                ServerLog.Info($"Quest: {username} submitted proof for quest #{questId}");
                KmhRouter.Notify(client, "positive", "Proof submitted for review.");
                NoticeAwaitingReview(questId, username, posterAffected);
                BroadcastSnapshot();
            }
            else
            {
                KmhRouter.Notify(client, "negative", "Proof submission failed - the quest may not be yours to report.");
                SendSnapshotTo(client);
            }
        }

        private static void OnReview(ServerClient client, KmhEnvelope env)
        {
            if (!TryQuestId(client, env, out string username, out long questId)) return;

            bool   approve = env?.GetBool("approve", false) ?? false;
            string note    = env?.GetString("note", "") ?? "";

            if (QuestStore.ReviewProof(username, questId, approve, note, out string claimerAffected))
            {
                ServerLog.Info($"Quest: {username} {(approve ? "approved" : "rejected")} proof for quest #{questId}");
                if (approve) NoticeApproved(questId, username, claimerAffected);
                else         NoticeRejected(questId, username, claimerAffected, note);
                BroadcastSnapshot();
                SendTreasurySnapshotToUsername(claimerAffected);
            }
            else
            {
                KmhRouter.Notify(client, "negative", "Review failed - the quest isn't awaiting your review.");
                SendSnapshotTo(client);
            }
        }

        private static void OnClaim(ServerClient client, KmhEnvelope env)
        {
            if (!TryQuestId(client, env, out string username, out long questId)) return;

            if (QuestStore.Claim(username, questId))
            {
                ServerLog.Info($"Quest: {username} claimed quest #{questId}");
                NoticeClaimed(questId, username);
                BroadcastSnapshot();
            }
            else
            {
                ServerLog.Verbose($"Quest claim rejected for {username} (quest #{questId})");
                KmhRouter.Notify(client, "negative", "Couldn't claim - the quest may be gone or already taken.");
                SendSnapshotTo(client);
            }
        }

        private static void OnSubmit(ServerClient client, KmhEnvelope env)
        {
            if (!TryQuestId(client, env, out string username, out long questId)) return;

            string stateBefore = QuestStore.PartiesOf(questId).State;
            if (QuestStore.Submit(username, questId, out string posterAffected))
            {
                ServerLog.Info($"Quest: {username} submitted quest #{questId}");
                // A delivery completes on submit and names its poster; a bounty waits for review - told once, not per re-submit.
                if (!string.IsNullOrEmpty(posterAffected)) NoticeDelivered(questId, username, posterAffected);
                else if (stateBefore == Dto.QuestEntry.StateClaimed) NoticeAwaitingReview(questId, username, QuestStore.PartiesOf(questId).Poster);
                BroadcastSnapshot();
                SendTreasurySnapshotTo(client);
                // A DeliverItem auto-complete moves items to the poster as well, and they are not the caller.
                SendTreasurySnapshotToUsername(posterAffected);
            }
            else
            {
                ServerLog.Verbose($"Quest submit rejected for {username} (quest #{questId} - missing items?)");
                KmhRouter.Notify(client, "negative", "Submit failed - you may be missing the required items.");
                SendSnapshotTo(client);
                SendTreasurySnapshotTo(client);
            }
        }

        private static void OnCancel(ServerClient client, KmhEnvelope env)
        {
            if (!TryQuestId(client, env, out string username, out long questId)) return;

            if (QuestStore.Cancel(username, questId))
            {
                ServerLog.Info($"Quest: {username} cancelled quest #{questId}");
                BroadcastSnapshot();
                SendTreasurySnapshotTo(client);
            }
            else
            {
                ServerLog.Verbose($"Quest cancel rejected for {username} (quest #{questId})");
                KmhRouter.Notify(client, "negative", "Couldn't cancel - not your quest, or it's already claimed.");
                SendSnapshotTo(client);
            }
        }

        // Bounty only: a DeliverItem quest is treasury-verifiable, so it auto-completes in OnSubmit and never lands here.
        private static void OnApprove(ServerClient client, KmhEnvelope env)
        {
            if (!TryQuestId(client, env, out string username, out long questId)) return;

            if (QuestStore.Approve(username, questId, out string claimerAffected))
            {
                ServerLog.Info($"Quest: {username} approved Bounty quest #{questId}");
                NoticeApproved(questId, username, claimerAffected);
                BroadcastSnapshot();
                // The bounty was escrowed at post time, so it is the claimer's balance that moved, not the approver's.
                SendTreasurySnapshotToUsername(claimerAffected);
            }
            else
            {
                ServerLog.Verbose($"Quest approve rejected for {username} (quest #{questId})");
                KmhRouter.Notify(client, "negative", "Approve failed - the quest isn't awaiting your sign-off.");
                SendSnapshotTo(client);
            }
        }

        // Notices are queued, so an absent party reads them on their next join.

        // The title is the poster's own text and a letter renders markup, so it is shown capped and inert.
        private static string Named(long id, string title)
        {
            string t = Util.KmhSafe.Cap((title ?? "").Replace('<', '(').Replace('>', ')'), 80);
            return t.Length == 0 ? $"#{id}" : $"#{id} '{t}'";
        }

        internal static void NoticeClaimed(long id, string claimer)
        {
            var q = QuestStore.PartiesOf(id);
            Notifications.KmhNotify.ToUser(q.Poster, "neutral", "Quest claimed",
                $"{claimer} claimed your quest {Named(id, q.Title)}.");
        }

        internal static void NoticeAwaitingReview(long id, string claimer, string poster)
        {
            var q = QuestStore.PartiesOf(id);
            int hours = QuestsConfig.Current.ReviewTimeoutHours;
            string timeout = hours > 0 ? $" If you do not review it, it reopens unpaid in {hours} h." : "";
            Notifications.KmhNotify.ToUser(string.IsNullOrEmpty(poster) ? q.Poster : poster, "neutral", "Quest awaiting your review",
                $"{claimer} submitted your quest {Named(id, q.Title)}. Open the quest board to approve it (pays the bounty) or reject it with a note.{timeout}");
        }

        internal static void NoticeDelivered(long id, string claimer, string poster)
        {
            var q = QuestStore.PartiesOf(id);
            Notifications.KmhNotify.ToUser(poster, "positive", "Quest completed",
                $"{claimer} delivered your quest {Named(id, q.Title)} - the goods are in your treasury and the bounty was paid.");
        }

        internal static void NoticeApproved(long id, string poster, string claimer)
        {
            var q = QuestStore.PartiesOf(id);
            Notifications.KmhNotify.ToUser(claimer, "positive", "Quest approved",
                $"{poster} approved your submission for quest {Named(id, q.Title)} - the bounty is paid to your treasury.");
        }

        internal static void NoticeRejected(long id, string poster, string claimer, string note)
        {
            var q = QuestStore.PartiesOf(id);
            string why = Util.KmhSafe.Cap((note ?? "").Trim().Replace('<', '(').Replace('>', ')'), 256);
            Notifications.KmhNotify.ToUser(claimer, "negative", "Quest submission rejected",
                $"{poster} rejected your submission for quest {Named(id, q.Title)}" + (why.Length > 0 ? $": \"{why}\"" : "")
                + ". The quest is open again and nothing was paid.");
        }

        // Named by the caller: an expired quest has already left the board.
        internal static void NoticeExpired(long id, string title, string poster, string claimer)
        {
            Notifications.KmhNotify.ToUser(poster, "neutral", "Quest expired",
                $"Your quest {Named(id, title)} expired - its escrowed bounty went back to your treasury.");
            if (!string.IsNullOrEmpty(claimer))
                Notifications.KmhNotify.ToUser(claimer, "neutral", "Quest expired",
                    $"Quest {Named(id, title)}, which you had claimed, expired before it was finished. Nothing was paid.");
        }

        public static string AdminCancel(long id) => AdminSettle(id, "refund", "admin");

        public static string AdminSettle(long id, string action, string admin)
        {
            string poster = null, claimer = null, why = null;
            bool ok;
            switch (action)
            {
                case "approve": ok = QuestStore.AdminReview(admin, id, true, out claimer, out poster);  break;
                case "reject":  ok = QuestStore.AdminReview(admin, id, false, out claimer, out poster); break;
                case "refund":  ok = QuestStore.AdminRefund(id, admin, out poster, out why);              break;
                default: return "Usage: kmh quest <approve|reject|refund> <id>";
            }
            if (!ok)
                return action == "refund"
                    ? $"Quest #{id} was not refunded: {why}."
                    : $"Quest #{id} is not waiting for review (see 'kmh inspect quests').";
            ServerLog.Info($"Quest #{id}: {action} by {admin}.");
            BroadcastSnapshot();
            SendTreasurySnapshotToUsername(poster);
            SendTreasurySnapshotToUsername(claimer);
            switch (action)
            {
                case "approve":
                    Notifications.KmhNotify.ToUser(claimer, "positive", "Quest approved", $"An admin approved your submission for quest #{id} - the bounty is paid.");
                    Notifications.KmhNotify.ToUser(poster, "neutral", "Quest settled", $"An admin approved the submission for your quest #{id}; its bounty was paid.");
                    return $"Quest #{id} approved - bounty paid to {claimer}.";
                case "reject":
                    Notifications.KmhNotify.ToUser(claimer, "neutral", "Quest reopened", $"An admin turned down your submission for quest #{id}; it is open again.");
                    return $"Quest #{id} rejected - reopened, escrow still held for {poster}.";
                default:
                    Notifications.KmhNotify.ToUser(poster, "neutral", "Quest cancelled",
                        "An admin cancelled your posted quest - any escrowed bounty was refunded to your treasury.");
                    return $"Quest #{id} cancelled - bounty refunded to {(string.IsNullOrEmpty(poster) ? "(poster)" : poster)}.";
            }
        }

        // Built for the recipient, so CanDeposit/CanWithdraw reflect their permissions rather than the caller's.
        private static void SendTreasurySnapshotToUsername(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            Treasury.Dto.TreasurySnapshot snapshot = Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendToUsername(username, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }

        private static void SendSnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            Dto.QuestSnapshot snapshot = QuestStore.BuildSnapshot(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.QuestSnapshot, snapshot);
        }

        internal static void BroadcastSnapshot()
        {
            // Derived once per broadcast, not per recipient: viewers in one guild share a single build+serialize.
            System.Collections.Generic.HashSet<string> nonPublic = QuestStore.PostersOfNonPublic();
            KmhRouter.BroadcastToInterested(KmhProtocol.Kind.QuestSnapshot,
                u => Features.Guilds.GuildVisibility.SnapshotShareKey(u, nonPublic),
                u => QuestStore.BuildSnapshot(u));
            // Quest activity is the only thing that moves reputation, so the tier badges refresh on the same beat.
            Features.Reputation.ReputationHandler.BroadcastSnapshot();
        }

        private static void SendTreasurySnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            Treasury.Dto.TreasurySnapshot snapshot = Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }
    }
}
