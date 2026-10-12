#if DEBUG
using System;
using System.Collections.Generic;
using System.Threading;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services
{
    /// <summary>
    /// Fake scan result for UI work (run the Debug build with --mock).
    /// Debug-only so that the release exe never carries sample data.
    /// </summary>
    public sealed class MockScanner : IScanner
    {
        public List<FoundItem> Scan(IProgress<ScanProgress> progress, CancellationToken token)
        {
            var steps = Scanner.StepNames;
            for (int i = 0; i < steps.Length; i++)
            {
                if (token.IsCancellationRequested) break;
                progress.Report(new ScanProgress(i, steps.Length, steps[i] + " 검사 중..."));
                Thread.Sleep(350);
            }

            const string U = @"C:\Users\sample";
            var list = new List<FoundItem>();

            var c1 = new FoundItem(ItemGroup.Cert, "홍길동 (개인)") { User = "sample (지금 계정)", Forced = true, Summary = "만료 2027-03-01" };
            c1.Paths.Add(U + @"\AppData\LocalLow\NPKI\yessign\USER\cn=홍길동()0011");
            c1.Contents.AddRange(new[] { "소유자: 홍길동", "종류: 개인 인증서 (은행, 정부24, 홈택스 등에 로그인할 때 사용)", "발급 기관: yessign", "유효 기간: 만료 2027-03-01" });
            list.Add(c1);

            var c2 = new FoundItem(ItemGroup.Cert, "[법인] 샘플상사") { User = "sample (지금 계정)", Default = false, Summary = "만료 2026-12-31" };
            c2.Paths.Add(U + @"\AppData\LocalLow\NPKI\yessign\USER\cn=샘플상사");
            c2.Contents.AddRange(new[] { "소유자: 샘플상사", "종류: 법인 인증서 (회사 업무용일 수 있음)" });
            list.Add(c2);

            var b1 = new FoundItem(ItemGroup.Browser, "Chrome (Default) - 저장된 비밀번호") { User = "sample (지금 계정)", Summary = "사이트 23곳" };
            b1.Paths.Add(U + @"\AppData\Local\Google\Chrome\User Data\Default\Login Data");
            b1.Paths.Add(U + @"\AppData\Local\Google\Chrome\User Data\Default\Login Data-journal");
            b1.Processes.Add("chrome");
            b1.Contents.AddRange(new[] { "비밀번호가 저장된 사이트 23곳, 계정 25개", "naver.com, google.com, coupang.com, kakao.com 외 19곳", "비밀번호 값은 읽지도, 화면에 표시하지도 않습니다." });
            list.Add(b1);

            var b2 = new FoundItem(ItemGroup.Browser, "Chrome (Default) - 로그인 상태 (쿠키 등)") { User = "sample (지금 계정)", Summary = "사이트 140곳" };
            b2.Paths.Add(U + @"\AppData\Local\Google\Chrome\User Data\Default\Network\Cookies");
            b2.Paths.Add(U + @"\AppData\Local\Google\Chrome\User Data\Default\Local Storage");
            b2.Processes.Add("chrome");
            b2.Contents.Add("로그인 상태나 설정이 저장된 사이트 140곳");
            list.Add(b2);

            var b3 = new FoundItem(ItemGroup.Browser, "Edge (Default) - 방문 기록, 캐시") { User = "sample (지금 계정)", BlockReason = "계정(sample@company.com)이 로그인되어 동기화 중이면 지워도 다시 내려옵니다. 브라우저에서 Ctrl+Shift+Delete를 누르고 기간을 '전체 기간'으로 골라 직접 지워 주세요." };
            b3.Paths.Add(U + @"\AppData\Local\Microsoft\Edge\User Data\Default\History");
            list.Add(b3);

            var m1 = new FoundItem(ItemGroup.Messenger, "카카오톡") { User = "sample (지금 계정)", Newest = DateTime.Today.AddDays(-1), Size = 1400L << 20, Count = 3200 };
            m1.Paths.Add(U + @"\AppData\Local\Kakao\KakaoTalk\users");
            m1.Processes.Add("KakaoTalk");
            m1.Contents.AddRange(new[] { "저장된 정보: 로그인 정보, 대화 내용, 받은 파일 캐시", "대화 내용은 암호화되어 있어서 이 도구로는 열어 볼 수 없습니다.", "지우면 이 PC에서 로그아웃됩니다. 휴대폰의 대화 내용은 그대로 남습니다." });
            list.Add(m1);

            var f1 = new FoundItem(ItemGroup.Files, "주민등록등본.pdf") { User = "sample (지금 계정)", Default = false, Summary = "'주민등록' 포함", Size = 210000, Count = 1 };
            f1.Paths.Add(U + @"\Desktop\주민등록등본.pdf");
            f1.Contents.AddRange(new[] { "찾은 이유: 파일 이름에 '주민등록' 단어가 들어 있습니다.", @"위치: C:\Users\sample\Desktop", "수정한 날짜: 2026-05-02 10:12" });
            list.Add(f1);

            var h1 = new FoundItem(ItemGroup.History, "최근에 연 파일 목록") { User = "sample (지금 계정)", Summary = "58개" };
            h1.ContentsOf.Add(U + @"\AppData\Roaming\Microsoft\Windows\Recent");
            h1.Contents.AddRange(new[] { "최근에 연 파일과 폴더 58개", "  2026-10-10  주간보고.xlsx", "  2026-10-09  여권사본.jpg" });
            list.Add(h1);

            var h2 = new FoundItem(ItemGroup.History, "복사해 둔 내용 (클립보드)") { User = "sample (지금 계정)", Summary = "텍스트 120자", ActionText = "복사해 둔 내용과 Win+V 기록을 지웁니다.", Action = () => { } };
            h2.Contents.Add("지금 복사해 둔 내용: 텍스트 120자 (내용은 화면에 표시하지 않습니다)");
            list.Add(h2);

            var w1 = new FoundItem(ItemGroup.Wifi, "MyHome_5G") { User = "PC 공통", Default = false, ActionText = "이 PC가 기억하는 이 Wi-Fi와 비밀번호를 지웁니다.", Action = () => { } };
            w1.Contents.Add("네트워크 이름: MyHome_5G");
            list.Add(w1);

            return list;
        }
    }
}
#endif
