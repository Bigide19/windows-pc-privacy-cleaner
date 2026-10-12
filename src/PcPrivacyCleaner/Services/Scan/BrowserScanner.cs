using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services.Scan
{
    /// <summary>3. 브라우저: saved passwords, cookies, history, autofill per browser profile.</summary>
    public static class BrowserScanner
    {
        private sealed class Browser
        {
            public string Name;
            public bool Chromium;
            public string Process;
            public string Rel;
        }

        /// <summary>
        /// One deletable part of a browser profile. Files are relative to the profile folder.
        /// SyncBlocked: comes back from the cloud when the browser account is signed in and syncing,
        /// so deleting the files is pointless; the user is told to clear it in the browser instead.
        /// Cookies are not synced, so they can always be deleted. Bookmarks are never deleted by default.
        /// </summary>
        private sealed class Part
        {
            public string Label;
            public bool SyncBlocked;
            public bool Default;
            public string Preview;
            public string[] Files;
        }

        private static readonly Browser[] Browsers =
        {
            new Browser { Name = "Chrome", Chromium = true, Process = "chrome", Rel = @"AppData\Local\Google\Chrome\User Data" },
            new Browser { Name = "Edge", Chromium = true, Process = "msedge", Rel = @"AppData\Local\Microsoft\Edge\User Data" },
            new Browser { Name = "웨일", Chromium = true, Process = "whale", Rel = @"AppData\Local\Naver\Naver Whale\User Data" },
            new Browser { Name = "Brave", Chromium = true, Process = "brave", Rel = @"AppData\Local\BraveSoftware\Brave-Browser\User Data" },
            new Browser { Name = "Firefox", Chromium = false, Process = "firefox", Rel = @"AppData\Roaming\Mozilla\Firefox\Profiles" },
        };

        private static readonly Part[] ChromiumParts =
        {
            new Part { Label = "저장된 비밀번호", SyncBlocked = true, Default = true, Preview = "logins",
                Files = new[] { "Login Data", "Login Data-journal", "Login Data For Account", "Login Data For Account-journal" } },
            new Part { Label = "로그인 상태 (쿠키 등)", SyncBlocked = false, Default = true, Preview = "cookies",
                Files = new[] { @"Network\Cookies", @"Network\Cookies-journal", "Cookies", "Cookies-journal", "Local Storage", "Session Storage", "IndexedDB" } },
            new Part { Label = "방문 기록, 캐시", SyncBlocked = true, Default = true, Preview = "history",
                Files = new[] { "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal", "Shortcuts", "Shortcuts-journal",
                                "Sessions", "Current Session", "Current Tabs", "Last Session", "Last Tabs",
                                "Network Action Predictor", "Network Action Predictor-journal", "Cache", "Code Cache", "GPUCache" } },
            new Part { Label = "자동완성 (주소, 카드, 입력 기록)", SyncBlocked = true, Default = true, Preview = "webdata",
                Files = new[] { "Web Data", "Web Data-journal" } },
        };

        private static readonly Part[] FirefoxParts =
        {
            new Part { Label = "저장된 비밀번호", SyncBlocked = true, Default = true, Preview = "ff-logins",
                Files = new[] { "logins.json", "logins-backup.json", "key4.db", "key3.db", "signons.sqlite" } },
            new Part { Label = "로그인 상태 (쿠키 등)", SyncBlocked = false, Default = true, Preview = "ff-cookies",
                Files = new[] { "cookies.sqlite", "cookies.sqlite-wal", "webappsstore.sqlite", @"storage\default" } },
            new Part { Label = "자동완성 (주소, 카드, 입력 기록)", SyncBlocked = true, Default = true, Preview = "ff-form",
                Files = new[] { "formhistory.sqlite", "autofill-profiles.json" } },
            new Part { Label = "열린 탭 복원 기록", SyncBlocked = false, Default = true, Preview = null,
                Files = new[] { "sessionstore.jsonlz4", "sessionstore-backups" } },
            // History and bookmarks share places.sqlite in Firefox, so this part starts unchecked.
            new Part { Label = "방문 기록 (북마크도 함께 지워짐)", SyncBlocked = true, Default = false, Preview = "ff-places",
                Files = new[] { "places.sqlite", "places.sqlite-wal", "favicons.sqlite", "favicons.sqlite-wal" } },
        };

        public static void Find(ScanSession s)
        {
            foreach (var pf in s.Profiles)
            {
                foreach (var b in Browsers)
                {
                    s.Token.ThrowIfCancellationRequested();
                    string baseDir = Path.Combine(pf.Path, b.Rel);
                    if (!Files.DirExists(baseDir)) continue;

                    IEnumerable<string> profileDirs;
                    try { profileDirs = Directory.GetDirectories(baseDir); }
                    catch { continue; }
                    // A Chromium profile folder (Default, Profile 1, ...) is one that has a Preferences file.
                    if (b.Chromium) profileDirs = profileDirs.Where(d => File.Exists(Path.Combine(d, "Preferences")));

                    foreach (var pd in profileDirs)
                    {
                        string email = SignedInAccount(b.Chromium, pd);
                        foreach (var part in b.Chromium ? ChromiumParts : FirefoxParts)
                        {
                            s.Token.ThrowIfCancellationRequested();
                            var paths = part.Files.Select(f => Path.Combine(pd, f)).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                            if (paths.Count == 0) continue;

                            var item = new FoundItem(ItemGroup.Browser,
                                string.Format("{0} ({1}) - {2}", b.Name, Path.GetFileName(pd), part.Label))
                            {
                                Default = part.Default
                            };
                            item.Paths.AddRange(paths);
                            item.Processes.Add(b.Process);
                            if (!string.IsNullOrEmpty(email) && part.SyncBlocked)
                            {
                                item.BlockReason = string.Format(
                                    "{0} 계정으로 로그인되어 있어서, 여기서 지워도 동기화로 다시 생깁니다. " +
                                    "브라우저에서 Ctrl+Shift+Delete를 누르고 기간을 '전체 기간'으로 골라 직접 지워 주세요.", email);
                            }
                            if (!string.IsNullOrEmpty(email)) item.Contents.Add("브라우저에 로그인된 계정: " + email);

                            if (part.Preview != null)
                            {
                                s.Report(string.Format("{0} {1} 확인 중...", b.Name, part.Label));
                                Preview(item, part.Preview, pd, b.Name);
                            }
                            s.Add(item, pf);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Account email of a signed-in browser profile. Chromium keeps it in Preferences (JSON); it is
        /// matched with a regex because only one field is needed. Firefox: signedInUser.json.
        /// </summary>
        private static string SignedInAccount(bool chromium, string profileDir)
        {
            try
            {
                if (chromium)
                {
                    string txt = File.ReadAllText(Path.Combine(profileDir, "Preferences"));
                    var m = Regex.Match(txt, "\"account_info\"\\s*:\\s*\\[\\s*\\{[^\\]]*?\"email\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value;
                }
                else
                {
                    string f = Path.Combine(profileDir, "signedInUser.json");
                    if (File.Exists(f))
                    {
                        var m = Regex.Match(File.ReadAllText(f), "\"email\"\\s*:\\s*\"([^\"]+)\"");
                        return m.Success ? m.Groups[1].Value : "Firefox 계정";
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Fills Summary/Contents with site names and counts. Never reads passwords, cookie values or card numbers.
        /// Failure only changes the text; the item stays in the list and can still be deleted.
        /// </summary>
        private static void Preview(FoundItem item, string kind, string dir, string browser)
        {
            var lines = new List<string>();
            string summary = "";
            try
            {
                switch (kind)
                {
                    case "logins":
                    {
                        var sites = new List<string>();
                        int accounts = 0;
                        bool any = false;
                        foreach (var f in new[] { "Login Data", "Login Data For Account" })
                        {
                            string db = Path.Combine(dir, f);
                            if (!File.Exists(db)) continue;
                            any = true;
                            var r = SitePreview.Query(db, new[] { "SELECT origin_url FROM logins WHERE blacklisted_by_user = 0" }, 5000);
                            if (r[0] != null) foreach (var row in r[0]) { sites.Add(SitePreview.SiteName(row[0])); accounts++; }
                        }
                        if (!any) throw new PreviewException(PreviewException.NoFile);
                        PasswordLines(sites, accounts, lines, out summary);
                        break;
                    }
                    case "cookies":
                    {
                        string db = Path.Combine(dir, @"Network\Cookies");
                        if (!File.Exists(db)) db = Path.Combine(dir, "Cookies");
                        var r = SitePreview.Query(db, new[] { "SELECT host_key, COUNT(*) FROM cookies GROUP BY host_key" }, 20000);
                        CookieLines(r[0], lines, out summary);
                        break;
                    }
                    case "history":
                    {
                        var r = SitePreview.Query(Path.Combine(dir, "History"), new[]
                        {
                            "SELECT COUNT(*), MIN(last_visit_time), MAX(last_visit_time) FROM urls WHERE last_visit_time > 0",
                            "SELECT url, visit_count FROM urls ORDER BY visit_count DESC LIMIT 500"
                        }, 500);
                        HistoryLines(r[0], r[1], SitePreview.ChromeTime, lines, out summary);
                        lines.Add("캐시와 열린 탭 기록도 함께 지웁니다.");
                        break;
                    }
                    case "webdata":
                    {
                        var r = SitePreview.Query(Path.Combine(dir, "Web Data"), new[]
                        {
                            "SELECT COUNT(*) FROM autofill",
                            "SELECT COUNT(*) FROM autofill_profiles",
                            "SELECT COUNT(*) FROM addresses",
                            "SELECT COUNT(*) FROM local_addresses",
                            "SELECT COUNT(*) FROM credit_cards"
                        }, 1);
                        long form = SitePreview.Number(r[0]);
                        // The address table name differs by browser version; use the largest one that exists.
                        long addr = Math.Max(Math.Max(SitePreview.Number(r[1]), SitePreview.Number(r[2])), SitePreview.Number(r[3]));
                        long card = SitePreview.Number(r[4]);
                        var parts = new List<string>();
                        if (form > 0) { parts.Add(string.Format("입력 기록 {0:N0}개", form)); lines.Add(string.Format("입력 기록 {0:N0}개 (검색창, 회원가입 양식 등에 입력한 값)", form)); }
                        if (addr > 0) { parts.Add(string.Format("주소 {0}개", addr)); lines.Add(string.Format("저장된 주소 {0}개 (이름, 전화번호, 주소)", addr)); }
                        if (card > 0) { parts.Add(string.Format("카드 {0}장", card)); lines.Add(string.Format("저장된 카드 {0}장 (카드 번호는 표시하지 않습니다)", card)); }
                        if (parts.Count == 0) { summary = "없음"; lines.Add("저장된 입력 기록, 주소, 카드가 없습니다."); }
                        else summary = string.Join(", ", parts);
                        break;
                    }
                    case "ff-logins":
                    {
                        // logins.json is plain JSON, so no SQLite is needed; only host names are taken.
                        string f = Path.Combine(dir, "logins.json");
                        if (!File.Exists(f)) throw new PreviewException(PreviewException.NoFile);
                        var sites = Regex.Matches(File.ReadAllText(f), "\"hostname\"\\s*:\\s*\"([^\"]+)\"")
                            .Cast<Match>().Select(m => SitePreview.SiteName(m.Groups[1].Value)).ToList();
                        PasswordLines(sites, sites.Count, lines, out summary);
                        break;
                    }
                    case "ff-cookies":
                    {
                        var r = SitePreview.Query(Path.Combine(dir, "cookies.sqlite"), new[] { "SELECT host, COUNT(*) FROM moz_cookies GROUP BY host" }, 20000);
                        CookieLines(r[0], lines, out summary);
                        break;
                    }
                    case "ff-places":
                    {
                        var r = SitePreview.Query(Path.Combine(dir, "places.sqlite"), new[]
                        {
                            "SELECT COUNT(*), MIN(last_visit_date), MAX(last_visit_date) FROM moz_places WHERE visit_count > 0",
                            "SELECT url, visit_count FROM moz_places WHERE visit_count > 0 ORDER BY visit_count DESC LIMIT 500",
                            "SELECT COUNT(*) FROM moz_bookmarks WHERE type = 1"
                        }, 500);
                        HistoryLines(r[0], r[1], SitePreview.FirefoxTime, lines, out summary);
                        long bm = SitePreview.Number(r[2]);
                        if (bm > 0) lines.Add(string.Format("북마크 {0}개도 같은 파일에 들어 있어서 함께 지워집니다.", bm));
                        break;
                    }
                    case "ff-form":
                    {
                        var r = SitePreview.Query(Path.Combine(dir, "formhistory.sqlite"), new[] { "SELECT COUNT(*) FROM moz_formhistory" }, 1);
                        long n = SitePreview.Number(r[0]);
                        if (n == 0) summary = "없음";
                        else if (n > 0)
                        {
                            summary = string.Format("입력 기록 {0:N0}개", n);
                            lines.Add(string.Format("입력 기록 {0:N0}개 (검색창, 회원가입 양식 등에 입력한 값)", n));
                        }
                        lines.Add("저장된 주소와 카드도 함께 지웁니다.");
                        break;
                    }
                }
            }
            catch (PreviewException ex) when (ex.Message == PreviewException.NoFile)
            {
                // Nothing to show; not worth a message.
                summary = "";
                lines.Clear();
            }
            catch (Exception ex)
            {
                summary = "";
                lines.Clear();
                lines.Add(SitePreview.FailText(ex, browser));
            }
            item.Summary = summary;
            item.Contents.AddRange(lines);
        }

        private static void PasswordLines(List<string> sites, int accounts, List<string> lines, out string summary)
        {
            var sl = SitePreview.Rank(sites);
            if (sl.Count == 0)
            {
                summary = "없음";
                lines.Add("저장된 비밀번호가 없습니다.");
                return;
            }
            summary = string.Format("사이트 {0}곳", sl.Count);
            lines.Add(string.Format("비밀번호가 저장된 사이트 {0}곳, 계정 {1}개", sl.Count, accounts));
            lines.Add(sl.Line);
            lines.Add("비밀번호 값은 읽지도, 화면에 표시하지도 않습니다.");
        }

        private static void CookieLines(List<string[]> rows, List<string> lines, out string summary)
        {
            var sites = new List<string>();
            var weights = new List<long>();
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    long w;
                    sites.Add(SitePreview.SiteName(row[0]));
                    weights.Add(long.TryParse(row[1], out w) ? w : 1);
                }
            }
            var sl = SitePreview.Rank(sites, weights);
            if (sl.Count == 0)
            {
                summary = "없음";
                lines.Add("로그인 상태가 저장된 사이트가 없습니다.");
                return;
            }
            summary = string.Format("사이트 {0}곳", sl.Count);
            lines.Add(string.Format("로그인 상태나 설정이 저장된 사이트 {0}곳", sl.Count));
            if (sl.Line.Length > 0) lines.Add(sl.Line);
        }

        private static void HistoryLines(List<string[]> stats, List<string[]> top, Func<string, DateTime?> toTime,
                                         List<string> lines, out string summary)
        {
            summary = "";
            long n = SitePreview.Number(stats);
            if (n == 0)
            {
                summary = "없음";
                lines.Add("방문 기록이 없습니다.");
            }
            else if (n > 0)
            {
                summary = string.Format("{0:N0}건", n);
                string from = Infrastructure.Format.Date(toTime(stats[0][1]));
                string to = Infrastructure.Format.Date(toTime(stats[0][2]));
                string line = string.Format("방문한 페이지 {0:N0}개", n);
                if (from.Length > 0) line += string.Format(" ({0} ~ {1})", from, to);
                lines.Add(line);
            }
            var sites = new List<string>();
            var weights = new List<long>();
            if (top != null)
            {
                foreach (var row in top)
                {
                    long w;
                    sites.Add(SitePreview.SiteName(row[0]));
                    weights.Add(long.TryParse(row[1], out w) ? w : 1);
                }
            }
            var sl = SitePreview.Rank(sites, weights, 10);
            if (sl.Line.Length > 0) lines.Add("자주 간 사이트: " + sl.Line);
        }
    }
}
