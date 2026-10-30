using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

// One isolated process per supplied RWT assembly set. No server boot and no sockets.
string addon = Path.GetFullPath(args[0]);
string[] probes = args.Skip(1).Select(Path.GetFullPath).ToArray();
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string dir in probes)
    {
        string path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
Assembly kmh = Assembly.LoadFrom(addon);
Type gate = kmh.GetType("KMHServerAddon.Features.Enforcement.EnforcementAdmission", true);
Type harmony = Assembly.Load("0Harmony").GetType("HarmonyLib.Harmony", true);
object patcher = Activator.CreateInstance(harmony, "kmh.admission.probe");
try
{
    var native = Assembly.Load(kmh.GetName().Name == "KMHAddon.RTServer" ? "RTServer" : "GameServer");
    var post = native.GetTypes().Single(t => t.Name == "PM_Login").GetMethod("PostLogin", all);
    Console.WriteLine("TARGET " + post.DeclaringType.FullName + "." + post);
    var server = post.DeclaringType.Assembly;
    Type master = server.GetTypes().Single(t => t.Name == "Master");
    string scratch = Path.Combine(AppContext.BaseDirectory, "native-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(scratch);
    string branch = Environment.GetEnvironmentVariable("KMH_PROBE_BRANCH") ?? "no-world";
    string planet = Path.Combine(scratch, "planet.json");
    if (branch != "no-world") File.WriteAllText(planet, "{}");
    master.GetProperty("WorldValues").PropertyType.GetProperty("SavePath", all).SetValue(null, planet);
    master.GetProperty("SavesPath", all).SetValue(null, scratch);
    Console.WriteLine("SERVER " + server.GetName());
    Console.WriteLine("SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(server.Location))));
    Type config = kmh.GetType("KMHServerAddon.Features.Enforcement.EnforcementConfig", true);
    object cfg = config.GetProperty("Current", all).GetValue(null);
    config.GetProperty("Enabled").SetValue(cfg, true);
    Type clientType = post.GetParameters()[0].ParameterType;
    object client = Activator.CreateInstance(clientType, new object[] { null, null, false });
    Type userType = new[] { "RTShared.Files.Player.FL_Player, RTShared", "RTShared.Files.ServerClient.FL_Player, RTShared", "TCPNetwork.Files.Client.UserFile, TCPNetwork" }
        .Select(n => Type.GetType(n, false)).FirstOrDefault(t => t != null) ?? throw new Exception("Unknown user layout");
    object user = Activator.CreateInstance(userType);
    userType.GetProperty("Username").SetValue(user, "admission-probe");
    clientType.GetMethods(all).Single(m => m.Name == "GetData" && m.IsGenericMethod && m.GetParameters().Length == 1)
        .MakeGenericMethod(userType).Invoke(client, new[] { user });
    if (branch == "save") File.WriteAllText(Path.Combine(scratch, "admission-probe.rws"), "probe");
    var listenerProperty = clientType.GetProperty("Listener", all);
    Type listenerType = listenerProperty.PropertyType;
    object listener = RuntimeHelpers.GetUninitializedObject(listenerType);
    listenerProperty.SetValue(client, listener);
    listenerType.GetProperty("TargetClient", all).SetValue(listener, client);
    MethodInfo enqueue = listenerType.GetMethod("EnqueuePacket", all);
    Type headerType = enqueue.GetParameters()[0].ParameterType;
    var suppress = gate.GetMethod("SuppressConfigCheck", all);
    object[] checkArgs = { true };
    if ((bool)suppress.Invoke(null, checkArgs) || (bool)checkArgs[0]) throw new Exception("Enabled suppression failed");
    config.GetProperty("Enabled").SetValue(cfg, false);
    if (!(bool)suppress.Invoke(null, new object[] { true })) throw new Exception("Disabled stock behavior failed");
    var harmonyMethod = harmony.Assembly.GetType("HarmonyLib.HarmonyMethod", true);
    var patch = harmony.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
    void Prefix(MethodInfo target, string hook)
    {
        var method = typeof(ProbeHooks).GetMethod(hook);
        object prefix = Activator.CreateInstance(harmonyMethod, method);
        patch.Invoke(patcher, new[] { target, prefix, null, null, null });
    }
    Prefix(userType.GetMethod("UpdateAdmin", all), nameof(ProbeHooks.Quiet));
    foreach (var printer in AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).Where(t => t.Name == "Printer"))
        foreach (var warning in printer.GetMethods(all).Where(m => m.Name == "Warning")) Prefix(warning, nameof(ProbeHooks.Quiet));
    Prefix(listenerType.GetMethod("MarkForDisconnect", all), nameof(ProbeHooks.Quiet));
    gate.GetMethod("Install", all).Invoke(null, new[] { patcher });
    config.GetProperty("Enabled").SetValue(cfg, true);
    int count = 0;
    // Null would throw inside native enqueue. Returning proves the installed prefix held it.
    foreach (string header in Enum.GetNames(headerType).Where(n => n.Contains("GlobalData") || n.Contains("World")
        || n.Contains("Save") || n.Contains("Scenario") || n.Contains("Storyteller") || n.Contains("Difficulty") || n.Contains("GameParameter")))
    {
        enqueue.Invoke(listener, new[] { Enum.Parse(headerType, header), null });
        Console.WriteLine("HELD native packet " + header);
        count++;
    }
    if (count < 3) throw new Exception("Too few world-state headers found");
    Prefix(enqueue, nameof(ProbeHooks.Capture));
    File.WriteAllText(planet, "{}");
    Type mods = server.GetTypes().Single(t => t.Name == "PM_Mods");
    MethodInfo presence = mods.GetMethod("CheckIfModConflict", all);
    object presencePatches = harmony.GetMethod("GetPatchInfo").Invoke(null, new object[] { presence });
    if (presencePatches != null) throw new Exception("RWT presence unexpectedly patched");
    Type modConfig = master.GetProperty("ModConfig").PropertyType;
    object serverMods = Activator.CreateInstance(modConfig), clientMods = Activator.CreateInstance(modConfig);
    master.GetProperty("ModConfig").SetValue(null, serverMods);
    var serverRows = (System.Collections.IList)modConfig.GetProperty("ModConfigs").GetValue(serverMods);
    var clientRows = (System.Collections.IList)modConfig.GetProperty("ModConfigs").GetValue(clientMods);
    Type rowType = modConfig.GetProperty("ModConfigs").PropertyType.GetGenericArguments()[0];
    object Row(string name, string mode)
    {
        object row = Activator.CreateInstance(rowType);
        (rowType.GetProperty("ModName") ?? rowType.GetProperty("FileName")).SetValue(row, name);
        var type = rowType.GetProperty("Type");
        type.SetValue(row, Enum.Parse(type.PropertyType, mode));
        return row;
    }
    object clientPresence = clientMods;
    if (presence.GetParameters()[1].ParameterType != modConfig)
    {
        clientPresence = Activator.CreateInstance(presence.GetParameters()[1].ParameterType);
        (clientPresence.GetType().GetProperty("RunningMods") ?? clientPresence.GetType().GetProperty("_runningMods")).SetValue(clientPresence, clientMods);
    }
    serverRows.Add(Row("required", "Required"));
    if (!(bool)presence.Invoke(null, new[] { client, clientPresence })) throw new Exception("Missing required mod was admitted");
    serverRows.Clear(); clientRows.Add(Row("unknown", "Optional"));
    if (!(bool)presence.Invoke(null, new[] { client, clientPresence })) throw new Exception("Unknown mod was admitted");
    Console.WriteLine("PASS original native presence rejects missing required and unknown mods");
    clientRows.Clear();
    object serverRow = Row("optional", "Optional"), clientRow = Row("optional", "Optional");
    serverRows.Add(serverRow); clientRows.Add(clientRow);
    var settingsCheck = mods.GetMethod("CheckForModConfigs", all);
    var orderCheck = mods.GetMethod("CheckForModOrder", all);
    if (settingsCheck != null && orderCheck != null)
    {
        object Hashed(Type type, string hash)
        {
            object value = Activator.CreateInstance(type);
            type.GetProperty("Hash").SetValue(value, hash);
            return value;
        }
        modConfig.GetProperty("AllowAllMods").SetValue(serverMods, false);
        modConfig.GetProperty("EnforceSettings").SetValue(serverMods, true);
        var settings = rowType.GetProperty("Settings");
        settings.SetValue(serverRow, Hashed(settings.PropertyType, "server"));
        settings.SetValue(clientRow, Hashed(settings.PropertyType, "client"));
        var order = modConfig.GetProperty("ModOrder");
        order.SetValue(serverMods, Hashed(order.PropertyType, "server-order"));
        order.SetValue(clientMods, Hashed(order.PropertyType, "client-order"));
        foreach (bool enabled in new[] { true, false })
        {
            config.GetProperty("Enabled").SetValue(cfg, enabled);
            foreach (var check in new[] { settingsCheck, orderCheck })
            {
                bool mismatch = (bool)check.Invoke(null, new[] { client, clientMods });
                if (mismatch == enabled) throw new Exception("Native conditional suppression failed: " + check.Name);
                Console.WriteLine("PASS " + check + (enabled ? " suppressed" : " stock mismatch"));
            }
        }
        if ((string)order.PropertyType.GetProperty("Hash").GetValue(order.GetValue(serverMods)) != "server-order")
            throw new Exception("Optional type unexpectedly cleared the order hash");
        Console.WriteLine("PASS stock Optional settings mismatch; Optional type retains exact order hash");
    }
    else Console.WriteLine("PASS legacy presence-only schema has no settings/order targets");
    if (branch == "no-world") File.Delete(planet);
    ProbeHooks.Packets.Clear();
    string policy = Environment.GetEnvironmentVariable("KMH_PROBE_POLICY") ?? "profile";
    bool admin = policy == "admin-on" || policy == "admin-off";
    userType.GetProperty("IsAdmin").SetValue(user, admin);
    config.GetProperty("AdminBypass").SetValue(cfg, policy == "admin-on");
    Type profile = kmh.GetType("KMHServerAddon.Features.Enforcement.EnforcementProfile", true);
    profile.GetField("_zip", all).SetValue(null, policy == "no-profile" ? Array.Empty<byte>() : new byte[] { 1 });
    profile.GetField("_hash", all).SetValue(null, policy == "no-profile" ? "" : "native-probe-profile");
    clientType.GetField("<IsVerified>k__BackingField", all).SetValue(client, true);
    Type network = listenerType.Assembly.GetTypes().Single(t => t.Name == "Network");
    object clients = network.GetProperty("ServerClients", all)?.GetValue(null)
        ?? network.GetField("ServerClients", all).GetValue(null);
    clients.GetType().GetMethod("TryAdd").Invoke(clients, new object[] { client, -1 });
    // Run native PostLogin's body, replacing only its outbound boundaries with recording sinks.
    foreach (string name in new[] { "SendPlayerRecount", "SendServerGlobalData", "SendLoginChatMessages", "SendWorld", "SendSaveToClient", "RequireWorldFile" })
    {
        var targets = server.GetTypes().SelectMany(t => t.GetMethods(all)).Where(m => m.Name == name && m.IsStatic).Distinct().ToArray();
        if (targets.Length != 1) throw new Exception("Unsupported native boundary: " + name);
        Prefix(targets[0], nameof(ProbeHooks.Record));
    }
    config.GetProperty("Enabled").SetValue(cfg, true);
    post.Invoke(null, new[] { client });
    if (ProbeHooks.Calls.Count != 0) throw new Exception("Native PostLogin escaped before verification");
    if (ProbeHooks.Packets.Count != 1 || !ProbeHooks.Packets[0].Contains("kmh.hello"))
        throw new Exception("Early hello did not traverse the native chat carrier");
    object sessions = gate.GetField("Sessions", all).GetValue(null);
    object[] lookup = { client, null };
    if (!(bool)sessions.GetType().GetMethod("TryGetValue").Invoke(sessions, lookup)) throw new Exception("No admission ticket");
    object ticket = lookup[1];
    Type ticketType = ticket.GetType();
    ticketType.GetMethod("AcceptHello", all).Invoke(ticket, new object[] { true });
    Type envelope = kmh.GetType("KMHServerAddon.SubProtocol.KmhEnvelope", true);
    string resolution = policy == "admin-on" ? "AdminBypass" : policy == "no-profile" ? "NoProfile" : "ProfileVerified";
    object stale = Activator.CreateInstance(envelope, new object[] { "kmh.enforcement.verified", new { active = true, resolution, admission_id = "stale" }, 2 });
    gate.GetMethod("OnVerified", all).Invoke(null, new[] { client, stale });
    if (ProbeHooks.Calls.Count != 0) throw new Exception("Stale verification released native PostLogin");
    object payload = gate.GetMethod("Bind", all).Invoke(null, new object[] { client, new { active = true, resolution } });
    object verified = Activator.CreateInstance(envelope, new object[] { "kmh.enforcement.verified", payload, 2 });
    for (int i = 0; i < 2; i++)
    {
        gate.GetMethod("OnVerified", all).Invoke(null, new[] { client, verified });
        // Native RWT can promote the first player; a duplicate must ignore post-release policy changes.
        userType.GetProperty("IsAdmin").SetValue(user, true);
    }
    post.Invoke(null, new[] { client });
    string[] expected = { "SendPlayerRecount", "SendServerGlobalData", "SendLoginChatMessages",
        branch == "save" ? "SendSaveToClient" : branch == "world" ? "SendWorld" : "RequireWorldFile" };
    if (!ProbeHooks.Calls.SequenceEqual(expected)) throw new Exception("Native release sequence: " + string.Join(",", ProbeHooks.Calls));
    if (ticketType.GetProperty("State", all).GetValue(ticket).ToString() != "Released") throw new Exception("Native release failed");
    Console.WriteLine("PASS native PostLogin held, then " + string.Join(" -> ", ProbeHooks.Calls) + " exactly once");
    Console.WriteLine("PASS: real Harmony installation, native packet hold, conditional suppression");
}

catch (Exception ex)
{
    Console.Error.WriteLine(ex.InnerException ?? ex);
    Environment.ExitCode = 1;
}

public static class ProbeHooks
{
    public static readonly List<string> Calls = new();
    public static readonly List<string> Packets = new();
    public static bool Capture(object[] __args)
    {
        object packet = __args[1];
        Packets.Add(packet?.GetType().GetProperty("Message")?.GetValue(packet)?.ToString() ?? __args[0].ToString());
        return false;
    }
    public static bool Record(MethodBase __originalMethod) { Calls.Add(__originalMethod.Name); return false; }
    public static bool Quiet() => false;
}
