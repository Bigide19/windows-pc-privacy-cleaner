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
            string mode = dryRun ? "연습 모드" : "삭제";
            AppLog.Write("===== {0} 시작: {1}개 항목 =====", mode, items.Count);

            for (int i = 0; i < items.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    AppLog.Write("사용자가 중지했습니다. 남은 {0}개 항목은 처리하지 않았습니다.", items.Count - i);
                    break;
                }
                var d = items[i];
                progress.Report(new ScanProgress(i, items.Count, d.Name));
                // Item name and user are logged on purpose (needed to troubleshoot), but never Summary/Contents.
                string label = string.Format("{0} > {1} [{2}]", d.GroupTitle, d.Name, d.User);

                if (dryRun)
                {
                    AppLog.Write("[연습] " + label);
                    foreach (var p in d.Paths) AppLog.Write("    " + p);
                    foreach (var p in d.ContentsOf) AppLog.Write("    " + p + "\\* (안의 내용)");
                    if (d.Action != null) AppLog.Write("    (파일이 아닌 설정 항목)");
                    result.Ok++;
                    continue;
                }

                try
                {
                    int failed = RemoveTargets(d);
                    if (d.Action != null) d.Action(d);
                    if (failed > 0) { result.Failed++; AppLog.Write("일부 실패: " + label); }
                    else { result.Ok++; AppLog.Write("완료: " + label); }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    AppLog.Write("실패: {0} ({1})", label, ex.Message);
                }
            }
            AppLog.Write("===== {0} 끝: 성공 {1}, 실패 {2} =====", mode, result.Ok, result.Failed);
            return result;
        }

        /// <summary>
        /// Paths are removed entirely; ContentsOf folders are emptied but kept.
        /// Returns how many Paths failed. In-use files inside ContentsOf are expected (Temp) and only logged.
        /// </summary>
        private static int RemoveTargets(FoundItem d)
        {
            int fail = 0;
            foreach (var p in d.Paths)
            {
                try { DeleteEntry(p); }
                catch (Exception ex)
                {
                    fail++;
                    AppLog.Write("  실패: {0} ({1})", p, ex.Message);
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
                if (skipped > 0) AppLog.Write("  사용 중이라 건너뜀: {0} 안의 {1}개", dir, skipped);
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
