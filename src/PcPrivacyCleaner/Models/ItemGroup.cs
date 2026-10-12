using System.Collections.Generic;
using System.Windows.Media;

namespace PcPrivacyCleaner.Models
{
    public enum ItemGroup
    {
        Cert,
        CertStore,
        Browser,
        Messenger,
        Files,
        History,
        Credential,
        Wifi
    }

    /// <summary>
    /// Group title shown in the list header, and the caution note shown under "주의" in the detail panel.
    /// The list title stays short; the long explanation lives in Note.
    /// Accent is the color bar on the group header and in the detail panel, so a long list
    /// still shows which group a row belongs to. Colors are iOS system colors; red is left out
    /// on purpose because it already means "delete / danger" elsewhere in the window.
    /// </summary>
    public sealed class GroupInfo
    {
        public ItemGroup Group { get; private set; }
        public int Order { get; private set; }
        public string Title { get; private set; }
        public string Note { get; private set; }
        public Brush Accent { get; private set; }

        // Related groups share one color: the two certificate groups, and the two account groups
        // (saved logins and Wi-Fi), so the list reads as six categories rather than eight.
        private const string Indigo = "#5856D6";
        private const string Blue = "#007AFF";
        private const string Green = "#34C759";
        private const string Pink = "#FF2D55";
        private const string Orange = "#FF9500";
        private const string Teal = "#30B0C7";

        private static readonly Dictionary<ItemGroup, GroupInfo> All = new Dictionary<ItemGroup, GroupInfo>
        {
            { ItemGroup.Cert, New(ItemGroup.Cert, 0, Indigo, "공동인증서",
                "휴대폰, USB, 집 PC에 사본이 없다면 지우기 전에 은행 사이트의 인증서 내보내기로 옮겨 두세요.") },
            { ItemGroup.CertStore, New(ItemGroup.CertStore, 1, Indigo, "Windows에 저장된 인증서",
                "회사 VPN, 사내 Wi-Fi 인증서가 섞여 있을 수 있습니다. 지우면 복귀 후 회사망에 접속하지 못할 수 있으니 개인 것만 체크하세요.") },
            { ItemGroup.Browser, New(ItemGroup.Browser, 2, Blue, "브라우저",
                "쿠키를 지우면 업무 사이트도 로그아웃됩니다. 북마크는 지우지 않습니다.") },
            { ItemGroup.Messenger, New(ItemGroup.Messenger, 3, Green, "메신저",
                "로그아웃 상태로 만들려면 데이터 폴더 전체를 지워야 합니다. 프로그램 자체는 지우지 않습니다.") },
            { ItemGroup.Files, New(ItemGroup.Files, 4, Pink, "개인 파일",
                "파일 이름으로 찾은 것이라 업무 파일이 섞일 수 있습니다. 하나씩 보고 체크하세요.") },
            { ItemGroup.History, New(ItemGroup.History, 5, Orange, "사용 기록, 휴지통, 임시 파일", "") },
            { ItemGroup.Credential, New(ItemGroup.Credential, 6, Teal, "Windows에 저장된 로그인 정보",
                "업무용 로그인(공유 폴더, 원격 데스크톱 등)과 섞여 있습니다. 개인 것만 체크하세요.") },
            { ItemGroup.Wifi, New(ItemGroup.Wifi, 7, Teal, "저장된 Wi-Fi",
                "회사 Wi-Fi를 지우면 복귀 후 비밀번호를 다시 입력해야 합니다.") },
        };

        private static GroupInfo New(ItemGroup g, int order, string color, string title, string note)
        {
            // Frozen so the brush can be shared across threads (items are built on the scan thread).
            var accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            accent.Freeze();
            return new GroupInfo { Group = g, Order = order, Title = title, Note = note, Accent = accent };
        }

        public static GroupInfo Get(ItemGroup group) { return All[group]; }
    }
}
