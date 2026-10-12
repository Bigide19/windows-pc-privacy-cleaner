using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services
{
    public sealed class CleanResult
    {
        public int Ok { get; set; }
        public int Failed { get; set; }
        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// Deletes the checked items. Runs on a background thread.
    /// The "close running programs?" prompt is shown by the UI before this starts.
    /// </summary>
    public static class Cleaner
    {
        public static CleanResult Clean(IList<FoundItem> items, bool dryRun, IProgress<ScanProgress> progress, CancellationToken token)
        {
            var result = new CleanResult();
            // Report-safe tallies: group titles and exception descriptions are code-defined text (see AppLog.Step).
            var byGroup = new Dictionary<string, int[]>();          // group title -> { ok, failed }
            var failures = new Dictionary<string, int>();           // AppLog.Describe(ex) -> count
            AppLog.Step(dryRun ? "===== 연습 모드 시작: {0}개 항목 =====" : "===== 삭제 시작: {0}개 항목 =====", items.Count);

            for (int i = 0; i < items.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    AppLog.Step("사용자가 중지했습니다. 남은 {0}개 항목은 처리하지 않았습니다.", items.Count - i);
                    break;
                }
                var d = items[i];
                progress.Report(new ScanProgress(i, items.Count, d.Name));
                // Item name and user are logged on purpose (needed to troubleshoot), but as Detail only and never
                // Summary/Contents. The report gets the per-group counts below instead.
                string label = string.Format("{0} > {1} [{2}]", d.GroupTitle, d.Name, d.User);
                int[] tally;
                if (!byGroup.TryGetValue(d.GroupTitle, out tally)) byGroup[d.GroupTitle] = tally = new int[2];

                if (dryRun)
                {
                    AppLog.Detail("[연습] " + label);
                    foreach (var p in d.Paths) AppLog.Detail("    " + p);
                    foreach (var p in d.ContentsOf) AppLog.Detail("    " + p + "\\* (안의 내용)");
                    if (d.Action != null) AppLog.Detail("    (파일이 아닌 설정 항목)");
                    result.Ok++;
                    tally[0]++;
                    continue;
                }

                try
                {
                    int failed = RemoveTargets(d, failures);
                    if (d.Action != null) d.Action();
                    if (failed > 0) { result.Failed++; tally[1]++; AppLog.Detail("일부 실패: " + label); }
                    else { result.Ok++; tally[0]++; AppLog.Detail("완료: " + label); }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    tally[1]++;
                    Count(failures, ex);
                    AppLog.Detail("실패: {0} ({1})", label, ex.Message);
                }
            }

            foreach (var g in byGroup)
                AppLog.Step(string.Format("{0}: 성공 {1}, 실패 {2}", g.Key, g.Value[0], g.Value[1]));
            foreach (var f in failures)
                AppLog.Step(string.Format("실패 이유: {0} x{1}", f.Key, f.Value));
            AppLog.Step(dryRun ? "===== 연습 모드 끝: 성공 {0}, 실패 {1} =====" : "===== 삭제 끝: 성공 {0}, 실패 {1} =====",
                result.Ok, result.Failed);
            return result;
        }

        private static void Count(Dictionary<string, int> failures, Exception ex)
        {
            string key = AppLog.Describe(ex);
            int n;
            failures.TryGetValue(key, out n);
            failures[key] = n + 1;
        }

        /// <summary>
        /// Paths are removed entirely; ContentsOf folders are emptied but kept.
        /// Returns how many Paths failed. In-use files inside ContentsOf are expected (Temp) and only logged.
        /// </summary>
        private static int RemoveTargets(FoundItem d, Dictionary<string, int> failures)
        {
            int fail = 0;
            foreach (var p in d.Paths)
            {
                try { DeleteEntry(p); }
                catch (Exception ex)
                {
                    fail++;
                    Count(failures, ex);
                    AppLog.Detail("  실패: {0} ({1})", p, ex.Message);
                }
            }
            foreach (var dir in d.ContentsOf)
            {
                if (!Directory.Exists(dir)) continue;
                int skipped = 0;
                IEnumerable<string> children;
                try { children = Directory.EnumerateFileSystemEntries(dir); }
                catch { continue; }
                foreach (var child in children)
                {
                    if (string.Equals(Path.GetFileName(child), "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    try { DeleteEntry(child); }
                    catch { skipped++; }
                }
                if (skipped > 0) AppLog.Detail("  사용 중이라 건너뜀: {0} 안의 {1}개", dir, skipped);
            }
            return fail;
        }

        /// <summary>Deletes a file or a whole folder. Missing paths are fine.</summary>
        public static void DeleteEntry(string path)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);   // read-only files would throw otherwise
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                DeleteTree(new DirectoryInfo(path));
            }
        }

        /// <summary>
        /// Recursive delete that clears read-only attributes first.
        /// Junctions and symlinks are removed as links and never followed, so a link inside
        /// a profile folder can not lead the delete somewhere else on the disk.
        /// </summary>
        private static void DeleteTree(DirectoryInfo dir)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                dir.Delete(false);
                return;
            }
            foreach (var f in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                f.Attributes = FileAttributes.Normal;
                f.Delete();
            }
            foreach (var sub in dir.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                DeleteTree(sub);
            }
            dir.Attributes = FileAttributes.Directory;
            dir.Delete(false);
        }
    }
}
