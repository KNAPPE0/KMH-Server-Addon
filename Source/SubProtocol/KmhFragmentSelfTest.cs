using System;
using System.Collections.Generic;
using System.Text;

namespace KMHServerAddon.SubProtocol
{
    internal static class KmhFragmentSelfTest
    {
        private static KmhEnvelope Big(int rows)
        {
            var items = new List<object>(rows);
            for (int i = 0; i < rows; i++)
                items.Add(new { def = "Bone", n = 75, note = "row " + i + " padding to make this realistically wide" });
            return new KmhEnvelope("kmh.treasury.snapshot", new { owner = "smith2b", items });
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            const int frame = 64 * 1024;

            string small = new KmhEnvelope("kmh.ping", new { x = 1 }).Serialize();
            r.Add(("Transport: a small envelope needs no fragmenting",
                   KmhFragments.Split("kmh.ping", small, frame).Count == 1, ""));

            KmhEnvelope big = Big(1500);   // comfortably past one frame, like a large modded vault
            string wire = big.Serialize();
            List<KmhEnvelope> parts = KmhFragments.Split(big.Kind, wire, frame);
            r.Add(("Transport: a snapshot past one frame is split into several",
                   wire.Length > frame && parts != null && parts.Count > 1,
                   $"{wire.Length} bytes -> {parts?.Count ?? 0} fragment(s)"));

            bool allFit = true;
            foreach (KmhEnvelope p in parts)
                if (Encoding.UTF8.GetByteCount(p.Serialize()) > frame) allFit = false;
            r.Add(("Transport: every fragment fits inside one physical frame", allFit, ""));

            var asm = new KmhFragments.Assembler();
            KmhEnvelope done = null;
            foreach (KmhEnvelope p in parts) { KmhEnvelope got = asm.Accept(p, out _); if (got != null) done = got; }
            r.Add(("Transport: fragments reassemble into the original envelope byte for byte",
                   done != null && done.Kind == big.Kind && done.Serialize() == wire, ""));

            var shuffled = new KmhFragments.Assembler();
            KmhEnvelope outOfOrder = null;
            for (int i = parts.Count - 1; i >= 0; i--)
            { KmhEnvelope got = shuffled.Accept(parts[i], out _); if (got != null) outOfOrder = got; }
            r.Add(("Transport: out-of-order fragments still reassemble correctly",
                   outOfOrder != null && outOfOrder.Serialize() == wire, ""));

            var dupes = new KmhFragments.Assembler();
            KmhEnvelope early = null;
            for (int i = 0; i < parts.Count - 1; i++) { dupes.Accept(parts[i], out _); dupes.Accept(parts[i], out _); }
            early = dupes.Accept(parts[0], out _);
            r.Add(("Transport: duplicate fragments never complete a transfer early", early == null, ""));
            KmhEnvelope finished = dupes.Accept(parts[parts.Count - 1], out _);
            r.Add(("Transport: the transfer still completes once the missing fragment arrives",
                   finished != null && finished.Serialize() == wire, ""));

            var dropped = new KmhFragments.Assembler();
            KmhEnvelope any = null;
            for (int i = 1; i < parts.Count; i++) { KmhEnvelope got = dropped.Accept(parts[i], out _); if (got != null) any = got; }
            r.Add(("Transport: a dropped fragment leaves nothing to apply", any == null, ""));

            var tampered = new KmhFragments.Assembler();
            KmhEnvelope bad = null;
            for (int i = 0; i < parts.Count; i++)
            {
                KmhEnvelope p = parts[i];
                if (i == 0)
                    p = new KmhEnvelope(KmhFragments.Kind, new
                    {
                        tid = p.GetString("tid", ""), kind = p.GetString("kind", ""), i = p.GetInt("i", 0),
                        n = p.GetInt("n", 0), len = p.GetInt("len", 0), hash = p.GetString("hash", ""),
                        data = Convert.ToBase64String(Encoding.UTF8.GetBytes("not the original bytes at all")),
                    });
                KmhEnvelope got = tampered.Accept(p, out _);
                if (got != null) bad = got;
            }
            r.Add(("Transport: a corrupted transfer is rejected by its hash rather than parsed",
                   bad == null, "a tampered chunk must never reach a feature handler"));

            var hostile = new KmhFragments.Assembler();
            string why1, why2, why3;
            hostile.Accept(new KmhEnvelope(KmhFragments.Kind, new
            { tid = "t", kind = "k", i = 0, n = 999999, len = 10, hash = "x", data = "" }), out why1);
            hostile.Accept(new KmhEnvelope(KmhFragments.Kind, new
            { tid = "t", kind = "k", i = 5, n = 2, len = 10, hash = "x", data = "" }), out why2);
            hostile.Accept(new KmhEnvelope(KmhFragments.Kind, new
            { tid = "t", kind = "k", i = 0, n = 2, len = int.MaxValue, hash = "x", data = "" }), out why3);
            r.Add(("Transport: absurd chunk counts, indexes and sizes are refused",
                   why1 != null && why2 != null && why3 != null, $"{why1} / {why2} / {why3}"));

            var flood = new KmhFragments.Assembler();
            string floodWhy = null;
            for (int i = 0; i < KmhFragments.MaxConcurrentPerPeer + 2; i++)
                flood.Accept(new KmhEnvelope(KmhFragments.Kind, new
                {
                    tid = "transfer" + i, kind = "k", i = 0, n = 4, len = 64, hash = "x",
                    data = Convert.ToBase64String(new byte[8]),
                }), out floodWhy);
            r.Add(("Transport: a peer cannot hold more transfers open than the limit allows",
                   floodWhy != null, floodWhy ?? "the concurrency cap did not refuse anything"));

            return r;
        }
    }
}
