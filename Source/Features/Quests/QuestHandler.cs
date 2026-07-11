using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Quests
{
    // Server-side handler for kmh.quest.* (counterpart to the patch mod's QuestHandler). Any mutation rebroadcasts
    // a fresh quest snapshot so every open Quest Board updates immediately.
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

            // deserialize the whole draft so per-kind fields (escort/defend/hunt/build) flow through
            Dto.QuestEntry draft = env.DataAs<Dto.QuestEntry>() ?? new Dto.QuestEntry();
            int expiresInHours   = env.GetInt("expires_hours", 0);

            long id = QuestStore.PostDraft(username, draft, expiresInHours, out string reason);
            if (id == 0)
            {
                ServerLog.Verbose($"Quest post rejected for {username}: {reason}");
                KmhRouter.Notify(client, "negative",
                    string.IsNullOrEmpty(reason) ? "Couldn't post that quest - check the fields and your post limit." : reason);
                SendTreasurySnapshotTo(client); // resync treasury if escrow attempt failed
                return;
            }

            ServerLog.Info($"Quest: {username} posted quest #{id} '{draft.Title}' (bounty {draft.BountySilver}s, kind {draft.Kind})");
            PlayerStats.PlayerStatsStore.BumpQuestsPosted(username);
            KmhRouter.Notify(client, "positive", $"Quest posted: {draft.Title}");

            BroadcastSnapshot();
            SendTreasurySnapshotTo(client); // poster's silver was escrowed
        }

        private static void OnAbandon(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            long   questId  = (long)(env?.GetInt("quest_id", 0) ?? 0);
            if (questId <= 0) return;

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
            string username = client?.GetData<UserFile>()?.Username;
            long   questId  = (long)(env?.GetInt("quest_id", 0) ?? 0);
            if (questId <= 0) return;

            if (QuestStore.VerifyComplete(username, questId, out string posterAffected))
            {
                ServerLog.Info($"Quest: {username} auto-verified quest #{questId}");
                BroadcastSnapshot();
                SendTreasurySnapshotTo(client);            // claimer got the bounty
                SendTreasurySnapshotToUsername(posterAffected);
            }
            else SendSnapshotTo(client);
        }

        private static void OnSubmitProof(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            long   questId  = (long)(env?.GetInt("quest_id", 0) ?? 0);
            if (questId <= 0) return;

            string proofText = env?.GetString("proof_text", "") ?? "";
            string proofUrl  = env?.GetString("proof_image_url", "") ?? "";

            if (QuestStore.SubmitProof(username, questId, proofText, proofUrl, out string posterAffected))
            {
                ServerLog.Info($"Quest: {username} submitted proof for quest #{questId}");
                KmhRouter.Notify(client, "positive", "Proof submitted for review.");
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
            string username = client?.GetData<UserFile>()?.Username;
            long   questId  = (long)(env?.GetInt("quest_id", 0) ?? 0);
            if (questId <= 0) return;

            bool   approve = env?.GetBool("approve", false) ?? false;
            string note    = env?.GetString("note", "") ?? "";

            if (QuestStore.ReviewProof(username, questId, approve, note, out string claimerAffected))
            {
                ServerLog.Info($"Quest: {username} {(approve ? "approved" : "rejected")} proof for quest #{questId}");
                BroadcastSnapshot();
                SendTreasurySnapshotToUsername(claimerAffected); // claimer paid on approve
            }
            else
            {
                KmhRouter.Notify(client, "negative", "Review failed - the quest isn't awaiting your review.");
                SendSnapshotTo(client);
            }
        }

        private static void OnClaim(ServerClient client, KmhEnvelope env)
        {
            string username  = client?.GetData<UserFile>()?.Username;
            long   questId   = (long)(env?.GetInt("quest_id", 0) ?? 0);
            if (questId <= 0) return;

            if (QuestStore.Claim(username, questId))
            {
                ServerLog.Info($"Quest: {username} claimed quest #{questId}");
                BroadcastSnapshot();
            }
            else
            {
                ServerLog.Verbose($"Quest claim rejected for {username} (quest #{questId})");
                KmhRouter.Notify(client, "negative", "Couldn't claim - the quest may be gone or already taken.");
                SendSnapshotTo(client); // corrective so the patch UI shows truth
            }
        }

        private static void OnSubmit(ServerClient client, KmhEnvelope env)
        {
            string username  = client?.GetData<UserFile>()?.Username;
            long   questId   = (long)(env?.GetInt("quest_id", 0) ?? 0);
            if (questId <= 0) return;

            if (QuestStore.Submit(username, questId, out string posterAffected))
            {
                ServerLog.Info($"Quest: {username} submitted quest #{questId}");
                BroadcastSnapshot();
                // Submitter's treasury may have changed (DeliverItem: bounty silver credited, target items
                // withdrawn)
                SendTreasurySnapshotTo(client);
                // DeliverItem auto-complete also moves items poster's way - push them a snapshot too if they're
                // online
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
            string username  = client?.GetData<UserFile>()?.Username;
            long   questId   = (long)(env?.GetInt("quest_id", 0) ?? 0);
            if (questId <= 0) return;

            if (QuestStore.Cancel(username, questId))
            {
                ServerLog.Info($"Quest: {username} cancelled quest #{questId}");
                BroadcastSnapshot();
                SendTreasurySnapshotTo(client); // bounty refunded
            }
            else
            {
                ServerLog.Verbose($"Quest cancel rejected for {username} (quest #{questId})");
                KmhRouter.Notify(client, "negative", "Couldn't cancel - not your quest, or it's already claimed.");
                SendSnapshotTo(client);
            }
        }

        // Bounty-kind sign-off: poster approves a Submitted Bounty quest. Server pays bounty silver to the claimer
        // + marks Completed. DeliverItem quests auto-complete inside OnSubmit (server-verifiable via treasury
        // check), so Approve doesn't apply to them
        private static void OnApprove(ServerClient client, KmhEnvelope env)
        {
            string username  = client?.GetData<UserFile>()?.Username;
            long   questId   = (long)(env?.GetInt("quest_id", 0) ?? 0);
            if (questId <= 0) return;

            if (QuestStore.Approve(username, questId, out string claimerAffected))
            {
                ServerLog.Info($"Quest: {username} approved Bounty quest #{questId}");
                BroadcastSnapshot();
                // Approver's treasury balance is unchanged (bounty was escrowed at post time), but the claimer's
                // just got credited - push their treasury directly so their dialog updates immediately
                SendTreasurySnapshotToUsername(claimerAffected);
            }
            else
            {
                ServerLog.Verbose($"Quest approve rejected for {username} (quest #{questId})");
                KmhRouter.Notify(client, "negative", "Approve failed - the quest isn't awaiting your sign-off.");
                SendSnapshotTo(client);
            }
        }

        // Admin recovery for a stuck quest: force-expire it (refunds the poster's escrowed bounty), push the poster's
        // treasury + an offline-safe notice, rebroadcast. Returns a console/chat-ready summary line.
        public static string AdminCancel(long id)
        {
            if (!QuestStore.ExpireQuest(id, out string poster))
                return $"Quest #{id} not found.";
            ServerLog.Info($"Quest #{id} cancelled by admin (bounty refunded to {poster}).");
            BroadcastSnapshot();
            if (!string.IsNullOrEmpty(poster))
            {
                SendTreasurySnapshotToUsername(poster);
                Notifications.KmhMail.ToUser(poster, "neutral", "Quest cancelled",
                    "An admin cancelled your posted quest - any escrowed bounty was refunded to your treasury.");
            }
            return $"Quest #{id} cancelled - bounty refunded to {(string.IsNullOrEmpty(poster) ? "(poster)" : poster)}.";
        }

        // Send a fresh treasury snapshot to a specific username if online. Caller-scoped so the recipient's
        // CanDeposit/CanWithdraw reflect their own permissions on their own vault
        private static void SendTreasurySnapshotToUsername(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            Treasury.Dto.TreasurySnapshot snapshot = Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendToUsername(username, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }

        // --- snapshot delivery helpers ---

        private static void SendSnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            Dto.QuestSnapshot snapshot = QuestStore.BuildSnapshot(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.QuestSnapshot, snapshot);
        }

        internal static void BroadcastSnapshot()
        {
            KmhRouter.BroadcastToInterested(KmhProtocol.Kind.QuestSnapshot, u => QuestStore.BuildSnapshot(u));
            // Quest activity is the only thing that moves reputation, so refresh the roster on the same beat -
            // keeps board tier badges current
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
