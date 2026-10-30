using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Frontier.Dto;

namespace KMHServerAddon.Features.Frontier
{
    // Pure-helper tests stay green with the at-most-once guards removed, so the stateful entry points are driven here.
    internal static class KmhFrontierStateSelfTest
    {
        // Far above anything a real run produces, so a leftover record cannot make a check pass by accident.
        private static long _nextOp = 900000;
        private static long Op() => System.Threading.Interlocked.Increment(ref _nextOp);

        private const string Claimable = World.Dto.ServerQuestDto.ConsequenceOutpostClaimable;

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            void Restart() { KmhWorldDirector.LoadFromDisk(); }

            Check("Frontier: a director file with null lists loads as empty lists, not a throw on every tick", () =>
            {
                DirectorState st = KmhWorldDirector.Normalize(new DirectorState
                {
                    TargetCooldowns = null, ActiveOperationIds = null, OutpostTiles = null, Resolutions = null,
                });
                DirectorState copy = st.CopyForRead();
                bool ok = copy.TargetCooldowns.Count == 0 && copy.ActiveOperationIds.Count == 0
                       && copy.OutpostTiles.Count == 0 && copy.Resolutions.Count == 0;
                return (ok, ok ? "every list empty" : "a list stayed null");
            });

            Check("Frontier: an operation cannot begin resolving twice", () =>
            {
                KmhWorldDirector.ClearForNewSeason();
                long id = Op();
                bool first  =  KmhWorldDirector.TryBeginResolution(id, Claimable, 11, out string w1);
                bool second = !KmhWorldDirector.TryBeginResolution(id, Claimable, 11, out string w2);
                // A different tile is still the same operation - the id is the key, not the target.
                bool third  = !KmhWorldDirector.TryBeginResolution(id, Claimable, 22, out string w3);
                KmhWorldDirector.ClearForNewSeason();
                bool ok = first && second && third;
                return (ok, ok ? "the journal refuses a repeat begin: " + w2
                              : $"first={first}({w1}), second={second}({w2}), otherTile={third}({w3}) - "
                                + "A CONSEQUENCE CAN BE APPLIED TWICE");
            });

            Check("Frontier: a resolved operation cannot begin again after a restart", () =>
            {
                KmhWorldDirector.ClearForNewSeason();
                long id = Op();
                if (!KmhWorldDirector.TryBeginResolution(id, Claimable, 12, out string w)) return (false, "begin: " + w);
                KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseConsequenceApplied);
                KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseResolved);

                Restart();
                bool refused = !KmhWorldDirector.TryBeginResolution(id, Claimable, 12, out string why);
                bool noResume = true;
                foreach (ResolutionRecord rec in KmhWorldDirector.UnfinishedResolutions())
                    if (rec.OperationId == id) noResume = false;

                KmhWorldDirector.ClearForNewSeason();
                bool ok = refused && noResume;
                return (ok, ok ? "a terminal record still refuses a repeat: " + why
                              : $"refusedAfterRestart={refused} ({why}), notQueuedForResume={noResume}");
            });

            Check("Frontier: a crash at every transition leaves exactly one record to resume", () =>
            {
                var report = new List<string>();
                bool ok = true;
                foreach (string stopAt in new[] { FrontierResolution.PhaseResolving,
                                                  FrontierResolution.PhaseConsequenceApplied,
                                                  FrontierResolution.PhaseResolved })
                {
                    KmhWorldDirector.ClearForNewSeason();
                    long id = Op();
                    if (!KmhWorldDirector.TryBeginResolution(id, Claimable, 13, out string bw))
                    { report.Add($"{stopAt}: begin failed ({bw})"); ok = false; continue; }
                    if (stopAt != FrontierResolution.PhaseResolving)
                        KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseConsequenceApplied);
                    if (stopAt == FrontierResolution.PhaseResolved)
                        KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseResolved);

                    Restart();
                    int queued = 0;
                    foreach (ResolutionRecord rec in KmhWorldDirector.UnfinishedResolutions())
                        if (rec.OperationId == id) queued++;

                    int want = stopAt == FrontierResolution.PhaseResolved ? 0 : 1;
                    if (queued != want) { ok = false; report.Add($"crash at {stopAt}: {queued} queued, expected {want}"); }
                    else report.Add($"{stopAt}->{queued}");
                }
                KmhWorldDirector.ClearForNewSeason();
                return (ok, ok ? "one resume for an unfinished phase, none for a finished one (" + string.Join(", ", report) + ")"
                              : string.Join("; ", report));
            });

            Check("Frontier: a phase never moves backwards or skips the consequence", () =>
            {
                KmhWorldDirector.ClearForNewSeason();
                long id = Op();
                if (!KmhWorldDirector.TryBeginResolution(id, Claimable, 14, out string w)) return (false, "begin: " + w);

                bool skip = !KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseResolved);
                bool step =  KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseConsequenceApplied);
                bool back = !KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseResolving);
                bool same = !KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseConsequenceApplied);
                bool fin  =  KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseResolved);
                bool past = !KmhWorldDirector.TryAdvanceResolution(id, FrontierResolution.PhaseResolved);

                KmhWorldDirector.ClearForNewSeason();
                bool ok = skip && step && back && same && fin && past;
                return (ok, ok ? "resolving -> consequence_applied -> resolved, and nothing else"
                              : $"skip={skip}, step={step}, back={back}, same={same}, finish={fin}, past={past}");
            });

            Check("Frontier: an unknown operation cannot be advanced into existence", () =>
            {
                KmhWorldDirector.ClearForNewSeason();
                bool refused = !KmhWorldDirector.TryAdvanceResolution(Op(), FrontierResolution.PhaseConsequenceApplied);
                bool noId    = !KmhWorldDirector.TryBeginResolution(0, Claimable, 15, out string w1);
                bool negId   = !KmhWorldDirector.TryBeginResolution(-5, Claimable, 15, out string w2);
                bool badCons = !KmhWorldDirector.TryBeginResolution(Op(), "not_a_consequence", 15, out string w3);
                KmhWorldDirector.ClearForNewSeason();
                bool ok = refused && noId && negId && badCons;
                return (ok, ok ? "advance needs a record; begin needs a real id and a dispatchable consequence"
                              : $"advanceUnknown={refused}, zeroId={noId}({w1}), negId={negId}({w2}), badConsequence={badCons}({w3})");
            });

            Check("Frontier: an answered placement question cannot be answered again after a restart", () =>
            {
                KmhWorldDirector.ClearForNewSeason();
                PlacementToken token = KmhWorldDirector.OpenPlacement("test_template", new[] { 99 });
                if (token == null) return (false, "could not open a placement question");

                bool secondQuestion = KmhWorldDirector.OpenPlacement("test_template", null) == null;

                KmhWorldDirector.TryResolvePlacement(out int _, out string _);

                Restart();
                DirectorState st = KmhWorldDirector.StateForReport();
                bool consumed = st.PendingPlacement != null && st.PendingPlacement.Consumed;
                // A consumed token is what lets the director ask a NEW question; an unconsumed one re-resolves.
                bool reResolved = KmhWorldDirector.TryResolvePlacement(out int _, out string why2);

                KmhWorldDirector.ClearForNewSeason();
                bool ok = secondQuestion && consumed && !reResolved;
                return (ok, ok ? "the token is consumed on disk, so a restart cannot place a second outpost from it"
                              : $"oneQuestionAtATime={secondQuestion}, consumedAfterRestart={consumed}, "
                                + $"resolvedAgain={reResolved}({why2}) - A RESTART CAN PLACE A SECOND OUTPOST");
            });

            Check("Frontier: a player may answer a placement question only once", () =>
            {
                KmhWorldDirector.ClearForNewSeason();
                PlacementToken token = KmhWorldDirector.OpenPlacement("test_template", new[] { 7 });
                if (token == null) return (false, "could not open a placement question");

                bool first  =  KmhWorldDirector.TryRecordProposal("alice", token.Id, 41, 0, "world-fp", out string w1);
                bool repeat = !KmhWorldDirector.TryRecordProposal("alice", token.Id, 42, 0, "world-fp", out string w2);
                bool other  =  KmhWorldDirector.TryRecordProposal("bob",   token.Id, 41, 0, "world-fp", out string w3);
                bool wrongToken = !KmhWorldDirector.TryRecordProposal("carol", "not-the-token", 41, 0, "world-fp", out string w4);
                bool excluded   = !KmhWorldDirector.TryRecordProposal("dave", token.Id, 7, 0, "world-fp", out string w5);

                KmhWorldDirector.ClearForNewSeason();
                bool ok = first && repeat && other && wrongToken && excluded;
                return (ok, ok ? "one vote per player; another token and an excluded tile are refused"
                              : $"first={first}({w1}), repeat={repeat}({w2}), other={other}({w3}), "
                                + $"wrongToken={wrongToken}({w4}), excluded={excluded}({w5})");
            });

            Check("Frontier: noting the same outpost or operation twice does not double-count it", () =>
            {
                KmhWorldDirector.ClearForNewSeason();
                long id = Op();
                KmhWorldDirector.NoteOutpost(31, true);
                KmhWorldDirector.NoteOutpost(31, true);
                KmhWorldDirector.NoteOperation(id, true);
                KmhWorldDirector.NoteOperation(id, true);
                int outposts = KmhWorldDirector.OutpostCount, ops = KmhWorldDirector.ActiveOperationCount;

                KmhWorldDirector.NoteOutpost(31, false);
                KmhWorldDirector.NoteOutpost(31, false);
                KmhWorldDirector.NoteOperation(id, false);
                KmhWorldDirector.NoteOperation(id, false);
                int after = KmhWorldDirector.OutpostCount + KmhWorldDirector.ActiveOperationCount;

                KmhWorldDirector.ClearForNewSeason();
                bool ok = outposts == 1 && ops == 1 && after == 0;
                return (ok, ok ? "add and remove are both idempotent"
                              : $"outposts={outposts} (expected 1), operations={ops} (expected 1), afterRemoval={after}");
            });

            Check("Frontier: a season wipe clears the journal and the pending question together", () =>
            {
                KmhWorldDirector.ClearForNewSeason();
                long id = Op();
                KmhWorldDirector.TryBeginResolution(id, Claimable, 16, out _);
                KmhWorldDirector.OpenPlacement("test_template", null);
                KmhWorldDirector.NoteOutpost(32, true);

                KmhWorldDirector.ClearForNewSeason();
                Restart();
                DirectorState st = KmhWorldDirector.StateForReport();
                bool cleared = st.Resolutions.Count == 0 && st.PendingPlacement == null
                            && st.OutpostTiles.Count == 0 && st.ActiveOperationIds.Count == 0;
                // The id is free again only because the wipe destroyed the world that record described.
                bool reusable = KmhWorldDirector.TryBeginResolution(id, Claimable, 16, out string why);

                KmhWorldDirector.ClearForNewSeason();
                bool ok = cleared && reusable;
                return (ok, ok ? "the wipe leaves no record, no question and no slots behind"
                              : $"clearedOnDisk={cleared}, idReusable={reusable}({why})");
            });

            Check("Frontier: reconciliation trusts Sites and World, not its own lists", () =>
            {
                // The director is a cache: after a crash between saves the stores correct it, never the reverse.
                var st = new DirectorState();
                st.OutpostTiles.Add(1); st.OutpostTiles.Add(2);
                st.ActiveOperationIds.Add(10);

                bool changed = KmhWorldDirector.ReconcileLists(st, new List<int> { 2, 3 }, new List<long> { 11 },
                                                               out int tiles, out int ops);
                bool corrected = st.OutpostTiles.Count == 2 && st.OutpostTiles.Contains(2) && st.OutpostTiles.Contains(3)
                              && st.ActiveOperationIds.Count == 1 && st.ActiveOperationIds.Contains(11);

                bool noopWhenAgreed = !KmhWorldDirector.ReconcileLists(st, new List<int> { 2, 3 },
                                                                      new List<long> { 11 }, out _, out _);
                bool ok = changed && corrected && noopWhenAgreed && tiles == 2 && ops == 2;
                return (ok, ok ? $"adopted the authoritative lists ({tiles} tile diff(s), {ops} operation diff(s)), then held steady"
                              : $"changed={changed}, corrected={corrected}, idempotent={noopWhenAgreed}, tiles={tiles}, ops={ops}");
            });

            return r;
        }
    }
}
