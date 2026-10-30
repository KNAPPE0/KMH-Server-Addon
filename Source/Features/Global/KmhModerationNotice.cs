using System;
using System.Collections.Generic;
using KMHServerAddon.Util;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    internal sealed class KmhModerationNotice
    {
        public string Listing = "Normal", Edits = "Open", Owner = "Normal", Message = "";
        public long ListingUntilUnix, MessageAtUnix;
        public bool PendingReview;

        public static KmhModerationNotice Current { get; private set; }
        private static string _lastKey = "";

        public bool Blocks => Listing == "ServerBanned" || Owner == "Banned";

        public string Summary
        {
            get
            {
                List<string> parts = new List<string>();
                switch (Listing)
                {
                    case "Hidden": parts.Add("this server is hidden from the directory"); break;
                    case "Suspended":
                        parts.Add("this server is suspended from the directory" + (ListingUntilUnix > 0
                            ? " until " + DateTimeOffset.FromUnixTimeSeconds(ListingUntilUnix).UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC" : ""));
                        break;
                    case "ListingBanned": parts.Add("this server's listing is banned"); break;
                    case "ServerBanned": parts.Add("this server is banned from Colony Nexus"); break;
                }
                if (Edits == "Review") parts.Add("listing changes wait for a moderator");
                if (Edits == "Locked") parts.Add("listing changes are locked");
                if (Owner == "Review") parts.Add("your listing changes wait for a moderator");
                if (Owner == "Restricted") parts.Add("your account cannot change listings");
                if (Owner == "Banned") parts.Add("your account may not publish listings");
                if (PendingReview) parts.Add("a listing change is waiting for review");
                if (parts.Count == 0 && Message.Length > 0) parts.Add("a message from the moderators");
                return string.Join("; ", parts);
            }
        }

        public string Remedy
        {
            get
            {
                string what;
                if (Blocks || Listing == "ListingBanned")
                    what = "Ask Colony Nexus moderation to review it; the server keeps running for its players meanwhile.";
                else if (Listing == "Hidden" || Listing == "Suspended")
                    what = "It returns to the directory when the moderators lift it" + (ListingUntilUnix > 0 ? " or the time runs out" : "")
                         + "; the server keeps running for its players meanwhile.";
                else if (Owner == "Restricted" || Edits == "Locked")
                    what = "Listing changes are refused until the moderators unlock them; the current listing stays as it is.";
                else if (PendingReview || Edits == "Review" || Owner == "Review")
                    what = "Players keep seeing the last approved listing until a moderator reviews the change. A newer change "
                         + "replaces the held one; setting it back to what is live withdraws it.";
                else what = "";
                if (Message.Length > 0) what = (what.Length > 0 ? what + " " : "") + "Message from the moderators: \"" + Message + "\"";
                return what;
            }
        }

        // Null clears it: Global sends nothing when there is nothing to say.
        public static void OnHeartbeatResponse(JToken token)
        {
            KmhModerationNotice n = Parse(token);
            string key = n == null ? "" : token.ToString(Newtonsoft.Json.Formatting.None);
            Current = n;
            if (key == _lastKey) return;
            _lastKey = key;
            if (n == null) { ServerLog.Info("Global: no moderation action applies to this server any more."); return; }
            string line = "Colony Nexus moderation: " + (n.Summary.Length > 0 ? n.Summary : "status changed") + "."
                        + (n.Message.Length > 0 ? " Message from the moderators: \"" + n.Message + "\"" : "");
            if (n.Listing != "Normal" || n.Owner != "Normal" || n.Edits == "Locked") ServerLog.Warn(line);
            else ServerLog.Info(line);
        }

        internal static KmhModerationNotice Parse(JToken token)
        {
            if (!(token is JObject o)) return null;
            return new KmhModerationNotice
            {
                Listing = (string)o["listing"] ?? "Normal",
                ListingUntilUnix = (long?)o["listing_until"] ?? 0,
                Edits = (string)o["edits"] ?? "Open",
                Owner = (string)o["owner"] ?? "Normal",
                PendingReview = (bool?)o["pending_review"] ?? false,
                Message = Clip((string)o["message"] ?? "", 500),
                MessageAtUnix = (long?)o["message_at"] ?? 0,
            };
        }

        // A server banned outright never gets a heartbeat answer, so the refusal itself carries the news.
        public static void OnBanned()
        {
            if (Current != null && Current.Listing == "ServerBanned") return;
            Current = new KmhModerationNotice { Listing = "ServerBanned" };
            _lastKey = "banned";
            ServerLog.Warn("Colony Nexus moderation: this server is banned from Colony Nexus, so it is not listed and "
                         + "Global refuses its heartbeats. The server itself keeps running for its players.");
        }

        // Global text is shown in a console; control characters never reach it.
        private static string Clip(string s, int max)
        {
            char[] chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (char.IsControl(chars[i])) chars[i] = ' ';
            string clean = new string(chars).Trim();
            return KmhSafe.Cap(clean, max);
        }

        internal static void ResetForTest() { Current = null; _lastKey = ""; }
    }
}
