using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PcPrivacyCleaner.Models;
using PcPrivacyCleaner.Services.Scan;

namespace PcPrivacyCleaner.Services
{
    /// <summary>Runs the eight scan steps on a background thread. One failing step does not stop the others.</summary>
    public sealed class Scanner : IScanner
    {
        private readonly RunContext _ctx;

        /// <summary>
        /// Step names appear in the progress box ("(2/8) Windows에 저장된 인증서 검사 중...") and in the log,
        /// so they use the same words as the list's group titles.
        /// </summary>
        private static readonly KeyValuePair<string, Action<ScanSession>>[] Steps =
        {
            Step("공동인증서", CertScanner.FindCerts),
            Step("Windows에 저장된 인증서", CertScanner.FindCertStore),
            Step("브라우저", BrowserScanner.Find),
            Step("메신저", MessengerScanner.Find),
            Step("개인 파일", PersonalFileScanner.Find),
            Step("사용 기록", HistoryScanner.Find),
            Step("저장된 로그인 정보", AccountScanner.FindCredentials),
            Step("저장된 Wi-Fi", AccountScanner.FindWifi),
        };

        /// <summary>Used by the Debug-only MockScanner so its fake progress shows the real step names.</summary>
        public static string[] StepNames { get { return Steps.Select(x => x.Key).ToArray(); } }

        public Scanner(RunContext ctx)
        {
            _ctx = ctx;
        }

        public List<FoundItem> Scan(IProgress<ScanProgress> progress, CancellationToken token)
        {
            var s = new ScanSession(_ctx, progress, token);
            s.Profiles = ScanSession.LoadProfiles(_ctx);
            var names = new List<string>();
            foreach (var p in s.Profiles) names.Add(p.Name);
            AppLog.Write("검사 시작: 사용자 {0}명 ({1})", s.Profiles.Count, string.Join(", ", names));

            for (int i = 0; i < Steps.Length; i++)
            {
                if (token.IsCancellationRequested) break;
                s.BeginStep(i, Steps.Length, Steps[i].Key);
                try
                {
                    Steps[i].Value(s);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Write("{0} 검사 중 오류: {1}", Steps[i].Key, ex.Message);
                }
            }
            AppLog.Write(token.IsCancellationRequested ? "검사 중지: {0}개 항목까지 찾음" : "검사 완료: {0}개 항목", s.Items.Count);
            return s.Items;
        }

        private static KeyValuePair<string, Action<ScanSession>> Step(string name, Action<ScanSession> run)
        {
            return new KeyValuePair<string, Action<ScanSession>>(name, run);
        }
    }
}
