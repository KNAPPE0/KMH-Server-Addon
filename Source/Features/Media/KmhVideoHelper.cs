using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Media
{
    internal static class KmhVideoHelper
    {
        private const int ProbeTimeoutMs = 30_000;
        private const int FetchTimeoutMs = 900_000;
        // A grandchild can hold the pipe open after the helper itself exits, so the reads get their own bound.
        private const int ReadGraceMs = 10_000;
        private const int KillGraceMs = 5_000;
        internal const int MaxUrlLength = 300;

        internal sealed class Facts
        {
            public string Title = "";
            public int    Seconds;
            public string Error = "";
        }

        private static string _tool, _ffmpeg;
        private static bool _searched;

        // Both tools: youtube serves picture and sound apart, and a player cannot open a file with no picture in it.
        public static bool Available => Tool().Length > 0 && Ffmpeg().Length > 0;

        public static string Tool()
        {
            Search();
            return _tool ?? "";
        }

        public static string Ffmpeg()
        {
            Search();
            return _ffmpeg ?? "";
        }

        public static void Forget() { _searched = false; _tool = null; _ffmpeg = null; }

        public static string MissingTools()
        {
            if (Available) return "";
            if (Tool().Length == 0 && Ffmpeg().Length == 0) return "yt-dlp and ffmpeg";
            return Tool().Length == 0 ? "yt-dlp" : "ffmpeg";
        }

        private static void Search()
        {
            if (_searched) return;
            _searched = true;
            _tool   = Find(MediaConfig.Current.Video.HelperPath, nameof(MediaVideoSettings.HelperPath), new[] { "yt-dlp.exe", "yt-dlp" }) ?? "";
            _ffmpeg = Find(MediaConfig.Current.Video.FfmpegPath, nameof(MediaVideoSettings.FfmpegPath), new[] { "ffmpeg.exe", "ffmpeg" }) ?? "";
            if (_tool.Length > 0)   ServerLog.Info($"Video server: using {System.IO.Path.GetFileName(_tool)}");
            if (_ffmpeg.Length > 0) ServerLog.Info($"Video server: using {System.IO.Path.GetFileName(_ffmpeg)}");
        }

        private static string Find(string configured, string setting, string[] names)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(configured)) return configured;
                // Without this a wrong path is unreportable: discovery succeeds and the setting does nothing.
                ServerLog.Warn($"Video server: {Describe(configured)} - ignoring it and searching the usual folders. "
                             + $"Media.json Video.{setting} expects the FULL PATH TO THE FILE, e.g. "
                             + $"\"Video\": {{ \"{setting}\": \"C:\\\\Program Files\\\\Tools\\\\{names[0]}\" }}.");
            }
            foreach (string dir in SearchDirs())
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (string name in names)
                {
                    try
                    {
                        string candidate = System.IO.Path.Combine(dir.Trim(), name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }
            return null;
        }

        // An owner drops the tools beside the exe, in KMH-Data, or unzips them keeping the bin folder they came in.
        internal static string[] SearchDirs()
        {
            var dirs = new System.Collections.Generic.List<string>();
            try { dirs.AddRange(UnderRoot(AppContext.BaseDirectory)); } catch { }
            try { dirs.AddRange(UnderRoot(KmhDataPaths.Folder)); } catch { }
            try { dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)); }
            catch { }
            return dirs.ToArray();
        }

        internal static string[] SearchDirsForTest(string root) => UnderRoot(root);

        // Names which way the path failed: "not found" sends owners hunting for a typo when they wrote a folder.
        internal static string Describe(string configured)
        {
            string p = (configured ?? "").Trim();
            try
            {
                if (Directory.Exists(p))
                    return $"the configured path '{p}' is a FOLDER, not the helper executable";
                string parent = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    return $"the configured path '{p}' does not exist (neither does its folder '{parent}')";
                if (!Path.IsPathRooted(p))
                    return $"the configured path '{p}' is relative and did not resolve from the server's working folder";
                return $"the configured path '{p}' does not exist";
            }
            catch (Exception ex) { return $"the configured path '{p}' could not be read ({ex.Message})"; }
        }

        private static string[] UnderRoot(string root)
        {
            var dirs = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(root)) return dirs.ToArray();
            dirs.Add(root);
            string tools = null;
            try { tools = System.IO.Path.Combine(root, "Tools"); dirs.Add(tools); } catch { }
            try { dirs.Add(System.IO.Path.Combine(root, "bin")); } catch { }
            try
            {
                foreach (string sub in Directory.GetDirectories(root))
                    dirs.Add(System.IO.Path.Combine(sub, "bin"));
            }
            catch { }
            // The release ships a Tools folder, so an ffmpeg archive unzipped into it leaves the binary a level down.
            try
            {
                if (tools != null)
                    foreach (string sub in Directory.GetDirectories(tools))
                    {
                        dirs.Add(sub);
                        dirs.Add(System.IO.Path.Combine(sub, "bin"));
                    }
            }
            catch { }
            return dirs.ToArray();
        }

        // The only link ever handed to the helper, rebuilt from the id rather than carried over from the player.
        internal static string CanonicalWatchUrl(string videoId)
        {
            if (string.IsNullOrEmpty(videoId) || videoId.Length != 11) return "";
            foreach (char c in videoId)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                       || c == '-' || c == '_';
                if (!ok) return "";
            }
            return "https://www.youtube.com/watch?v=" + videoId;
        }

        // Shape only, so the self-test can prove the rules without asking the internet to resolve a name.
        internal static bool SafeWatchUrlShape(string url, out string why)
        {
            why = "";
            string raw = url ?? "";
            if (raw.Length == 0) { why = "no link"; return false; }
            if (raw.Length > MaxUrlLength) { why = "that link is far too long"; return false; }

            // Characters a valid URI cannot hold unencoded. '&' and ';' are left alone: they are ordinary query
            // punctuation, and what the helper is given is rebuilt from the id anyway.
            foreach (char c in raw)
                if (char.IsControl(c) || c == ' ' || c == '"' || c == '\'' || c == '`' || c == '\\'
                    || c == '|' || c == '<' || c == '>' || c == '^' || c == '{' || c == '}')
                { why = "that link contains a character a link cannot hold"; return false; }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri u) || u == null) { why = "that is not a link"; return false; }
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)
            { why = "only http and https links are fetched"; return false; }
            if (!string.IsNullOrEmpty(u.UserInfo)) { why = "a link carrying a sign-in is not fetched"; return false; }
            if (KmhVideoHandler.VideoIdOf(raw).Length == 0) { why = "that is not a video link"; return false; }
            return true;
        }

        // The helper reaches the network on our behalf, so the same address rules the image resolver uses apply.
        // They bind the watch host; where the site then redirects its own player is not ours to police.
        internal static bool SafeWatchUrl(string url, out string why)
        {
            if (!SafeWatchUrlShape(url, out why)) return false;
            Uri u = new Uri(url);
            if (!KmhMediaGuard.ResolveAndCheck(u.Host, out _, out string addrWhy))
            { why = "that link's host is refused: " + addrWhy; return false; }
            return true;
        }

        // Whatever the uploader wrote, drawn under the chat line on every client: capped and made inert.
        internal static string TitleOf(string line) => Util.KmhSafe.Cap((line ?? "").Trim().Replace('<', '(').Replace('>', ')'), 120);

        // Only the two binaries KMH looks for, by name, as a real file; a configured path may not name a shell.
        internal static bool IsSafeToolPath(string path, string expected, out string why)
        {
            why = "";
            string p = path ?? "";
            if (p.Length == 0) { why = $"no {expected} on this server"; return false; }
            foreach (char c in p)
                if (char.IsControl(c)) { why = $"the {expected} path contains a control character"; return false; }

            string full;
            try { full = Path.GetFullPath(p); }
            catch { why = $"the {expected} path cannot be read"; return false; }
            if (!File.Exists(full)) { why = $"{expected} is not a file on this server"; return false; }

            string name = Path.GetFileNameWithoutExtension(full);
            string ext  = Path.GetExtension(full);
            if (!string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)
                || !(ext.Length == 0 || string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)))
            {
                why = $"the configured {expected} path points at '{Path.GetFileName(full)}', not {expected}";
                return false;
            }
            return true;
        }

        // H.264, AAC, no frame-rate cap: at 1080p the site often has only 60fps, and capping it drops you to 720p.
        internal static string Format(int maxHeight)
            => $"bv*[vcodec^=avc1][height<={maxHeight}]+ba[ext=m4a]/b[ext=mp4][height<={maxHeight}]"
             + $"/bv*[height<={maxHeight}]+ba/b";

        // One entry per argument, and the link last behind "--" so it can never be read as an option.
        // --ignore-config keeps a machine-wide yt-dlp config from adding arguments of its own.
        internal static IReadOnlyList<string> ProbeArguments(string watchUrl)
            => new List<string>
            {
                "--ignore-config", "-q", "--no-warnings", "--no-playlist", "--skip-download",
                "--print", "%(duration)s",
                "--print", "%(title)s",
                "--", watchUrl ?? "",
            };

        // faststart puts the index at the front; "res,+fps" takes the tallest rung, at 30fps where the site has one.
        // The ffmpeg hand-off is this fixed pair plus a path KMH found: no part of it comes from a player.
        internal static IReadOnlyList<string> FetchArguments(string watchUrl, int maxHeight, string target,
                                                             string ffmpeg, int maxFileMB)
            => new List<string>
            {
                "--ignore-config", "-q", "--no-warnings", "--no-playlist",
                "--ffmpeg-location", ffmpeg ?? "",
                "--max-filesize", maxFileMB + "M",
                "--postprocessor-args", "ffmpeg:-movflags +faststart",
                "-S", "res,+fps",
                "-f", Format(maxHeight),
                "--merge-output-format", "mp4",
                "--no-mtime",
                "-o", target ?? "",
                "--", watchUrl ?? "",
            };

        public static Facts Probe(string watchUrl)
        {
            var facts = new Facts();
            if (!SafeWatchUrl(watchUrl, out string why)) { facts.Error = why; return facts; }

            string output = Run(ProbeArguments(watchUrl), ProbeTimeoutMs, out string error);
            if (error.Length > 0) { facts.Error = error; return facts; }

            string[] lines = output.Replace("\r", "").Split('\n');
            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out int seconds)) facts.Seconds = seconds;
            if (lines.Length > 1) facts.Title = TitleOf(lines[1]);
            return facts;
        }

        public static string Fetch(string watchUrl, int maxHeight, string target, out string error)
        {
            error = "";
            // The helper writes where it is told, so it is only ever told a path inside this server's own cache.
            if (!KmhVideoRelay.InCache(target)) { error = "the video server would not write that file"; return ""; }
            if (!SafeWatchUrl(watchUrl, out error)) return "";

            string ffmpeg = Ffmpeg();
            if (!IsSafeToolPath(ffmpeg, "ffmpeg", out error)) return "";

            int maxFileMB = MediaConfig.Current.Video.MaxFileMegabytes;
            Run(FetchArguments(watchUrl, maxHeight, target, ffmpeg, maxFileMB), FetchTimeoutMs, out error);
            if (error.Length > 0) return "";
            if (!File.Exists(target)) { error = $"nothing playable under {maxFileMB} MB"; return ""; }
            return target;
        }

        // ArgumentList, never Arguments: each entry reaches the process unparsed, so a link cannot become a second argument.
        internal static ProcessStartInfo BuildStartInfo(string tool, IReadOnlyList<string> arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = tool ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            foreach (string a in arguments ?? Array.Empty<string>()) info.ArgumentList.Add(a ?? "");
            return info;
        }

        private static string Run(IReadOnlyList<string> arguments, int timeoutMs, out string error)
        {
            error = "";
            string tool = Tool();
            if (!IsSafeToolPath(tool, "yt-dlp", out error)) return "";

            Process process = null;
            try
            {
                process = Process.Start(BuildStartInfo(tool, arguments));
                if (process == null) { error = "the video helper would not start"; return ""; }
                try { process.StandardInput.Close(); } catch { }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    KillTree(process);
                    error = "the video helper took too long";
                    return "";
                }
                if (!Task.WaitAll(new Task[] { stdout, stderr }, ReadGraceMs))
                {
                    KillTree(process);
                    error = "the video helper took too long";
                    return "";
                }

                if (process.ExitCode != 0)
                    error = Trim(stderr.Result.Length > 0 ? stderr.Result : "the video helper failed");
                return stdout.Result;
            }
            catch (Exception ex) { error = Trim(ex.GetBaseException().Message); return ""; }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) KillTree(process); } catch { }
                    try { process.Dispose(); } catch { }
                }
            }
        }

        // yt-dlp starts ffmpeg, so killing only the helper leaves the merge running and the part-file growing.
        private static void KillTree(Process p)
        {
            try { p.Kill(entireProcessTree: true); }
            catch { try { p.Kill(); } catch { } }
            try { p.WaitForExit(KillGraceMs); } catch { }
        }

        internal static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text) sb.Append(char.IsControl(c) ? ' ' : c);
            string one = sb.ToString().Trim();
            return one.Length <= 140 ? one : one.Substring(0, 137) + "…";
        }
    }
}
