using System;

namespace PcPrivacyCleaner.Infrastructure
{
    /// <summary>Display formatting shared by the list and the detail panel.</summary>
    public static class Format
    {
        /// <summary>"12.3 MB (45개)" style. Returns "-" when there is nothing to measure.</summary>
        public static string Size(long bytes, int count)
        {
            string text = "-";
            if (bytes >= 1L << 30) text = string.Format("{0:N1} GB", bytes / (double)(1L << 30));
            else if (bytes >= 1L << 20) text = string.Format("{0:N1} MB", bytes / (double)(1L << 20));
            else if (bytes >= 1L << 10) text = string.Format("{0:N0} KB", bytes / (double)(1L << 10));
            else if (bytes > 0) text = bytes + " B";

            if (count > 1)
            {
                if (text == "-") return count + "개";
                return string.Format("{0} ({1}개)", text, count);
            }
            return text;
        }

        public static string Date(DateTime? time)
        {
            return time.HasValue ? time.Value.ToString("yyyy-MM-dd") : "";
        }
    }
}
