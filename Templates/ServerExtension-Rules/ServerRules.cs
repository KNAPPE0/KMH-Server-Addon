using System;
using System.Collections.Generic;
using KMH.Sdk.Server;
using KMH.Sdk.Server.Apis;
using KMH.Sdk.Server.Events;
using KMH.Sdk.Server.Hooks;

namespace ServerRules
{
    // Template: veto hooks, economy events and per-extension state in one file. Copy this folder, rename, and adapt.
    public sealed class ServerRulesExtension : IKmhServerExtension
    {
        public string Name    => "Server Rules";
        public string Version => "1.0.0";

        private IKmhServerHost    _host;
        private IExtensionStorage _storage;
        private Rules             _rules = new Rules();
        private Stats             _stats = new Stats();

        // Matching is a case-insensitive defName substring, so "Gun_" also blocks modded guns; empty = nothing banned.
        private sealed class Rules
        {
            public List<string> BannedItemSubstrings { get; set; } = new List<string> { "Gun_", "Grenade", "Bomb" };
            public int          MaxQuestBountySilver  { get; set; } = 100000;
        }

        private sealed class Stats
        {
            public Dictionary<string, int> PurchasesByPlayer { get; set; } = new Dictionary<string, int>();
        }

        public void Register(IKmhServerHost host)
        {
            _host    = host;
            _storage = host.StorageFor(Name);

            // Defaults are written on first run so the owner has a file to edit.
            if (_storage.TryLoad("rules.json", out Rules loadedRules) && loadedRules != null) _rules = loadedRules;
            else _storage.Save("rules.json", _rules);

            if (_storage.TryLoad("stats.json", out Stats loadedStats) && loadedStats != null) _stats = loadedStats;

            // A Deny blocks the action with a player-facing reason before anything is escrowed.
            host.Hooks.OnMarketplaceListing(ctx => ItemVerdict(ctx.ItemDefName));
            host.Hooks.OnAuctionListing   (ctx => ItemVerdict(ctx.ItemDefName));
            host.Hooks.OnWant             (ctx => ItemVerdict(ctx.ItemDefName));
            host.Hooks.OnQuestPost        (ctx => ctx.BountySilver > _rules.MaxQuestBountySilver
                ? KmhHookVerdict.Deny($"Quest bounties are capped at {_rules.MaxQuestBountySilver} silver here.")
                : KmhHookVerdict.Allow);

            host.Events.MarketplaceBuy += OnMarketplaceBuy;

            host.Log.Info($"Active: {_rules.BannedItemSubstrings.Count} banned pattern(s), max quest bounty {_rules.MaxQuestBountySilver} silver.");
        }

        private KmhHookVerdict ItemVerdict(string defName)
        {
            if (!string.IsNullOrEmpty(defName) && _rules.BannedItemSubstrings != null)
                foreach (string ban in _rules.BannedItemSubstrings)
                    if (!string.IsNullOrEmpty(ban) && defName.IndexOf(ban, StringComparison.OrdinalIgnoreCase) >= 0)
                        return KmhHookVerdict.Deny($"'{defName}' is not tradable on this server.");
            return KmhHookVerdict.Allow;
        }

        private void OnMarketplaceBuy(MarketplaceBuyEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.BuyerUsername)) return;
            _stats.PurchasesByPlayer.TryGetValue(e.BuyerUsername, out int n);
            _stats.PurchasesByPlayer[e.BuyerUsername] = n + 1;
            _storage.Save("stats.json", _stats);
        }

        public void Shutdown() => _storage?.Save("stats.json", _stats);
    }
}
