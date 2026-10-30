using System;
using System.Collections.Generic;

namespace KMHServerAddon.AdminCommands
{
    // Also reached as `kmh inspect guild ...`, which passes the same words on.
    internal static class KmhGuildCommands
    {
        public static void Run(string[] args, Action<string> reply)
        {
            switch (args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "")
            {
                case "inspect":
                {
                    string name = args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : "";
                    if (Features.Guilds.GuildStore.InspectGuild(name, out List<string> lines))
                        foreach (string l in lines) reply(l);
                    else reply(string.IsNullOrEmpty(name) ? "Usage: kmh guild inspect <guild name>" : $"No guild named '{name}'.");
                    break;
                }
                case "repair":
                {
                    int fixes = Features.Guilds.GuildStore.RepairMembership(out List<string> notes);
                    reply(fixes == 0 ? "Guild membership is consistent - nothing to repair."
                                     : $"Guild membership repaired: {fixes} entry(ies) reconciled.");
                    foreach (string n in notes) reply($"  {n}");
                    break;
                }
                default:
                    reply($"=== Guilds === {Features.Guilds.GuildStore.ListGuilds().Count} guild(s). 'kmh guild inspect <name>' shows detail; "
                        + "'kmh guild repair' reconciles membership. Manage in-game (Guild Hall) or Discord.");
                    break;
            }
        }
    }
}
