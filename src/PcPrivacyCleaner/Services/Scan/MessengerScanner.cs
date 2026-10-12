using System.Collections.Generic;
using System.IO;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services.Scan
{
    /// <summary>4. 메신저: login data folders of desktop messengers.</summary>
    public static class MessengerScanner
    {
        /// <summary>
        /// GuardExe/GuardSub: when the program itself is installed inside the data folder (per-user install),
        /// only the GuardSub subfolder is deleted instead of the whole folder, so the program keeps working.
        /// </summary>
        private sealed class Messenger
        {
            public string Name;
            public string Process;
            public string Detail;
            public string[] Rels;
            public string GuardExe;
            public string GuardSub;
        }

        private static readonly Messenger[] Messengers =
        {
            new Messenger { Name = "카카오톡", Process = "KakaoTalk", Detail = "로그인 정보, 대화 내용, 받은 파일 사본",
                Rels = new[] { @"AppData\Local\Kakao\KakaoTalk" }, GuardExe = "KakaoTalk.exe", GuardSub = "users" },
            new Messenger { Name = "텔레그램", Process = "Telegram", Detail = "로그인 정보, 최근 대화 내용",
                Rels = new[] { @"AppData\Roaming\Telegram Desktop\tdata" } },
            new Messenger { Name = "라인", Process = "LINE", Detail = "로그인 정보, 최근 대화 내용",
                Rels = new[] { @"AppData\Local\LINE\Data" } },
            new Messenger { Name = "디스코드", Process = "Discord", Detail = "로그인 정보",
                Rels = new[] { @"AppData\Roaming\discord\Local Storage", @"AppData\Roaming\discord\Session Storage",
                               @"AppData\Roaming\discord\Network\Cookies", @"AppData\Roaming\discord\Cookies" } },
        };

        public static void Find(ScanSession s)
        {
            foreach (var pf in s.Profiles)
            {
                foreach (var m in Messengers)
                {
                    s.Token.ThrowIfCancellationRequested();
                    var paths = new List<string>();
                    foreach (var rel in m.Rels)
                    {
                        string p = Path.Combine(pf.Path, rel);
                        if (!File.Exists(p) && !Directory.Exists(p)) continue;
                        if (m.GuardExe != null && File.Exists(Path.Combine(p, m.GuardExe)))
                        {
                            p = Path.Combine(p, m.GuardSub);
                            if (!Directory.Exists(p)) continue;
                        }
                        paths.Add(p);
                    }
                    if (paths.Count == 0) continue;

                    var item = new FoundItem(ItemGroup.Messenger, m.Name);
                    item.Paths.AddRange(paths);
                    item.Processes.Add(m.Process);
                    // Messenger data is encrypted, so only its kind is described. "최근 수정" hints at when it was last used.
                    item.Contents.Add("저장된 정보: " + m.Detail);
                    item.Contents.Add("대화 내용은 암호화되어 있어서 이 도구로는 열어 볼 수 없습니다.");
                    item.Contents.Add("지우면 이 PC에서 로그아웃됩니다. 휴대폰의 대화 내용은 그대로 남습니다.");
                    s.Add(item, pf);
                }
            }
        }
    }
}
