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
    /// Only the app version and the Windows version go into the URL (pre-filled answers).
    /// Never add scan results, paths or log lines here: the URL ends up in browser history.
    /// </summary>
    public static class IssueReport
    {
        // Form "개인정보 정리 도구 문제 알리기", owned by bigide19@gmail.com.
        // The entry ids come from the published form (FB_PUBLIC_LOAD_DATA_ in the viewform page).
        // If the form questions are recreated, these ids change and pre-fill silently stops working.
        public const string FormUrl = "https://docs.google.com/forms/d/e/1FAIpQLScp4_JlkQUYk-tIyAgmH0UPVszZvAktMK3yr1Zx0xZPKFf0Fg/viewform";
        private const string AppVersionEntry = "entry.1921256525";
        private const string WindowsVersionEntry = "entry.1821665125";

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

        /// <summary>The form URL with the two version answers pre-filled.</summary>
        public static string BuildUrl()
        {
            return FormUrl + "?usp=pp_url"
                + "&" + AppVersionEntry + "=" + System.Uri.EscapeDataString(AppVersion)
                + "&" + WindowsVersionEntry + "=" + System.Uri.EscapeDataString(WindowsVersion());
        }

        /// <summary>Opens the form in the default browser. Throws when no browser can be started.</summary>
        public static void Open()
        {
            Process.Start(new ProcessStartInfo(BuildUrl()) { UseShellExecute = true });
        }
    }
}
