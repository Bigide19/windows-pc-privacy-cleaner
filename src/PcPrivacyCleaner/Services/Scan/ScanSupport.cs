using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services.Scan
{
    /// <summary>A Windows user profile to scan.</summary>
    public sealed class UserProfile
    {
        public string Path { get; set; }
        public string Sid { get; set; }
        public string Name { get; set; }
        public bool IsCurrent { get; set; }

        public string Label { get { return IsCurrent ? Name + " (지금 계정)" : Name; } }
    }

    /// <summary>State shared by the scan steps: who to scan, where results go, progress and cancellation.</summary>
    public sealed class ScanSession
    {
        private readonly IProgress<ScanProgress> _progress;
        private int _step;
        private int _stepCount;

        public ScanSession(RunContext ctx, IProgress<ScanProgress> progress, CancellationToken token)
        {
            Context = ctx;
            _progress = progress;
            Token = token;
            Items = new List<FoundItem>();
        }

        public RunContext Context { get; private set; }
        public CancellationToken Token { get; private set; }
        public List<FoundItem> Items { get; private set; }
        public List<UserProfile> Profiles { get; set; }
        public UserProfile Current { get { return Profiles.First(p => p.IsCurrent); } }

        public void BeginStep(int step, int stepCount, string title)
        {
            _step = step;
            _stepCount = stepCount;
            Report(title + " 검사 중...");
        }

        /// <summary>Sub-step text inside the current step, e.g. "Chrome 방문 기록 확인 중...".</summary>
        public void Report(string text)
        {
            _progress.Report(new ScanProgress(_step, _stepCount, text));
        }

        /// <summary>
        /// Registers a result. Size, count and newest time are measured from Paths/ContentsOf unless the
        /// scanner already set Size. Items of other accounts get the account name shown in the list.
        /// </summary>
        public FoundItem Add(FoundItem item, UserProfile owner = null)
        {
            if (item.Size < 0)
            {
                var st = PathStats.Measure(item.Paths.Concat(item.ContentsOf), Token);
                item.Size = st.Size;
                if (item.Count < 0) item.Count = st.Count;
                item.Newest = st.Newest;
            }
            if (owner != null)
            {
                item.User = owner.Label;
                if (!owner.IsCurrent) item.OtherUserName = owner.Name;
            }
            Items.Add(item);
            return item;
        }

        /// <summary>Admin: every real profile on the PC. Otherwise: only the current user.</summary>
        public static List<UserProfile> LoadProfiles(RunContext ctx)
        {
            var list = new List<UserProfile>();
            if (ctx.IsAdmin)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT LocalPath, SID, Special FROM Win32_UserProfile"))
                    {
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            var special = mo["Special"] as bool?;
                            var path = mo["LocalPath"] as string;
                            var sid = mo["SID"] as string;
                            if (special == true || string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                            list.Add(new UserProfile
                            {
                                Path = path,
                                Sid = sid,
                                Name = System.IO.Path.GetFileName(path),
                                IsCurrent = string.Equals(sid, ctx.Sid, StringComparison.OrdinalIgnoreCase)
                            });
                        }
                    }
                }
                catch
                {
                    list.Clear();
                }
            }
            if (!list.Any(p => p.IsCurrent))
            {
                list.Add(new UserProfile
                {
                    Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Sid = ctx.Sid,
                    Name = Environment.UserName,
                    IsCurrent = true
                });
            }
            return list;
        }
    }

    /// <summary>Total size, file count and newest write time of a set of files/folders.</summary>
    public sealed class PathStats
    {
        public long Size { get; private set; }
        public int Count { get; private set; }
        public DateTime? Newest { get; private set; }

        public static PathStats Measure(IEnumerable<string> paths, CancellationToken token)
        {
            var st = new PathStats();
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (File.Exists(p)) st.AddFile(new FileInfo(p));
                else if (Directory.Exists(p))
                {
                    foreach (var f in Files.Walk(p, token)) st.AddFile(f);
                }
            }
            return st;
        }

        private void AddFile(FileInfo f)
        {
            try
            {
                Size += f.Length;
                Count++;
                var t = f.LastWriteTime;
                if (!Newest.HasValue || t > Newest.Value) Newest = t;
            }
            catch
            {
                // The file vanished or is not readable; skip it.
            }
        }
    }

    public static class Files
    {
        /// <summary>
        /// Recursive file listing that survives access-denied folders and does not follow junctions/symlinks
        /// (Directory.EnumerateFiles with AllDirectories stops at the first error).
        /// </summary>
        public static IEnumerable<FileInfo> Walk(string root, CancellationToken token)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                if (token.IsCancellationRequested) yield break;
                var dir = stack.Pop();
                FileInfo[] files;
                DirectoryInfo[] subs;
                try
                {
                    files = dir.GetFiles();
                    subs = dir.GetDirectories();
                }
                catch
                {
                    continue;
                }
                foreach (var f in files) yield return f;
                foreach (var s in subs)
                {
                    if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s);
                }
            }
        }

        /// <summary>Directory.Exists that never throws.</summary>
        public static bool DirExists(string path)
        {
            try { return Directory.Exists(path); } catch { return false; }
        }
    }
}
