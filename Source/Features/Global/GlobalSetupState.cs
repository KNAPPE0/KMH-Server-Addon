using System;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Global
{
    // One explicit state, not a pile of booleans: "linked but not registered" is where setup turns ambiguous.
    internal enum SetupStage
    {
        NotStarted,
        GlobalExplained,
        OwnerLinkPending,
        OwnerVerified,
        MetadataConfigured,
        PreflightPassed,
        RegistrationPending,
        Registered,
        Deferred,
        Failed,
    }

    // Deliberately holds nothing secret: no Steam ticket, no access token.
    internal sealed class GlobalSetupState
    {
        public int    SchemaVersion { get; set; } = 1;
        public string Stage         { get; set; } = SetupStage.NotStarted.ToString();

        // An identity, never a credential.
        public string OwnerUserId   { get; set; } = "";
        public string OwnerDisplay  { get; set; } = "";

        // A handle Global already knows; the human code is never written down.
        public string PendingLinkId { get; set; } = "";
        public long   PendingLinkExpiresUnix { get; set; }

        public string LastErrorCode { get; set; } = "";
        public string LastCorrelationId { get; set; } = "";
        public string DeferredUtc  { get; set; } = "";

        // Registered without an owner, for Guild Federation only. Cleared when an owner claims the identity.
        public bool   BackendRegistered { get; set; }

        // Never persisted: a stored copy would load last and silently overwrite Stage.
        [Newtonsoft.Json.JsonIgnore]
        public SetupStage Current
        {
            get => Enum.TryParse(Stage, out SetupStage s) ? s : SetupStage.NotStarted;
            set => Stage = value.ToString();
        }

        // An owner link in progress over a backend identity does not unregister it.
        [Newtonsoft.Json.JsonIgnore]
        public bool IsRegistered => Current == SetupStage.Registered || BackendRegistered;

        [Newtonsoft.Json.JsonIgnore]
        public bool IsOwnerRegistered => Current == SetupStage.Registered && !BackendRegistered;

        [Newtonsoft.Json.JsonIgnore]
        public bool HasDeferred => Current == SetupStage.Deferred;

        private static GlobalSetupState _current;
        public static GlobalSetupState Current_ => _current ?? (_current = LoadOrDefault());

        public static GlobalSetupState LoadOrDefault()
            => JsonFileStore.TryLoad(KmhDataPaths.GlobalSetupFile, out GlobalSetupState loaded) && loaded != null
                ? loaded : new GlobalSetupState();

        // Not EnsureGenerated: this is KMH state, not a config, and must not land in the `kmh reload` tables.
        public static void EnsureCreated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.GlobalSetupFile))
                JsonFileStore.Save(KmhDataPaths.GlobalSetupFile, new GlobalSetupState());
        }

        public static void Reload() => _current = LoadOrDefault();

        public bool Save()
        {
            bool ok = JsonFileStore.Save(KmhDataPaths.GlobalSetupFile, this);
            _current = this;
            return ok;
        }

        public bool Advance(SetupStage stage)
        {
            Current = stage;
            if (stage != SetupStage.Failed) { LastErrorCode = ""; LastCorrelationId = ""; }
            return Save();
        }

        public bool Fail(string code, string correlation)
        {
            Current = SetupStage.Failed;
            LastErrorCode = code ?? "";
            LastCorrelationId = correlation ?? "";
            return Save();
        }

        internal static GlobalSetupState ApplyForTest(GlobalSetupState s) { GlobalSetupState p = _current; _current = s; return p; }
    }
}
