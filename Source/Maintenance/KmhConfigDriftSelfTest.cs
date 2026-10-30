using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    // What a fresh install writes must be what the code reads; a drifted generator fails silently and ships false defaults.
    internal static class KmhConfigDriftSelfTest
    {
        private const BindingFlags Serialized = BindingFlags.Public | BindingFlags.Instance;

        // Generators that do not just serialize the model are compared by hand; an unlisted one fails the first check.
        private static readonly (string Name, Type Config, string Builder)[] HandBuilt =
        {
            ("Discord",     typeof(Features.Discord.DiscordConfig),       "BuildDefaultFile"),
            ("Enforcement", typeof(Features.Enforcement.EnforcementConfig), "BuildDefaultFile"),
        };

        // Emitted by the generator on purpose and never read back as a field.
        private static readonly string[] IgnoredKeys = { "_readme" };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            List<(string name, string path, object sample)> targets =
                KmhConfigValidation.ConfigTargets().ToList();

            Check("Config drift: every generator either serializes its model or is compared against it", () =>
            {
                // IL tells a model serialization (Save) from a typed literal (WriteAllText) without writing any files.
                var unlisted = new List<string>();
                foreach ((string name, string _, object sample) in targets)
                {
                    Type t = sample.GetType();
                    List<string> calls = KmhIlReader.CallsMadeBy(t.FullName, "EnsureGenerated");
                    bool serializesModel = calls.Contains("Save");
                    bool listed = HandBuilt.Any(h => h.Config == t);
                    if (!serializesModel && !listed) unlisted.Add(name);
                    // A generator that is BOTH is fine - the hand-built ones save the JObject they derived.
                }
                return (unlisted.Count == 0,
                        unlisted.Count == 0
                            ? $"{targets.Count} generator(s): {targets.Count - HandBuilt.Length} serialize the model, "
                              + $"{HandBuilt.Length} are compared field by field"
                            : "neither serializes its model nor is listed: " + string.Join(", ", unlisted));
            });

            Check("Config drift: a hand-built default emits exactly what its model declares", () =>
            {
                var problems = new List<string>();
                foreach ((string name, Type cfg, string builder) in HandBuilt)
                {
                    MethodInfo m = cfg.GetMethod(builder, BindingFlags.NonPublic | BindingFlags.Public
                                                        | BindingFlags.Static);
                    if (m == null) { problems.Add($"{name}: no {builder}()"); continue; }
                    JObject emitted = m.Invoke(null, null) as JObject;
                    if (emitted == null) { problems.Add($"{name}: {builder}() returned no object"); continue; }

                    JObject model = JObject.FromObject(Activator.CreateInstance(cfg));
                    foreach (string missing in KeysMissingFrom(model, emitted))
                        problems.Add($"{name}: '{missing}' is on the model but a fresh install never gets it");
                    foreach (string extra in KeysMissingFrom(emitted, model))
                        if (Array.IndexOf(IgnoredKeys, extra) < 0)
                            problems.Add($"{name}: '{extra}' is written but nothing reads it");
                }
                return (problems.Count == 0,
                        problems.Count == 0 ? $"{HandBuilt.Length} hand-built default(s) match their model"
                                            : string.Join("; ", problems));
            });

            Check("Config drift: a hand-built default carries the model's values, not its own", () =>
            {
                var problems = new List<string>();
                foreach ((string name, Type cfg, string builder) in HandBuilt)
                {
                    MethodInfo m = cfg.GetMethod(builder, BindingFlags.NonPublic | BindingFlags.Public
                                                        | BindingFlags.Static);
                    JObject emitted = m?.Invoke(null, null) as JObject;
                    if (emitted == null) continue;
                    JObject model = JObject.FromObject(Activator.CreateInstance(cfg));
                    foreach (JProperty p in model.Properties())
                    {
                        JToken mine = emitted[p.Name];
                        if (mine == null) continue;   // already reported as missing above
                        if (!JToken.DeepEquals(p.Value, mine))
                            problems.Add($"{name}.{p.Name}: file says {Short(mine)}, the code defaults to {Short(p.Value)}");
                    }
                }
                return (problems.Count == 0,
                        problems.Count == 0 ? "every hand-written default equals its runtime default"
                                            : string.Join("; ", problems));
            });

            Check("Config drift: no config declares a field it cannot round-trip", () =>
            {
                // A read-only property serializes out but is dropped on load, so an owner's edit is silently ignored.
                var problems = new List<string>();
                foreach ((string name, string _, object sample) in targets)
                {
                    JObject emitted = JObject.FromObject(sample);
                    foreach (PropertyInfo p in sample.GetType().GetProperties(Serialized))
                    {
                        if (!p.CanRead) continue;
                        if (p.GetIndexParameters().Length > 0) continue;
                        if (p.GetCustomAttribute<Newtonsoft.Json.JsonIgnoreAttribute>() != null) continue;
                        if (emitted[p.Name] == null) continue;          // not emitted at all, nothing to round-trip
                        if (!p.CanWrite) problems.Add($"{name}.{p.Name} is written to the file but has no setter");
                    }
                }
                return (problems.Count == 0,
                        problems.Count == 0 ? $"{targets.Count} config(s) round-trip"
                                            : string.Join("; ", problems));
            });

            Check("Config drift: every nested settings object is materialised, never null", () =>
            {
                // A null nested object serializes as null, so every field under it reverts on the next load.
                var problems = new List<string>();
                foreach ((string name, string _, object sample) in targets)
                {
                    JObject emitted = JObject.FromObject(sample);
                    foreach (JProperty p in emitted.Properties())
                    {
                        if (p.Value.Type != JTokenType.Null) continue;
                        PropertyInfo pi = sample.GetType().GetProperty(p.Name, Serialized);
                        Type pt = pi?.PropertyType;
                        if (pt == null) continue;
                        // Strings and nullable scalars are legitimately blank; a settings class is not.
                        if (pt == typeof(string) || !pt.IsClass) continue;
                        if (pt.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(pt)) continue;
                        problems.Add($"{name}.{p.Name} ({pt.Name}) defaults to null");
                    }
                }
                return (problems.Count == 0,
                        problems.Count == 0 ? "every nested settings object has a default instance"
                                            : string.Join("; ", problems));
            });

            Check("Config drift: SchemaVersion is a positive number on every config", () =>
            {
                var problems = new List<string>();
                foreach ((string name, string _, object sample) in targets)
                {
                    PropertyInfo p = sample.GetType().GetProperty("SchemaVersion", Serialized);
                    if (p == null) { problems.Add($"{name}: none"); continue; }
                    object v = p.GetValue(sample);
                    if (!(v is int n) || n <= 0) problems.Add($"{name}: {v}");
                }
                return (problems.Count == 0,
                        problems.Count == 0 ? $"{targets.Count} config(s) versioned"
                                            : "bad SchemaVersion: " + string.Join(", ", problems));
            });

            Check("Config drift: the config catalog and the load-test agree on how many configs exist", () =>
            {
                // Two independent lists of the same thing; the moment they disagree, one of them is being maintained.
                int withGenerator = typeof(KmhConfigDriftSelfTest).Assembly.GetTypes()
                    .Count(t => t.IsClass && t.GetMethod("EnsureGenerated",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null);
                bool ok = withGenerator == targets.Count;
                return (ok, ok ? $"{targets.Count} config(s) on both sides"
                              : $"{withGenerator} type(s) generate a config file but {targets.Count} are load-tested");
            });

            Check("Config drift: the defaults the update posts and release notes quote are the ones a server starts with", () =>
            {
                // Owners were told these numbers, so changing one changes a published claim, not just a tuning value.
                var wrong = new List<string>();
                int pinned = 0;
                void Pin(string what, object got, object published)
                {
                    pinned++;
                    if (!Equals(got, published)) wrong.Add($"{what} is {got}, published as {published}");
                }
                var eco = new Features.Economy.EconomyConfig();
                eco.Treasury.Clamp();
                Pin("Economy Treasury.MaxSilverDepositPerTx", eco.Treasury.MaxSilverDepositPerTx, 1_000_000L);
                Pin("Economy Treasury.MaxItemDepositQtyPerTx", eco.Treasury.MaxItemDepositQtyPerTx, 5_000);
                Pin("Economy Treasury.AccessFeeSilver", eco.Treasury.AccessFeeSilver, 0);
                var mail = new Features.Mail.MailConfig();
                mail.Clamp();
                Pin("Mail UnclaimedAttachmentDays", mail.UnclaimedAttachmentDays, 14);
                Pin("Mail MaxAttachSilver", mail.MaxAttachSilver, 500_000L);
                Pin("Mail AttachmentFeePercent", mail.AttachmentFeePercent, 0.0);
                Pin("Mail AttachmentFeeFlat", mail.AttachmentFeeFlat, 0);
                var chat = new Features.Chat.ChatConfig();
                chat.Clamp();
                Pin("Chat AllowImagePreviews", chat.AllowImagePreviews, true);
                Pin("Chat MaxImageBytes", chat.MaxImageBytes, 32 * 1024 * 1024);
                var media = new Features.Media.MediaConfig();
                media.Clamp();
                Pin("Media Watch.MaxHeight", media.Watch.MaxHeight, 1080);
                var sites = new Features.Sites.SitesConfig();
                sites.Clamp();
                Pin("Sites Limits.WorkersPerPlayerPerSite", sites.Limits.WorkersPerPlayerPerSite, 3);
                var world = Features.World.WorldConfig.ClampForTest(new Features.World.WorldConfig());
                Pin("World WorldQuests.Scaling.ActivityWindowMinutes", world.WorldQuests.Scaling.ActivityWindowMinutes, 2880);
                return (wrong.Count == 0, wrong.Count == 0 ? $"{pinned} published default(s) hold"
                                                           : string.Join("; ", wrong));
            });

            // The unknown-key doctor must pass every generated file; one false alarm teaches owners to ignore it.
            Check("Config keys: every generated config file scans clean - no setting KMH writes is reported as unknown", () =>
            {
                var noise = new List<(string File, string Key)>();
                foreach ((string name, string _, object sample) in targets)
                {
                    object file = Array.Exists(HandBuilt, h => h.Name == name)
                        ? HandBuilt.First(h => h.Name == name).Config.GetMethod(HandBuilt.First(h => h.Name == name).Builder,
                              BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, null)
                        : sample;
                    JObject o = file as JObject ?? JObject.FromObject(file);
                    KmhConfigKeyScan.Walk(o, sample.GetType(), "", name, noise, 0);
                }
                return (noise.Count == 0, noise.Count == 0 ? $"{targets.Count} file(s) clean"
                                                           : string.Join(", ", noise.Select(n => n.File + ":" + n.Key)));
            });

            Check("Config keys: a misspelt setting, top-level or nested, is named; notes and correct keys are not", () =>
            {
                var found = new List<(string File, string Key)>();
                KmhConfigKeyScan.Walk(JObject.Parse("{ \"MaxOpenPerUser\": 3, \"maxopenperuser\": 4, \"ReviewTimeoutHourz\": 5, \"_readme\": \"x\" }"),
                                      typeof(Features.Quests.QuestsConfig), "", "Quests", found, 0);
                KmhConfigKeyScan.Walk(JObject.Parse("{ \"Marketplace\": { \"TaxPercent\": 5, \"TaxPrecent\": 9 } }"),
                                      typeof(Features.Economy.EconomyConfig), "", "Economy", found, 0);
                string got = string.Join(",", found.Select(f => f.File + ":" + f.Key));
                return (got == "Quests:ReviewTimeoutHourz,Economy:Marketplace.TaxPrecent", got);
            });

            return r;
        }

        private static IEnumerable<string> KeysMissingFrom(JObject expected, JObject actual)
            => expected.Properties().Select(p => p.Name).Where(n => actual[n] == null);

        private static string Short(JToken t)
        {
            string s = t?.ToString(Newtonsoft.Json.Formatting.None) ?? "null";
            return s.Length > 60 ? s.Substring(0, 57) + "..." : s;
        }
    }
}
