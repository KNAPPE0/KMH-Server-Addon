using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace KmhDiscordCoordinator
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") return CoordinatorSelfTest.Run();

            string configPath = args.Length > 0 ? args[0] : "coordinator.json";

            if (args.Length > 1 && args[1] == "--write-sample")
            {
                File.WriteAllText(configPath, JsonConvert.SerializeObject(CoordinatorConfig.Sample(), Formatting.Indented));
                Console.WriteLine($"wrote {configPath}");
                return 0;
            }

            CoordinatorConfig cfg;
            try { cfg = CoordinatorConfig.Load(configPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"config {configPath}: {ex.Message}");
                return 2;
            }

            string token = CoordinatorConfig.TokenFromEnvironment();
            if (token.Length == 0)
            {
                Console.Error.WriteLine("KMH_DISCORD_BOT_TOKEN is not set - the coordinator is the only place the token belongs");
                return 2;
            }
            CoordLog.RegisterSecret(token);

            GlobalRevocation.Configure(cfg.GlobalBaseUrl, cfg.RevocationCheckSeconds, cfg.GlobalGraceSeconds);

            ServerRegistry registry = new ServerRegistry(cfg);
            GatewayHost gateway = new GatewayHost(cfg, registry);
            HttpApi api = new HttpApi(cfg, registry, gateway);

            using CancellationTokenSource stopping = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

            CoordLog.Info($"Colony Nexus Discord coordinator starting - {cfg.Servers.Count} server(s) configured");
            api.Start(stopping.Token);
            await gateway.StartAsync(token).ConfigureAwait(false);

            try { await Task.Delay(Timeout.Infinite, stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            CoordLog.Info("stopping");
            api.Stop();
            await gateway.StopAsync().ConfigureAwait(false);
            return 0;
        }
    }
}
