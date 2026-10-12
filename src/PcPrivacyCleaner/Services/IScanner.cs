using System;
using System.Collections.Generic;
using System.Threading;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services
{
    public sealed class ScanProgress
    {
        public ScanProgress(int step, int stepCount, string text)
        {
            Step = step;
            StepCount = stepCount;
            Text = text;
        }

        public int Step { get; private set; }
        public int StepCount { get; private set; }
        public string Text { get; private set; }
    }

    /// <summary>
    /// Runs on a background thread. Returns whatever was found so far if cancelled.
    /// </summary>
    public interface IScanner
    {
        List<FoundItem> Scan(IProgress<ScanProgress> progress, CancellationToken token);
    }
}
