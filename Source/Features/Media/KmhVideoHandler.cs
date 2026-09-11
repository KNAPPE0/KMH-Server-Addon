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
            public readonly List<ServerClient> Waiting = new List<ServerClient>();
            public string Video = "", Title = "";
            public DateTime Started = DateTime.UtcNow, Finished;
            public bool Done;

            // What it takes to run this fetch, carried so a queued job can start without the request that made it.
            public string Key = "", User = "", WatchUrl = "", VideoId = "";
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

            if (!cfg.WatchPagePlayback || !cfg.VideoServerEnabled) { Reply(client, watchUrl, "", "", "video links are off on this server"); return; }
            if (!KmhVideoRelay.Running) { Reply(client, watchUrl, "", "", "this server is not serving video right now"); return; }
            if (videoId.Length == 0) { Reply(client, watchUrl, "", "", "that is not a video link"); return; }

            int height = Math.Clamp(env?.GetInt("height", 0) ?? 0, 0, cfg.WatchMaxHeight);
            if (height <= 0) height = cfg.WatchMaxHeight;

            // A room full of players clicking the same link fetches once: the rest join the job or take its answer.
            string key = videoId + "|" + height;
            Job answered = null;
            bool joined = false, full = false;
            var abandoned = new List<ServerClient>();
            lock (Gate)
            {
                Sweep();
                if (Jobs.TryGetValue(key, out Job existing))
                {
                    if (existing.Done) answered = existing;
                    else if (DateTime.UtcNow - existing.Started < JobLife) { existing.Waiting.Add(client); joined = true; }
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
                    // A queued job is never Done, so the sweep never reaches it - cap the backlog, not just concurrency.
                    if (Waiting.Count >= MaxWaitingJobs) full = true;
                    else
                    {
                        var job = new Job
                        {
                            Key = key, User = username, WatchUrl = watchUrl, VideoId = videoId,
                            Height = height, MaxSeconds = cfg.WatchMaxSeconds,
                        };
                        job.Waiting.Add(client);
                        Jobs[key] = job;
                        Waiting.Enqueue(job);
                    }
                }
            }
            foreach (ServerClient stranded in abandoned)
                Reply(stranded, watchUrl, "", "", "that fetch gave up - try again");

            if (answered != null) { Reply(client, watchUrl, answered.Video, answered.Title, ""); return; }
            if (full) { Reply(client, watchUrl, "", "", "too many videos are queued right now - try again shortly"); return; }
            if (joined) return;

            Pump();
        }

        private static void Pump()
        {
            int allowed = Math.Max(1, MediaConfig.Current.VideoMaxConcurrentFetches);
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
                    try { Work(job.Key, job.User, job.WatchUrl, job.VideoId, job.Height, job.MaxSeconds); }
                    finally
                    {
                        lock (Gate) _running--;
                        Pump();
                    }
                });
            }
        }

        private static void Work(string key, string username, string watchUrl, string videoId, int height, int maxSeconds)
        {
            string token = "", title = "", reason = "";
            try
            {
                string file = KmhVideoCache.PathFor(videoId, height);
                title = KmhVideoCache.TitleOf(file);
                if (!KmhVideoCache.Has(file))
                {
                    KmhVideoHelper.Facts facts = KmhVideoHelper.Probe(watchUrl);
                    title = facts.Title;
                    if (facts.Error.Length > 0) reason = facts.Error;
                    // A live stream has no duration, and fetching one runs until the timeout and fills the cache.
                    else if (facts.Seconds <= 0) reason = "that looks like a live stream - it has no length to fetch";
                    else if (maxSeconds > 0 && facts.Seconds > maxSeconds)
                        reason = $"too long to play here - {facts.Seconds / 60} min, and this server's limit is {maxSeconds / 60} min";
                    else
                    {
                        KmhVideoCache.EnsureFolder();
                        ServerLog.Info($"Video server: fetching {watchUrl} at {height}p for {username}.");
                        file = KmhVideoHelper.Fetch(watchUrl, height, file, out string error);
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
                    else ServerLog.Info($"Video server: serving {watchUrl} to {username}.");
                }
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Video server: fetch threw - {ex.Message}");
                reason = "the video server hit an error";
            }
            Finish(key, watchUrl, token, title, reason);
        }

        private static void Finish(string key, string watchUrl, string video, string title, string reason)
        {
            var tell = new List<ServerClient>();
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
            foreach (ServerClient waiter in tell) Reply(waiter, watchUrl, video, title, reason);
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
            foreach (char c in id)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return "";
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
