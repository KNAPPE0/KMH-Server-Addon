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

            f["chat.theme.accent"]      = chat.Theme.Accent ?? "";
            f["chat.theme.server"]      = chat.Theme.ServerChat ?? "";
            f["chat.theme.guild"]       = chat.Theme.GuildChat ?? "";
            f["chat.theme.dm"]          = chat.Theme.DirectMsg ?? "";
            f["chat.theme.discord"]     = chat.Theme.Discord ?? "";
            f["chat.theme.name"]        = chat.Theme.NameNormal ?? "";
            f["chat.theme.text"]        = chat.Theme.TextNormal ?? "";
            f["chat.theme.name.dc"]     = chat.Theme.NameDiscord ?? "";
            f["chat.theme.text.dc"]     = chat.Theme.TextDiscord ?? "";
            f["chat.discord.marker"]    = Chat.ChatConfig.ClampMarker(chat.Theme.DiscordMarker);

            f["chat.image.previews"]    = chat.AllowImagePreviews.ToString();
            f["chat.image.typed"]       = chat.AllowTypedImageUrls.ToString();
            f["chat.image.maxbytes"]    = chat.MaxImageBytes.ToString();
            f["chat.image.hosts"]       = JoinSorted(chat.ImageHostAllowList);
            f["chat.image.exts"]        = JoinSorted(chat.ImageFileExtensions);
            f["chat.video.exts"]        = JoinSorted(chat.VideoFileExtensions);
            f["chat.video.allowed"]     = chat.AllowVideoLinks.ToString();
            f["chat.proxy.transcode"]   = chat.DiscordProxyTranscode.ToString();
            f["chat.proxy.hosts"]       = JoinSorted(chat.TranscodeHosts);
            f["chat.prefer.original"]   = chat.PreferOriginalForAnimated.ToString();

            f["chat.blocking"]          = chat.Blocking.Enabled.ToString();
            f["chat.history"]           = chat.Limits.PersistHistory.ToString();
            f["chat.recent"]            = chat.Limits.MaxRecentPerChannel.ToString();

            f["staff.badges"]           = staff.ShowStaffBadges.ToString();
            f["staff.rwtadmins"]        = staff.TreatRwtAdminsAsStaff.ToString();
            f["staff.wire"]             = Identity.StaffConfig.Current.BadgeWire() ?? "";
            f["staff.owners"]           = JoinSorted(staff.Owners);
            f["staff.developers"]       = JoinSorted(staff.Developers);
            f["staff.admins"]           = JoinSorted(staff.Admins);
            f["staff.moderators"]       = JoinSorted(staff.Moderators);
            f["staff.ops"]              = JoinSorted(staff.Ops);

            f["media.resolver"]         = media.Images.ResolverEnabled.ToString();
            f["media.src.bytes"]        = media.Images.MaxSourceBytes.ToString();
            f["media.out.bytes"]        = media.Images.MaxOutputBytes.ToString();
            f["media.pixels"]           = media.Images.MaxPixels.ToString();
            f["media.dimension"]        = media.Images.MaxDimension.ToString();
            f["media.frames"]           = media.Images.MaxFrames.ToString();
            f["media.timeout"]          = media.Fetch.TimeoutSeconds.ToString();
            f["media.redirects"]        = media.Fetch.MaxRedirects.ToString();
            f["media.video.seconds"]    = media.Video.MaxSeconds.ToString();

            f["feature.chat"]           = FeaturesConfig.Current.Chat.ToString();
            f["capabilities"]           = SubProtocol.KmhCapabilities.Manifest ?? "";

            return f;
        }

        private static string JoinSorted(IEnumerable<string> items)
            => string.Join(",", (items ?? Enumerable.Empty<string>()).OrderBy(x => x));

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
