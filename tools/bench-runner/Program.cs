using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

// Median and p95 over repeated samples at several scales, so a change is judged on a distribution, not one run.
class B
{
    static string[] _probe;
    static Assembly _asm;

    static int Main(string[] a)
    {
        if (a.Length < 1) { Console.WriteLine("usage: bench <addon.dll> <probe dirs...>"); return 2; }
        string target = Path.GetFullPath(a[0]);
        _probe = a.Skip(1).Select(Path.GetFullPath).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        _asm = Assembly.LoadFrom(target);
        Console.WriteLine($"assembly: {_asm.GetName().Name} v{_asm.GetName().Version}");
        Console.WriteLine($"runtime:  {Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}, "
                        + $"server-gc={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine();

        Ready();

        var rows = new List<Row>();
        rows.AddRange(FrameCodecScales());
        rows.AddRange(ItemKeyScales());
        rows.AddRange(EnvelopeScales());
        rows.AddRange(EncodeScales());
        rows.AddRange(DisplayTextScales());
        rows.AddRange(JsonStrategyProbe());
        rows.AddRange(PersistenceScales());

        Console.WriteLine($"{"scenario",-46}{"n",8}{"median",12}{"p95",12}{"per-op",14}");
        Console.WriteLine(new string('-', 92));
        foreach (Row r in rows) Console.WriteLine(r);
        Console.WriteLine();
        Console.WriteLine($"rows: {rows.Count}");
        return 0;
    }

    // Best effort: the paths timed here are pure, so a host assembly this harness cannot load must not stop the run.
    static void Ready()
    {
        try
        {
            Type t = Types().FirstOrDefault(x => x.Name == "KmhReadiness");
            t?.GetMethod("Ready", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
        }
        catch (Exception) { Console.WriteLine("note: readiness not faked (host assembly absent); pure paths unaffected"); }
    }

    static Type[] Types()
    {
        try { return _asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(x => x != null).ToArray(); }
    }

    static Type T(string name) => Types().FirstOrDefault(x => x.Name == name);

    static MethodInfo M(Type t, string name, int argc) => t?.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argc);

    // Bound, not reflected: MethodInfo.Invoke's ~0.1us is the small ops' whole budget, so it would time the harness.
    static TDel Bind<TDel>(MethodInfo m) where TDel : class
        => m == null ? null : Delegate.CreateDelegate(typeof(TDel), m, false) as TDel;

    sealed class Row
    {
        public string Name; public int N; public double Median, P95; public string Unit = "us/op";
        public double PerOp;
        public override string ToString()
            => $"{Name,-46}{N,8}{Median,10:0.000}ms{P95,10:0.000}ms{PerOp,11:0.000} {Unit}";
    }

    // Samples is deliberately plural: one timing is noise, and a p95 is what a player feels as a stutter.
    static Row Measure(string name, int n, int opsPerSample, Action body, int samples = 9, int warmup = 3)
    {
        for (int i = 0; i < warmup; i++) body();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var ms = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            ms[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(ms);
        double median = ms[samples / 2];
        double p95 = ms[(int)Math.Min(samples - 1, Math.Ceiling(0.95 * samples) - 1)];
        return new Row { Name = name, N = n, Median = median, P95 = p95, PerOp = median * 1000.0 / opsPerSample };
    }

    // A save is a deep copy under the store lock, then serialize and write outside it; timed apart so each part shows.
    static IEnumerable<Row> PersistenceScales()
    {
        Type copy = T("KmhDeepCopy"), store = T("JsonFileStore"), vault = T("TreasurySnapshot"), entry = T("PlayerLeaderboardEntry");
        if (copy == null || store == null || vault == null) yield break;
        MethodInfo ofList = copy.GetMethod("OfList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo toJson = store.GetMethod("ToJson", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(object));
        MethodInfo save = store.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "Save" && m.GetParameters().Length == 3).MakeGenericMethod(typeof(object));
        string dir = Path.Combine(Path.GetTempPath(), "kmh-bench-persist");
        Directory.CreateDirectory(dir);
        long seq = 1;

        foreach (int n in new[] { 50, 200, 1000 })
        {
            IList vaults = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(vault));
            for (int i = 0; i < n; i++) vaults.Add(Fill(vault, 0, i, 20, 40));
            MethodInfo copyVaults = ofList.MakeGenericMethod(vault);
            object copied = copyVaults.Invoke(null, new object[] { vaults });
            string json = (string)toJson.Invoke(null, new object[] { copied });
            string path = Path.Combine(dir, "vaults-" + n + ".json");
            yield return Measure($"persist: deep copy {n} vaults (lock held)", n, 1, () => copyVaults.Invoke(null, new object[] { vaults }));
            yield return Measure($"persist: serialize {n} vaults ({json.Length / 1024}KB)", n, 1, () => toJson.Invoke(null, new object[] { copied }));
            yield return Measure($"persist: durable write {n} vaults", n, 1, () => save.Invoke(null, new object[] { path, copied, seq++ }), samples: 7, warmup: 2);
        }

        if (entry != null)
            foreach (int n in new[] { 50, 200 })
            {
                IList entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entry));
                for (int i = 0; i < n; i++) entries.Add(Fill(entry, 0, i, 4, 4));
                MethodInfo copyEntries = ofList.MakeGenericMethod(entry);
                string path = Path.Combine(dir, "stats-" + n + ".json");
                yield return Measure($"persist: one PlayerStats save, {n} players", n, 1,
                    () => save.Invoke(null, new object[] { path, copyEntries.Invoke(null, new object[] { entries }), seq++ }), samples: 7, warmup: 2);
            }
        try { Directory.Delete(dir, true); } catch (Exception) { }
    }

    // Every public member filled, collections sized as asked, so a size sweep scales the real shape of the DTO.
    static object Fill(Type t, int depth, int seed, int dictSize, int listSize)
    {
        if (t == typeof(string)) return "v" + seed + "-" + depth;
        Type nullable = Nullable.GetUnderlyingType(t);
        if (nullable != null) return Fill(nullable, depth, seed, dictSize, listSize);
        if (t.IsEnum) return Enum.GetValues(t).GetValue(0);
        if (t == typeof(bool)) return true;
        if (t.IsPrimitive) return Convert.ChangeType(seed % 100 + 1, t);
        if (t == typeof(decimal)) return 7m;
        if (t == typeof(DateTime)) return new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        if (t == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        if (t == typeof(TimeSpan)) return TimeSpan.FromMinutes(7);
        if (t == typeof(Guid)) return Guid.Empty;
        if (depth > 3 || t.IsArray) return null;
        if (t.IsGenericType)
        {
            Type def = t.GetGenericTypeDefinition(); Type[] args = t.GetGenericArguments();
            if (def == typeof(List<>) || def == typeof(HashSet<>))
            {
                object c = Activator.CreateInstance(t);
                MethodInfo add = t.GetMethod("Add");
                // Sized only on the DTO itself: nested collections stay small, as they are in real stores.
                for (int i = 0, count = depth <= 1 ? listSize : 2; i < count; i++) add.Invoke(c, new[] { Key(args[0], i) ?? Fill(args[0], depth + 1, i, dictSize, listSize) });
                return c;
            }
            if (def == typeof(Dictionary<,>))
            {
                IDictionary d = (IDictionary)Activator.CreateInstance(t);
                for (int i = 0, count = depth <= 1 ? dictSize : 2; i < count; i++) { object k = Key(args[0], i); if (k != null) d[k] = Fill(args[1], depth + 1, i, dictSize, listSize); }
                return d;
            }
            return null;
        }
        if (!t.IsClass) return Activator.CreateInstance(t);
        object o;
        try { o = Activator.CreateInstance(t, true); } catch (Exception) { return null; }
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (p.CanWrite && p.GetIndexParameters().Length == 0)
                try { p.SetValue(o, Fill(p.PropertyType, depth + 1, seed, dictSize, listSize)); } catch (Exception) { }
        return o;
    }

    static object Key(Type t, int i)
        => t == typeof(string) ? "Item" + i : t.IsPrimitive && t != typeof(bool) ? Convert.ChangeType(i + 1, t) : null;

    // The frame codec sits under every message on the wire, so its cost is paid by every feature.
    static IEnumerable<Row> FrameCodecScales()
    {
        Type codec = T("KmhFrameCodec");
        var deflate = Bind<Func<byte[], byte[]>>(M(codec, "Deflate", 1));
        var inflate = Bind<Func<byte[], int, byte[]>>(M(codec, "Inflate", 2));
        if (deflate == null || inflate == null) yield break;

        foreach (int rows in new[] { 25, 200, 2000 })
        {
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(SnapshotJson(rows));
            byte[] packed = deflate(raw);
            const int ops = 200;

            yield return Measure($"frame codec: deflate a {rows}-row snapshot ({raw.Length / 1024}KB)", rows, ops,
                () => { for (int i = 0; i < ops; i++) deflate(raw); });

            if (packed != null)
                yield return Measure($"frame codec: inflate it back", rows, ops,
                    () => { for (int i = 0; i < ops; i++) inflate(packed, 8 * 1024 * 1024); });
        }
    }

    // Every vault row, marketplace row and want is keyed through this on both sides of the wire.
    static IEnumerable<Row> ItemKeyScales()
    {
        Type k = T("ItemKey");
        var compose = Bind<Func<string, string, int, string>>(M(k, "Compose", 3));
        var matches = Bind<Func<string, string, string, int, bool>>(M(k, "Matches", 4));
        if (compose == null) yield break;

        string[] defs = { "Steel", "MedicineHerbal", "MealSimple", "Cloth", "Plasteel", "ComponentIndustrial" };
        string[] stuff = { null, "Steel", "Plasteel", "WoodLog" };
        const int ops = 200000;

        yield return Measure("item key: compose", ops, ops, () =>
        {
            for (int i = 0; i < ops; i++) compose(defs[i % defs.Length], stuff[i % stuff.Length], i % 6);
        });

        if (matches != null)
        {
            string key = compose("Steel", "Plasteel", 3);
            yield return Measure("item key: match against a want", ops, ops,
                () => { for (int i = 0; i < ops; i++) matches(key, "Steel", "Plasteel", 3); });
        }
    }

    // A snapshot is serialized once per share key, but a chat line is serialized per message.
    static IEnumerable<Row> EnvelopeScales()
    {
        Type env = T("KmhEnvelope");
        ConstructorInfo ctor = env?.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length >= 2 && c.GetParameters()[0].ParameterType == typeof(string));
        MethodInfo ser = env?.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance);
        var parse = Bind<Func<string, object>>(M(env, "TryParse", 1));
        if (ctor == null || ser == null) yield break;
        int argc = ctor.GetParameters().Length;
        object[] Args(object payload) => argc == 2 ? new[] { "kmh.bench", payload }
                                                   : new object[] { "kmh.bench", payload, -1 };

        foreach (int rows in new[] { 25, 200, 2000 })
        {
            object payload = Rows(rows);
            object one = ctor.Invoke(Args(payload));
            int ops = rows >= 2000 ? 50 : 400;

            // The envelope is built once and serialized once per share key, so the build is inside the timing.
            yield return Measure($"envelope: build + serialize {rows} rows", rows, ops,
                () => { for (int i = 0; i < ops; i++) ser.Invoke(ctor.Invoke(Args(payload)), null); });

            if (parse != null)
            {
                string wire = (string)ser.Invoke(one, null);
                yield return Measure($"envelope: parse {rows} rows back", rows, ops,
                    () => { for (int i = 0; i < ops; i++) parse(wire); });
            }
        }
    }

    // Allocation beside time: an intermediate copy of a large payload is garbage the GC pays for.
    static IEnumerable<Row> EncodeScales()
    {
        Type api = T("KmhApiServer");
        Type env = T("KmhEnvelope");
        MethodInfo encode = api?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Encode" && m.GetParameters().Length == 2);
        ConstructorInfo ctor = env?.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length >= 2 && c.GetParameters()[0].ParameterType == typeof(string));
        if (encode == null || ctor == null) yield break;
        int argc = ctor.GetParameters().Length;

        foreach (int rows in new[] { 25, 200, 2000 })
        {
            object[] args = argc == 2 ? new object[] { "kmh.bench", Rows(rows) } : new object[] { "kmh.bench", Rows(rows), -1 };
            object one = ctor.Invoke(args);
            int ops = rows >= 2000 ? 50 : 400;
            foreach (bool compress in new[] { false, true })
            {
                object[] call = { one, compress };
                string what = compress ? "deflated" : "plain";
                yield return Measure($"encode: {rows} rows to a {what} frame", rows, ops,
                    () => { for (int i = 0; i < ops; i++) encode.Invoke(null, call); });
                yield return Allocated($"encode: {rows} rows to a {what} frame", rows, ops,
                    () => { for (int i = 0; i < ops; i++) encode.Invoke(null, call); });
            }
        }
    }

    static Row Allocated(string name, int n, int ops, Action body)
    {
        body();
        var kb = new double[5];
        for (int i = 0; i < kb.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            kb[i] = (GC.GetAllocatedBytesForCurrentThread() - before) / 1024.0 / ops;
        }
        Array.Sort(kb);
        return new Row { Name = name + " (alloc)", N = n, PerOp = kb[kb.Length / 2], Unit = "KB/op" };
    }

    // Runs per drawn chat line and per drawn browser row, so it is frame cost, not request cost.
    static IEnumerable<Row> DisplayTextScales()
    {
        var inert = Bind<Func<string, int, string>>(M(T("KmhDisplayText"), "Inert", 2));
        if (inert == null) yield break;

        string plain = "a perfectly ordinary chat line with nothing special in it at all";
        string nasty = "<color=#ff0000><size=40>shout</size></color> and <b>more</b> " + new string('x', 200);
        const int ops = 200000;

        yield return Measure("display text: inert a plain line", ops, ops,
            () => { for (int i = 0; i < ops; i++) inert(plain, 512); });
        yield return Measure("display text: inert a rich-text line", ops, ops,
            () => { for (int i = 0; i < ops; i++) inert(nasty, 512); });
    }

    // Not a KMH path: splits an envelope parse into per-call serializer setup versus reading the bytes.
    static IEnumerable<Row> JsonStrategyProbe()
    {
        string json = SnapshotJson(200);
        var cached = Newtonsoft.Json.JsonSerializer.CreateDefault();
        const int ops = 400;

        yield return Measure("probe: JsonConvert.DeserializeObject<JObject>", 200, ops,
            () => { for (int i = 0; i < ops; i++) Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json); });

        yield return Measure("probe: cached JsonSerializer + JsonTextReader", 200, ops, () =>
        {
            for (int i = 0; i < ops; i++)
                using (var sr = new StringReader(json))
                using (var jr = new Newtonsoft.Json.JsonTextReader(sr))
                    cached.Deserialize<Newtonsoft.Json.Linq.JObject>(jr);
        });

        yield return Measure("probe: JObject.Parse", 200, ops,
            () => { for (int i = 0; i < ops; i++) Newtonsoft.Json.Linq.JObject.Parse(json); });

        // A size sweep, because one size cannot tell a fixed per-call cost from a per-byte one.
        foreach (int r in new[] { 1, 5, 25, 100, 500 })
        {
            string small = SnapshotJson(r);
            int n = small.Length;
            yield return Measure($"probe: JObject.Parse {r,4} rows ({n,6} bytes)", r, ops,
                () => { for (int i = 0; i < ops; i++) Newtonsoft.Json.Linq.JObject.Parse(small); });
        }
    }

    // Shaped like a real marketplace snapshot: repeated keys, short values, a few long strings.
    static string SnapshotJson(int rows)
    {
        var sb = new System.Text.StringBuilder("{\"kind\":\"kmh.marketplace\",\"data\":{\"listings\":[");
        for (int i = 0; i < rows; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(i)
              .Append(",\"def\":\"Steel\",\"stuff\":null,\"qty\":").Append(i % 997)
              .Append(",\"seller\":\"player_").Append(i % 23)
              .Append("\",\"price\":\"12.50\",\"quality\":\"Normal\",\"posted\":\"2026-09-18T02:00:00Z\"}");
        }
        return sb.Append("]}}").ToString();
    }

    static object Rows(int n)
    {
        var list = new List<object>(n);
        for (int i = 0; i < n; i++)
            list.Add(new { id = i, def = "Steel", qty = i % 997, seller = "player_" + (i % 23), price = "12.50" });
        return new { listings = list };
    }

    static Assembly Resolve(object s, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name + ".dll";
        foreach (string dir in _probe)
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { } }
        }
        return null;
    }
}
