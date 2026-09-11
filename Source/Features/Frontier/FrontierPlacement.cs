using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Frontier.Dto;

namespace KMHServerAddon.Features.Frontier
{
    // The server has no world graph, so placement rests on independent clients deriving the same tile from one nonce.
    internal static class FrontierPlacement
    {
        // A lone proposer would choose freely if modified, which is why accepting one is an owner decision.
        public static bool TryAccept(PlacementToken token, bool allowSingleProposer, out int tile, out string why)
        {
            tile = -1; why = null;
            if (token == null) { why = "no placement token"; return false; }
            if (token.Consumed) { why = "that token was already used"; return false; }
            if (token.Proposals == null || token.Proposals.Count == 0) { why = "no proposals yet"; return false; }

            // Keyed on the fingerprint too, since a matching tile from a different world is not agreement.
            var votes = new Dictionary<string, List<PlacementProposal>>(StringComparer.Ordinal);
            foreach (PlacementProposal p in token.Proposals)
            {
                if (p == null || p.Tile < 0 || string.IsNullOrEmpty(p.WorldFingerprint)) continue;
                if (IsExcluded(token, p.Tile)) continue;   // a client proposing a tile we already refused
                string key = p.Layer + ":" + p.Tile + "|" + p.WorldFingerprint;
                if (!votes.TryGetValue(key, out List<PlacementProposal> list))
                    votes[key] = list = new List<PlacementProposal>();
                bool dupe = false;
                foreach (PlacementProposal seen in list)
                    if (string.Equals(seen.Username, p.Username, StringComparison.OrdinalIgnoreCase)) dupe = true;
                if (!dupe) list.Add(p);   // one vote per player, or one client could corroborate itself
            }
            if (votes.Count == 0) { why = "no usable proposal"; return false; }

            // Fully ordered on purpose, or equally-supported tiles would be separated by dictionary order and rerun differently.
            List<PlacementProposal> best = null;
            foreach (KeyValuePair<string, List<PlacementProposal>> kv in votes)
            {
                if (best == null || kv.Value.Count > best.Count) { best = kv.Value; continue; }
                if (kv.Value.Count != best.Count) continue;
                if (kv.Value[0].ReceivedUtcTicks != best[0].ReceivedUtcTicks)
                {
                    if (kv.Value[0].ReceivedUtcTicks < best[0].ReceivedUtcTicks) best = kv.Value;
                    continue;
                }
                if (kv.Value[0].Tile < best[0].Tile) best = kv.Value;
            }

            if (best.Count >= 2) { tile = best[0].Tile; return true; }
            if (allowSingleProposer) { tile = best[0].Tile; return true; }
            why = "only one client proposed a tile and single-proposer placement is off";
            return false;
        }

        public static bool IsExcluded(PlacementToken token, int tile)
        {
            if (token?.ExcludeTiles == null) return false;
            foreach (int t in token.ExcludeTiles) if (t == tile) return true;
            return false;
        }

        public static bool IsExpired(PlacementToken token, long nowTicks)
            => token == null || (token.ExpiresUtcTicks > 0 && nowTicks >= token.ExpiresUtcTicks);

        // Pure, so every refusal the store depends on is testable without a live director.
        public static bool ProposalFits(PlacementToken token, string username, string tokenId, int tile,
                                        string fingerprint, long nowTicks, out string why)
        {
            why = null;
            if (token == null || !string.Equals(token.Id, tokenId, StringComparison.Ordinal))
            { why = "unknown placement token"; return false; }
            if (IsExpired(token, nowTicks)) { why = "that placement question has expired"; return false; }
            if (token.Consumed) { why = "that token was already used"; return false; }
            if (string.IsNullOrEmpty(username)) { why = "no caller"; return false; }
            if (tile < 0) { why = "no tile proposed"; return false; }
            if (string.IsNullOrEmpty(fingerprint)) { why = "no world fingerprint"; return false; }
            if (IsExcluded(token, tile)) { why = "that tile is already taken or cooling down"; return false; }
            if (token.Proposals != null)
                foreach (PlacementProposal p in token.Proposals)
                    if (p != null && string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase))
                    { why = "already answered"; return false; }
            return true;
        }

        // One question at a time, or a client could answer twice under two different token ids.
        public static bool CanOpenNew(PlacementToken existing, long nowTicks)
            => existing == null || existing.Consumed || IsExpired(existing, nowTicks);
    }
}
