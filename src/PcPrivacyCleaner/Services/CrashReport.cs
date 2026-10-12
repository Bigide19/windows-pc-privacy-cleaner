using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PcPrivacyCleaner.Services
{
    /// <summary>
    /// Notices that the previous run ended in a crash, so it can be reported on the next start.
    ///
    /// Some crashes can not be caught by the app at all (an access violation inside winsqlite3.dll, a stack
    /// overflow, Environment.FailFast): the process is gone before any handler runs. So every run leaves a marker,
    /// run-&lt;pid&gt;.tmp next to the exe, that also receives the run's Step lines (AppLog.StepFilePath).
    /// A normal exit deletes it. A marker found at the next start whose process no longer exists means the
    /// previous run did not exit normally; Windows' own crash records in the Application event log then say
    /// whether it was a crash (as opposed to Task Manager or a power cut).
    ///
    /// Everything this class produces may go into the report, so it holds Step-level data only:
    /// Step lines, exception types, module names, exception codes and stack frame names. No paths.
    /// </summary>
    public static class CrashReport
    {
        private const string MarkerPattern = "run-*.tmp";

        /// <summary>Header lines of FindWindowsCrashRecords start with this, one per record; detail lines are indented.</summary>
        public static int CountRecords(List<string> lines)
        {
            return lines.Count(l => !l.StartsWith(" "));
        }

        /// <summary>The Step line that AppDomain.UnhandledException leaves before the process dies.</summary>
        public const string FatalStep = "치명적 오류";

        private static string _markerPath;

        public sealed class UnfinishedRun
        {
            public DateTime StartedAt { get; set; }
            public List<string> Steps { get; set; }

            /// <summary>The app itself recorded a fatal error before it died (crash kind ②).</summary>
            public bool HadFatalStep { get { return Steps.Any(s => s.Contains(FatalStep)); } }
        }

        /// <summary>
        /// Call once at startup, before Start(). Returns the most recent run that did not exit normally, or null.
        /// Markers of runs that are still alive (the tool opened twice) are left alone; all others are deleted.
        /// </summary>
        public static UnfinishedRun TakeUnfinishedRun(string dir)
        {
            UnfinishedRun latest = null;
            string[] markers;
            try { markers = Directory.GetFiles(dir, MarkerPattern); }
            catch { return null; }

            foreach (var path in markers)
            {
                int pid;
                string name = Path.GetFileNameWithoutExtension(path);
                if (!int.TryParse(name.Substring(name.IndexOf('-') + 1), out pid)) continue;
                if (IsRunningInstance(pid)) continue;
                try
                {
                    var run = new UnfinishedRun
                    {
                        StartedAt = File.GetCreationTime(path),
                        Steps = File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList()
                    };
                    if (latest == null || run.StartedAt > latest.StartedAt) latest = run;
                    File.Delete(path);
                }
                catch
                {
                    // An unreadable marker is not worth bothering the user about.
                }
            }
            return latest;
        }

        private static bool IsRunningInstance(int pid)
        {
            if (pid == Process.GetCurrentProcess().Id) return true;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return string.Equals(p.ProcessName, "PcPrivacyCleaner", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;   // no such process
            }
        }

        /// <summary>Creates this run's marker. Skipped silently when the exe folder is read-only.</summary>
        public static void Start(string dir)
        {
            string path = Path.Combine(dir, "run-" + Process.GetCurrentProcess().Id + ".tmp");
            try
            {
                File.WriteAllText(path, "", new UTF8Encoding(true));
                _markerPath = path;
                AppLog.StepFilePath = path;
            }
            catch
            {
                _markerPath = null;
            }
        }

        /// <summary>
        /// Normal exit, or a crash the user was already asked about: removes the marker so the next start
        /// does not ask again.
        /// </summary>
        public static void Finish()
        {
            AppLog.StepFilePath = null;
            if (_markerPath == null) return;
            try { File.Delete(_markerPath); } catch { }
            _markerPath = null;
        }

        /// <summary>
        /// Windows' crash records for this exe since the given time: "Application Error" (1000, native crash)
        /// and ".NET Runtime" (1026, unhandled .NET exception or FailFast). Reading the Application log needs
        /// no admin rights. Returns sanitized lines, or an empty list when there is nothing or the log can not be read.
        /// </summary>
        public static List<string> FindWindowsCrashRecords(DateTime since)
        {
            var lines = new List<string>();
            try
            {
                // A few seconds of slack for timestamp rounding only. A wider window would pick up the crash of
                // the run before (Windows can write that record well after the process ended).
                string from = since.AddSeconds(-5).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var query = new EventLogQuery("Application", PathType.LogName,
                    "*[System[(EventID=1000 or EventID=1026) and TimeCreated[@SystemTime>='" + from + "']]]");
                using (var reader = new EventLogReader(query))
                {
                    for (EventRecord e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
                    {
                        using (e)
                        {
                            if (e.Id == 1000 && e.ProviderName == "Application Error") AddNativeCrash(e, lines);
                            else if (e.Id == 1026 && e.ProviderName == ".NET Runtime") AddDotNetCrash(e, lines);
                        }
                    }
                }
            }
            catch
            {
                // Event log service off or access denied by policy: report without it.
            }
            return lines;
        }

        /// <summary>
        /// Event 1000 data: [0] app name, [3] faulting module, [6] exception code, [7] fault offset.
        /// The paths in [10] and [11] contain the user name and are never read.
        /// </summary>
        private static void AddNativeCrash(EventRecord e, List<string> lines)
        {
            var p = e.Properties;
            if (p.Count < 8) return;
            if (!string.Equals(Convert.ToString(p[0].Value), "PcPrivacyCleaner.exe", StringComparison.OrdinalIgnoreCase)) return;
            lines.Add(string.Format("Windows 오류 기록 ({0:HH:mm:ss}): 모듈 {1}, 예외 코드 {2}, 위치 {3}",
                e.TimeCreated, SafeToken(Path.GetFileName(Convert.ToString(p[3].Value))),
                SafeToken(Convert.ToString(p[6].Value)), SafeToken(Convert.ToString(p[7].Value))));
        }

        /// <summary>
        /// Event 1026 is one text block. Only the exception type lines and the "at Namespace.Class.Method(" part of
        /// stack lines are kept: the labels are localized on Korean Windows, and FailFast adds a free-text message,
        /// so lines are matched by shape, not by label.
        /// </summary>
        private static void AddDotNetCrash(EventRecord e, List<string> lines)
        {
            string text = e.Properties.Count > 0 ? Convert.ToString(e.Properties[0].Value) : "";
            if (text.IndexOf("PcPrivacyCleaner.exe", StringComparison.OrdinalIgnoreCase) < 0) return;
            lines.Add(string.Format(".NET 오류 기록 ({0:HH:mm:ss}):", e.TimeCreated));
            int frames = 0;   // nested types appear as Outer+Inner, hence the '+' in the pattern below
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                var type = Regex.Match(line, @":\s*((?:System|Microsoft|PcPrivacyCleaner)(?:\.\w+)+(?:Exception|Error))\s*$");
                if (type.Success) { lines.Add("  " + type.Groups[1].Value); continue; }
                var frame = Regex.Match(line, @"^\s+\S+\s+((?:System|Microsoft|PcPrivacyCleaner)[\w.<>`+]*\.[\w<>`]+)\(");
                if (frame.Success && frames < 12) { lines.Add("    " + frame.Groups[1].Value); frames++; }
            }
        }

        /// <summary>Event values are expected to be a file name or a hex number; anything else is dropped.</summary>
        private static string SafeToken(string value)
        {
            value = (value ?? "").Trim();
            return Regex.IsMatch(value, @"^[\w.\-]{1,64}$") ? value : "?";
        }
    }
}
