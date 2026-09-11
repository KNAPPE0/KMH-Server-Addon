using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KMHServerAddon.Features.Comms
{
    // Every Communications value a client is told about, in one comparable form, so boot and reload can be diffed.
    internal static class CommsPresentation
    {
        // Sorted, so a diff reports a real change rather than an ordering artefact.
        internal static SortedDictionary<string, string> Fields()
        {
            var f = new SortedDictionary<string, string>(System.StringComparer.Ordinal);

            Chat.ChatConfig chat = Chat.ChatConfig.Current;
            Identity.StaffConfig staff = Identity.StaffConfig.Current;
            Media.MediaConfig media = Media.MediaConfig.Current;

            f["chat.theme.accent"]      = chat.ThemeAccent ?? "";
            f["chat.theme.server"]      = chat.ThemeServerChat ?? "";
            f["chat.theme.guild"]       = chat.ThemeGuildChat ?? "";
            f["chat.theme.dm"]          = chat.ThemeDirectMsg ?? "";
            f["chat.theme.discord"]     = chat.ThemeDiscord ?? "";
            f["chat.theme.name"]        = chat.ThemeNameNormal ?? "";
            f["chat.theme.text"]        = chat.ThemeTextNormal ?? "";
            f["chat.theme.name.dc"]     = chat.ThemeNameDiscord ?? "";
            f["chat.theme.text.dc"]     = chat.ThemeTextDiscord ?? "";
            f["chat.discord.marker"]    = Chat.ChatConfig.ClampMarker(chat.DiscordMarker);

            f["chat.image.previews"]    = chat.AllowImagePreviews.ToString();
            f["chat.image.typed"]       = chat.AllowTypedImageUrls.ToString();
            f["chat.image.maxbytes"]    = chat.MaxImageBytes.ToString();
            f["chat.image.hosts"]       = string.Join(",", (chat.ImageHostAllowList ?? new string[0]).OrderBy(x => x));
            f["chat.image.exts"]        = string.Join(",", (chat.ImageFileExtensions ?? new string[0]).OrderBy(x => x));
            f["chat.video.exts"]        = string.Join(",", (chat.VideoFileExtensions ?? new string[0]).OrderBy(x => x));
            f["chat.video.allowed"]     = chat.AllowVideoLinks.ToString();
            f["chat.proxy.transcode"]   = chat.DiscordProxyTranscode.ToString();
            f["chat.proxy.hosts"]       = string.Join(",", (chat.TranscodeHosts ?? new string[0]).OrderBy(x => x));
            f["chat.prefer.original"]   = chat.PreferOriginalForAnimated.ToString();

            f["chat.blocking"]          = chat.BlockingEnabled.ToString();
            f["chat.history"]           = chat.PersistHistory.ToString();
            f["chat.recent"]            = chat.MaxRecentPerChannel.ToString();

            f["staff.badges"]           = staff.ShowStaffBadges.ToString();
            f["staff.rwtadmins"]        = staff.TreatRwtAdminsAsStaff.ToString();
            f["staff.wire"]             = Identity.StaffConfig.Current.BadgeWire() ?? "";
            f["staff.owners"]           = string.Join(",", (staff.Owners ?? new List<string>()).OrderBy(x => x));
            f["staff.developers"]       = string.Join(",", (staff.Developers ?? new List<string>()).OrderBy(x => x));
            f["staff.admins"]           = string.Join(",", (staff.Admins ?? new List<string>()).OrderBy(x => x));
            f["staff.moderators"]       = string.Join(",", (staff.Moderators ?? new List<string>()).OrderBy(x => x));
            f["staff.ops"]              = string.Join(",", (staff.Ops ?? new List<string>()).OrderBy(x => x));

            f["media.resolver"]         = media.ServerMediaResolverEnabled.ToString();
            f["media.src.bytes"]        = media.MaxSourceBytes.ToString();
            f["media.out.bytes"]        = media.MaxOutputBytes.ToString();
            f["media.pixels"]           = media.MaxPixels.ToString();
            f["media.dimension"]        = media.MaxDimension.ToString();
            f["media.frames"]           = media.MaxFrames.ToString();
            f["media.timeout"]          = media.TimeoutSeconds.ToString();
            f["media.redirects"]        = media.MaxRedirects.ToString();
            f["media.video.seconds"]    = media.MaxVideoSeconds.ToString();

            f["feature.chat"]           = FeaturesConfig.Current.Chat.ToString();
            f["capabilities"]           = SubProtocol.KmhCapabilities.Manifest ?? "";

            return f;
        }

        internal static string Snapshot()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in Fields()) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            return sb.ToString();
        }

        internal static List<string> Diff(SortedDictionary<string, string> a, SortedDictionary<string, string> b)
        {
            var diffs = new List<string>();
            foreach (string key in a.Keys.Concat(b.Keys).Distinct().OrderBy(x => x))
            {
                a.TryGetValue(key, out string av);
                b.TryGetValue(key, out string bv);
                if (!string.Equals(av ?? "", bv ?? "", System.StringComparison.Ordinal))
                    diffs.Add($"{key}: boot='{Trim(av)}' reload='{Trim(bv)}'");
            }
            return diffs;
        }

        private static string Trim(string s)
            => s == null ? "(absent)" : (s.Length <= 60 ? s : s.Substring(0, 57) + "...");
    }
}
