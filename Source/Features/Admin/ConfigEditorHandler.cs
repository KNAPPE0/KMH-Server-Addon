using System.Collections.Generic;
using KMHServerAddon.AdminCommands;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Identity;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Admin
{
    // Owner only, never a live RWT admin: an admin can moderate a session, an owner owns the server.
    internal static class ConfigEditorHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ConfigRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ConfigApply,   OnApply);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ConfigRestart, OnRestart);
        }

        // The packet supplies neither fact: the name comes from the authenticated session, so a self-drawn owner badge
        // changes nothing, and the grant from the server console, so being on the Owners list is not enough.
        private static string OwnerOrNull(ServerClient client, out string why)
        {
            why = "";
            string who = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(who) || !StaffRegistry.IsOwner(who))
            {
                why = "Server settings are owner-only.";
                return null;
            }
            if (!ConfigEditorGrant.Allows(who, System.DateTimeOffset.UtcNow))
            {
                why = "Settings access has to be opened at the server console: kmh config-editor allow "
                    + who + ". It then closes on its own.";
                return null;
            }
            return who;
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            string owner = OwnerOrNull(client, out string why);
            if (owner == null) { Refuse(client, why); return; }

            string area = env?.GetString("area") ?? "";
            if (string.IsNullOrEmpty(area))
            {
                KmhRouter.SendTo(client, KmhProtocol.Kind.ConfigSchema, new { areas = KmhConfigSchema.Areas() });
                return;
            }

            List<ConfigFieldSpec> fields = KmhConfigSchema.Describe(area);
            KmhRouter.SendTo(client, KmhProtocol.Kind.ConfigSchema, new
            {
                area,
                fields = Wire(fields),
            });
        }

        // Shaped here rather than serialising the spec type, so a field added to the spec for the server's own use
        // does not silently start travelling to clients.
        private static List<object> Wire(List<ConfigFieldSpec> fields)
        {
            var wire = new List<object>();
            foreach (ConfigFieldSpec f in fields)
                wire.Add(new
                {
                    path = f.Path, type = f.Type, value = f.Value,
                    def = f.Default, ro = f.ReadOnly, secret = f.Secret,
                });
            return wire;
        }

        private static void OnApply(ServerClient client, KmhEnvelope env)
        {
            string owner = OwnerOrNull(client, out string why);
            if (owner == null)
            {
                // Logged at warn, not debug: an attempt to write server settings without a grant is worth seeing.
                ServerLog.Warn($"Config: refused a settings write from '{client?.GetData<UserFile>()?.Username}' - {why}");
                Refuse(client, why);
                return;
            }

            string area = env?.GetString("area") ?? "";
            Dictionary<string, string> edits = env?.GetStringMap("edits") ?? new Dictionary<string, string>();

            bool ok = KmhConfigApply.Apply(area, edits, owner, out string error, out string ownerNote);
            KmhRouter.SendTo(client, KmhProtocol.Kind.ConfigApplied, new
            {
                area,
                ok,
                reason = ok ? ownerNote : error,
            });

            if (!ok) ServerLog.Info($"Config: {owner} was refused an edit to {area} - {error}");
        }

        // Same gate as a settings write: it is the settings the owner is trying to make take effect.
        private static void OnRestart(ServerClient client, KmhEnvelope env)
        {
            string owner = OwnerOrNull(client, out string why);
            if (owner == null)
            {
                ServerLog.Warn($"Restart: refused from '{client?.GetData<UserFile>()?.Username}' - {why}");
                Refuse(client, why);
                return;
            }

            // Answered before the process goes away, or the owner's client only ever sees the disconnect.
            KmhRouter.SendTo(client, KmhProtocol.Kind.ConfigApplied, new
            {
                area = "",
                ok = true,
                reason = $"Restarting the server now - {Maintenance.KmhRestart.Describe()}.",
            });

            // Off the network thread and after a beat, so the acknowledgement above is on the wire before the
            // process starts tearing itself down.
            System.Threading.Tasks.Task.Run(() =>
            {
                System.Threading.Thread.Sleep(500);
                try { Maintenance.KmhRestart.Request(owner, m => ServerLog.Info($"Restart: {m}")); }
                // An unobserved fault here would leave the server running after telling the owner it restarted.
                catch (Exception ex) { ServerLog.Error($"Restart: request threw ({ex.GetType().Name}: {ex.Message})."); }
            });
        }

        private static void Refuse(ServerClient client, string why)
        {
            KmhRouter.SendTo(client, KmhProtocol.Kind.ConfigApplied, new { area = "", ok = false, reason = why });
            KmhRouter.Notify(client, "negative", why);
        }
    }
}
