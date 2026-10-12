using System.Diagnostics;
using Microsoft.Win32;

namespace PcPrivacyCleaner.Services
{
    /// <summary>
    /// [문제 알리기]: opens the Google Form for bug reports in the default browser.
    ///
    /// The app itself never connects to the network. Reading browser password files and then sending
    /// something over HTTP is exactly what info-stealer malware does, so antivirus/EDR would flag it.
    /// Opening a URL in the browser keeps the exe offline, and the user decides whether to submit.
    ///
    /// Only the app version, the Windows version and the Step lines of the log go into the URL (pre-filled answers).
    /// Step lines hold stage names, counts and exception types by construction (see AppLog). Never add Detail lines,
    /// scan results or paths here: the URL ends up in browser history.
    /// </summary>
    public static class IssueReport
    {
        // Form "개인정보 정리 도구 문제 알리기", owned by bigide19@gmail.com.
        // The entry ids come from the published form (FB_PUBLIC_LOAD_DATA_ in the viewform page).
        // If the form questions are recreated, these ids change and pre-fill silently stops working.
        public const string FormUrl = "https://docs.google.com/forms/d/e/1FAIpQLScp4_JlkQUYk-tIyAgmH0UPVszZvAktMK3yr1Zx0xZPKFf0Fg/viewform";
        private const string AppVersionEntry = "entry.1921256525";
        private const string WindowsVersionEntry = "entry.1821665125";
        private const string ProgressEntry = "entry.2101693869";   // 진행 기록 (long answer)

        /// <summary>
        /// Budget for the 진행 기록 answer, URL-encoded. Korean text grows about nine times when encoded, and the whole
        /// URL has to stay well inside what browsers and Google accept, so older lines are dropped first.
        /// </summary>
        public const int ProgressBudget = 6000;

        /// <summary>"0.4.0": the release workflow passes -p:Version, which becomes the assembly version.</summary>
        public static string AppVersion
        {
            get
            {
                var v = typeof(IssueReport).Assembly.GetName().Version;
                return string.Format("{0}.{1}.{2}", v.Major, v.Minor, v.Build);
            }
        }

        /// <summary>
        /// e.g. "Windows 11 Pro 24H2 (26100.4061)". Many users do not know whether they run 10 or 11,
        /// so the app fills this in for them. Returns "" when the registry can not be read.
        /// </summary>
        public static string WindowsVersion()
        {
            try
            {
                // 64-bit view: the 32-bit view of this key is redirected on 64-bit Windows.
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k == null) return "";
                    string product = k.GetValue("ProductName") as string ?? "Windows";
                    // DisplayVersion ("23H2") exists from 20H2; older builds only have ReleaseId ("1909").
                    string display = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string ?? "";
                    string buildText = k.GetValue("CurrentBuild") as string ?? "";
                    object ubr = k.GetValue("UBR");

                    // Windows 11 still writes "Windows 10 ..." into ProductName. Build 22000 and later is Windows 11.
                    int build;
                    if (int.TryParse(buildText, out build) && build >= 22000)
                        product = product.Replace("Windows 10", "Windows 11");

                    string text = product;
                    if (display.Length > 0) text += " " + display;
                    if (buildText.Length > 0) text += " (" + buildText + (ubr != null ? "." + ubr : "") + ")";
                    return text;
                }
            }
            catch
            {
                return "";
            }
        }

        /// <summary>The form URL with both versions and the given progress text pre-filled.</summary>
        public static string BuildUrl(string progress)
        {
            string url = FormUrl + "?usp=pp_url"
                + "&" + AppVersionEntry + "=" + System.Uri.EscapeDataString(AppVersion)
                + "&" + WindowsVersionEntry + "=" + System.Uri.EscapeDataString(WindowsVersion());
            if (!string.IsNullOrEmpty(progress)) url += "&" + ProgressEntry + "=" + System.Uri.EscapeDataString(progress);
            return url;
        }

        /// <summary>[문제 알리기]: this run's Step lines.</summary>
        public static void Open()
        {
            Open(AppLog.StepsText(ProgressBudget));
        }

        /// <summary>Opens the form in the default browser. Throws when no browser can be started.</summary>
        public static void Open(string progress)
        {
            Process.Start(new ProcessStartInfo(BuildUrl(progress)) { UseShellExecute = true });
        }

        /// <summary>
        /// Opens the form and logs the outcome. Returns false when no browser could be started; the caller then
        /// shows OpenFailedText so the user can type the address by hand.
        /// </summary>
        public static bool TryOpen(string progress)
        {
            try
            {
                Open(progress);
                AppLog.Step("문제 알리기 양식을 브라우저로 열었습니다.");
                return true;
            }
            catch (System.Exception ex)
            {
                // No default browser, or the browser is blocked by company policy.
                AppLog.StepError("문제 알리기 양식을 열지 못했습니다", ex);
                return false;
            }
        }

        public static string OpenFailedText
        {
            get { return "브라우저를 열지 못했습니다. 아래 주소를 브라우저에 직접 입력해 주세요.\n\n" + FormUrl; }
        }

        /// <summary>Asked before opening the form after an error, so the user knows what goes along.</summary>
        public const string Question =
            "[예]를 누르면 문제 알리기 양식을 엽니다. 진행 기록(단계, 개수, 오류 종류)이 함께 채워지고, 파일 이름과 경로는 들어가지 않습니다.";
    }
}
