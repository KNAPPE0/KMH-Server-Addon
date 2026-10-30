using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Features.Global;

namespace KMHServerAddon.Maintenance
{
    // The vector below is pinned in KMH Global's CanonicalVectorTests too, so drift fails a suite, not production.
    internal static class KmhGlobalSigningSelfTest
    {
        private const string ExpectedHeartbeatVector = "304bd81bfa1697de8fc4d9bee4e3c72969d4a87b94eab8d9412f3dc9448eec46";
        // The domain check only compares domain strings, so a list added on one side only is caught by these bytes.
        private const string ExpectedModProfileVector = "b7e9d9afe9c151d108e6db7124a293e126f351603398cc7cf8fec3c83488ceba";
        // A Federation report batch carries a list of records, which the domain and field-chain checks cannot see into.
        private const string ExpectedFederationContributeVector = "5ac1d4a198c0a10cb808013b5118821f546e6d4d6f91ae6f01385c9e3030cbd1";
        // The rebind proof normalizes its code and sorts its links before signing; the key rotation is signed by two keys.
        private const string ExpectedServerRebindVector = "55459c5479fc95b7ccfe29ed7144ff968101d9e3bbd701c5d25a6803f80348ac";
        private const string ExpectedKeyRotationVector = "cb952274f32a0fe780546c4c8a062f37bd9922a47c990280f9ae5908dc989c41";

        private static RebindProveBody RebindVector() => new RebindProveBody
        {
            Code = "ab12-cd34 ef56", ServerId = "0123456789abcdef0123456789abcdef", PublicKey = "pk:1\nwith a newline",
            IssuedAtUnix = 1_757_000_000, Nonce = "nonce-rebind",
            Links = new List<string> { new string('f', 32), new string('0', 32), new string('a', 32) },
        };

        private static FederationContributeBody ContributeVector() => new FederationContributeBody
        {
            ServerId = "0123456789abcdef0123456789abcdef", IssuedAtUnix = 1700000000, Nonce = "nonce-1", Generation = 2,
            LinkId = "fedcba9876543210fedcba9876543210", BatchId = "batch-1",
            Entries = new System.Collections.Generic.List<FederationContributionEntry>
            {
                new FederationContributionEntry { ReportId = "r-1", Category = "quest", Amount = 1,
                                                  Player = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", OccurredAtUnix = 1699999990 },
                new FederationContributionEntry { ReportId = "r-2", Category = "site", Amount = 3, Player = "", OccurredAtUnix = 1699999995 },
            },
        };

        private static ModProfileBody ModVector() => new ModProfileBody
        {
            ServerId        = "0123456789abcdef0123456789abcdef",
            IssuedAtUnix    = 1_757_000_000,
            Nonce           = "ffffffffffffffffffffffffffffffff",
            Policy          = "Enforced",
            EnforceSettings = true,
            EnforceOrder    = true,
            Required        = new[] { "Core", "Royalty" },
            Optional        = new[] { "NiceTextures" },
            // Non-empty on purpose: a side that drops either list entirely produces different bytes.
            Order           = new[] { "ludeon.rimworld", "ludeon.rimworld.royalty" },
            OrderHash       = "96B1CA5E9008E1930A5106749DFD035B1742A0C5B345C6C71AA111B60FAB45B3",
            Hash            = "deadbeef",
            Revision        = 9,
            // Field for field Global's vector: only the two pinned hashes agreeing proves both sides sign the same bytes.
            Entries = new[]
            {
                new Features.Mods.ModEntry { Name = "Core",    Role = "required", Kind = "local",
                    Id = "Core",       PackageId = "ludeon.rimworld",         Author = "Ludeon" },
                new Features.Mods.ModEntry { Name = "Royalty", Role = "required", Kind = "expansion",
                    Id = "Royalty",    PackageId = "ludeon.rimworld.royalty", Author = "Ludeon" },
                new Features.Mods.ModEntry { Name = "NiceTextures", Role = "optional", Kind = "workshop",
                    Id = "2009463077", PackageId = "someone.nicetextures",    Author = "Someone" },
            },
        };

        private static HeartbeatBody Vector() => new HeartbeatBody
        {
            ServerId         = "0123456789abcdef0123456789abcdef",
            Generation       = 3,
            Sequence         = 42,
            IssuedAtUnix     = 1_757_000_000,
            Nonce            = "ffffffffffffffffffffffffffffffff",
            Status           = "Online",
            Endpoint         = "play.example.net:5000",
            Players          = 17,
            MaxPlayers       = 64,
            RimWorldVersion  = "1.6",
            RwtVersion       = "26.8.31.1",
            KmhVersion       = "1.3.1",
            Protocol         = 2,
            Capabilities     = new List<string> { "roadworks", "frontier", "chat" },
            ManifestRevision = 9,
            ManifestHash     = "deadbeef",
            MetadataRevision = 3,
            // True rather than the default, so a side that drops the field entirely produces different bytes.
            RequiresPassword = true,
        };

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Global: signed heartbeat bytes match Colony Nexus Global's pinned vector", () =>
            {
                string actual = Hex(SHA256.HashData(Vector().Canonical()));
                return (actual == ExpectedHeartbeatVector, actual == ExpectedHeartbeatVector
                    ? "byte-identical to the Global implementation"
                    : $"DRIFT - this server produces {actual}, Global expects {ExpectedHeartbeatVector}");
            });

            Check("Global: signed mod manifest bytes match Colony Nexus Global's pinned vector", () =>
            {
                string actual = Hex(SHA256.HashData(ModVector().Canonical()));
                return (actual == ExpectedModProfileVector, actual == ExpectedModProfileVector
                    ? "byte-identical to the Global implementation"
                    : $"DRIFT - this server produces {actual}, Global expects {ExpectedModProfileVector}");
            });

            Check("Global: signed federation report bytes match Colony Nexus Global's pinned vector", () =>
            {
                string actual = Hex(SHA256.HashData(ContributeVector().Canonical()));
                return (actual == ExpectedFederationContributeVector, actual == ExpectedFederationContributeVector
                    ? "byte-identical to the Global implementation"
                    : $"DRIFT - this server produces {actual}, Global expects {ExpectedFederationContributeVector}");
            });

            Check("Global: signed rebind proof and key rotation bytes match Colony Nexus Global's pinned vectors", () =>
            {
                string rebind = Hex(SHA256.HashData(RebindVector().Canonical()));
                RebindProveBody reordered = RebindVector();
                reordered.Links.Reverse();
                reordered.Code = "AB12CD34EF56";
                string same = Hex(SHA256.HashData(reordered.Canonical()));
                string rotation = Hex(SHA256.HashData(new RecoverBody
                {
                    ChallengeId = "c0ffee0000000000000000000000c0ff", ServerId = "0123456789abcdef0123456789abcdef", PublicKey = "pk:2",
                }.Canonical()));
                bool ok = rebind == ExpectedServerRebindVector && same == rebind && rotation == ExpectedKeyRotationVector;
                return (ok, ok ? "byte-identical to the Global implementation, whatever order the links are listed in"
                              : $"DRIFT - rebind {rebind} (reordered {same}), rotation {rotation}");
            });

            Check("Global: the signature format is fixed-width P1363, not DER", () =>
            {
                using ECDsa key = KmhGlobalSigning.CreateKey();
                byte[] sig = KmhGlobalSigning.FromBase64Url(KmhGlobalSigning.Sign(key, Vector().Canonical()));
                bool ok = sig != null && sig.Length == KmhGlobalSigning.SignatureBytes;
                return (ok, ok ? $"{sig.Length} bytes" : $"expected {KmhGlobalSigning.SignatureBytes} bytes, got {sig?.Length ?? -1}");
            });

            Check("Global: a field's contents cannot shift the field boundary", () =>
            {
                byte[] a = KmhCanonicalPayload.For("d").Add("x\n5:y").Add("z").Build();
                byte[] b = KmhCanonicalPayload.For("d").Add("x").Add("y").Add("z").Build();
                bool ok = Hex(a) != Hex(b);
                return (ok, ok ? "length-prefixed fields are unambiguous" : "TWO DIFFERENT FIELD SPLITS PRODUCED THE SAME BYTES");
            });

            Check("Global: capability order does not change the signature, membership does", () =>
            {
                string one = KmhCanonicalPayload.HashSet(new[] { "a", "b", "c" });
                string two = KmhCanonicalPayload.HashSet(new[] { "c", "b", "a" });
                string three = KmhCanonicalPayload.HashSet(new[] { "a", "b" });
                bool ok = one == two && one != three;
                return (ok, ok ? "sorted set hash" : $"order={one == two} membership={one != three}");
            });

            Check("Global: the private key is never part of anything sent", () =>
            {
                using ECDsa key = KmhGlobalSigning.CreateKey();
                string pub = KmhGlobalSigning.ExportPublicKey(key);
                string priv = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
                bool ok = !pub.Contains(priv) && KmhGlobalSigning.FromBase64Url(pub) != null;
                return (ok, ok ? "registration carries the public half only" : "THE EXPORTED KEY CONTAINS PRIVATE MATERIAL");
            });

            Check("Global: owner display reads the real display_name response field", () =>
            {
                var body = Newtonsoft.Json.Linq.JObject.Parse("{\"owner_user_id\":\"authoritative-id\",\"display_name\":\"Owner Persona\"}");
                return (KmhGlobalRegistration.ReadOwnerDisplay(body) == "Owner Persona", "display_name is display-only");
            });

            // The poll id redeems the owner's approval, so the poll path must never be logged, even on timeout.
            Check("Global: an owner link's poll id never reaches a log line", () =>
            {
                string id = Guid.NewGuid().ToString("N");
                string shown = KmhGlobalClient.Loggable($"/api/v1/owner-links/{id}/poll");
                bool hidden = shown == "/api/v1/owner-links/{id}/poll";
                bool others = KmhGlobalClient.Loggable("/api/v1/servers/heartbeat") == "/api/v1/servers/heartbeat";
                bool noRaw = true;
                foreach (string file in new[] { "Features/Global/KmhGlobalClient.cs", "Features/Global/KmhGlobalSafety.cs" })
                {
                    string src = KmhSecurityAuditSelfTest.ReadSource(file);
                    noRaw &= src != null && !System.Text.RegularExpressions.Regex.IsMatch(src, @"ServerLog\.\w+\(\$""[^""\r\n]*\{path\}");
                }
                return (hidden && others && noRaw, $"masked={hidden} othersUnchanged={others} noRawPathInLogs={noRaw}");
            });

            Check("Global: an absent owner display never prints a blank name", () =>
                (KmhGlobalRegistration.OwnerVerifiedMessage(null) == "Global: owner verified."
                    && KmhGlobalRegistration.OwnerVerifiedMessage("  ") == "Global: owner verified."
                    && KmhGlobalRegistration.OwnerVerifiedMessage("Owner") == "Global: owner verified as Owner.", "safe optional display"));

            // Tickets are trusted INSTEAD of asking Global, so every failure mode is asserted with a local pinned key.
            using (ECDsa directory = KmhGlobalSigning.CreateKey())
            {
                string pub = KmhGlobalSigning.ExportPublicKey(directory);
                string myId = new string('a', 32);
                DateTimeOffset now = DateTimeOffset.UtcNow;

                KmhDirectoryKey.ResetForTest();
                AdoptKey(pub);

                Func<string, long, KmhJoinTicket> make = (audience, expires) =>
                {
                    KmhJoinTicket t = new KmhJoinTicket
                    {
                        PlayerGlobalId = new string('b', 32), Username = "knappe", ServerId = audience,
                        IssuedAtUnix = now.ToUnixTimeSeconds(), ExpiresAtUnix = expires,
                        Nonce = Guid.NewGuid().ToString("N"), KeyFingerprint = KmhGlobalSigning.Fingerprint(pub),
                    };
                    t.Signature = KmhGlobalSigning.Sign(directory, t.Canonical());
                    return t;
                };

                long good = now.AddMinutes(2).ToUnixTimeSeconds();

                KmhJoinTicketVerifier.ResetForTest();
                Check("Global: a ticket signed for this server verifies", () =>
                    (KmhJoinTicketVerifier.Verify(make(myId, good), myId, now) == TicketResult.Ok, ""));

                Check("Global: a ticket addressed to another server is refused", () =>
                    (KmhJoinTicketVerifier.Verify(make(new string('c', 32), good), myId, now)
                        == TicketResult.WrongAudience, "KMH-CON-403"));

                Check("Global: an expired ticket is refused", () =>
                    (KmhJoinTicketVerifier.Verify(make(myId, now.AddSeconds(-1).ToUnixTimeSeconds()), myId, now)
                        == TicketResult.Expired, "KMH-CON-401"));

                Check("Global: the same ticket cannot be presented twice", () =>
                {
                    KmhJoinTicketVerifier.ResetForTest();
                    KmhJoinTicket once = make(myId, good);
                    TicketResult first = KmhJoinTicketVerifier.Verify(once, myId, now);
                    TicketResult again = KmhJoinTicketVerifier.Verify(once, myId, now);
                    return (first == TicketResult.Ok && again == TicketResult.Replayed, $"{first} then {again}");
                });

                Check("Global: edited content does not survive the signature", () =>
                {
                    KmhJoinTicketVerifier.ResetForTest();
                    KmhJoinTicket t = make(myId, good);
                    t.PlayerGlobalId = new string('d', 32);
                    return (KmhJoinTicketVerifier.Verify(t, myId, now) == TicketResult.BadSignature, "KMH-CON-400");
                });

                Check("Global: a ticket from a different directory key is refused", () =>
                {
                    KmhJoinTicketVerifier.ResetForTest();
                    using ECDsa impostor = KmhGlobalSigning.CreateKey();
                    KmhJoinTicket t = make(myId, good);
                    t.Signature = KmhGlobalSigning.Sign(impostor, t.Canonical());
                    return (KmhJoinTicketVerifier.Verify(t, myId, now) == TicketResult.BadSignature, "wrong signer");
                });

                Check("Global: a swapped directory key is refused, never silently adopted", () =>
                {
                    using ECDsa other = KmhGlobalSigning.CreateKey();
                    string otherPub = KmhGlobalSigning.ExportPublicKey(other);
                    bool adopted = AdoptKey(otherPub);
                    return (!adopted && KmhDirectoryKey.Fingerprint == KmhGlobalSigning.Fingerprint(pub),
                            "pinned key held");
                });

                // A pin must not survive a directory move: real tickets would fail and the old key would still sign.
                Check("Global: a key pinned from one directory is not trusted after moving to another", () =>
                {
                    GlobalConfig had = GlobalConfig.Current;
                    try
                    {
                        GlobalConfig.ApplyForTest(GlobalConfig.ClampForTest(new GlobalConfig { BaseUrl = "https://preview.example" }));
                        bool forgot = !KmhDirectoryKey.Known && KmhJoinTicketVerifier.Verify(make(myId, good), myId, now) == TicketResult.NoKey;
                        using ECDsa moved = KmhGlobalSigning.CreateKey();
                        string movedPub = KmhGlobalSigning.ExportPublicKey(moved);
                        bool adopted = AdoptKey(movedPub);
                        bool pinnedThere = adopted && KmhDirectoryKey.Fingerprint == KmhGlobalSigning.Fingerprint(movedPub);
                        GlobalConfig.ApplyForTest(had);
                        bool notBack = !KmhDirectoryKey.Known;
                        return (forgot && pinnedThere && notBack, $"oldPinForgotten={forgot} newDirectoryPinned={pinnedThere} notTrustedBackHome={notBack}");
                    }
                    finally
                    {
                        GlobalConfig.ApplyForTest(had);
                        KmhDirectoryKey.ResetForTest();
                        AdoptKey(pub);
                    }
                });

                Check("Global: a key whose fingerprint does not match itself is refused", () =>
                {
                    KmhDirectoryKey.ResetForTest();
                    bool adopted = KmhDirectoryKey.Adopt(Newtonsoft.Json.Linq.JObject.FromObject(new
                    {
                        algorithm = "ES256", public_key = pub, fingerprint = new string('f', 64),
                    }));
                    return (!adopted && !KmhDirectoryKey.Known, "mismatched label refused");
                });

                // Without a pinned key nothing can be verified, and "unverified" must never read as "fine".
                Check("Global: with no directory key a ticket is unverified rather than accepted", () =>
                {
                    KmhDirectoryKey.ResetForTest();
                    KmhJoinTicketVerifier.ResetForTest();
                    return (KmhJoinTicketVerifier.Verify(make(myId, good), myId, now) == TicketResult.NoKey, "");
                });

                KmhDirectoryKey.ResetForTest();
                KmhJoinTicketVerifier.ResetForTest();
            }

            // Only an array replaces the held ban list; anything else keeps it, or a quiet Global would re-admit every ban.
            Check("Global: a heartbeat without a ban list does not clear the one held", () =>
            {
                KmhNetworkBans.ResetForTest();
                KmhNetworkBans.OnHeartbeatResponse(Newtonsoft.Json.Linq.JArray.FromObject(new[]
                {
                    new { user_id = new string('b', 32), steam_id64 = 76561198000000123L, reason = "raiding", until = 0 },
                }));
                int held = KmhNetworkBans.Count;

                KmhNetworkBans.OnHeartbeatResponse(null);
                bool kept = KmhNetworkBans.Count == held;
                bool found = KmhNetworkBans.Find(new string('b', 32), DateTimeOffset.UtcNow.ToUnixTimeSeconds()) != null;
                KmhNetworkBans.ResetForTest();
                return (held == 1 && kept && found, $"held={held} kept={kept} found={found}");
            });

            Check("Global: a network ban with an expiry stops applying once it passes", () =>
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                KmhNetworkBans.ResetForTest();
                KmhNetworkBans.OnHeartbeatResponse(Newtonsoft.Json.Linq.JArray.FromObject(new[]
                {
                    new { user_id = new string('e', 32), steam_id64 = 0L, reason = "cooling off", until = now + 60 },
                }));
                bool live = KmhNetworkBans.Find(new string('e', 32), now) != null;
                bool gone = KmhNetworkBans.Find(new string('e', 32), now + 120) == null;
                KmhNetworkBans.ResetForTest();
                return (live && gone, $"live={live} gone={gone}");
            });

            return r;
        }

        private static bool AdoptKey(string publicKey) => KmhDirectoryKey.Adopt(Newtonsoft.Json.Linq.JObject.FromObject(new
        {
            algorithm = "ES256", public_key = publicKey, fingerprint = KmhGlobalSigning.Fingerprint(publicKey),
        }));

        private static string Hex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
