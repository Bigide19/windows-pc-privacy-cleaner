using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PcPrivacyCleaner.Interop;

namespace PcPrivacyCleaner.Services.Scan
{
    /// <summary>Why a preview could not be read. Each maps to a different on-screen sentence.</summary>
    public sealed class PreviewException : Exception
    {
        public PreviewException(string reason) : base(reason) { }
        public const string Locked = "LOCKED";
        public const string NoFile = "NOFILE";
        public const string NoSqlite = "NOSQLITE";
    }

    /// <summary>Helpers that turn browser data into "site names and counts" without reading any secret values.</summary>
    public static class SitePreview
    {
        private static bool? _sqliteWorks;

        /// <summary>
        /// Copies the SQLite file to a temp folder and reads the copy. The browser may be writing the original;
        /// a running Chromium browser keeps Cookies exclusively locked, so the copy fails -> Locked.
        /// The copy holds personal data, so it is deleted right after reading.
        /// </summary>
        public static List<string[]>[] Query(string dbPath, string[] sqls, int maxRows)
        {
            if (!File.Exists(dbPath)) throw new PreviewException(PreviewException.NoFile);
            if (_sqliteWorks == false) throw new PreviewException(PreviewException.NoSqlite);

            string tmp = Path.Combine(Path.GetTempPath(), "pcclean-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string copy = Path.Combine(tmp, "db");
                try
                {
                    File.Copy(dbPath, copy);
                    // WAL-mode databases (Firefox) keep recent changes in the -wal file.
                    if (File.Exists(dbPath + "-wal")) File.Copy(dbPath + "-wal", copy + "-wal");
                }
                catch
                {
                    throw new PreviewException(PreviewException.Locked);
                }
                try
                {
                    var r = WinSqlite.Run(copy, sqls, maxRows);
                    _sqliteWorks = true;
                    return r;
                }
                catch (DllNotFoundException ex)
                {
                    _sqliteWorks = false;
                    AppLog.Write("내용 미리보기(winsqlite3.dll)를 사용할 수 없습니다: " + ex.Message);
                    throw new PreviewException(PreviewException.NoSqlite);
                }
                catch (EntryPointNotFoundException ex)
                {
                    _sqliteWorks = false;
                    AppLog.Write("내용 미리보기(winsqlite3.dll)를 사용할 수 없습니다: " + ex.Message);
                    throw new PreviewException(PreviewException.NoSqlite);
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        public static string FailText(Exception ex, string program)
        {
            var pe = ex as PreviewException;
            if (pe != null && pe.Message == PreviewException.Locked)
                return string.Format("{0} 브라우저가 켜져 있어서 내용을 읽지 못했습니다. 브라우저를 닫고 [다시 검사]를 누르면 보입니다.", program);
            if (pe != null && pe.Message == PreviewException.NoSqlite)
                return "이 PC에서는 내용 미리보기를 사용할 수 없습니다. 지우는 데에는 문제가 없습니다.";
            return "내용을 읽지 못했습니다. 지우는 데에는 문제가 없습니다.";
        }

        private static readonly HashSet<string> SecondLevel = new HashSet<string>
        {
            "co", "or", "go", "ne", "ac", "re", "pe", "com", "net", "org", "gov", "edu"
        };

        /// <summary>
        /// Reduces a URL or host to a site name such as "naver.com" or "hometax.go.kr".
        /// Two-level country domains (co.kr, go.kr, ...) keep three labels; a leading "www." is dropped.
        /// </summary>
        public static string SiteName(string urlOrHost)
        {
            if (string.IsNullOrEmpty(urlOrHost)) return "";
            string h = urlOrHost;
            if (h.Contains("://"))
            {
                Uri uri;
                if (!Uri.TryCreate(h, UriKind.Absolute, out uri)) return "";
                h = uri.Host;
            }
            h = h.Trim().TrimStart('.').ToLowerInvariant();
            if (h.StartsWith("www.")) h = h.Substring(4);
            if (h.Length == 0) return "";
            System.Net.IPAddress ip;
            if (System.Net.IPAddress.TryParse(h, out ip)) return h;
            var parts = h.Split('.');
            if (parts.Length <= 2) return h;
            int n = 2;
            if (parts[parts.Length - 1].Length == 2 && SecondLevel.Contains(parts[parts.Length - 2])) n = 3;
            return string.Join(".", parts.Skip(parts.Length - n));
        }

        public sealed class SiteList
        {
            public int Count { get; set; }
            public string Line { get; set; }
        }

        /// <summary>
        /// Groups site names, ranks them by weight (visit count, cookie count, or occurrences) and
        /// produces "a, b, c 외 N곳".
        /// </summary>
        public static SiteList Rank(IList<string> sites, IList<long> weights = null, int show = 15)
        {
            var totals = new Dictionary<string, long>();
            for (int i = 0; i < sites.Count; i++)
            {
                var s = sites[i];
                if (string.IsNullOrEmpty(s)) continue;
                long w = (weights != null && i < weights.Count) ? Math.Max(1, weights[i]) : 1;
                long cur;
                totals[s] = totals.TryGetValue(s, out cur) ? cur + w : w;
            }
            var sorted = totals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToList();
            string line = string.Join(", ", sorted.Take(show));
            if (sorted.Count > show) line += string.Format(" 외 {0}곳", sorted.Count - show);
            return new SiteList { Count = sorted.Count, Line = line };
        }

        public static long Number(List<string[]> rows)
        {
            long v;
            if (rows == null || rows.Count == 0 || !long.TryParse(rows[0][0], out v)) return -1;
            return v;
        }

        /// <summary>Chromium time: microseconds since 1601-01-01 UTC.</summary>
        public static DateTime? ChromeTime(string value)
        {
            long v;
            if (!long.TryParse(value, out v) || v <= 0) return null;
            try { return DateTime.FromFileTimeUtc(v * 10).ToLocalTime(); } catch { return null; }
        }

        /// <summary>Firefox time: microseconds since 1970-01-01 UTC.</summary>
        public static DateTime? FirefoxTime(string value)
        {
            long v;
            if (!long.TryParse(value, out v) || v <= 0) return null;
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(v * 10).ToLocalTime(); } catch { return null; }
        }
    }
}
