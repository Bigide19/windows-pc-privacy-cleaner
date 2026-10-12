namespace PcPrivacyCleaner.Models
{
    public enum DetailLineKind
    {
        /// <summary>Plain sentence.</summary>
        Normal,
        /// <summary>Small gray caption, e.g. "지우는 위치:".</summary>
        Muted,
        /// <summary>A file system path under "지우는 위치". Small gray, indented.</summary>
        Path
    }

    /// <summary>One line of the detail panel.</summary>
    public sealed class DetailLine
    {
        public DetailLine(string text, DetailLineKind kind = DetailLineKind.Normal)
        {
            Text = text;
            Kind = kind;
        }

        public string Text { get; private set; }
        public DetailLineKind Kind { get; private set; }
    }
}
