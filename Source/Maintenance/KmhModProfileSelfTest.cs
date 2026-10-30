using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Global;
using KMHServerAddon.Features.Mods;

namespace KMHServerAddon.Maintenance
{
    // The browser draws what this produces, so every state a listing can show has to be reachable and distinct here.
    internal static class KmhModProfileSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Mods: the policy name is one of the four the browser knows", () =>
            {
                string[] want = { "Unknown", "AllowAll", "NotSet", "Enforced" };
                var seen = new List<string>();
                foreach (ModPolicy p in Enum.GetValues(typeof(ModPolicy)))
                    seen.Add(new ModProfileSnapshot { Policy = p }.PolicyName);
                bool ok = seen.Count == want.Length;
                foreach (string w in want) if (!seen.Contains(w)) ok = false;
                return (ok, ok ? string.Join(", ", seen.ToArray()) : "policy names and enum disagree");
            });

            Check("Mods: the hash covers the enforced lists", () =>
            {
                ModProfileSnapshot a = Snap(ModPolicy.Enforced, new[] { "Core", "Harmony" }, new string[0]);
                ModProfileSnapshot b = Snap(ModPolicy.Enforced, new[] { "Core" }, new string[0]);
                bool ok = ModProfile.HashOf(a) != ModProfile.HashOf(b);
                return (ok, ok ? "dropping a required mod changes the hash" : "A CHANGED MOD LIST HASHES THE SAME");
            });

            // A manual snapshot's folder name never matches a Workshop client's, so it silently refuses every player.
            Check("Mods: a version-stamped RimWorld Together entry is named as unmatchable", () =>
            {
                ModProfileSnapshot bad = Snap(ModPolicy.Enforced,
                    new[] { "Core", "RimWorld Together - 26.6.9.1 Snapshot", ModProfile.KmhModName },
                    new string[0]);
                string found = ModProfile.UnmatchableRwtEntry(bad);
                return (found == "RimWorld Together - 26.6.9.1 Snapshot", found ?? "(not detected)");
            });

            Check("Mods: the correct RimWorld Together entry raises nothing", () =>
            {
                ModProfileSnapshot good = Snap(ModPolicy.Enforced,
                    new[] { "Core", "RimWorld Together", ModProfile.KmhModName }, new string[0]);
                string found = ModProfile.UnmatchableRwtEntry(good);
                return (found == null, found ?? "clean");
            });

            Check("Mods: KMH's own mod is not mistaken for a bad RimWorld Together entry", () =>
            {
                ModProfileSnapshot only = Snap(ModPolicy.Enforced,
                    new[] { "Core", ModProfile.KmhModName, ModProfile.LegacyKmhModName }, new string[0]);
                string found = ModProfile.UnmatchableRwtEntry(only);
                return (found == null, found ?? "clean");
            });

            // RWT requires every entry, so a stamped one still refuses everybody even beside the correct one.
            Check("Mods: a stamped RimWorld Together entry is named even when the correct one is listed too", () =>
            {
                ModProfileSnapshot both = Snap(ModPolicy.Enforced,
                    new[] { "Core", "RimWorld Together", "RimWorld Together - 26.6.9.1 Snapshot" }, new string[0]);
                string found = ModProfile.UnmatchableRwtEntry(both);
                return (found == "RimWorld Together - 26.6.9.1 Snapshot", found ?? "(not detected)");
            });

            // The rebrand changed KMH's reported mod name, so a list naming the old one refuses every player.
            Check("Mods: a required list naming KMH's pre-1.3.1 mod name is called out", () =>
            {
                ModProfileSnapshot old = Snap(ModPolicy.Enforced,
                    new[] { "Core", "RimWorld Together", ModProfile.LegacyKmhModName }, new string[0]);
                string found = ModProfile.StaleKmhEntry(old);
                return (found == ModProfile.LegacyKmhModName, found ?? "(not detected)");
            });

            Check("Mods: the current KMH mod name raises nothing", () =>
            {
                ModProfileSnapshot now = Snap(ModPolicy.Enforced,
                    new[] { "Core", "RimWorld Together", ModProfile.KmhModName }, new string[0]);
                string found = ModProfile.StaleKmhEntry(now);
                return (found == null, found ?? "clean");
            });

            // Listing both is not "fixed": RWT requires every entry, so the old one still refuses every player.
            Check("Mods: the old KMH name is called out even when the new one is listed beside it", () =>
            {
                ModProfileSnapshot both = Snap(ModPolicy.Enforced,
                    new[] { "Core", ModProfile.KmhModName, ModProfile.LegacyKmhModName }, new string[0]);
                string found = ModProfile.StaleKmhEntry(both);
                return (found == ModProfile.LegacyKmhModName, found ?? "(not detected)");
            });

            Check("Mods: a server enforcing nothing is never warned about the old KMH name", () =>
            {
                bool ok = ModProfile.StaleKmhEntry(Snap(ModPolicy.AllowAll,
                              new[] { ModProfile.LegacyKmhModName }, new string[0])) == null
                       && ModProfile.StaleKmhEntry(Snap(ModPolicy.NotSet,
                              new[] { ModProfile.LegacyKmhModName }, new string[0])) == null
                       && ModProfile.StaleKmhEntry(Snap(ModPolicy.Enforced,
                              new string[0], new[] { ModProfile.LegacyKmhModName })) == null;
                return (ok, ok ? "only an enforced REQUIRED entry refuses anybody" : "WARNED ANYWAY");
            });

            // The broken list usually never changes, so the warning latches per name rather than per revision.
            Check("Mods: a name warning is said once per name, however often the profile is read", () =>
            {
                ModProfile.ForgetNameWarningsForTest();
                bool first  = ModProfile.ShouldSayOnce("kmh:" + ModProfile.LegacyKmhModName);
                bool again  = ModProfile.ShouldSayOnce("kmh:" + ModProfile.LegacyKmhModName);
                bool other  = ModProfile.ShouldSayOnce("rwt:RimWorld Together - 26.6.9.1 Snapshot");
                ModProfile.ForgetNameWarningsForTest();
                bool afresh = ModProfile.ShouldSayOnce("kmh:" + ModProfile.LegacyKmhModName);
                bool ok = first && !again && other && afresh;
                return (ok, ok ? "said once, repeated reads stay quiet, a different name still speaks"
                              : $"first={first}, again={again}, otherName={other}, afterReset={afresh}");
            });

            // Pins both names here; the contract check compares the current one against the client's About.xml.
            Check("Mods: the two KMH mod names are the released ones and are distinct", () =>
            {
                bool ok = ModProfile.KmhModName == "KMH: Colony Nexus"
                       && ModProfile.LegacyKmhModName == "KMH Patch for RimWorld Together"
                       && ModProfile.KmhModName != ModProfile.LegacyKmhModName;
                return (ok, ok ? ModProfile.KmhModName : "THE REBRAND MOVED AND THIS DID NOT");
            });

            Check("Mods: a server enforcing nothing is never warned about mod names", () =>
            {
                ModProfileSnapshot open = Snap(ModPolicy.AllowAll,
                    new[] { "RimWorld Together - 26.6.9.1 Snapshot" }, new string[0]);
                ModProfileSnapshot unset = Snap(ModPolicy.NotSet,
                    new[] { "RimWorld Together - 26.6.9.1 Snapshot" }, new string[0]);
                bool ok = ModProfile.UnmatchableRwtEntry(open) == null
                       && ModProfile.UnmatchableRwtEntry(unset) == null;
                return (ok, ok ? "AllowAll and NotSet refuse nobody, so there is nothing to warn about" : "WARNED ANYWAY");
            });

            Check("Mods: required and optional are not interchangeable in the hash", () =>
            {
                ModProfileSnapshot a = Snap(ModPolicy.Enforced, new[] { "Core" }, new[] { "Rimatomics" });
                ModProfileSnapshot b = Snap(ModPolicy.Enforced, new[] { "Core", "Rimatomics" }, new string[0]);
                bool ok = ModProfile.HashOf(a) != ModProfile.HashOf(b);
                return (ok, ok ? "moving a mod between lists changes the hash" : "REQUIRED AND OPTIONAL HASH ALIKE");
            });

            Check("Mods: the policy flags are part of the hash", () =>
            {
                ModProfileSnapshot a = Snap(ModPolicy.Enforced, new[] { "Core" }, new string[0]);
                ModProfileSnapshot b = Snap(ModPolicy.Enforced, new[] { "Core" }, new string[0]);
                b.EnforceSettings = true;
                bool ok = ModProfile.HashOf(a) != ModProfile.HashOf(b);
                return (ok, ok ? "turning settings enforcement on republishes" : "A POLICY CHANGE IS INVISIBLE");
            });

            Check("Mods: a name containing a separator cannot forge another list's hash", () =>
            {
                ModProfileSnapshot a = Snap(ModPolicy.Enforced, new[] { "A\nB" }, new string[0]);
                ModProfileSnapshot b = Snap(ModPolicy.Enforced, new[] { "A", "B" }, new string[0]);
                bool ok = ModProfile.HashOf(a) != ModProfile.HashOf(b);
                return (ok, ok ? "length-prefixed entries keep the split honest" : "A CRAFTED MOD NAME COLLIDES");
            });

            Check("Mods: AllowAll and an empty enforced list are different states", () =>
            {
                bool ok = ModProfile.HashOf(Snap(ModPolicy.AllowAll, new string[0], new string[0]))
                       != ModProfile.HashOf(Snap(ModPolicy.NotSet, new string[0], new string[0]));
                return (ok, ok ? "'any mods' never reads as 'none published'" : "TWO POLICIES SHARE ONE HASH");
            });

            Check("Global: the heartbeat reports the profile the browser will compare against", () =>
            {
                ModProfileSnapshot snap = Snap(ModPolicy.Enforced, new[] { "Core", "Harmony" }, new[] { "Rimatomics" });
                ModProfile.ApplyForTest(snap, 12);
                bool ok = GlobalStatusProbe.ManifestHash() == snap.Hash
                       && GlobalStatusProbe.ModPolicy() == "Enforced"
                       && GlobalStatusProbe.ModCount() == 3;
                return (ok, ok ? "hash, policy and count all come from one snapshot"
                              : $"hash={GlobalStatusProbe.ManifestHash()} policy={GlobalStatusProbe.ModPolicy()} count={GlobalStatusProbe.ModCount()}");
            });

            Check("Global: a host is published only when the owner set one", () =>
            {
                GlobalConfig cfg = new GlobalConfig { PublicHost = "" };
                GlobalConfig prev = GlobalConfig.ApplyForTest(cfg);
                try
                {
                    // At most ":<port>", which Global cannot publish: it only pairs it with the address it saw.
                    string e = GlobalStatusProbe.Endpoint();
                    bool blank = e == "" || (e.StartsWith(":") && e.Length > 1 && e.IndexOf(':', 1) < 0);
                    // A scheme or a path here would be pasted straight into a connect box that wants a host.
                    GlobalConfig.ClampForTest(new GlobalConfig { PublicHost = "https://example.com/x" });
                    GlobalConfig bad = new GlobalConfig { PublicHost = "https://example.com/x" };
                    GlobalConfig.ClampForTest(bad);
                    return (blank && bad.PublicHost == "",
                            blank && bad.PublicHost == "" ? "no host, no address; a url is refused"
                                                          : $"endpoint='{GlobalStatusProbe.Endpoint()}' host='{bad.PublicHost}'");
                }
                finally { GlobalConfig.ApplyForTest(prev); }
            });

            // RWT's FL_ModOrder: DataString is RimWorld's ModsConfig.xml verbatim; Hash is SHA-256 of those bytes.
            const string Doc = "<ModsConfigData><version>1.6.4871 rev590</version><activeMods>"
                             + "<li>brrainz.harmony</li><li>ludeon.rimworld</li>"
                             + "<li>ludeon.rimworld.royalty</li></activeMods></ModsConfigData>";
            string docHash = RwtModOrder.Sha256Upper(System.Text.Encoding.UTF8.GetBytes(Doc));

            Check("Mods: a corrected Workshop id changes the hash, so the profile is republished", () =>
            {
                ModProfileSnapshot a = Snap(ModPolicy.Enforced, new[] { "Harmony" }, new string[0]);
                a.Entries.Add(new ModEntry { Name = "Harmony", Role = ModRole.Required,
                                             Kind = ModKind.Workshop, Id = "2009463077" });
                ModProfileSnapshot b = Snap(ModPolicy.Enforced, new[] { "Harmony" }, new string[0]);
                b.Entries.Add(new ModEntry { Name = "Harmony", Role = ModRole.Required,
                                             Kind = ModKind.Workshop, Id = "1234567890" });
                bool ok = ModProfile.HashOf(a) != ModProfile.HashOf(b);
                return (ok, ok ? "the id is part of the revision hash"
                               : "A CHANGED WORKSHOP ID WOULD NEVER REPUBLISH");
            });

            Check("Mods: an entry field that changes nothing else still moves the hash", () =>
            {
                ModProfileSnapshot a = Snap(ModPolicy.Enforced, new[] { "Harmony" }, new string[0]);
                a.Entries.Add(new ModEntry { Name = "Harmony", Role = ModRole.Required,
                                             Kind = ModKind.Workshop, Id = "2009463077",
                                             PackageId = "brrainz.harmony" });
                ModProfileSnapshot b = Snap(ModPolicy.Enforced, new[] { "Harmony" }, new string[0]);
                b.Entries.Add(new ModEntry { Name = "Harmony", Role = ModRole.Required,
                                             Kind = ModKind.Workshop, Id = "2009463077",
                                             PackageId = "someone.else" });
                bool ok = ModProfile.HashOf(a) != ModProfile.HashOf(b);
                return (ok, ok ? "every published entry field is covered" : "a published field is outside the hash");
            });

            Check("Mods: a Workshop file id is told apart from an expansion and from Core", () =>
            {
                bool ok = ModEntry.KindOf("2009463077") == ModKind.Workshop
                       && ModEntry.KindOf("Royalty") == ModKind.Expansion
                       && ModEntry.KindOf("Anomaly") == ModKind.Expansion
                       && ModEntry.KindOf("Odyssey") == ModKind.Expansion
                       && ModEntry.KindOf("Core") == ModKind.Local
                       && ModEntry.KindOf("KMHPatch") == ModKind.Local
                       && ModEntry.KindOf("") == ModKind.Local;
                return (ok, ok ? "workshop, expansion and local are distinct"
                               : "A MOD KIND IS MISCLASSIFIED");
            });

            Check("Mods: only a plain Workshop file id is offered for subscription", () =>
            {
                bool ok = ModEntry.LooksLikeWorkshopId("2009463077")
                       && !ModEntry.LooksLikeWorkshopId("12345")
                       && !ModEntry.LooksLikeWorkshopId("2009463077x")
                       && !ModEntry.LooksLikeWorkshopId("Royalty")
                       && !ModEntry.LooksLikeWorkshopId("")
                       && !ModEntry.LooksLikeWorkshopId(new string('9', 21));
                return (ok, ok ? "digits only, six to twenty" : "A NON-ID WOULD REACH A SUBSCRIBE BUTTON");
            });

            Check("Mods: expansions become optional only when the owner turned it on", () =>
            {
                ModEntry royalty = new ModEntry { Name = "Royalty", Kind = ModKind.Expansion, Id = "Royalty" };
                string off = new ModPolicyConfig().RoleFor(royalty, ModRole.Required);
                string on = new ModPolicyConfig { ExpansionsOptional = true }.RoleFor(royalty, ModRole.Required);
                bool ok = off == ModRole.Required && on == ModRole.Optional;
                return (ok, ok ? "off by default, optional when asked" : $"off={off} on={on}");
            });

            Check("Mods: making expansions optional leaves ordinary mods required", () =>
            {
                ModEntry harmony = new ModEntry { Name = "Harmony", Kind = ModKind.Workshop, Id = "2009463077" };
                string role = new ModPolicyConfig { ExpansionsOptional = true }.RoleFor(harmony, ModRole.Required);
                bool ok = role == ModRole.Required;
                return (ok, ok ? "only expansions move" : $"a workshop mod became {role}");
            });

            Check("Mods: no policy can produce a role RWT's admission does not read", () =>
            {
                ModEntry expansion = new ModEntry { Name = "Royalty", Kind = ModKind.Expansion, Id = "Royalty" };
                ModEntry workshop  = new ModEntry { Name = "NiceTextures", Kind = ModKind.Workshop, Id = "2009463077" };
                var cfg = new ModPolicyConfig
                {
                    ExpansionsOptional = true,
                    Required = new[] { "NiceTextures" },
                    Optional = new[] { "Royalty", "NiceTextures" },
                };
                foreach (ModEntry e in new[] { expansion, workshop })
                    foreach (string live in new[] { ModRole.Required, ModRole.Optional })
                    {
                        string role = cfg.RoleFor(e, live);
                        if (role != ModRole.Required && role != ModRole.Optional)
                            return (false, $"{e.Name} from {live} became {role}");
                    }
                return (true, "required or optional, never a third answer");
            });

            Check("Mods: the owner's named lists override the expansion switch, and required wins a conflict", () =>
            {
                ModEntry royalty = new ModEntry { Name = "Royalty", Kind = ModKind.Expansion, Id = "Royalty" };
                ModEntry nice = new ModEntry { Name = "NiceTextures", Kind = ModKind.Workshop, Id = "2009463077" };
                var cfg = new ModPolicyConfig
                {
                    ExpansionsOptional = true,
                    Required = new[] { "Royalty" },
                    Optional = new[] { "Royalty", "NiceTextures" },
                };
                bool ok = cfg.RoleFor(royalty, ModRole.Required) == ModRole.Required
                       && cfg.RoleFor(nice, ModRole.Required) == ModRole.Optional;
                return (ok, ok ? "named lists win, and required beats optional"
                               : "A CONTRADICTION RESOLVED THE PERMISSIVE WAY");
            });

            Check("Mods: a policy naming a mod this server does not list is reported, never applied", () =>
            {
                ModProfileSnapshot snap = Snap(ModPolicy.Enforced, new[] { "Harmony" }, new string[0]);
                snap.Entries.Add(new ModEntry { Name = "Harmony", Kind = ModKind.Workshop, Id = "2009463077" });
                var cfg = new ModPolicyConfig { Required = new[] { "NotInstalledHere" } };
                List<string> unknown = cfg.UnmatchedNames(snap);
                bool ok = unknown.Count == 1 && unknown[0] == "NotInstalledHere"
                       && cfg.UnmatchedNames(null).Count == 0;
                return (ok, ok ? "named and ignored" : $"{unknown.Count} unmatched name(s)");
            });

            Check("Mods: the shipped default enforces exactly what RimWorld Together already did", () =>
            {
                var cfg = new ModPolicyConfig();
                bool ok = !cfg.ExpansionsOptional && cfg.Required.Length == 0 && cfg.Optional.Length == 0;
                return (ok, ok ? "no policy until the owner writes one"
                               : "THE DEFAULT CHANGES AN EXISTING SERVER'S RULES");
            });

            Check("Mods: turning the expansion switch back off restores what the server required", () =>
            {
                // Stands in for an RWT mod entry; the projection writes the role back here as into RWT's own list.
                ModProfile.ForgetBaselineForTest();
                FakeRwtEntry royalty = new FakeRwtEntry { ModName = "Royalty", ModID = "Royalty" };

                string on = Project(royalty, new ModPolicyConfig { ExpansionsOptional = true });
                string off = Project(royalty, new ModPolicyConfig());

                bool ok = on == ModRole.Optional && off == ModRole.Required
                       && royalty.Type == FakeRwtType.Required;
                ModProfile.ForgetBaselineForTest();
                return (ok, ok ? "optional, then required again" : $"on={on} off={off} entry={royalty.Type}");
            });

            Check("Mods: a role the server itself changes becomes the new baseline", () =>
            {
                ModProfile.ForgetBaselineForTest();
                FakeRwtEntry mod = new FakeRwtEntry { ModName = "NiceTextures", ModID = "2009463077" };
                var optional = new ModPolicyConfig { Optional = new[] { "NiceTextures" } };

                Project(mod, optional);
                // An owner re-sync made RWT require it; KMH must not restore its older role over that.
                mod.Type = FakeRwtType.Required;
                string after = Project(mod, new ModPolicyConfig());

                bool ok = after == ModRole.Required && mod.Type == FakeRwtType.Required;
                ModProfile.ForgetBaselineForTest();
                return (ok, ok ? "the server's newer answer wins" : $"KMH re-applied {after}");
            });

            Check("Mods: a build whose mod roles cannot be written publishes what it actually enforces", () =>
            {
                ModProfile.ForgetBaselineForTest();
                // Nothing can be written back, so publishing "optional" would promise a join the server still refuses.
                var unwritable = new UnwritableRwtEntry();
                ModEntry me = new ModEntry { Name = "Royalty", Id = "Royalty", Kind = ModKind.Expansion };
                string role = ModProfile.Project(unwritable, new ModPolicyConfig { ExpansionsOptional = true },
                                                 me, ModRole.Required);
                bool ok = role == ModRole.Required && me.Role == ModRole.Required;
                ModProfile.ForgetBaselineForTest();
                return (ok, ok ? "falls back to what RWT enforces" : $"published {role} it cannot enforce");
            });

            Check("Mods: an entry goes on the wire under the names Global reads", () =>
            {
                // ModEntry has no JsonProperty names: its field names are the signed JSON keys Global reads (package_id).
                // The DTO parity check pairs files and these two are not a pair, so the wire names are pinned here.
                var body = new Features.Global.ModProfileBody
                {
                    Entries = new[]
                    {
                        new ModEntry { Name = "Harmony", Role = ModRole.Required, Kind = ModKind.Workshop,
                                       Id = "2009463077", PackageId = "brrainz.harmony", Author = "pardeike" },
                    },
                };

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(body);
                string[] want = { "\"name\"", "\"role\"", "\"kind\"", "\"id\"", "\"package_id\"", "\"author\"" };
                var absent = new List<string>();
                foreach (string k in want) if (json.IndexOf(k, StringComparison.Ordinal) < 0) absent.Add(k);

                bool ok = absent.Count == 0;
                return (ok, ok ? "all six entry fields use Global's names"
                               : "MISSING ON THE WIRE: " + string.Join(", ", absent.ToArray()));
            });

            Check("Mods: the disclosure names what is inside a mod entry, not just 'entries'", () =>
            {
                List<string> fields = KmhGlobalDisclosure.Fields(typeof(Features.Global.ModProfileBody));
                // Disclosure must name nested fields (Workshop ids leave the server); one word for a record discloses nothing.
                string[] want = { "entries.id", "entries.package_id", "entries.kind", "entries.name",
                                  "entries.role", "entries.author" };
                var absent = new List<string>();
                foreach (string w in want) if (!fields.Contains(w)) absent.Add(w);

                bool nothingLost = fields.Contains("required") && fields.Contains("order_hash")
                                && !fields.Contains("signature");
                bool ok = absent.Count == 0 && nothingLost;
                return (ok, ok ? fields.Count + " field(s), entries expanded"
                               : absent.Count > 0 ? "NOT DISCLOSED: " + string.Join(", ", absent.ToArray())
                                                  : "a top-level field went missing");
            });

            // RWT's entries carry a name and a Workshop id, never a package id, so the order is the only source.
            ModProfileSnapshot Matched(string[] order, params ModEntry[] entries)
            {
                var s = new ModProfileSnapshot { Order = new List<string>(order) };
                s.Entries.AddRange(entries);
                ModProfile.MatchPackageIds(s);
                return s;
            }
            ModEntry Entry(string name, string id) => new ModEntry { Name = name, Id = id, Kind = ModEntry.KindOf(id) };

            Check("package ids: Core and the expansions get RimWorld's own ids, and only when the order lists them", () =>
            {
                ModProfileSnapshot s = Matched(new[] { "brrainz.harmony", "ludeon.rimworld", "someone.royalty", "ludeon.rimworld.royalty" },
                    Entry("Core", "Core"), Entry("Royalty", "Royalty"), Entry("Biotech", "Biotech"), Entry("Harmony", "2009463077"));
                bool ok = s.Entries[0].PackageId == "ludeon.rimworld" && s.Entries[1].PackageId == "ludeon.rimworld.royalty"
                       && s.Entries[2].PackageId == "" && s.Entries[3].PackageId == "brrainz.harmony";
                return (ok, string.Join(", ", s.Entries.ConvertAll(e => $"{e.Name}={e.PackageId}")));
            });

            Check("package ids: a name two order ids could mean, or an id two entries could claim, is left blank", () =>
            {
                ModProfileSnapshot twoIds = Matched(new[] { "a.coolmod", "b.coolmod" }, Entry("Cool Mod", "1111111111"));
                ModProfileSnapshot twoEntries = Matched(new[] { "a.coolmod" }, Entry("Cool Mod", "1111111111"), Entry("CoolMod", "2222222222"));
                ModProfileSnapshot notOfficial = Matched(new[] { "ludeon.rimworld" }, Entry("RimWorld", "3333333333"), Entry("Core", "Core"));
                bool ok = twoIds.Entries[0].PackageId == ""
                       && twoEntries.Entries[0].PackageId == "" && twoEntries.Entries[1].PackageId == ""
                       && notOfficial.Entries[0].PackageId == "" && notOfficial.Entries[1].PackageId == "ludeon.rimworld";
                return (ok, $"two ids -> '{twoIds.Entries[0].PackageId}'; two entries -> '{twoEntries.Entries[0].PackageId}'/'{twoEntries.Entries[1].PackageId}'; "
                          + $"a Workshop 'RimWorld' -> '{notOfficial.Entries[0].PackageId}', Core -> '{notOfficial.Entries[1].PackageId}'");
            });

            Check("Mods: a mod the config names twice is published once, required if either entry says so", () =>
            {
                ModProfileSnapshot s = Matched(new[] { "brrainz.harmony" },
                    new ModEntry { Name = "Harmony", Role = ModRole.Optional, Kind = ModKind.Workshop, Id = "2009463077" },
                    new ModEntry { Name = "HugsLib", Role = ModRole.Required, Kind = ModKind.Workshop, Id = "818773962" },
                    new ModEntry { Name = "Harmony", Role = ModRole.Required, Kind = ModKind.Workshop, Id = "2009463077" },
                    new ModEntry { Name = "HugsLib", Role = ModRole.Required, Kind = ModKind.Workshop, Id = "818773962" });
                s.Optional.Add("Harmony"); s.Required.AddRange(new[] { "HugsLib", "Harmony", "HugsLib" });
                ModProfile.MergeRepeatedNames(s);
                ModProfile.MatchPackageIds(s);
                bool ok = s.Entries.Count == 2 && s.Entries[0].Name == "Harmony" && s.Entries[0].Role == ModRole.Required
                       && s.Entries[0].PackageId == "brrainz.harmony" && s.Optional.Count == 0
                       && string.Join(",", s.Required) == "Harmony,HugsLib";
                return (ok, $"entries [{string.Join(", ", s.Entries.ConvertAll(e => e.Name + ":" + e.Role + ":" + e.PackageId))}] "
                          + $"required [{string.Join(",", s.Required)}] optional [{string.Join(",", s.Optional)}]");
            });

            // The checks above call the steps directly; this pins that the real Read() runs them, in that order.
            const string readOrder = "Mods: reading RWT's config merges repeated names, then matches package ids";
            string profileSrc = KmhSecurityAuditSelfTest.ReadSource(System.IO.Path.Combine("Features", "Mods", "ModProfile.cs"));
            if (profileSrc == null) r.Add(Diagnostics.KmhSelfTestResult.Skipped(readOrder, "no source tree beside this build"));
            else Check(readOrder, () =>
            {
                string src = profileSrc;
                int read = src.IndexOf("private static ModProfileSnapshot Read()", StringComparison.Ordinal);
                int end = read < 0 ? -1 : src.IndexOf("snap.Hash = HashOf(snap);", read, StringComparison.Ordinal);
                int merge = end < 0 ? -1 : src.IndexOf("MergeRepeatedNames(snap);", read, end - read, StringComparison.Ordinal);
                int match = end < 0 ? -1 : src.IndexOf("MatchPackageIds(snap);", read, end - read, StringComparison.Ordinal);
                return (merge > 0 && match > merge, $"Read@{read} merge@{merge} match@{match} hash@{end}");
            });

            Check("mod order: RWT's published order decodes to package ids in document order", () =>
            {
                RwtModOrder.Parsed p = RwtModOrder.Read(Doc, docHash);
                bool ok = p.Trusted && p.Order.Count == 3
                       && p.Order[0] == "brrainz.harmony" && p.Order[1] == "ludeon.rimworld"
                       && p.Order[2] == "ludeon.rimworld.royalty";
                return (ok, ok ? string.Join(" > ", p.Order.ToArray()) : "ORDER DID NOT DECODE");
            });

            Check("mod order: a stated hash that does not cover the document is refused, not used", () =>
            {
                RwtModOrder.Parsed p = RwtModOrder.Read(Doc, new string('a', 64));
                // Fails safe to an empty order, which enforces nothing rather than enforcing something unproven.
                return (!p.Trusted && p.Order.Count == 0, p.Failure);
            });

            Check("mod order: the real hash is SHA-256 of the document as UTF-8", () =>
            {
                bool ok = docHash.Length == 64 && RwtModOrder.Read(Doc, docHash.ToLowerInvariant()).Trusted;
                return (ok, ok ? "case-insensitive match" : "HASH RULE IS WRONG");
            });

            Check("mod order: no order published is an empty order, never an empty enforcement", () =>
            {
                RwtModOrder.Parsed none = RwtModOrder.Read(null, null);
                RwtModOrder.Parsed blank = RwtModOrder.Read("", "");
                return (!none.Trusted && none.Order.Count == 0 && !blank.Trusted && blank.Order.Count == 0,
                        "older RWT and an unpublished order both enforce nothing");
            });

            Check("mod order: a malformed document fails safe instead of throwing", () =>
            {
                RwtModOrder.Parsed bad = RwtModOrder.Read("<ModsConfigData><activeMods><li>a", null);
                RwtModOrder.Parsed junk = RwtModOrder.Read("not xml at all", null);
                return (!bad.Trusted && bad.Order.Count == 0 && !junk.Trusted && junk.Order.Count == 0,
                        "malformed refused");
            });

            Check("mod order: a repeated package id is one position, not two", () =>
            {
                string doc = "<ModsConfigData><activeMods><li>a.b</li><li>a.b</li><li>c.d</li></activeMods></ModsConfigData>";
                RwtModOrder.Parsed p = RwtModOrder.Read(doc, null);
                return (p.Order.Count == 2 && p.Order[0] == "a.b" && p.Order[1] == "c.d",
                        string.Join(",", p.Order.ToArray()));
            });

            Check("mod order: an oversized order document is refused before it is parsed", () =>
            {
                string huge = "<ModsConfigData><activeMods>"
                            + new string('x', RwtModOrder.MaxDataStringBytes + 16) + "</activeMods></ModsConfigData>";
                RwtModOrder.Parsed p = RwtModOrder.Read(huge, null);
                return (!p.Trusted && p.Order.Count == 0, p.Failure);
            });

            Check("mod order: an order naming more mods than KMH accepts is refused whole", () =>
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder("<ModsConfigData><activeMods>");
                for (int i = 0; i <= RwtModOrder.MaxEntries; i++) sb.Append("<li>mod.").Append(i).Append("</li>");
                sb.Append("</activeMods></ModsConfigData>");
                RwtModOrder.Parsed p = RwtModOrder.Read(sb.ToString(), null);
                return (!p.Trusted && p.Order.Count == 0, p.Failure);
            });

            Check("mod order: the document is read with no external entity resolution", () =>
            {
                string xxe = "<!DOCTYPE d [<!ENTITY e SYSTEM " + Quote("file:///etc/passwd")
                           + ">]><ModsConfigData><activeMods><li>&e;</li></activeMods></ModsConfigData>";
                RwtModOrder.Parsed p = RwtModOrder.Read(xxe, null);
                return (!p.Trusted && p.Order.Count == 0, "DTD refused: " + p.Failure);
            });

            return r;
        }

        private static string Quote(string s) => ((char)34) + s + ((char)34);

        // Mirrors the supported RWT's two-value ModType, reached by reflection as RWT's own entry is.
        private enum FakeRwtType { Required, Optional }

        private sealed class FakeRwtEntry
        {
            public FakeRwtType Type;
            public string ModName = "";
            public string ModID = "";
        }

        // A build that exposes no writable role, which is the case the projection has to fail closed on.
        private sealed class UnwritableRwtEntry
        {
            public string ModName = "";
            public string ModID = "";
        }

        private static string Project(FakeRwtEntry entry, ModPolicyConfig policy)
        {
            ModEntry me = new ModEntry
            {
                Name = entry.ModName,
                Id   = entry.ModID,
                Kind = ModEntry.KindOf(entry.ModID),
            };
            string live = entry.Type == FakeRwtType.Optional ? ModRole.Optional : ModRole.Required;
            return ModProfile.Project(entry, policy, me, live);
        }

        private static ModProfileSnapshot Snap(ModPolicy policy, string[] required, string[] optional)
        {
            ModProfileSnapshot s = new ModProfileSnapshot
            {
                Policy = policy,
                Required = new List<string>(required),
                Optional = new List<string>(optional),
            };
            s.Hash = ModProfile.HashOf(s);
            return s;
        }
    }
}
