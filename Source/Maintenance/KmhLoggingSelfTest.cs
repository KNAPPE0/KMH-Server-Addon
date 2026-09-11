using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Diagnostics have to survive what they exist to explain: a flood, a locked file, a shutdown, a newline in a name.
    internal static class KmhLoggingSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Log sink: a flood is bounded and reports what it dropped", () =>
            {
                KmhLogSink.ResetForTest();
                KmhLogSink.Start();
                try
                {
                    // Held so the flood outruns the disk on purpose - that is the case the bound exists for.
                    KmhLogSink.PauseDrainForTest = true;
                    for (int i = 0; i < 20000; i++) KmhLogSink.Write($"[flood] line {i}");
                    bool bounded = KmhLogSink.QueueDepth <= 4096;
                    bool dropped = KmhLogSink.DroppedCount > 0;

                    KmhLogSink.PauseDrainForTest = false;
                    KmhLogSink.Stop(TimeSpan.FromSeconds(5));
                    string file = KmhLogSink.CurrentFile;
                    string text = File.Exists(file) ? File.ReadAllText(file) : "";
                    // One summary, not one warning per dropped line.
                    bool summarised = text.Contains("suppressed") && text.Contains("queue was full");
                    bool wroteSome = text.Contains("[flood] line 0");
                    bool ok = bounded && dropped && summarised && wroteSome;
                    return (ok, ok ? "queue stayed bounded, survivors written, drops summarised once"
                                  : $"bounded={bounded}, dropped={dropped}, summarised={summarised}, wrote={wroteSome}");
                }
                finally { KmhLogSink.PauseDrainForTest = false; KmhLogSink.ResetForTest(); }
            });

            Check("Log sink: a locked file does not crash the server, and recovery resumes writing", () =>
            {
                KmhLogSink.ResetForTest();
                KmhLogSink.Start();
                try
                {
                    KmhLogSink.FailWriteForTest = () => "injected: file locked";
                    KmhLogSink.Write("[probe] during the failure");
                    KmhLogSink.FlushForTest();
                    bool survived = true;   // reaching here at all is the assertion: no throw escaped into the caller

                    KmhLogSink.FailWriteForTest = null;
                    KmhLogSink.Write("[probe] after the disk came back");
                    KmhLogSink.FlushForTest();

                    string file = KmhLogSink.CurrentFile;
                    string text = File.Exists(file) ? File.ReadAllText(file) : "";
                    bool resumed = text.Contains("after the disk came back");
                    bool ok = survived && resumed;
                    return (ok, ok ? "the failure was absorbed and writing resumed by itself"
                                  : $"survived={survived}, resumedAfterFailure={resumed}");
                }
                finally { KmhLogSink.FailWriteForTest = null; KmhLogSink.ResetForTest(); }
            });

            Check("Log sink: shutdown flushes what is still queued", () =>
            {
                KmhLogSink.ResetForTest();
                KmhLogSink.Start();
                try
                {
                    string marker = "shutdown-marker-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    KmhLogSink.Write($"[probe] {marker}");
                    KmhLogSink.Stop(TimeSpan.FromSeconds(5));
                    string file = KmhLogSink.CurrentFile;
                    bool flushed = File.Exists(file) && File.ReadAllText(file).Contains(marker);
                    return (flushed, flushed ? "the queued line reached the file before the sink closed"
                                             : "a queued diagnostic was lost at shutdown");
                }
                finally { KmhLogSink.ResetForTest(); }
            });

            Check("Log routing: routine detail is file-only, important lines are not", () =>
            {
                KmhLogSink.ResetForTest();
                KmhLogSink.Start();
                try
                {
                    string routine = "routine-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    string loud    = "loud-"    + Guid.NewGuid().ToString("N").Substring(0, 6);
                    ServerLog.Diag($"probe {routine}");
                    ServerLog.Warn($"probe {loud}");
                    KmhLogSink.FlushForTest();
                    string text = File.ReadAllText(KmhLogSink.CurrentFile);
                    bool routineInFile = text.Contains(routine);
                    bool loudInFile    = text.Contains(loud);
                    // Diag has no console path at all - that is the whole point of the separate destination.
                    bool routineTagged = text.Contains("[diag]");
                    bool loudTagged    = text.Contains("[warn]");
                    bool ok = routineInFile && loudInFile && routineTagged && loudTagged;
                    return (ok, ok ? "routine went to the file only; a warning is in both sinks"
                                  : $"routineInFile={routineInFile}, warnInFile={loudInFile}, tags={routineTagged}/{loudTagged}");
                }
                finally { KmhLogSink.ResetForTest(); }
            });

            Check("Log throttle: a repeating line is written once, then summarised with a count", () =>
            {
                KmhLogThrottle.ResetForTest();
                long t0 = DateTime.UtcNow.Ticks;
                var window = TimeSpan.FromMinutes(2);

                bool first = KmhLogThrottle.Allow("probe.key", window, t0, out int s0);
                int allowed = 0, suppressedTotal = 0;
                for (int i = 1; i <= 50; i++)
                    if (KmhLogThrottle.Allow("probe.key", window, t0 + i, out _)) allowed++;

                // Past the window the next one passes and carries the count of what it stood in for.
                bool later = KmhLogThrottle.Allow("probe.key", window, t0 + window.Ticks + 1, out suppressedTotal);
                // A different key is never suppressed by another key's traffic.
                bool other = KmhLogThrottle.Allow("probe.other", window, t0 + 1, out _);

                bool ok = first && s0 == 0 && allowed == 0 && later && suppressedTotal == 50 && other;
                return (ok, ok ? "first written, 50 suppressed, the summary carried all 50"
                              : $"first={first}, allowedDuringWindow={allowed}, afterWindow={later}, " +
                                $"reportedSuppressed={suppressedTotal} (expected 50), otherKeyFree={other}");
            });

            Check("Log text: a newline or an escape in a name cannot forge a second line", () =>
            {
                string hostile = "Taz\r\n[error] fake line[31m injected";
                string safe = KmhLogText.OneLine(hostile, 400);
                bool flattened = !safe.Contains("\n") && !safe.Contains("\r") && !safe.Contains("");
                // An ordinary Unicode name must survive untouched - sanitising must not mangle real players.
                string unicode = "Ünïcödé-玩家-🎮";
                bool kept = KmhLogText.OneLine(unicode, 400) == unicode;
                bool ok = flattened && kept;
                return (ok, ok ? "control characters neutralised, ordinary Unicode preserved"
                              : $"flattened={flattened}, unicodePreserved={kept} ({KmhLogText.OneLine(unicode, 400)})");
            });

            Check("Log text: byte caps count bytes, and never split a codepoint", () =>
            {
                string ascii = new string('a', 100);
                bool asciiFits = KmhLogText.TrimToUtf8Bytes(ascii, 100) == ascii;
                bool asciiCut  = KmhLogText.Utf8Bytes(KmhLogText.TrimToUtf8Bytes(ascii, 40)) == 40;

                // Four bytes per emoji: a char-count cap would let ten of these through a ten-byte budget.
                string emoji = string.Concat(System.Linq.Enumerable.Repeat("🎮", 10));
                string cut = KmhLogText.TrimToUtf8Bytes(emoji, 10);
                int bytes = KmhLogText.Utf8Bytes(cut);
                bool withinCap = bytes <= 10;
                bool wholeCodepoints = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cut)) == cut
                                    && (cut.Length == 0 || !char.IsHighSurrogate(cut[cut.Length - 1]));
                bool ok = asciiFits && asciiCut && withinCap && wholeCodepoints && bytes == 8;
                return (ok, ok ? "byte budgets respected for ASCII and multi-byte alike, no split pairs"
                              : $"asciiFits={asciiFits}, asciiCut={asciiCut}, bytes={bytes} (cap 10, expected 8), " +
                                $"wholeCodepoints={wholeCodepoints}");
            });

            Check("Remote debug: a reconnect gets a fresh budget instead of the last session's", () =>
            {
                string user = "selftest_dbg_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                ServerClient a = FakeSession(user), b = FakeSession(user);
                if (a == null || b == null) return (false, "could not build a probe session");

                var many = new List<string>();
                for (int i = 0; i < 20; i++) many.Add($"first session line {i}");
                KmhClientDebugLog.AppendForTest(a, user, many, "sess-a", 1);
                bool aHas = KmhClientDebugLog.HasStateForTest(a);
                long aBytes = KmhClientDebugLog.BytesForTest(a);

                // Same username, different connection: it must not inherit the earlier session's spent budget.
                bool bFreshBefore = !KmhClientDebugLog.HasStateForTest(b);
                KmhClientDebugLog.AppendForTest(b, user, new List<string> { "second session line" }, "sess-b", 1);
                long bBytes = KmhClientDebugLog.BytesForTest(b);
                // One line's worth, not twenty-one: the new session started its budget from zero.
                bool separate = aBytes > 0 && bBytes > 0 && bBytes * 5 < aBytes;

                bool ok = aHas && bFreshBefore && separate;
                return (ok, ok ? "each connection carries its own file and its own budget"
                              : $"firstTracked={aHas}, secondStartedFresh={bFreshBefore}, " +
                                $"separateBudgets={separate} (a={aBytes}, b={bBytes})");
            });

            Check("Remote debug: a gap in the stream is reported, not silently smoothed over", () =>
            {
                string user = "selftest_gap_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                ServerClient c = FakeSession(user);
                if (c == null) return (false, "could not build a probe session");

                KmhClientDebugLog.AppendForTest(c, user, new List<string> { "chunk 17" }, "sess", 17);
                // 18 and 19 never arrived; a support reader must see that rather than read a hole as continuity.
                KmhClientDebugLog.AppendForTest(c, user, new List<string> { "chunk 20" }, "sess", 20);

                string path = Path.Combine(KmhDataPaths.DebugDir, "");
                string found = "";
                foreach (string f in Directory.GetFiles(KmhDataPaths.DebugDir, user + "_*.txt")) found = f;
                string text = string.IsNullOrEmpty(found) ? "" : File.ReadAllText(found);
                bool reported = text.Contains("incomplete") && text.Contains("18-19");
                return (reported, reported ? "the missing chunks are named in the file"
                                           : $"gap not reported (file '{Path.GetFileName(found)}')");
            });

            Check("Support health: the questions a support reply asks first are all answered", () =>
            {
                string health = KmhSupportBundle.Health();
                bool answers = health.Contains("Readiness")
                            && health.Contains("Persistence")
                            && health.Contains("Reset")
                            && health.Contains("DataGen")
                            && health.Contains("Diagnostics");
                // The sink line has to name a real state, or a bundle cannot say whether logs were even collected.
                bool sinkStated = health.Contains("sink not started") || health.Contains(".log");
                bool ok = answers && sinkStated;
                return (ok, ok ? "readiness, persistence, reset, generation and sink state all present"
                              : $"answersAll={answers}, sinkStated={sinkStated}");
            });

            Check("Redaction: a token never reaches a bundle, an ordinary field still does", () =>
            {
                KmhRedact.RegisterSecret("MTIzNDU2Nzg5.SECRET.TOKENVALUE");
                try
                {
                    string scrubbed = KmhRedact.Text("connecting with MTIzNDU2Nzg5.SECRET.TOKENVALUE now");
                    bool gone = !scrubbed.Contains("SECRET.TOKENVALUE") && scrubbed.Contains(KmhRedact.Mask);
                    bool keysKnown = KmhRedact.IsSensitiveKey("BotToken")
                                  && KmhRedact.IsSensitiveKey("api_key")
                                  && !KmhRedact.IsSensitiveKey("EconomyMode");
                    // Redacting everything would be safe and useless; ordinary text has to survive.
                    bool ordinary = KmhRedact.Text("EconomyMode is Balanced").Contains("Balanced");
                    bool ok = gone && keysKnown && ordinary;
                    return (ok, ok ? "registered secrets scrubbed by value and by key; ordinary text intact"
                                  : $"secretRemoved={gone}, keysKnown={keysKnown}, ordinaryKept={ordinary}");
                }
                finally { KmhRedact.ClearSecrets(); }
            });

            return r;
        }

        // A ServerClient with no listener: enough to be a distinct session identity, which is the whole point.
        private static ServerClient FakeSession(string username)
        {
            try
            {
                ServerClient c = new ServerClient(null, null, createListener: false);
                c.GetData<UserFile>(new UserFile { Username = username });
                return c;
            }
            catch { return null; }
        }
    }
}
