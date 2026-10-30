using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Media
{
    internal static class KmhVideoHandler
    {
        private static readonly TimeSpan ResultLife = TimeSpan.FromHours(2);
        private static readonly TimeSpan JobLife = TimeSpan.FromMinutes(10);

        private sealed class Job
        {
            // Each waiter keeps the link it sent: clients match the answer by their own exact text.
            public readonly List<(ServerClient Client, string WatchUrl)> Waiting = new List<(ServerClient, string)>();
            public string Video = "", Title = "";
            public DateTime Started = DateTime.UtcNow, Finished;
            public bool Done;

            // FetchUrl is the rebuilt link the helper is given.
            public string Key = "", User = "", FetchUrl = "", VideoId = "";
            public int Height, MaxSeconds;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>(StringComparer.Ordinal);

        // Each fetch is a process, a download and a merge, so twenty pasted links queue rather than all starting.
        private static readonly Queue<Job> Waiting = new Queue<Job>();
        internal const int MaxWaitingJobs = 32;
        private static int _running;

        internal static int WaitingCountForTest { get { lock (Gate) return Waiting.Count; } }

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.VideoResolve, OnResolve);
        }

        private static void OnResolve(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            MediaConfig cfg = MediaConfig.Current;
            string watchUrl = (env?.GetString("watch_url") ?? "").Trim();
            string videoId = VideoIdOf(watchUrl);

            if (!cfg.Watch.PagePlayback || !cfg.Video.ServerEnabled) { Reply(client, watchUrl, "", "", "video links are off on this server"); return; }
            if (!KmhVideoRelay.Running) { Reply(client, watchUrl, "", "", "this server is not serving video right now"); return; }
            if (videoId.Length == 0) { Reply(client, watchUrl, "", "", "that is not a video link"); return; }

            // Nothing the player typed is ever run: the link the helper gets is rebuilt from the id alone.
            string fetchUrl = KmhVideoHelper.CanonicalWatchUrl(videoId);
            if (fetchUrl.Length == 0) { Reply(client, watchUrl, "", "", "that is not a video link"); return; }

            int height = SnapHeight(env?.GetInt("height", 0) ?? 0, cfg.Watch.MaxHeight);

            // A room full of players clicking the same link fetches once: the rest join the job or take its answer.
            string key = videoId + "|" + height;
            Job answered = null;
            bool joined = false;
            string refused = "";
            var abandoned = new List<(ServerClient Client, string WatchUrl)>();
            lock (Gate)
            {
                Sweep();
                if (Jobs.TryGetValue(key, out Job existing))
                {
                    if (existing.Done) answered = existing;
                    else if (DateTime.UtcNow - existing.Started < JobLife) { existing.Waiting.Add((client, watchUrl)); joined = true; }
                    else
                    {
                        // Giving up on it silently leaves whoever asked first waiting for an answer that never comes.
                        abandoned.AddRange(existing.Waiting);
                        existing.Waiting.Clear();
                        Jobs.Remove(key);
                    }
                }
                if (answered == null && !joined)
                {
                    var job = new Job
                    {
                        Key = key, User = username, FetchUrl = fetchUrl, VideoId = videoId,
                        Height = height, MaxSeconds = cfg.Watch.MaxSeconds,
                    };
                    job.Waiting.Add((client, watchUrl));
                    TryQueueLocked(job, out refused);
                }
            }
            foreach ((ServerClient stranded, string theirs) in abandoned)
                Reply(stranded, theirs, "", "", "that fetch gave up - try again");

            if (answered != null) { Reply(client, watchUrl, answered.Video, answered.Title, ""); return; }
            if (refused.Length > 0) { Reply(client, watchUrl, "", "", refused); return; }
            if (joined) return;

            Pump();
        }

        internal const int MaxJobsPerPlayer = 3;
        internal static readonly int[] Heights = { 144, 240, 360, 480, 720, 1080 };

        // A rung, not any number: each height is its own fetch and cache file, so free heights mean a thousand of them.
        internal static int SnapHeight(int requested, int max)
        {
            if (requested <= 0 || requested > max) requested = max;
            int best = Heights[0];
            foreach (int h in Heights) if (h <= requested) best = h;
            return Math.Min(best, max);
        }

        // Queued jobs are never Done and escape the sweep, so the backlog and each player's share are capped.
        private static bool TryQueueLocked(Job job, out string refusal)
        {
            refusal = "";
            if (Waiting.Count >= MaxWaitingJobs) { refusal = "too many videos are queued right now - try again shortly"; return false; }
            int mine = 0;
            foreach (Job j in Jobs.Values) if (!j.Done && string.Equals(j.User, job.User, StringComparison.Ordinal)) mine++;
            if (mine >= MaxJobsPerPlayer) { refusal = "your other videos are still loading - try again when they are ready"; return false; }
            Jobs[job.Key] = job;
            Waiting.Enqueue(job);
            return true;
        }

        internal static string QueueForTest(string key, string user)
        {
            lock (Gate) return TryQueueLocked(new Job { Key = key, User = user }, out string why) ? "" : why;
        }

        internal static void WaitForTest(string key, string user, ServerClient client, string watchUrl)
        {
            lock (Gate)
            {
                if (!Jobs.TryGetValue(key, out Job j)) Jobs[key] = j = new Job { Key = key, User = user };
                j.Waiting.Add((client, watchUrl));
            }
        }

        internal static void ForgetForTest(string user)
        {
            lock (Gate)
            {
                foreach (string k in new List<string>(Jobs.Keys)) if (Jobs[k].User == user) Jobs.Remove(k);
                Job[] keep = Waiting.ToArray();
                Waiting.Clear();
                foreach (Job j in keep) if (j.User != user) Waiting.Enqueue(j);
            }
        }

        private static void Pump()
        {
            int allowed = Math.Max(1, MediaConfig.Current.Video.MaxConcurrentFetches);
            while (true)
            {
                Job job = null;
                lock (Gate)
                {
                    if (_running >= allowed) return;
                    while (Waiting.Count > 0)
                    {
                        Job candidate = Waiting.Dequeue();
                        // Abandoned while it queued: its key belongs to a newer job, and fetching twice helps nobody.
                        if (Jobs.TryGetValue(candidate.Key, out Job current) && ReferenceEquals(current, candidate))
                        {
                            job = candidate;
                            break;
                        }
                    }
                    if (job == null) return;
                    _running++;
                }
                _ = Task.Run(() =>
                {
                    try { Work(job.Key, job.User, job.FetchUrl, job.VideoId, job.Height, job.MaxSeconds); }
                    finally
                    {
                        lock (Gate) _running--;
                        Pump();
                    }
                });
            }
        }

        private static void Work(string key, string username, string fetchUrl, string videoId, int height, int maxSeconds)
        {
            string token = "", title = "", reason = "";
            try
            {
                string file = KmhVideoCache.PathFor(videoId, height);
                title = KmhVideoCache.TitleOf(file);
                if (!KmhVideoCache.Has(file))
                {
                    KmhVideoHelper.Facts facts = KmhVideoHelper.Probe(fetchUrl);
                    title = facts.Title;
                    if (facts.Error.Length > 0) reason = facts.Error;
                    // A live stream has no duration, and fetching one runs until the timeout and fills the cache.
                    else if (facts.Seconds <= 0) reason = "that looks like a live stream - it has no length to fetch";
                    else if (maxSeconds > 0 && facts.Seconds > maxSeconds)
                        reason = $"too long to play here - {facts.Seconds / 60} min, and this server's limit is {maxSeconds / 60} min";
                    else
                    {
                        KmhVideoCache.EnsureFolder();
                        // The id, not what the player typed: a log line is read by people and parsed by tools.
                        ServerLog.Info($"Video server: fetching {videoId} at {height}p for {username}.");
                        file = KmhVideoHelper.Fetch(fetchUrl, height, file, out string error);
                        if (file.Length == 0) reason = error.Length > 0 ? error : "nothing playable was found";
                        else
                        {
                            KmhVideoCache.RememberTitle(file, title);
                            KmhVideoCache.Sweep();
                        }
                    }
                }

                if (reason.Length == 0)
                {
                    KmhVideoCache.Touch(file);
                    token = KmhVideoRelay.Publish(file, "video/mp4");
                    if (token.Length == 0) reason = "the video server could not publish that file";
                    else ServerLog.Info($"Video server: serving {videoId} to {username}.");
                }
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Video server: fetch threw - {ex.Message}");
                reason = "the video server hit an error";
            }
            Finish(key, token, title, reason);
        }

        internal static void Finish(string key, string video, string title, string reason)
        {
            var tell = new List<(ServerClient Client, string WatchUrl)>();
            lock (Gate)
            {
                if (Jobs.TryGetValue(key, out Job job))
                {
                    tell.AddRange(job.Waiting);
                    job.Waiting.Clear();
                    if (reason.Length > 0) Jobs.Remove(key);
                    else { job.Video = video; job.Title = title; job.Finished = DateTime.UtcNow; job.Done = true; }
                }
            }
            foreach ((ServerClient waiter, string theirs) in tell) Reply(waiter, theirs, video, title, reason);
        }

        private static void Sweep()
        {
            DateTime cutoff = DateTime.UtcNow - ResultLife;
            foreach (string key in new List<string>(Jobs.Keys))
                if (Jobs[key].Done && Jobs[key].Finished < cutoff) Jobs.Remove(key);
        }

        // The id, not the whole link: two players pasting the same video with different tracking tails share one fetch.
        internal static string VideoIdOf(string url)
        {
            Uri u;
            try { if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out u) || u == null) return ""; }
            catch { return ""; }
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return "";

            string host = (u.Host ?? "").ToLowerInvariant();
            string path = u.AbsolutePath ?? "";
            if (host == "youtu.be") return Clean(path.TrimStart('/'));
            if (host != "youtube.com" && !host.EndsWith(".youtube.com", StringComparison.Ordinal)) return "";

            foreach (string prefix in new[] { "/shorts/", "/embed/", "/live/", "/v/" })
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Clean(path.Substring(prefix.Length));
            if (!path.Equals("/watch", StringComparison.OrdinalIgnoreCase)) return "";

            foreach (string pair in (u.Query ?? "").TrimStart('?').Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == "v") return Clean(Uri.UnescapeDataString(pair.Substring(eq + 1)));
            }
            return "";
        }

        private static string Clean(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            int cut = id.IndexOfAny(new[] { '/', '?', '&', '#' });
            if (cut >= 0) id = id.Substring(0, cut);
            if (id.Length != 11) return "";
            // ASCII only: a Unicode digit passes IsLetterOrDigit then maps to '_', so two ids would share one cache file.
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                       || c == '-' || c == '_';
                if (!ok) return "";
            }
            return id;
        }

        private static void Reply(ServerClient client, string watchUrl, string video, string title, string reason)
        {
            try
            {
                KmhRouter.SendTo(client, KmhProtocol.Kind.VideoResolved, new
                {
                    watch_url = watchUrl,
                    port      = KmhVideoRelay.Port,
                    video     = video,
                    title     = title ?? "",
                    reason    = reason,
                });
            }
            catch (Exception ex) { ServerLog.Verbose($"Video server: could not answer a waiting player - {ex.Message}"); }
        }
    }
}
