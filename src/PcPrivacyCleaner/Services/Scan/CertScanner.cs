using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using PcPrivacyCleaner.Models;

namespace PcPrivacyCleaner.Services.Scan
{
    /// <summary>
    /// 1. 공동인증서 (NPKI / GPKI / EPKI) and 2. Windows 인증서 저장소.
    /// </summary>
    public static class CertScanner
    {
        private sealed class Root
        {
            public string Path;
            public UserProfile Profile;   // null = shared location (drive root, Program Files)
        }

        private sealed class CertInfo
        {
            public string Name;
            public string Subject;
            public string ExpText;     // list summary: "만료 2027-03-01" or "만료됨"
            public string ValidText;   // detail line:  "2027-03-01까지" or "만료됨"
        }

        /// <summary>Folders where certificates usually live. Each path appears once.</summary>
        private static List<Root> FindRoots(ScanSession s)
        {
            var cands = new List<Root>();
            foreach (var pf in s.Profiles)
            {
                foreach (var rel in new[] { @"AppData\LocalLow\NPKI", @"AppData\Roaming\NPKI", @"AppData\LocalLow\GPKI", @"AppData\LocalLow\EPKI" })
                    cands.Add(new Root { Path = Path.Combine(pf.Path, rel), Profile = pf });
            }
            // NPKI at the root of a fixed disk is shared by the PC. Removable disks (USB) are never touched.
            foreach (var drv in DriveInfo.GetDrives())
            {
                if (drv.DriveType != DriveType.Fixed || !drv.IsReady) continue;
                foreach (var n in new[] { "NPKI", "GPKI", "EPKI" })
                    cands.Add(new Root { Path = Path.Combine(drv.RootDirectory.FullName, n) });
            }
            foreach (var pfDir in ProgramFilesDirs())
                cands.Add(new Root { Path = Path.Combine(pfDir, "NPKI") });

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return cands.Where(c => seen.Add(c.Path) && Files.DirExists(c.Path)).ToList();
        }

        private static IEnumerable<string> ProgramFilesDirs()
        {
            var a = Environment.GetEnvironmentVariable("ProgramFiles");
            var b = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(a)) yield return a;
            if (!string.IsNullOrEmpty(b) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) yield return b;
        }

        /// <summary>Deleting under Program Files needs admin rights.</summary>
        private static bool NeedsAdmin(string path)
        {
            return ProgramFilesDirs().Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Reads owner name and expiry from the certificate file. Falls back to the folder name
        /// ("cn=홍길동()0011...,ou=...") when the file can not be parsed.
        /// </summary>
        private static CertInfo ReadCert(string file, string fallbackName)
        {
            string name = "", subject = "";
            DateTime? expires = null;
            try
            {
                using (var c = new X509Certificate2(file))
                {
                    subject = c.Subject;
                    name = c.GetNameInfo(X509NameType.SimpleName, false);
                    expires = c.NotAfter;
                }
            }
            catch (CryptographicException) { }

            if (string.IsNullOrEmpty(name))
            {
                var m = Regex.Match(fallbackName ?? "", "cn=([^,]+)", RegexOptions.IgnoreCase);
                if (m.Success) name = m.Groups[1].Value;
            }
            // Korean certificate CNs look like "이름()일련번호"; keep only the name.
            name = Regex.Replace(name ?? "", @"\(\)\d*$", "").Trim();
            if (name.Length == 0) name = "(이름을 읽지 못함)";

            string exp = "", valid = "";
            if (expires.HasValue)
            {
                bool expired = expires.Value < DateTime.Now;
                exp = expired ? "만료됨" : "만료 " + expires.Value.ToString("yyyy-MM-dd");
                valid = expired ? "만료됨" : expires.Value.ToString("yyyy-MM-dd") + "까지";
            }
            return new CertInfo { Name = name, Subject = subject, ExpText = exp, ValidText = valid };
        }

        public static void FindCerts(ScanSession s)
        {
            foreach (var root in FindRoots(s))
            {
                s.Token.ThrowIfCancellationRequested();
                string kind = Path.GetFileName(root.Path).ToUpperInvariant();
                string block = (!s.Context.IsAdmin && NeedsAdmin(root.Path))
                    ? "관리자 권한이 필요합니다. 위쪽 [관리자로 실행]을 눌러 주세요." : null;

                // NPKI layout: <CA>\USER\<certificate folder>\signCert.der + signPri.key.
                // One certificate folder is one certificate.
                var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sc in Files.Walk(root.Path, s.Token).Where(f => string.Equals(f.Name, "signCert.der", StringComparison.OrdinalIgnoreCase)))
                {
                    var dir = sc.Directory;
                    handled.Add(dir.FullName);
                    var info = ReadCert(sc.FullName, dir.Name);

                    // A corporate (사업자) certificate may be needed for company work (홈택스, 세금계산서), so it starts unchecked.
                    bool isCorp = Regex.IsMatch(info.Subject + " " + dir.Name, "corporation|corp4|법인|사업자", RegexOptions.IgnoreCase);
                    string ca = (dir.Parent != null && dir.Parent.Parent != null) ? dir.Parent.Parent.Name : "";

                    string kindText = "개인 인증서 (은행, 정부24, 홈택스 등에 로그인할 때 사용)";
                    if (kind != "NPKI") kindText = kind + " 업무용 인증서 (공공기관, 교육기관용)";
                    else if (isCorp) kindText = "법인 인증서 (회사 업무용일 수 있음)";

                    FoundItem item;
                    if (kind != "NPKI")
                        // GPKI/EPKI are civil-service/education certificates: checked by default, but can be unchecked.
                        item = new FoundItem(ItemGroup.Cert, string.Format("[업무용] {0} ({1})", info.Name, kind));
                    else if (isCorp)
                        item = new FoundItem(ItemGroup.Cert, "[법인] " + info.Name) { Default = false };
                    else
                        item = new FoundItem(ItemGroup.Cert, info.Name + " (개인)") { Forced = true };

                    item.BlockReason = block;
                    item.Summary = info.ExpText;
                    item.Paths.Add(dir.FullName);
                    item.Contents.Add("소유자: " + info.Name);
                    item.Contents.Add("종류: " + kindText);
                    if (ca.Length > 0) item.Contents.Add("발급 기관: " + ca);
                    if (info.ValidText.Length > 0) item.Contents.Add("유효 기간: " + info.ValidText);
                    s.Add(item, root.Profile);
                    if (root.Profile == null) item.User = "PC 공통";
                }

                // GPKI/EPKI can also be loose files: <name>_sig.cer / <name>_sig.key / <name>_env.cer ...
                // NPKI folders may hold CA certificates as loose files, so this is skipped for NPKI.
                if (kind == "NPKI") continue;
                var loose = Files.Walk(root.Path, s.Token)
                    .Where(f => (f.Extension.Equals(".cer", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".key", StringComparison.OrdinalIgnoreCase))
                                && !handled.Contains(f.DirectoryName))
                    .GroupBy(f => Path.Combine(f.DirectoryName, Regex.Replace(Path.GetFileNameWithoutExtension(f.Name), "_(sig|env)$", "", RegexOptions.IgnoreCase)),
                             StringComparer.OrdinalIgnoreCase);
                foreach (var g in loose)
                {
                    var cer = g.FirstOrDefault(f => f.Extension.Equals(".cer", StringComparison.OrdinalIgnoreCase));
                    if (cer == null) continue;
                    var info = ReadCert(cer.FullName, Path.GetFileNameWithoutExtension(cer.Name));
                    var item = new FoundItem(ItemGroup.Cert, string.Format("[업무용] {0} ({1})", info.Name, kind))
                    {
                        BlockReason = block,
                        Summary = info.ExpText
                    };
                    item.Paths.AddRange(g.Select(f => f.FullName));
                    item.Contents.Add("소유자: " + info.Name);
                    item.Contents.Add("종류: " + kind + " 업무용 인증서 (공공기관, 교육기관용)");
                    if (info.ValidText.Length > 0) item.Contents.Add("유효 기간: " + info.ValidText);
                    s.Add(item, root.Profile);
                    if (root.Profile == null) item.User = "PC 공통";
                }
            }
        }

        /// <summary>
        /// Current user's personal certificate store, only certificates with a private key.
        /// Company VPN / 802.1X Wi-Fi certificates may be here, so everything starts unchecked.
        /// </summary>
        public static void FindCertStore(ScanSession s)
        {
            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);
                foreach (var c in store.Certificates)
                {
                    if (!c.HasPrivateKey) continue;
                    string subj = c.GetNameInfo(X509NameType.SimpleName, false);
                    string issuer = c.GetNameInfo(X509NameType.SimpleName, true);
                    string exp = c.NotAfter < DateTime.Now ? "만료됨" : "만료 " + c.NotAfter.ToString("yyyy-MM-dd");
                    var item = new FoundItem(ItemGroup.CertStore, subj)
                    {
                        Default = false,
                        Size = 0,
                        Count = 1,
                        Summary = exp,
                        Data = c.Thumbprint,
                        ActionText = "Windows에 저장된 이 인증서를 지웁니다.",
                        Action = DeleteStoreCert
                    };
                    item.Contents.Add("이름: " + subj);
                    item.Contents.Add("발급한 곳: " + issuer);
                    item.Contents.Add("유효 기간: " + (c.NotAfter < DateTime.Now ? "만료됨" : c.NotAfter.ToString("yyyy-MM-dd") + "까지"));
                    item.Contents.Add("로그인이나 전자 서명에 사용할 수 있는 인증서입니다.");
                    s.Add(item, s.Current);
                }
            }
        }

        /// <summary>
        /// Removing the certificate from the store leaves its private key behind, so the key is deleted first
        /// (CNG key or legacy CSP container), then the certificate.
        /// </summary>
        private static void DeleteStoreCert(FoundItem item)
        {
            string thumb = (string)item.Data;
            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false);
                if (found.Count == 0) return;
                var cert = found[0];
                try
                {
                    var key = cert.GetRSAPrivateKey();
                    var cng = key as RSACng;
                    var csp = key as RSACryptoServiceProvider;
                    if (cng != null) cng.Key.Delete();
                    else if (csp != null) { csp.PersistKeyInCsp = false; csp.Clear(); }
                    else
                    {
                        // Elliptic-curve certificates have no RSA key; their CNG key is deleted the same way.
                        var ec = cert.GetECDsaPrivateKey() as ECDsaCng;
                        if (ec != null) ec.Key.Delete();
                        else AppLog.Write("  인증서 개인키 종류를 알 수 없어서 인증서만 지웁니다.");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("  인증서 개인키는 지우지 못했습니다 ({0}). 인증서만 지웁니다.", ex.Message);
                }
                store.Remove(cert);
            }
        }
    }
}
