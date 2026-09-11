using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Mail
{
    internal sealed class MailConfig
    {
        public int SchemaVersion { get; set; } = 1;

        public int MaxSubjectLength  { get; set; } = 64;
        public int MaxBodyLength     { get; set; } = 1024;
        public int MaxInboxPerUser   { get; set; } = 100;
        public int MaxSendsPerWindow { get; set; } = 10;
        public int SendWindowSeconds { get; set; } = 60;
        public int RetentionDays     { get; set; } = 30;   // read mail is pruned after this

        // 0 disables the auto-return, and goods sent to a mistyped or never-returning player then strand forever.
        public int UnclaimedAttachmentDays { get; set; } = 14;

        public int  MaxAttachItemTypes { get; set; } = 12;
        public int  MaxAttachGearTypes { get; set; } = 12;
        public int  MaxAttachItemQty   { get; set; } = 1_000_000;
        public long MaxAttachSilver    { get; set; } = 500_000;

        // The flat part applies to item-only gifts too, and the fee is charged on top of the attachment, not out of it.
        public double AttachmentFeePercent { get; set; } = 0.0;
        public int    AttachmentFeeFlat    { get; set; } = 0;

        // Checked before escrow, so a refused letter never takes goods out of the sender's treasury.
        public bool BlockAppliesToMail { get; set; } = true;

        private static MailConfig _current;
        public static MailConfig Current => _current ?? (_current = LoadOrDefault());
        public static void Reload() { _current = null; }

        public static MailConfig LoadOrDefault()
        {
            MailConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.MailConfigFile, out MailConfig loaded) && loaded != null ? loaded : new MailConfig();
            cfg.Clamp();
            return cfg;
        }

        // Keep every limit in a sane range so a hand-edited config can't disable the guards or strand value.
        public void Clamp()
        {
            MaxSubjectLength  = Clamp(MaxSubjectLength,   8, 256);
            MaxBodyLength     = Clamp(MaxBodyLength,     16, 8000);
            MaxInboxPerUser   = Clamp(MaxInboxPerUser,    5, 1000);
            MaxSendsPerWindow = Clamp(MaxSendsPerWindow,  1, 240);
            SendWindowSeconds = Clamp(SendWindowSeconds,  1, 3600);
            RetentionDays     = Clamp(RetentionDays,      1, 3650);

            UnclaimedAttachmentDays = Clamp(UnclaimedAttachmentDays, 0, 3650);

            MaxAttachItemTypes = Clamp(MaxAttachItemTypes, 1, 100);
            MaxAttachGearTypes = Clamp(MaxAttachGearTypes, 1, 100);
            MaxAttachItemQty   = Clamp(MaxAttachItemQty,   1, 10_000_000);
            if (MaxAttachSilver < 1)             MaxAttachSilver = 1;
            if (MaxAttachSilver > 1_000_000_000) MaxAttachSilver = 1_000_000_000;

            if (AttachmentFeePercent < 0)  AttachmentFeePercent = 0;
            if (AttachmentFeePercent > 50) AttachmentFeePercent = 50;   // never let a fee swallow the gift
            if (AttachmentFeeFlat < 0)     AttachmentFeeFlat = 0;
            if (AttachmentFeeFlat > 1_000_000) AttachmentFeeFlat = 1_000_000;
        }

        public long FeeFor(long attachedSilver, bool hasAnyAttachment)
        {
            if (!hasAnyAttachment) return 0;
            long pct = AttachmentFeePercent <= 0 || attachedSilver <= 0
                ? 0
                : (long)System.Math.Floor(attachedSilver * (AttachmentFeePercent / 100.0));
            return pct + AttachmentFeeFlat;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.MailConfigFile))
                JsonFileStore.Save(KmhDataPaths.MailConfigFile, new MailConfig());
        }
    }
}
