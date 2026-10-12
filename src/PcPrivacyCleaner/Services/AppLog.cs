using System;
using System.IO;
using System.Text;

namespace PcPrivacyCleaner.Services
{
    /// <summary>
    /// Run log written next to the exe as log-yyyyMMdd-HHmmss.txt (Desktop as fallback).
    /// It is not kept in %TEMP% because the "임시 파일" item would delete it.
    ///
    /// Only operational facts go here: steps, counts, deleted paths, errors.
    /// Never write FoundItem.Summary/Contents (site names, recent file names) - see FoundItem.
    /// Thread-safe: scan and clean run on a background thread.
    /// </summary>
    public static class AppLog
    {
        private static readonly object Gate = new object();

        public static string FilePath { get; private set; }

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
                // The tool folder can be read-only (e.g. opened straight from inside the zip). Fall back to Desktop.
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "privacy-clean-log-" + stamp + ".txt");
            }
            FilePath = path;
        }

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            lock (Gate)
            {
                try { File.AppendAllText(FilePath, line + "\r\n", new UTF8Encoding(true)); }
                catch { /* logging must never break the tool */ }
            }
            var handler = LineAdded;
            if (handler != null) handler(line);
        }

        public static void Write(string format, params object[] args)
        {
            Write(string.Format(format, args));
        }
    }
}
