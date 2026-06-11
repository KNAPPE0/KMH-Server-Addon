using System;
using System.Collections.Generic;
using KMH.Sdk.Server;
using KMH.Sdk.Server.Apis;
using KMH.Sdk.Server.Events;

namespace WelcomeBonus
{
    // Example KMH server-side extension. Drops 100 silver into a new
    // player's treasury the first time they connect, with a one-shot
    // "welcomed" flag persisted so repeated reconnects don't re-claim.
    //
    // What this demonstrates:
    //   1. Class implementing IKmhServerExtension with a parameterless
    //      ctor (required by the loader).
    //   2. Capturing the host on Register so event handlers + APIs
    //      stay reachable later.
    //   3. Subscribing to IKmhEvents.PlayerJoined.
    //   4. Reading + writing per-extension state via IExtensionStorage.
    //   5. Calling ITreasuryApi.DepositSilver on the SDK contract.
    //   6. Logging through IServerLog so output is prefixed
    //      "[ext:Welcome Bonus]".
    //
    // Tweak the BonusAmount or WelcomeMessage constants below.
    public sealed class WelcomeBonusExtension : IKmhServerExtension
    {
        private const int    BonusAmount    = 100;
        private const string WelcomeMessage = "Welcome to the server! 100 silver has been added to your treasury.";

        public string Name    => "Welcome Bonus";
        public string Version => "1.0.0";

        private IKmhServerHost _host;
        private IExtensionStorage _storage;

        // Tracked-state file: one-line list of usernames already welcomed
        // so a player who disconnects + reconnects in the same session
        // doesn't get repeat bonuses.
        private sealed class State
        {
            public List<string> Welcomed { get; set; } = new List<string>();
        }
        private State _state = new State();

        public void Register(IKmhServerHost host)
        {
            _host    = host;
            _storage = host.StorageFor(Name);

            // Load persisted welcomed-users list. Missing file = empty.
            if (_storage.TryLoad("welcomed_users.json", out State loaded) && loaded != null)
            {
                _state = loaded;
            }

            host.Events.PlayerJoined += OnPlayerJoined;

            host.Log.Info($"Registered. Bonus amount: {BonusAmount} silver. {_state.Welcomed.Count} users already welcomed.");
        }

        public void Shutdown()
        {
            // Unsubscribe defensively. The event bus is a singleton that
            // outlives extensions on graceful shutdown, so leaving the
            // subscription wouldn't crash anything but could deliver
            // events to a half-disposed handler if Shutdown logic ever
            // grows.
            if (_host != null)
            {
                _host.Events.PlayerJoined -= OnPlayerJoined;
            }
            _host = null;
        }

        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            if (string.IsNullOrEmpty(e?.Username)) return;

            // Already welcomed? Skip.
            foreach (string u in _state.Welcomed)
            {
                if (string.Equals(u, e.Username, StringComparison.OrdinalIgnoreCase))
                {
                    _host.Log.Verbose($"{e.Username} already welcomed previously - skipping bonus.");
                    return;
                }
            }

            // Deposit the bonus. Returns false if the treasury reports
            // an error (rare - invalid amount or missing username).
            if (!_host.Treasury.DepositSilver(e.Username, BonusAmount, note: $"{Name} extension"))
            {
                _host.Log.Warn($"Treasury rejected the bonus deposit for {e.Username}.");
                return;
            }

            // Persist the welcomed flag so subsequent reconnects skip.
            _state.Welcomed.Add(e.Username);
            _storage.Save("welcomed_users.json", _state);

            _host.Log.Info($"Welcomed {e.Username} with {BonusAmount} silver. ({WelcomeMessage})");
        }
    }
}
