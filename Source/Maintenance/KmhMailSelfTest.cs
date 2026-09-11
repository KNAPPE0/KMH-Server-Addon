using System.Collections.Generic;
using KMHServerAddon.Features.Mail;
using KMHServerAddon.Features.Mail.Dto;

namespace KMHServerAddon.Maintenance
{
    // Escrow must resolve exactly once, so claim-once and refund-once are the checks that matter here.
    internal static class KmhMailSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            bool self = !MailStore.TryPrepare("bob", "BOB", "hi", "yo", out _, out _, out string sr);
            r.Add(("Mail: self-send rejected (case-insensitive)", self, sr ?? ""));

            bool empty = !MailStore.TryPrepare("bob", "amy", "", "   ", out _, out _, out string er);
            r.Add(("Mail: empty subject+body rejected", empty, er ?? ""));

            string longBody = new string('x', MailStore.MaxBodyLength + 50);
            MailStore.TryPrepare("bob", "amy", "", longBody, out _, out string clamped, out _);
            bool clampOk = clamped.Length == MailStore.MaxBodyLength;
            r.Add(("Mail: overlong body clamped", clampOk, $"{clamped.Length}/{MailStore.MaxBodyLength}"));

            MailStore.TryPrepare("bob", "amy", "ab\ttail", "line1\nline2\0", out string subj, out string body, out _);
            bool sanitized = subj == "ab\ttail" && body == "line1\nline2";
            r.Add(("Mail: control chars stripped, \\n\\t kept", sanitized, $"subj='{subj}' body='{body}'"));

            bool valid = MailStore.TryPrepare("bob", "amy", "hello", "world", out _, out _, out string vr) && vr == null;
            r.Add(("Mail: valid message passes", valid, valid ? "ok" : vr));

            bool noRecip = !MailStore.TryPrepare("bob", "", "hi", "yo", out _, out _, out _);
            r.Add(("Mail: empty recipient rejected", noRecip, ""));

            var claim = new MailMessage { AttachedSilver = 500, AttachState = MailStore.AttachEscrowed };
            bool c1 = MailStore.TryResolveAttachment(claim, MailStore.AttachClaimed);
            bool c2 = MailStore.TryResolveAttachment(claim, MailStore.AttachClaimed);
            r.Add(("Mail: claim-once (silver, second rejected)",
                c1 && !c2 && claim.AttachedSilver == 500 && claim.AttachState == MailStore.AttachClaimed, $"c1={c1} c2={c2}"));

            bool noRefundAfterClaim = !MailStore.TryResolveAttachment(claim, MailStore.AttachRefunded);
            r.Add(("Mail: claimed can't be refunded", noRefundAfterClaim, ""));

            var refund = new MailMessage { AttachedSilver = 250, AttachState = MailStore.AttachEscrowed };
            bool f1 = MailStore.TryResolveAttachment(refund, MailStore.AttachRefunded);
            bool f2 = MailStore.TryResolveAttachment(refund, MailStore.AttachRefunded);
            r.Add(("Mail: refund-once (silver, second rejected)",
                f1 && !f2 && refund.AttachState == MailStore.AttachRefunded, $"f1={f1} f2={f2}"));

            // Items-only attachment: still an open attachment, resolves once (claim-once applies to items too).
            var itemMsg = new MailMessage { AttachedItems = new Dictionary<string, int> { { "Steel", 40 } }, AttachState = MailStore.AttachEscrowed };
            bool hasItem = MailStore.HasAttachment(itemMsg);
            bool i1 = MailStore.TryResolveAttachment(itemMsg, MailStore.AttachClaimed);
            bool i2 = MailStore.TryResolveAttachment(itemMsg, MailStore.AttachClaimed);
            r.Add(("Mail: items-only attachment claim-once", hasItem && i1 && !i2, $"has={hasItem} i1={i1} i2={i2}"));

            // Gear-only (full-state payload) attachment: same open-attachment + claim-once guarantee.
            var gearMsg = new MailMessage
            {
                AttachedPayloads = new List<Items.KmhThingPayload> { new Items.KmhThingPayload { DefName = "MeleeWeapon_LongSword", StackCount = 1 } },
                AttachState = MailStore.AttachEscrowed,
            };
            bool hasGear = MailStore.HasAttachment(gearMsg);
            bool g1 = MailStore.TryResolveAttachment(gearMsg, MailStore.AttachClaimed);
            bool g2 = MailStore.TryResolveAttachment(gearMsg, MailStore.AttachClaimed);
            r.Add(("Mail: gear-only attachment claim-once", hasGear && g1 && !g2, $"has={hasGear} g1={g1} g2={g2}"));

            var none = new MailMessage { AttachedSilver = 0, AttachState = MailStore.AttachNone };
            r.Add(("Mail: no attachment resolves to nothing", !MailStore.HasAttachment(none) && !MailStore.TryResolveAttachment(none, MailStore.AttachClaimed), ""));

            var bad = new MailMessage { AttachedSilver = 100, AttachState = MailStore.AttachEscrowed };
            bool badRejected = !MailStore.TryResolveAttachment(bad, MailStore.AttachEscrowed) && bad.AttachState == MailStore.AttachEscrowed;
            r.Add(("Mail: invalid transition target rejected", badRejected, ""));

            // SumStacks counts units because a granted list may split stacks, which is what detects a partial withdraw.
            var split = new List<Items.KmhThingPayload>
            {
                new Items.KmhThingPayload { DefName = "Steel", StackCount = 2 },
                new Items.KmhThingPayload { DefName = "Steel", StackCount = 3 },
            };
            bool sumOk = MailStore.SumStacks(split) == 5 && MailStore.SumStacks(null) == 0;
            bool partialDetected = MailStore.SumStacks(split) < 7;   // asked 7, granted 5 -> rollback path
            r.Add(("Mail: gear unit-count detects partial withdrawal", sumOk && partialDetected, $"units={MailStore.SumStacks(split)}"));

            // A message with no attachment is never charged, whatever the fee settings say.
            var noFee = new MailConfig();
            bool feeOff = noFee.FeeFor(1000, true) == 0;
            var feeCfg = new MailConfig { AttachmentFeePercent = 10, AttachmentFeeFlat = 5 };
            bool feePct   = feeCfg.FeeFor(1000, true) == 105;   // 100 percent-fee + 5 flat
            bool feeFlat  = feeCfg.FeeFor(0, true) == 5;        // gear/items only -> flat still applies
            bool feeNone  = feeCfg.FeeFor(1000, false) == 0;    // no attachment -> no fee
            r.Add(("Mail: attachment fee off by default, math correct when on",
                feeOff && feePct && feeFlat && feeNone, $"off={feeOff} pct={feeCfg.FeeFor(1000, true)} flat={feeCfg.FeeFor(0, true)}"));

            var clampCfg = new MailConfig { AttachmentFeePercent = 999, AttachmentFeeFlat = -5, MaxInboxPerUser = 0, UnclaimedAttachmentDays = -1 };
            clampCfg.Clamp();
            bool cfgClamped = clampCfg.AttachmentFeePercent <= 50 && clampCfg.AttachmentFeeFlat == 0
                           && clampCfg.MaxInboxPerUser >= 5 && clampCfg.UnclaimedAttachmentDays >= 0;
            r.Add(("Mail: config clamped to sane range", cfgClamped,
                $"pct={clampCfg.AttachmentFeePercent} flat={clampCfg.AttachmentFeeFlat} inbox={clampCfg.MaxInboxPerUser}"));

            // Recall/timeout reuse the refund-once transition, so an already-resolved attachment can never be recovered twice.
            var recalled = new MailMessage { AttachedSilver = 300, AttachState = MailStore.AttachEscrowed };
            bool rc1 = MailStore.TryResolveAttachment(recalled, MailStore.AttachRefunded);
            bool rc2 = MailStore.TryResolveAttachment(recalled, MailStore.AttachRefunded);
            r.Add(("Mail: recall/timeout can't double-refund", rc1 && !rc2, $"rc1={rc1} rc2={rc2}"));

            return r;
        }
    }
}
