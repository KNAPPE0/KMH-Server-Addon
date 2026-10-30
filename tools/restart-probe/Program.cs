using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

string mode = args[0], root = Path.GetFullPath(args[1]), target = Path.GetFullPath(args[2]);
string[] probes = args.Skip(3).Select(Path.GetFullPath).ToArray();
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string probe in probes)
    {
        string path = Path.Combine(probe, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
Assembly client = Assembly.LoadFrom(target);
Type applier = client.GetType("KMHPatch.Features.Enforcement.EnforcementProfileApplier", true);
int exit = 1;
try
{
    applier.GetField("RootOverrideForTest", flags).SetValue(null, root);
    applier.GetField("QueueForTest", flags).SetValue(null, (Action<Action>)(action => action()));
    int restarts = 0;
    applier.GetField("RestartForTest", flags).SetValue(null, (Action)(() => restarts++));
    client.GetType("KMHPatch.SubProtocol.KmhDispatcher", true).GetProperty("ServerId", flags).SetValue(null, "process-restart-test");
    string config = Path.Combine(root, "Config"), source = Path.Combine(root, "source");
    string state = Path.Combine(root, "KMH_ENFORCE_STATE.json"), generationFile = Path.Combine(root, "generation.txt");
    Directory.CreateDirectory(config); Directory.CreateDirectory(source);
    if (mode == "apply")
    {
        if (File.Exists(state)) throw new Exception("Apply requires a fresh test root");
        File.WriteAllText(Path.Combine(config, "settings.xml"), "<settings>personal</settings>");
        File.WriteAllText(Path.Combine(source, "settings.xml"), "<settings>server</settings>");
    }
    else applier.GetMethod("LoadState", flags).Invoke(null, null);
    Type utility = client.GetType("KMHPatch.Features.Enforcement.ConfigProfileUtility", true);
    byte[] zip = (byte[])utility.GetMethod("CreateConfigZipBytes", flags).Invoke(null, new object[] { source, null });
    string hash = (string)utility.GetMethod("Sha256Hex", flags).Invoke(null, new object[] { zip });
    Type flow = client.GetType("KMHPatch.Features.Enforcement.EnforcementFlow", true);
    if (mode == "apply")
    {
        flow.GetField("<PendingHash>k__BackingField", flags).SetValue(null, hash);
        flow.GetField("_awaitingApply", flags).SetValue(null, true);
    }
    for (int attempt = 0; attempt < 2; attempt++)
    {
        applier.GetMethod("OnProfileBegin", flags).Invoke(null, new object[] { hash, 1, zip.Length });
        applier.GetMethod("OnProfileChunk", flags).Invoke(null, new object[] { hash, 0, Convert.ToBase64String(zip) });
        applier.GetMethod("OnProfileEnd", flags).Invoke(null, new object[] { hash });
    }
    using var disk = JsonDocument.Parse(File.ReadAllText(state));
    string generation = disk.RootElement.GetProperty("ApplyGeneration").GetString();
    if (!disk.RootElement.GetProperty("RestartConsumed").GetBoolean()
        || (bool)applier.GetProperty("RecoveryRequired", flags).GetValue(null)) throw new Exception("Ticket is not durable and valid");
    if (restarts != (mode == "apply" ? 1 : 0)) throw new Exception("Unexpected restart count: " + restarts);
    if (mode == "apply") File.WriteAllText(generationFile, generation);
    else if (File.ReadAllText(generationFile) != generation) throw new Exception("Reconnect created a second apply generation");
    Console.WriteLine($"PASS process {Environment.ProcessId}: {mode}, restarts={restarts}, same durable generation={generation}");
    exit = 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.InnerException ?? ex); }
finally { applier.GetMethod("StopWatching", flags).Invoke(null, null); }
Environment.Exit(exit);
