using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services.Scan
{
    /// <summary>6. 사용 기록, 휴지통, 임시 파일, 클립보드.</summary>
    public static class HistoryScanner
    {
        /// <summary>
        /// Run box (Win+R), Explorer address bar and search, recent documents, Open/Save dialog history.
        /// Relative to HKEY_CURRENT_USER, so only the current user can be cleaned.
        /// </summary>
        private static readonly string[] RegHistoryKeys =
        {
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU"
        };

        public static void Find(ScanSession s)
        {
            foreach (var pf in s.Profiles)
            {
                s.Token.ThrowIfCancellationRequested();
                AddRecent(s, pf);
                AddTemp(s, pf);
                AddRecycleBin(s, pf);
                if (!pf.IsCurrent) continue;
                // Current user only from here on.
                AddRegistryHistory(s, pf);
                AddClipboard(s, pf);
            }
        }

        /// <summary>Recent items and jump lists are plain files, so other users' can be cleaned too.</summary>
        private static void AddRecent(ScanSession s, UserProfile pf)
        {
            string recent = Path.Combine(pf.Path, @"AppData\Roaming\Microsoft\Windows\Recent");
            if (!Files.DirExists(recent)) return;
            var item = new FoundItem(ItemGroup.History, "최근에 연 파일 목록");
            item.ContentsOf.Add(recent);
            try
            {
                // Each shortcut (.lnk) is named after a recently opened file or folder.
                var lnks = new DirectoryInfo(recent).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).ToList();
                item.Summary = lnks.Count + "개";
                item.Contents.Add(string.Format("최근에 연 파일과 폴더 {0}개", lnks.Count));
                foreach (var l in lnks.Take(15))
                    item.Contents.Add(string.Format("  {0}  {1}", l.LastWriteTime.ToString("yyyy-MM-dd"), Path.GetFileNameWithoutExtension(l.Name)));
                item.Contents.Add("작업 표시줄에서 프로그램 아이콘을 오른쪽 클릭하면 나오는 최근 목록도 함께 지웁니다.");
            }
            catch
            {
                item.Contents.Add(recent);
            }
            s.Add(item, pf);
        }

        private static void AddTemp(ScanSession s, UserProfile pf)
        {
            string temp = Path.Combine(pf.Path, @"AppData\Local\Temp");
            if (!Files.DirExists(temp)) return;
            var item = new FoundItem(ItemGroup.History, "임시 파일");
            item.ContentsOf.Add(temp);
            item.Contents.Add("프로그램이 작업 중에 만든 임시 파일입니다. 열어 본 첨부 파일의 사본이 남아 있기도 합니다.");
            s.Add(item, pf);
        }

        /// <summary>The Recycle Bin lives in <drive>\$Recycle.Bin\<user SID> on every fixed drive.</summary>
        private static void AddRecycleBin(ScanSession s, UserProfile pf)
        {
            var bins = new List<string>();
            foreach (var drv in DriveInfo.GetDrives())
            {
                if (drv.DriveType != DriveType.Fixed || !drv.IsReady) continue;
                string bin = Path.Combine(drv.RootDirectory.FullName, "$Recycle.Bin", pf.Sid ?? "");
                if (Files.DirExists(bin)) bins.Add(bin);
            }
            if (bins.Count == 0) return;
            var item = new FoundItem(ItemGroup.History, "휴지통");
            item.ContentsOf.AddRange(bins);
            try
            {
                int total;
                var names = RecycleBinNames(bins, 15, out total);
                item.Summary = total + "개";
                item.Contents.Add(string.Format("휴지통에 있는 항목 {0}개", total));
                if (names.Count > 0)
                {
                    item.Contents.Add("최근에 버린 파일:");
                    item.Contents.AddRange(names);
                }
            }
            catch
            {
                item.Contents.Add(string.Join(", ", bins));
            }
            s.Add(item, pf);
        }

        /// <summary>
        /// Each deleted item has a "$I..." file holding its original path. Newest first.
        ///   version 1 (Vista~8): fixed 260-character UTF-16 path at offset 24
        ///   version 2 (10~):     4-byte character count at offset 24, then the UTF-16 path
        /// </summary>
        private static List<string> RecycleBinNames(List<string> bins, int max, out int total)
        {
            var infos = new List<FileInfo>();
            foreach (var b in bins)
            {
                try { infos.AddRange(new DirectoryInfo(b).GetFiles("$I*")); } catch { }
            }
            total = infos.Count;
            var names = new List<string>();
            foreach (var f in infos.OrderByDescending(x => x.LastWriteTime).Take(max))
            {
                try
                {
                    var bytes = File.ReadAllBytes(f.FullName);
                    long ver = BitConverter.ToInt64(bytes, 0);
                    string path = "";
                    if (ver == 2)
                    {
                        int len = BitConverter.ToInt32(bytes, 24);
                        path = Encoding.Unicode.GetString(bytes, 28, Math.Min((len - 1) * 2, bytes.Length - 28));
                    }
                    else if (ver == 1)
                    {
                        path = Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24));
                    }
                    path = path.Split('\0')[0];
                    if (path.Length > 0) names.Add(string.Format("  {0}  {1}", f.LastWriteTime.ToString("yyyy-MM-dd"), Path.GetFileName(path)));
                }
                catch { }
            }
            return names;
        }

        private static void AddRegistryHistory(ScanSession s, UserProfile pf)
        {
            int count = 0;
            foreach (var k in RegHistoryKeys)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(k))
                {
                    if (key != null) count += key.ValueCount + key.SubKeyCount;
                }
            }
            if (count == 0) return;
            var item = new FoundItem(ItemGroup.History, "실행 창, 탐색기 검색 기록")
            {
                // Not files: the size column shows "-" and the count goes to the summary instead.
                Size = 0,
                Count = 0,
                Summary = count + "개",
                ActionText = "위 사용 기록을 지웁니다. 로그아웃했다가 다시 로그인하면 완전히 사라집니다.",
                Action = _ =>
                {
                    foreach (var k in RegHistoryKeys) Registry.CurrentUser.DeleteSubKeyTree(k, false);
                }
            };
            try { item.Contents.AddRange(RegistryHistoryLines(10)); }
            catch { item.Contents.Add("Windows 사용 기록"); }
            s.Add(item, pf);
        }

        /// <summary>
        /// Human-readable values only, at most max per key.
        ///   RunMRU: typed commands (a trailing "\1" is removed)  TypedPaths: strings
        ///   WordWheelQuery, RecentDocs: UTF-16 binary values; the text is the part before the first NUL.
        /// </summary>
        private static List<string> RegistryHistoryLines(int max)
        {
            var sections = new[]
            {
                new[] { RegHistoryKeys[0], "실행 창(Win+R)에 입력한 명령" },
                new[] { RegHistoryKeys[1], "탐색기 주소창에 입력한 경로" },
                new[] { RegHistoryKeys[2], "탐색기에서 검색한 단어" },
                new[] { RegHistoryKeys[3], "최근 연 파일" },
            };
            var lines = new List<string>();
            foreach (var sec in sections)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(sec[0]))
                {
                    if (key == null) continue;
                    var values = new List<string>();
                    foreach (var vn in key.GetValueNames())
                    {
                        if (vn.Length == 0 || vn.StartsWith("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                        object v = key.GetValue(vn);
                        string text = v is byte[] ? Encoding.Unicode.GetString((byte[])v).Split('\0')[0] : Convert.ToString(v);
                        if (text.EndsWith("\\1")) text = text.Substring(0, text.Length - 2);
                        text = text.Trim();
                        if (text.Length > 0) values.Add(text);
                    }
                    if (values.Count == 0) continue;
                    string line = string.Format("{0} {1}개: {2}", sec[1], values.Count, string.Join(", ", values.Take(max)));
                    if (values.Count > max) line += " ...";
                    lines.Add(line);
                }
            }
            lines.Add("파일 열기, 저장 창에서 마지막으로 연 폴더 기록도 함께 지웁니다.");
            return lines;
        }

        /// <summary>
        /// Only the kind of clipboard content is shown, never the content. The clipboard is an STA-only API,
        /// so it is touched on the UI thread through the dispatcher.
        /// </summary>
        private static void AddClipboard(ScanSession s, UserProfile pf)
        {
            string summary = "";
            try
            {
                summary = Application.Current.Dispatcher.Invoke(() =>
                {
                    if (Clipboard.ContainsText()) return string.Format("텍스트 {0}자", Clipboard.GetText().Length);
                    if (Clipboard.ContainsImage()) return "이미지";
                    if (Clipboard.ContainsFileDropList()) return string.Format("파일 {0}개", Clipboard.GetFileDropList().Count);
                    return "비어 있음";
                });
            }
            catch { }

            var item = new FoundItem(ItemGroup.History, "복사해 둔 내용 (클립보드)")
            {
                Size = 0,
                Count = 0,
                Summary = summary,
                ActionText = "복사해 둔 내용과 Win+V 기록을 지웁니다.",
                Action = _ => Application.Current.Dispatcher.Invoke(ClearClipboard)
            };
            if (summary.Length > 0) item.Contents.Add(string.Format("지금 복사해 둔 내용: {0} (내용은 화면에 표시하지 않습니다)", summary));
            item.Contents.Add("Win+V를 누르면 나오는, 예전에 복사한 내용도 함께 지웁니다.");
            s.Add(item, pf);
        }

        /// <summary>
        /// Clipboard history (Win+V) is a WinRT API. .NET Framework can load WinRT types by this
        /// assembly-qualified name without any extra reference, so it is called through reflection.
        /// </summary>
        private static void ClearClipboard()
        {
            Clipboard.Clear();
            try
            {
                var t = Type.GetType("Windows.ApplicationModel.DataTransfer.Clipboard, Windows.ApplicationModel.DataTransfer, ContentType=WindowsRuntime");
                var m = t == null ? null : t.GetMethod("ClearHistory", BindingFlags.Public | BindingFlags.Static);
                if (m == null) throw new InvalidOperationException("ClearHistory 없음");
                m.Invoke(null, null);
            }
            catch
            {
                AppLog.Write("  클립보드 기록(Win+V)은 지우지 못했습니다. 설정 > 시스템 > 클립보드에서 [지우기]를 눌러 주세요.");
            }
        }
    }
}
