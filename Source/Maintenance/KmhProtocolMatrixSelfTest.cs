using System.Collections.Generic;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Older peers are refused outright, so every direction of a protocol mismatch is pinned.
    internal static class KmhProtocolMatrixSelfTest
    {
        private const int V2 = 2;
        private const int V3 = 3;

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            r.Add(("Protocol: this build speaks v3", KmhProtocol.CurrentVersion == V3,
                   $"v{KmhProtocol.CurrentVersion}"));

            var cases = new (string Name, int ClientV, bool Enforced, KmhHandshakeHandler.HandshakeVerdict Want)[]
            {
                ("a v1.3.1 client on an enforcing server",      V3, true,  KmhHandshakeHandler.HandshakeVerdict.Compatible),
                ("a v1.3.1 client on a non-enforcing server",   V3, false, KmhHandshakeHandler.HandshakeVerdict.Compatible),
                ("a v1.3.0/v1.2.x client, enforcement ON",      V2, true,  KmhHandshakeHandler.HandshakeVerdict.RefuseAndHold),
                ("a v1.3.0/v1.2.x client, enforcement OFF",     V2, false, KmhHandshakeHandler.HandshakeVerdict.RefuseAndDisable),
                ("a client that declared no version at all",     0, true,  KmhHandshakeHandler.HandshakeVerdict.RefuseAndHold),
                ("a client from the future",                      9, false, KmhHandshakeHandler.HandshakeVerdict.RefuseAndDisable),
            };

            foreach (var c in cases)
            {
                KmhHandshakeHandler.HandshakeVerdict got = KmhHandshakeHandler.Judge(c.ClientV, c.Enforced);
                r.Add(($"Protocol: {c.Name}", got == c.Want, $"{got} (wanted {c.Want})"));
            }

            // Judged on the declared number alone: a verdict that hinged on hydration or a timeout could be raced.
            r.Add(("Protocol: a mismatch is never resolved by waiting",
                   KmhHandshakeHandler.Judge(V2, true) == KmhHandshakeHandler.Judge(V2, true)
                   && KmhHandshakeHandler.Judge(V2, false) != KmhHandshakeHandler.HandshakeVerdict.Compatible,
                   "deterministic, and no version but the current one is ever compatible"));

            // Enforcement is what decides whether a refusal locks the player out; it must not decide compatibility.
            r.Add(("Protocol: enforcement changes the consequence, never the verdict",
                   KmhHandshakeHandler.Judge(V3, true) == KmhHandshakeHandler.Judge(V3, false)
                   && KmhHandshakeHandler.Judge(V2, true) != KmhHandshakeHandler.Judge(V2, false),
                   "same compatibility either way; different consequence"));

            // Both directions carry a named reason: without this the far side sees a silent drop and blames RWT.
            r.Add(("Protocol: an incompatibility has its own message kind in both directions",
                   KmhProtocol.Kind.Incompatible == "kmh.hello.incompatible",
                   KmhProtocol.Kind.Incompatible));

            return r;
        }
    }
}
