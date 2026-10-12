using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PcPrivacyCleaner.Services
{
    /// <summary>
    /// Run log written next to the exe as log-yyyyMMdd-HHmmss.txt (Desktop as fallback).
    /// It is not kept in %TEMP% because the "임시 파일" item would delete it.
    ///
    /// Every line is one of two kinds, chosen where it is written:
    ///   - Step:   what the tool did. Stage names, counts, exception types and code locations only.
    ///             These are the only lines that ever leave the PC: [문제 알리기] puts them into the form
    ///             (see IssueReport), and they survive a crash in the run marker (see CrashReport).
    ///             Never pass account names, item names, paths, site names or exception messages to Step.
    ///   - Detail: everything else needed to troubleshoot on the PC itself (account and item names,
    ///             paths, exception messages). Log file and the window's log panel only.
    /// Never write FoundItem.Summary/Contents (site names, recent file names) to either kind - see FoundItem.
    /// Thread-safe: scan and clean run on a background thread.
    /// </summary>
    public static class AppLog
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Steps = new List<string>();

        public static string FilePath { get; private set; }

        /// <summary>
        /// Second file that receives Step lines only: the run marker (CrashReport). If the process dies,
        /// the marker still holds what this run did, and the next start can offer to report it.
        /// </summary>
        public static string StepFilePath { get; set; }

        /// <summary>Raised for every line. The UI marshals it to the dispatcher itself.</summary>
        public static event Action<string> LineAdded;

        public static void Initialize(string exeDir)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(exeDir, "log-" + stamp + ".txt");
            try
            {
                File.AppendAllText(path, "", new UTF8Encoding(true));
            }
            catch
            {
                // The tool folder can be read-only (e.g. a protected or network folder). Fall back to Desktop.
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "privacy-clean-log-" + stamp + ".txt");
            }
            FilePath = path;
        }

        // ---------- Step: may be sent in a report ----------

        /// <summary>Fixed text chosen in code (a literal, or one of a few literals). Never text that came from the PC.</summary>
        public static void Step(string text)
        {
            string line = Stamp(text);
            lock (Gate)
            {
                Steps.Add(line);
                Append(FilePath, line);
                if (StepFilePath != null) Append(StepFilePath, line);
            }
            Raise(line);
        }

        /// <summary>
        /// Fixed format with numbers only. The arguments are numeric on purpose: a name or a path can not
        /// be passed here by mistake, because it would not compile.
        /// </summary>
        public static void Step(string format, params long[] numbers)
        {
            Step(string.Format(format, numbers.Cast<object>().ToArray()));
        }

        /// <summary>
        /// "what" is fixed text. The Step line gets the exception type, its HRESULT and where in this app's code
        /// it was thrown; the message (which can hold a path) goes to a Detail line only.
        /// </summary>
        public static void StepError(string what, Exception ex)
        {
            Step(what + ": " + Describe(ex));
            Detail("  오류 내용: " + (ex == null ? "" : ex.Message));
        }

        // ---------- Detail: stays on this PC ----------

        public static void Detail(string message)
        {
            string line = Stamp(message);
            lock (Gate)
            {
                Append(FilePath, line);
            }
            Raise(line);
        }

        public static void Detail(string format, params object[] args)
        {
            Detail(string.Format(format, args));
        }

        // ---------- report ----------

        /// <summary>
        /// The most recent Step lines, as many as fit when URL-encoded into maxEncodedLength characters.
        /// Korean text triples in size when encoded, so the limit is on the encoded form, not on the text.
        /// </summary>
        public static string StepsText(int maxEncodedLength)
        {
            List<string> copy;
            lock (Gate) copy = new List<string>(Steps);
            return TakeTail(copy, maxEncodedLength);
        }

        /// <summary>Keeps the last lines that fit; drops whole lines from the top and says so.</summary>
        public static string TakeTail(IList<string> lines, int maxEncodedLength)
        {
            var kept = new List<string>();
            int size = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                int add = Uri.EscapeDataString(lines[i] + "\n").Length;
                if (size + add > maxEncodedLength) { kept.Insert(0, "(앞부분 생략)"); break; }
                size += add;
                kept.Insert(0, lines[i]);
            }
            return string.Join("\n", kept);
        }

        /// <summary>
        /// "IOException 0x80070020 at Cleaner.DeleteEntry &lt; Cleaner.RemoveTargets": type, HRESULT and up to three
        /// frames from this app's own code. Frames are class and method names, so they carry no user data.
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "알 수 없는 오류";
            // The interesting exception is the one inside these wrappers.
            while (ex.InnerException != null && (ex is AggregateException || ex is TargetInvocationException || ex is TypeInitializationException))
                ex = ex is AggregateException ? ((AggregateException)ex).Flatten().InnerExceptions[0] : ex.InnerException;

            var frames = new StackTrace(ex, false).GetFrames() ?? new StackFrame[0];
            var ours = frames
                .Select(f => f.GetMethod())
                .Where(m => m != null && m.DeclaringType != null && (m.DeclaringType.Namespace ?? "").StartsWith("PcPrivacyCleaner"))
                .Select(FrameName)
                .Distinct()
                .Take(3)
                .ToList();
            string text = ex.GetType().Name + string.Format(" 0x{0:X8}", ex.HResult);
            if (ours.Count > 0) text += " at " + string.Join(" < ", ours);
            return text;
        }

        /// <summary>
        /// Class.Method, with compiler-generated names (async state machines, lambdas) mapped back to the method
        /// they were written in: "&lt;ScanAsync&gt;d__42.MoveNext" becomes "MainViewModel.ScanAsync".
        /// </summary>
        private static string FrameName(MethodBase m)
        {
            Type t = m.DeclaringType;
            string method = m.Name;
            while (t.Name.StartsWith("<") && t.DeclaringType != null)
            {
                method = Inner(t.Name) ?? method;
                t = t.DeclaringType;
            }
            if (t.Name.StartsWith("<>") && t.DeclaringType != null) t = t.DeclaringType;   // <>c lambda holder
            return t.Name + "." + (Inner(method) ?? method);
        }

        private static string Inner(string generated)
        {
            int a = generated.IndexOf('<'), b = generated.IndexOf('>');
            return a >= 0 && b > a + 1 ? generated.Substring(a + 1, b - a - 1) : null;
        }

        // ---------- plumbing ----------

        private static string Stamp(string text)
        {
            return DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        }

        private static void Append(string path, string line)
        {
            if (path == null) return;
            try { File.AppendAllText(path, line + "\r\n", new UTF8Encoding(true)); }
            catch { /* logging must never break the tool */ }
        }

        private static void Raise(string line)
        {
            var handler = LineAdded;
            if (handler != null) handler(line);
        }
    }
}
