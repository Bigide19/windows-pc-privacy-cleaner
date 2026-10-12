using PcPrivacyCleaner.Interop;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services.Scan
{
    /// <summary>7. 저장된 로그인 정보 (Windows Credential Manager) and 8. 저장된 Wi-Fi. Both start unchecked (mixed with work entries).</summary>
    public static class AccountScanner
    {
        public static void FindCredentials(ScanSession s)
        {
            foreach (var c in NativeCredentials.List())
            {
                s.Token.ThrowIfCancellationRequested();
                // Generic targets look like "LegacyGeneric:target=xxx"; show only the part after "target=".
                string display = c.Target;
                int at = display.IndexOf("target=", System.StringComparison.OrdinalIgnoreCase);
                if (at >= 0) display = display.Substring(at + 7);

                var item = new FoundItem(ItemGroup.Credential, display)
                {
                    Default = false,
                    Size = 0,
                    Count = 1,
                    Summary = c.UserName,
                    ActionText = "Windows에 저장된 이 로그인 정보만 지웁니다. 다음에 접속할 때 아이디와 비밀번호를 다시 입력해야 합니다.",
                    Action = () => NativeCredentials.Delete(c.Target, c.Type)
                };
                item.Contents.Add("접속 대상: " + display);
                if (!string.IsNullOrEmpty(c.UserName)) item.Contents.Add("계정: " + c.UserName);
                item.Contents.Add("공유 폴더나 원격 접속 같은 곳에 들어갈 때 자동으로 입력되는 아이디와 비밀번호입니다. 비밀번호는 표시하지 않습니다.");
                s.Add(item, s.Current);
            }
        }

        public static void FindWifi(ScanSession s)
        {
            foreach (var p in WlanApi.ListProfiles())
            {
                s.Token.ThrowIfCancellationRequested();
                var item = new FoundItem(ItemGroup.Wifi, p.Name)
                {
                    Default = false,
                    Size = 0,
                    Count = 1,
                    User = "PC 공통",
                    ActionText = "이 PC가 기억하는 이 Wi-Fi와 비밀번호를 지웁니다. 다음에 접속할 때 비밀번호를 다시 입력해야 합니다.",
                    Action = () => WlanApi.DeleteProfile(p)
                };
                item.Contents.Add("네트워크 이름: " + p.Name);
                item.Contents.Add("이 PC가 기억하는 Wi-Fi입니다. 근처에 가면 저장된 비밀번호로 자동 접속합니다.");
                if (!s.Context.IsAdmin) item.Contents.Add("관리자 권한이 없으면 지우지 못할 수 있습니다.");
                s.Add(item);
            }
        }
    }
}
