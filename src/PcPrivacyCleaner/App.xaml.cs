using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using PcPrivacyCleaner.Services;
using PcPrivacyCleaner.ViewModels;

namespace PcPrivacyCleaner
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Allow paths longer than 260 characters (deep browser cache folders). These switches must be
            // set before the first file system call, and setting them in code means no .exe.config is needed.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandledException;

            var ctx = RunContext.Capture();
            ctx.ScanOnly = e.Args.Any(a => string.Equals(a, "--scan-only", StringComparison.OrdinalIgnoreCase));
            AppLog.Initialize(ctx.ExeDir);
            AppLog.Write("개인정보 정리 도구 시작. 실행 계정: {0}, 관리자: {1}, 로그인 사용자: {2}",
                ctx.IdentityName, ctx.IsAdmin, ctx.ConsoleUser);

            IScanner scanner = new Scanner(ctx);
#if DEBUG
            // UI work without touching real data: PcPrivacyCleaner.exe --mock
            if (e.Args.Any(a => string.Equals(a, "--mock", StringComparison.OrdinalIgnoreCase)))
            {
                scanner = new MockScanner();
                ctx.UseSampleIdentity();
            }

            // Developer check: PcPrivacyCleaner.exe --selftest <file>
            // Runs the real scan without a window and writes one line per item (no Contents lines, which hold
            // site and file names). Debug-only so the release exe can never dump scan results to disk.
            int st = Array.FindIndex(e.Args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
            if (st >= 0 && st + 1 < e.Args.Length)
            {
                RunSelfTest(scanner, e.Args[st + 1]);
                return;
            }
#endif
            var window = new MainWindow(new MainViewModel(scanner, ctx));
            MainWindow = window;
            window.Show();
        }

#if DEBUG
        private void RunSelfTest(IScanner scanner, string outFile)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Scan on a worker thread while this (UI) thread keeps pumping: the clipboard probe needs the dispatcher.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var sb = new System.Text.StringBuilder();
                try
                {
                    var progress = new Progress<ScanProgress>(_ => { });
                    var items = await System.Threading.Tasks.Task.Run(() =>
                    {
                        var list = scanner.Scan(progress, System.Threading.CancellationToken.None);
                        foreach (var it in list) it.Scope = Models.DeleteScope.Build(it);
                        return list;
                    });
                    foreach (var it in items)
                    {
                        sb.AppendLine(string.Join(" | ", it.GroupTitle, it.DisplayName, it.SummaryText, it.ScopeShort, it.SizeText,
                            "contents=" + it.Contents.Count, it.Default ? "default" : "off", it.IsBlocked ? "BLOCKED" : ""));
                    }
                    sb.AppendLine("TOTAL " + items.Count);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("ERROR " + ex);
                }
                System.IO.File.WriteAllText(outFile, sb.ToString(), new System.Text.UTF8Encoding(false));
                Shutdown();
            }));
        }
#endif

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Write("처리하지 못한 오류: " + e.Exception);
            MessageBox.Show("오류가 발생했습니다.\n\n" + e.Exception.Message + "\n\n기록 파일: " + AppLog.FilePath,
                "개인정보 정리 도구", MessageBoxButton.OK, MessageBoxImage.Error);
            // Keep the window alive; the user can retry or close it.
            e.Handled = true;
        }
    }
}
