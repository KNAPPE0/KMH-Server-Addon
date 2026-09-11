using System;
using System.Collections.Generic;
using KMH.Sdk.Server;
using KMH.Sdk.Server.Apis;
using KMH.Sdk.Server.Events;

namespace WelcomeBonus
{
    // Template: a one-shot silver bonus on first join. The loader requires a parameterless constructor.
    public sealed class WelcomeBonusExtension : IKmhServerExtension
    {
        private const int    BonusAmount    = 100;
        private const string WelcomeMessage = "Welcome to the server! 100 silver has been added to your treasury.";

        public string Name    => "Welcome Bonus";
        public string Version => "1.0.0";

        private IKmhServerHost _host;
        private IExtensionStorage _storage;

        // Persisted, or a reconnect in the same session claims the bonus again.
        private sealed class State
        {
            public List<string> Welcomed { get; set; } = new List<string>();
        }
        private State _state = new State();

        public void Register(IKmhServerHost host)
        {
            _host    = host;
            _storage = host.StorageFor(Name);

            if (_storage.TryLoad("welcomed_users.json", out State loaded) && loaded != null)
            {
                _state = loaded;
            }

            host.Events.PlayerJoined += OnPlayerJoined;

            host.Log.Info($"Registered. Bonus amount: {BonusAmount} silver. {_state.Welcomed.Count} users already welcomed.");
        }

        public void Shutdown()
        {
            // The event bus outlives the extension, so an un-removed handler can be called half-disposed.
            if (_host != null)
            {
                _host.Events.PlayerJoined -= OnPlayerJoined;
            }
            _host = null;
        }

        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            if (string.IsNullOrEmpty(e?.Username)) return;

            foreach (string u in _state.Welcomed)
            {
                if (string.Equals(u, e.Username, StringComparison.OrdinalIgnoreCase))
                {
                    _host.Log.Verbose($"{e.Username} already welcomed previously - skipping bonus.");
                    return;
                }
            }

            // Check the result: a discarded false silently burns the silver.
            if (!_host.Treasury.DepositSilver(e.Username, BonusAmount, note: $"{Name} extension"))
            {
                _host.Log.Warn($"Treasury rejected the bonus deposit for {e.Username}.");
                return;
            }

            _state.Welcomed.Add(e.Username);
            _storage.Save("welcomed_users.json", _state);

            _host.Log.Info($"Welcomed {e.Username} with {BonusAmount} silver. ({WelcomeMessage})");
        }
    }
}
