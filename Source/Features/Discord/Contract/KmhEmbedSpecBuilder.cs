using System;

namespace Kmh.Discord.Wire
{
    // Shaped like the Discord builder it replaces, so the formatting code reads the same without depending on it.
    public sealed class KmhEmbedSpecBuilder
    {
        private readonly KmhEmbedSpec _spec = new KmhEmbedSpec();

        public KmhEmbedSpecBuilder WithTitle(string title)        { _spec.Title = title; return this; }
        public KmhEmbedSpecBuilder WithDescription(string text)   { _spec.Description = text; return this; }
        public KmhEmbedSpecBuilder WithColor(uint rgb)            { _spec.ColorRgb = rgb; return this; }
        public KmhEmbedSpecBuilder WithFooter(string footer)      { _spec.Footer = footer; return this; }
        public KmhEmbedSpecBuilder WithThumbnailUrl(string url)   { _spec.ThumbnailUrl = url; return this; }
        public KmhEmbedSpecBuilder WithImageUrl(string url)       { _spec.ImageUrl = url; return this; }
        public KmhEmbedSpecBuilder WithIcon(string fileName)      { _spec.IconFileName = fileName; return this; }

        public KmhEmbedSpecBuilder WithCurrentTimestamp()
        {
            _spec.TimestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return this;
        }

        public KmhEmbedSpecBuilder AddField(string name, string value, bool inline = false)
        {
            _spec.Fields.Add(new KmhEmbedField { Name = name ?? "", Value = value ?? "", Inline = inline });
            return this;
        }

        public string Description => _spec.Description;
        public int    FieldCount  => _spec.Fields.Count;

        public KmhEmbedSpec Build() => _spec;
    }
}
