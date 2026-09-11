using System;
using System.Diagnostics;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Media
{
    internal static class KmhVideoHelper
    {
        private const int ProbeTimeoutMs = 30_000;
        private const int FetchTimeoutMs = 900_000;

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
            _tool   = Find(MediaConfig.Current.VideoHelperPath, new[] { "yt-dlp.exe", "yt-dlp" }) ?? "";
            _ffmpeg = Find(MediaConfig.Current.FfmpegPath,      new[] { "ffmpeg.exe", "ffmpeg" }) ?? "";
            if (_tool.Length > 0)   ServerLog.Info($"Video server: using {System.IO.Path.GetFileName(_tool)}");
            if (_ffmpeg.Length > 0) ServerLog.Info($"Video server: using {System.IO.Path.GetFileName(_ffmpeg)}");
        }

        private static string Find(string configured, string[] names)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
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

        // H.264, AAC, no frame-rate cap: at 1080p the site often has only 60fps, and capping it drops you to 720p.
        internal static string Format(int maxHeight)
            => $"bv*[vcodec^=avc1][height<={maxHeight}]+ba[ext=m4a]/b[ext=mp4][height<={maxHeight}]"
             + $"/bv*[height<={maxHeight}]+ba/b";

        public static Facts Probe(string watchUrl)
        {
            var facts = new Facts();
            string output = Run($"-q --no-warnings --no-playlist --skip-download --print \"%(duration)s\" --print \"%(title)s\" \"{watchUrl}\"",
                                ProbeTimeoutMs, out string error);
            if (error.Length > 0) { facts.Error = error; return facts; }

            string[] lines = output.Replace("\r", "").Split('\n');
            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out int seconds)) facts.Seconds = seconds;
            if (lines.Length > 1) facts.Title = lines[1].Trim();
            return facts;
        }

        // faststart puts the index at the front; "res,+fps" takes the tallest rung, at 30fps where the site has one.
        internal static string FetchArguments(string watchUrl, int maxHeight, string target, string ffmpeg, int maxFileMB)
            => $"-q --no-warnings --no-playlist --ffmpeg-location \"{ffmpeg}\" --max-filesize {maxFileMB}M"
             + " --postprocessor-args \"ffmpeg:-movflags +faststart\" -S \"res,+fps\""
             + $" -f \"{Format(maxHeight)}\" --merge-output-format mp4 --no-mtime -o \"{target}\" \"{watchUrl}\"";

        public static string Fetch(string watchUrl, int maxHeight, string target, out string error)
        {
            int maxFileMB = MediaConfig.Current.VideoMaxFileMegabytes;
            Run(FetchArguments(watchUrl, maxHeight, target, Ffmpeg(), maxFileMB), FetchTimeoutMs, out error);
            if (error.Length > 0) return "";
            if (!File.Exists(target)) { error = $"nothing playable under {maxFileMB} MB"; return ""; }
            return target;
        }

        private static string Run(string arguments, int timeoutMs, out string error)
        {
            error = "";
            string tool = Tool();
            if (tool.Length == 0) { error = "no video helper installed on the server"; return ""; }

            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using Process process = Process.Start(info);
                if (process == null) { error = "the video helper would not start"; return ""; }

                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    error = "the video helper took too long";
                    return "";
                }
                if (process.ExitCode != 0) error = Trim(errors.Length > 0 ? errors : "the video helper failed");
                return output;
            }
            catch (Exception ex) { error = Trim(ex.Message); return ""; }
        }

        internal static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return one.Length <= 140 ? one : one.Substring(0, 137) + "…";
        }
    }
}
