using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services.Scan
{
    /// <summary>5. 개인 파일: files in Desktop/Documents/Downloads/Pictures whose names look like personal papers.</summary>
    public static class PersonalFileScanner
    {
        /// <summary>
        /// A file name containing one of these is probably a personal document. They overlap with work
        /// documents, so every hit starts unchecked.
        /// </summary>
        private static readonly string[] Keywords =
        {
            "주민등록", "등본", "초본", "가족관계", "기본증명", "혼인관계", "신분증", "운전면허", "여권",
            "통장", "계좌", "원천징수", "급여명세", "연말정산", "근로소득", "소득금액", "건강보험",
            "진단서", "소견서", "처방전", "검진", "이력서", "자기소개서", "경력증명", "재직증명",
            "졸업증명", "성적증명", "임대차", "전세", "월세", "보험증권", "청약", "대출", "카드명세",
            "출생", "임신", "출산", "산모", "육아휴직", "resume", "passport"
        };

        private static readonly Regex KeywordRegex =
            new Regex(string.Join("|", Keywords.Select(Regex.Escape)), RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const int HitLimit = 300;
        private const string KakaoFolderName = "카카오톡 받은 파일";

        /// <summary>
        /// The user's own folders. For the current user the real locations are used (they may have moved to OneDrive);
        /// other profiles use the default folder names.
        /// </summary>
        private static List<string> UserFolders(UserProfile pf)
        {
            var list = new List<string>();
            if (pf.IsCurrent)
            {
                list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                list.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                list.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                string dl = null;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"))
                    {
                        if (k != null) dl = k.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
                    }
                }
                catch { }
                list.Add(string.IsNullOrEmpty(dl) ? Path.Combine(pf.Path, "Downloads") : dl);
            }
            else
            {
                foreach (var n in new[] { "Desktop", "Documents", "Downloads", "Pictures" }) list.Add(Path.Combine(pf.Path, n));
            }
            return list.Where(p => !string.IsNullOrEmpty(p) && Files.DirExists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void Find(ScanSession s)
        {
            foreach (var pf in s.Profiles)
            {
                var roots = UserFolders(pf);

                // "카카오톡 받은 파일" is shown as one item for the whole folder (checked by default).
                var kakaoDirs = roots.Select(r => Path.Combine(r, KakaoFolderName)).Where(Files.DirExists)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var k in kakaoDirs) AddKakaoFolder(s, pf, k);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int hits = 0;
                foreach (var r in roots)
                {
                    s.Report("개인 파일 찾는 중: " + r);
                    foreach (var f in Files.Walk(r, s.Token))
                    {
                        if (!seen.Add(f.FullName)) continue;
                        if (kakaoDirs.Any(k => f.FullName.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase))) continue;

                        // Files copied from a Mac keep Hangul decomposed (NFD); compose them before matching.
                        string name = f.Name.Normalize(NormalizationForm.FormC);
                        var m = KeywordRegex.Match(name);
                        if (!m.Success) continue;

                        if (++hits > HitLimit) break;
                        var item = new FoundItem(ItemGroup.Files, name)
                        {
                            Default = false,
                            Size = SafeLength(f),
                            Count = 1,
                            Summary = string.Format("'{0}' 포함", m.Value)
                        };
                        item.Paths.Add(f.FullName);
                        item.Contents.Add(string.Format("찾은 이유: 파일 이름에 '{0}' 단어가 들어 있습니다.", m.Value));
                        item.Contents.Add("위치: " + f.DirectoryName);
                        item.Contents.Add("수정한 날짜: " + f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                        s.Add(item, pf);
                    }
                    if (hits > HitLimit)
                    {
                        AppLog.Step("개인 파일 후보가 {0}개를 넘어서 나머지는 표시하지 않았습니다.", HitLimit);
                        AppLog.Detail("  해당 사용자: " + pf.Label);
                        break;
                    }
                }
            }
        }

        private static void AddKakaoFolder(ScanSession s, UserProfile pf, string dir)
        {
            var item = new FoundItem(ItemGroup.Files, KakaoFolderName + " (폴더 전체)");
            item.Paths.Add(dir);
            try
            {
                var all = Files.Walk(dir, s.Token).ToList();
                item.Summary = string.Format("파일 {0}개", all.Count);
                var types = all.GroupBy(f => f.Extension.ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(6)
                    .Select(g => string.Format("{0} {1}개", g.Key.Length > 0 ? g.Key : "(확장자 없음)", g.Count())).ToList();
                if (types.Count > 0) item.Contents.Add("파일 종류: " + string.Join(", ", types));
                var recent = all.OrderByDescending(f => f.LastWriteTime).Take(10).ToList();
                if (recent.Count > 0)
                {
                    item.Contents.Add("최근에 받은 파일:");
                    foreach (var f in recent) item.Contents.Add(string.Format("  {0}  {1}", f.LastWriteTime.ToString("yyyy-MM-dd"), f.Name));
                }
            }
            catch
            {
                item.Contents.Add(dir);
            }
            s.Add(item, pf);
        }

        private static long SafeLength(FileInfo f)
        {
            try { return f.Length; } catch { return 0; }
        }
    }
}
