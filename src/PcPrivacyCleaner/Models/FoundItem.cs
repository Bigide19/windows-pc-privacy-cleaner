using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PcPrivacyCleaner.Infrastructure;

namespace PcPrivacyCleaner.Models
{
    /// <summary>
    /// One row of the scan result.
    ///
    /// Privacy rule: Summary and Contents hold personal data (site names, file names).
    /// They are for the screen only and must never be written to the log file,
    /// because the user may send the log to someone when asking for help.
    /// </summary>
    public sealed class FoundItem : ObservableObject
    {
        public FoundItem(ItemGroup group, string name)
        {
            Group = group;
            Name = name;
            Paths = new List<string>();
            ContentsOf = new List<string>();
            Processes = new List<string>();
            Contents = new List<string>();
            Size = -1;
            Count = -1;
            Default = true;
        }

        public ItemGroup Group { get; private set; }
        public string Name { get; private set; }
        public string User { get; set; }

        /// <summary>Files or folders deleted entirely.</summary>
        public List<string> Paths { get; private set; }

        /// <summary>Folders kept in place; only what is inside them is deleted (Temp, Recent, Recycle Bin).</summary>
        public List<string> ContentsOf { get; private set; }

        /// <summary>
        /// Non-file work (certificate store, credentials, Wi-Fi, registry, clipboard). The scanner captures
        /// whatever the delete needs (thumbprint, credential target, Wi-Fi profile) in the closure.
        /// </summary>
        public Action Action { get; set; }

        /// <summary>Plain-language description of what Action does, shown under "지우는 범위".</summary>
        public string ActionText { get; set; }

        /// <summary>Checked when the list first appears.</summary>
        public bool Default { get; set; }

        /// <summary>Cannot be unchecked (personal NPKI certificate).</summary>
        public bool Forced { get; set; }

        /// <summary>Why this cannot be deleted right now. Non-empty means gray and uncheckable.</summary>
        public string BlockReason { get; set; }

        /// <summary>Process names (no ".exe") that lock these files while running.</summary>
        public List<string> Processes { get; private set; }

        public long Size { get; set; }
        public int Count { get; set; }

        /// <summary>Most recent write time among the files, used as a hint for "last used".</summary>
        public DateTime? Newest { get; set; }

        /// <summary>One-line summary for the list column (e.g. "사이트 23곳").</summary>
        public string Summary { get; set; }

        /// <summary>Lines under "찾은 내용".</summary>
        public List<string> Contents { get; private set; }

        /// <summary>Computed once after the scan (it touches the disk).</summary>
        public DeleteScope Scope { get; set; }

        // ---------- list view helpers ----------

        public GroupInfo GroupInfo { get { return GroupInfo.Get(Group); } }
        public int GroupOrder { get { return GroupInfo.Order; } }
        public string GroupTitle { get { return GroupInfo.Title; } }

        /// <summary>Insertion order, used as the secondary sort key so the list keeps scan order inside a group.</summary>
        public int Index { get; set; }

        public bool IsBlocked { get { return !string.IsNullOrEmpty(BlockReason); } }
        public bool CanToggle { get { return !Forced && !IsBlocked; } }

        /// <summary>
        /// Set for items that belong to another Windows account (admin mode scans every profile).
        /// The list then shows the account name in front of the item name.
        /// </summary>
        public string OtherUserName { get; set; }

        public string DisplayName
        {
            get
            {
                string name = Name;
                if (!string.IsNullOrEmpty(OtherUserName)) name = "[" + OtherUserName + "] " + name;
                if (IsBlocked) return "[지울 수 없음] " + name;
                if (Forced) return "[필수] " + name;
                return name;
            }
        }

        public string SummaryText
        {
            get
            {
                if (IsBlocked) return "지울 수 없음 (오른쪽 설명 참고)";
                if (!string.IsNullOrEmpty(Summary)) return Summary;
                if (Newest.HasValue) return "최근 수정 " + Format.Date(Newest);
                return "";
            }
        }

        public string ScopeShort { get { return Scope != null ? Scope.Short : ""; } }

        // ---------- detail panel helpers ----------

        public string MetaText
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(User)) parts.Add("사용자: " + User);
                string size = SizeText;
                if (size != "-") parts.Add("크기: " + size);
                if (Newest.HasValue) parts.Add("최근 수정: " + Format.Date(Newest));
                return string.Join("     ", parts);
            }
        }

        public bool HasContents { get { return Contents.Count > 0; } }
        public string Note { get { return GroupInfo.Note; } }
        public bool HasNote { get { return !string.IsNullOrEmpty(Note); } }
        public string SizeText { get { return Format.Size(Size, Count); } }

        private bool _isChecked;

        /// <summary>Forced items stay checked and blocked items stay unchecked whatever the UI asks.</summary>
        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (Forced) value = true;
                if (IsBlocked) value = false;
                Set(ref _isChecked, value);
            }
        }

        public void ResetCheck()
        {
            IsChecked = (Forced || Default) && !IsBlocked;
        }
    }

    /// <summary>
    /// What an item actually deletes, written for a non-developer. The short form goes to the list;
    /// the lines go to the detail panel. Exact paths come last in small gray text so the
    /// "does it wipe the whole AppData folder?" worry is answered in plain words first.
    /// </summary>
    public sealed class DeleteScope
    {
        public string Short { get; private set; }
        public List<DetailLine> Lines { get; private set; }

        /// <summary>Friendly names for the processes we may need to close.</summary>
        private static readonly Dictionary<string, string> ProgramNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", "Chrome" }, { "msedge", "Edge" }, { "whale", "웨일" }, { "brave", "Brave" }, { "firefox", "Firefox" },
            { "KakaoTalk", "카카오톡" }, { "Telegram", "텔레그램" }, { "LINE", "라인" }, { "Discord", "디스코드" },
        };

        public static string ProgramName(string process)
        {
            string name;
            return ProgramNames.TryGetValue(process, out name) ? name : process;
        }

        public static DeleteScope Build(FoundItem d)
        {
            var files = new List<string>();
            var folders = new List<string>();
            foreach (var p in d.Paths)
            {
                if (Directory.Exists(p)) folders.Add(p); else files.Add(p);
            }

            var shortParts = new List<string>();
            if (files.Count > 0) shortParts.Add(string.Format("파일 {0}개", files.Count));
            if (folders.Count > 0) shortParts.Add(string.Format("폴더 {0}개", folders.Count));
            if (d.ContentsOf.Count > 0) shortParts.Add("폴더 안 파일");
            if (shortParts.Count == 0 && d.Action != null) shortParts.Add("Windows 설정");

            var lines = new List<DetailLine>();
            if (files.Count > 0 && folders.Count == 0)
                lines.Add(new DetailLine(string.Format("이 항목에 해당하는 파일 {0}개만 지웁니다. 같은 폴더에 있는 다른 파일은 그대로 둡니다.", files.Count)));
            else if (folders.Count > 0 && files.Count == 0)
                lines.Add(new DetailLine(string.Format("아래 폴더 {0}개를 안에 든 것까지 모두 지웁니다. 그 밖의 폴더는 건드리지 않습니다.", folders.Count)));
            else if (files.Count > 0)
                lines.Add(new DetailLine(string.Format("이 항목에 해당하는 파일 {0}개와 폴더 {1}개만 지웁니다. 그 밖의 파일은 그대로 둡니다.", files.Count, folders.Count)));

            if (d.ContentsOf.Count > 0)
                lines.Add(new DetailLine("폴더는 남겨 두고 안에 든 내용만 비웁니다. 다른 프로그램이 사용 중인 파일은 건너뜁니다."));
            if (!string.IsNullOrEmpty(d.ActionText))
                lines.Add(new DetailLine(d.ActionText));
            if (d.Processes.Count > 0)
                lines.Add(new DetailLine(string.Format("지울 때 {0} 프로그램이 켜져 있으면 닫아도 되는지 먼저 물어봅니다.",
                    string.Join(", ", d.Processes.Select(ProgramName)))));

            if (files.Count + folders.Count + d.ContentsOf.Count > 0)
            {
                lines.Add(new DetailLine("지우는 위치:", DetailLineKind.Muted));
                foreach (var f in files) lines.Add(new DetailLine(f, DetailLineKind.Path));
                foreach (var f in folders) lines.Add(new DetailLine(f, DetailLineKind.Path));
                foreach (var f in d.ContentsOf) lines.Add(new DetailLine(f + " (안의 내용만)", DetailLineKind.Path));
            }

            return new DeleteScope { Short = string.Join(", ", shortParts), Lines = lines };
        }
    }
}
