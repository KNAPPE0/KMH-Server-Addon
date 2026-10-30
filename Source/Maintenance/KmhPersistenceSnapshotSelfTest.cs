using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    // Stores save a deep copy taken under their lock, so a save never serializes an object another thread changes.
    internal static class KmhPersistenceSnapshotSelfTest
    {
        private static readonly Type[] Stores =
        {
            typeof(Features.Treasury.TreasuryStore), typeof(Features.Sites.SiteStore), typeof(Features.Marketplace.MarketplaceStore),
            typeof(Features.Guilds.GuildStore), typeof(Features.Quests.QuestStore), typeof(Features.Mail.MailStore),
            typeof(Features.World.WorldStore), typeof(Features.PlayerStats.PlayerStatsStore), typeof(Features.Auctions.AuctionStore),
            typeof(Features.Delivery.DeliveryStore), typeof(Features.WantBoard.WantStore), typeof(Features.Roadworks.RoadworksStore),
            typeof(Features.Recovery.RecoveryStore), typeof(Features.Notifications.NotificationStore), typeof(Features.Sites.SiteCatalogStore),
            typeof(Features.Seasons.SeasonStore), typeof(Features.Reputation.ReputationStore), typeof(Features.Chat.ChatStore),
            typeof(Features.Guilds.Contributions.KmhGuildContributionLedger), typeof(Features.Discord.DiscordUserState),
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            List<(string, bool, string)> r = new List<(string, bool, string)>();
            Generic(r);
            Sweep(r);
            Stress(r);
            return r;
        }

        private sealed class Leaf { public string Name = ""; public List<int> Numbers = new List<int>(); }
        private struct Holder { public Leaf Inner; public int Count; }
        private sealed record Note(string Text, List<string> Tags);
        private sealed class Graph
        {
            public Dictionary<string, Leaf> ByName = new Dictionary<string, Leaf>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Leaf[] Array = new Leaf[0];
            public Leaf SharedA, SharedB;
            public Holder Box;
            public Note Record;
            public JObject Extra;
            [Newtonsoft.Json.JsonIgnore] public Graph Self;
            private readonly List<long> _hidden = new List<long> { 1, 2, 3 };
            public List<long> Hidden => _hidden;
            public DateTimeOffset When = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        }

        private static void Generic(List<(string, bool, string)> r)
        {
            Leaf shared = new Leaf { Name = "shared", Numbers = { 1 } };
            Graph g = new Graph
            {
                Array = new[] { new Leaf { Name = "a" } }, SharedA = shared, SharedB = shared,
                Box = new Holder { Inner = new Leaf { Name = "boxed" }, Count = 2 }, Record = new Note("n", new List<string> { "t" }),
                Extra = JObject.Parse("{\"a\":[1,{\"b\":2}]}"),
            };
            g.ByName["Alpha"] = new Leaf { Name = "Alpha", Numbers = { 5 } };
            g.Seen.Add("Beta");
            g.Self = g;
            string before = Json(g);
            Graph c = KmhDeepCopy.Of(g);

            r.Add(("snapshot: a deep copy serializes exactly like the original", Json(c) == before, ""));
            c.ByName["alpha"].Numbers.Add(9); c.Seen.Add("gamma"); c.Array[0].Name = "changed"; c.Box.Inner.Name = "x";
            c.Record.Tags.Add("u"); ((JArray)c.Extra["a"]).Add(3); c.Hidden.Add(4); c.SharedA.Numbers.Add(2);
            r.Add(("snapshot: changing the copy leaves the original untouched", Json(g) == before, ""));
            r.Add(("snapshot: a case-insensitive dictionary or set stays case-insensitive",
                   c.ByName.ContainsKey("ALPHA") && c.Seen.Contains("BETA"), ""));
            r.Add(("snapshot: one object referenced twice is one object in the copy, and a cycle stays a cycle",
                   ReferenceEquals(c.SharedA, c.SharedB) && !ReferenceEquals(c.SharedA, shared) && ReferenceEquals(c.Self, c), ""));
        }

        // Derived by reflection, so a member or collection type added later is covered without editing a list.
        private static void Sweep(List<(string, bool, string)> r)
        {
            int types = 0;
            List<string> bad = new List<string>();
            foreach (Type store in Stores)
                foreach (Type t in store.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Concat(MemberTypes(store)).Distinct())
                {
                    if (!t.IsClass || t.IsAbstract || t.ContainsGenericParameters || typeof(Delegate).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null) continue;
                    if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) continue;
                    types++;
                    try
                    {
                        object o = Populate(t, 0);
                        string before = Json(o);
                        object c = KmhDeepCopy.Of(o);
                        if (Json(c) != before) { bad.Add(t.Name + ": copy serializes differently"); continue; }
                        string shared = Shared(o, c, new HashSet<object>(ReferenceEqualityComparer.Instance), t.Name, 0);
                        if (shared != null) bad.Add(shared);
                    }
                    catch (Exception ex) { bad.Add(t.Name + ": " + ex.GetType().Name + " " + ex.Message); }
                }
            r.Add(("snapshot: every persisted DTO of the snapshotting stores copies exactly and shares nothing mutable",
                   bad.Count == 0 && types >= 20, $"{types} type(s); {string.Join("; ", bad.Take(4))}"));
        }

        private static IEnumerable<Type> MemberTypes(Type store)
        {
            foreach (FieldInfo f in store.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                foreach (Type a in Elements(f.FieldType)) yield return a;
        }

        private static IEnumerable<Type> Elements(Type t)
        {
            if (t.IsArray) { yield return t.GetElementType(); yield break; }
            if (!t.IsGenericType) { if (t.IsClass && t != typeof(string) && t != typeof(object)) yield return t; yield break; }
            foreach (Type a in t.GetGenericArguments())
                foreach (Type e in Elements(a)) yield return e;
        }

        private static object Populate(Type t, int depth)
        {
            if (t == typeof(string)) return "v" + depth;
            Type nullable = Nullable.GetUnderlyingType(t);
            if (nullable != null) return Populate(nullable, depth);
            if (t.IsEnum) { Array v = Enum.GetValues(t); return v.GetValue(v.Length - 1); }
            if (t == typeof(bool)) return true;
            if (t.IsPrimitive) return Convert.ChangeType(7, t);
            if (t == typeof(decimal)) return 7m;
            if (t == typeof(DateTime)) return new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            if (t == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            if (t == typeof(TimeSpan)) return TimeSpan.FromMinutes(7);
            if (t == typeof(Guid)) return new Guid("0123456789abcdef0123456789abcdef");
            if (typeof(JToken).IsAssignableFrom(t)) return JObject.Parse("{\"k\":[1,{\"n\":2}]}");
            if (depth > 4) return null;
            if (t.IsArray)
            {
                Array a = Array.CreateInstance(t.GetElementType(), 2);
                for (int i = 0; i < 2; i++) a.SetValue(Populate(t.GetElementType(), depth + 1), i);
                return a;
            }
            if (t.IsGenericType)
            {
                Type def = t.GetGenericTypeDefinition();
                Type[] args = t.GetGenericArguments();
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(Queue<>))
                {
                    object c = Activator.CreateInstance(t);
                    MethodInfo add = t.GetMethod(def == typeof(Queue<>) ? "Enqueue" : "Add");
                    for (int i = 0; i < 2; i++) add.Invoke(c, new[] { Distinct(args[0], i, depth) });
                    return c;
                }
                if (def == typeof(Dictionary<,>) || def == typeof(SortedDictionary<,>))
                {
                    IDictionary d = (IDictionary)Activator.CreateInstance(t);
                    for (int i = 0; i < 2; i++) d[Distinct(args[0], i, depth)] = Populate(args[1], depth + 1);
                    return d;
                }
            }
            if (!t.IsClass && !t.IsValueType) return null;
            object o;
            try { o = Activator.CreateInstance(t, true); } catch (Exception) { return null; }
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                try { p.SetValue(o, Populate(p.PropertyType, depth + 1)); } catch (Exception) { }
            }
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (f.IsInitOnly) continue;
                try { f.SetValue(o, Populate(f.FieldType, depth + 1)); } catch (Exception) { }
            }
            return o;
        }

        private static object Distinct(Type t, int i, int depth)
        {
            if (t == typeof(string)) return "k" + i;
            if (t.IsPrimitive && t != typeof(bool)) return Convert.ChangeType(i + 1, t);
            if (t.IsEnum) return Enum.GetValues(t).GetValue(i % Enum.GetValues(t).Length);
            return Populate(t, depth + 1);
        }

        private static string Shared(object o, object c, HashSet<object> walked, string path, int depth)
        {
            if (o == null || depth > 12) return null;
            Type t = o.GetType();
            if (KmhDeepCopy.IsImmutable(t)) return null;
            if (!t.IsValueType)
            {
                if (ReferenceEquals(o, c)) return path + " is shared";
                if (!walked.Add(o)) return null;
            }
            if (o is JToken) return null;
            if (o is IDictionary od && c is IDictionary cd)
            {
                foreach (DictionaryEntry e in od)
                {
                    string s = Shared(e.Value, cd[e.Key], walked, path + "[" + e.Key + "]", depth + 1);
                    if (s != null) return s;
                }
                return null;
            }
            if (o is IEnumerable oe && !(o is string) && c is IEnumerable ce)
            {
                List<object> a = oe.Cast<object>().ToList(), b = ce.Cast<object>().ToList();
                for (int i = 0; i < a.Count && i < b.Count; i++)
                {
                    string s = Shared(a[i], b[i], walked, path + "[" + i + "]", depth + 1);
                    if (s != null) return s;
                }
                return null;
            }
            for (Type ty = t; ty != null && ty != typeof(object); ty = ty.BaseType)
                foreach (FieldInfo f in ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    string s = Shared(f.GetValue(o), f.GetValue(c), walked, path + "." + f.Name, depth + 1);
                    if (s != null) return s;
                }
            return null;
        }

        private static void Stress(List<(string, bool, string)> r)
        {
            string[] users = Enumerable.Range(0, 4).Select(i => "kmh-snapshot-stress-" + i).ToArray();
            long racesBefore = JsonFileStore.LiveObjectRaces;
            int refused = 0, saves = 0;
            try
            {
                using CancellationTokenSource stop = new CancellationTokenSource();
                Task saver = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested) { if (!Features.Treasury.TreasuryStore.SaveToDisk()) Interlocked.Increment(ref refused); Interlocked.Increment(ref saves); }
                });
                Task[] writers = users.Select(u => Task.Run(() =>
                {
                    for (int i = 0; i < 40; i++)
                        if (!Features.Treasury.TreasuryStore.DepositItem(u, "SnapshotStressDef" + (i % 7), 1, "snapshot stress"))
                            Interlocked.Increment(ref refused);
                })).ToArray();
                Task.WaitAll(writers, TimeSpan.FromSeconds(60));
                stop.Cancel();
                saver.Wait(TimeSpan.FromSeconds(30));
            }
            finally
            {
                foreach (string u in users) Features.Treasury.TreasuryStore.PurgeOwnerForTest(u);
                Features.Treasury.TreasuryStore.SaveToDisk();
            }
            long races = JsonFileStore.LiveObjectRaces - racesBefore;
            r.Add(("snapshot: saving while four players deposit never races a live object or refuses a deposit",
                   races == 0 && refused == 0 && saves > 0, $"{saves} saves, {races} race(s), {refused} refused"));
        }

        private static string Json(object o) => JsonFileStore.ToJson(o);
    }
}
