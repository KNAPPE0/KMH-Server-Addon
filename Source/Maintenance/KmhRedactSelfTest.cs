using System.Collections.Generic;
using System.Linq;

namespace KMHServerAddon.Maintenance
{
    // Fake secrets only. A support bundle that leaks a token is worse than no support bundle.
    internal static class KmhRedactSelfTest
    {
        private const string FakeBotToken = "MTIzNDU2Nzg5MDEyMzQ1Njc4.GhIjKl.FAKEfakeFAKEfakeFAKEfakeFAKEfake123";
        private const string FakeApiKey   = "sk-FAKE-1234567890abcdefFAKE";
        private const string FakeWebhook  = "https://discord.com/api/webhooks/111222333/FAKEsecretFAKEsecret";

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            KmhRedact.ClearSecrets();

            string json = "{\n"
                        + "  \"Enabled\": true,\n"
                        + "  \"Bot\": { \"Token\": \"" + FakeBotToken + "\", \"GuildId\": 42 },\n"
                        + "  \"ApiKey\": \"" + FakeApiKey + "\",\n"
                        + "  \"WebhookUrl\": \"" + FakeWebhook + "\",\n"
                        + "  \"Nested\": { \"Deep\": { \"ClientSecret\": \"" + FakeApiKey + "\" } },\n"
                        + "  \"ChannelId\": 998877,\n"
                        + "  \"ServerName\": \"Test Server\"\n"
                        + "}";

            string redacted = KmhRedact.Json(json);
            r.Add(("redact: a bot token never survives config redaction", !redacted.Contains(FakeBotToken), ""));
            r.Add(("redact: nested secrets are found at any depth", !redacted.Contains(FakeApiKey), ""));
            r.Add(("redact: a webhook url is treated as a secret", !redacted.Contains("FAKEsecretFAKEsecret"), ""));
            r.Add(("redact: non-secret values are kept, so the bundle is still useful",
                   redacted.Contains("998877") && redacted.Contains("Test Server") && redacted.Contains("42"), ""));
            r.Add(("redact: the shape of the config survives", redacted.Contains("\"Bot\"") && redacted.Contains(KmhRedact.Mask), ""));

            // A token pasted into a log line has no key name to match on, so the value itself must be known.
            string log = $"[12:00] Discord: connecting with {FakeBotToken} now\r\n[12:01] all good";
            r.Add(("redact: a secret already seen in a config is scrubbed from log text too",
                   !KmhRedact.Text(log).Contains(FakeBotToken) && KmhRedact.Text(log).Contains("all good"), ""));

            KmhRedact.ClearSecrets();
            r.Add(("redact: a Discord-shaped token is caught even if this build never loaded it",
                   !KmhRedact.Text(log).Contains(FakeBotToken), ""));
            string labelled1 = KmhRedact.Text("token=hunter2hunter2hunter2");
            string labelled2 = KmhRedact.Text("Authorization: Bearer abcdefghijklmnop");
            r.Add(("redact: a labelled secret is caught whatever its shape",
                   !labelled1.Contains("hunter2hunter2hunter2") && !labelled2.Contains("abcdefghijklmnop"),
                   $"[{labelled1}] [{labelled2}]"));

            KmhRedact.RegisterSecret(FakeApiKey);
            r.Add(("redact: case differences do not let a secret through",
                   !KmhRedact.Text("key is " + FakeApiKey.ToUpperInvariant()).Contains(FakeApiKey.ToUpperInvariant()), ""));

            r.Add(("redact: key names are matched by meaning, not by exact spelling",
                   KmhRedact.IsSensitiveKey("Token") && KmhRedact.IsSensitiveKey("api_key")
                   && KmhRedact.IsSensitiveKey("ClientSecret") && KmhRedact.IsSensitiveKey("AuthorizationHeader")
                   && !KmhRedact.IsSensitiveKey("ChannelId") && !KmhRedact.IsSensitiveKey("ServerName"), ""));

            r.Add(("redact: a short value is not registered, so ordinary words are never masked",
                   KmhRedact.Text("the word secretly appears here").Contains("secretly"), ""));

            // A support bundle carries logs to a stranger, and a signed CDN url is a read capability for that file.
            const string SignedUrl = "https://cdn.discordapp.com/attachments/1/2/a.gif?ex=69572795&is=6955d615&hm=deadbeefcafe0123456789";
            string scrubbed = KmhRedact.Text("Media resolve abc: fetching " + SignedUrl);
            r.Add(("redact: a signed url's signature is masked, and the rest still identifies the media",
                   !scrubbed.Contains("deadbeefcafe0123456789") && scrubbed.Contains("hm=")
                   && scrubbed.Contains("cdn.discordapp.com/attachments/1/2/a.gif") && scrubbed.Contains("ex=69572795"),
                   scrubbed));
            r.Add(("redact: a url with no signature is left alone",
                   KmhRedact.Text("https://i.imgur.com/a.png") == "https://i.imgur.com/a.png", ""));

            (string Line, string Address)[] addresses =
            {
                ("refused 'x' from 203.0.113.7 - banned", "203.0.113.7"), ("from 198.51.100.23.", "198.51.100.23"),
                ("peer 192.0.2.1:25555 left", "192.0.2.1"), ("peer [2001:db8::7]:5000", "2001:db8::7"),
                ("2001:0db8:85a3:0000:0000:8a2e:0370:7334 joined", "2001:0db8:85a3:0000:0000:8a2e:0370:7334"),
                ("via ::ffff:192.0.2.9", "192.0.2.9"), ("loopback ::1 only", "::1"),
            };
            List<string> leaked = addresses.Select(a => KmhRedact.Addresses(a.Line))
                                           .Where((s, i) => s.Contains(addresses[i].Address) || !s.Contains(KmhRedact.AddressMask))
                                           .ToList();
            r.Add(("redact: an IPv4 or IPv6 address bound for Global is masked, with or without a port",
                   leaked.Count == 0, string.Join(" | ", leaked)));
            string[] kept =
            {
                "[2026-10-07T12:34:56.789Z] [warn] slow", "KMHAddon.RTShared v1.3.1.0", "mac aa:bb:cc:dd:ee:ff", "ratio 1.5.2", "Foo::Bar()",
            };
            List<string> changed = kept.Where(s => KmhRedact.Addresses(s) != s).ToList();
            r.Add(("redact: timestamps, versions, MAC-like and C++-like text are not taken for addresses",
                   changed.Count == 0, string.Join(" | ", changed.Select(KmhRedact.Addresses))));

            bool threw = false;
            try { KmhRedact.Json("not json at all { ["); KmhRedact.Text(null); KmhRedact.Json(null); KmhRedact.Addresses(null); }
            catch { threw = true; }
            r.Add(("redact: malformed input is still redacted and never throws", !threw, ""));

            KmhRedact.ClearSecrets();
            return r;
        }
    }
}
