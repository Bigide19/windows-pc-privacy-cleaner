using System;
using System.Linq;
using System.Threading.Tasks;
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
            AppDomain.CurrentDomain.UnhandledException += OnFatalException;
            // Exceptions of tasks nobody awaited. They do not end the process on .NET 4.5+, but they are still bugs.
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                AppLog.StepError("처리되지 않은 작업 오류", args.Exception);
                args.SetObserved();
            };

            var ctx = RunContext.Capture();
            ctx.StartInDryRun = e.Args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
            AppLog.Initialize(ctx.ExeDir);
            // Look for a crashed previous run before this run creates its own marker.
            var unfinished = CrashReport.TakeUnfinishedRun(ctx.ExeDir);
            CrashReport.Start(ctx.ExeDir);
            AppLog.Step(string.Format("개인정보 정리 도구 {0} 시작, 관리자: {1}, 다른 계정으로 실행: {2}",
                IssueReport.AppVersion, ctx.IsAdmin ? "예" : "아니요", ctx.OtherAccount ? "예" : "아니요"));
            AppLog.Detail("  실행 계정: {0}, 로그인 사용자: {1}", ctx.IdentityName, ctx.ConsoleUser);

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
            // Terms before anything is scanned (see ConsentWindow). [관리자로 실행] passes --agreed, so the person who
            // just agreed is not asked again by the elevated copy.
            if (e.Args.Any(a => string.Equals(a, "--agreed", StringComparison.OrdinalIgnoreCase)))
            {
                AppLog.Step("사용 조건에 동의한 상태로 다시 실행했습니다.");
            }
            else
            {
                // While the dialog is up it is the only window; without this, closing it would end the app
                // before the main window exists.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (new ConsentWindow().ShowDialog() != true)
                {
                    AppLog.Step("사용 조건에 동의하지 않아 종료했습니다.");
                    Shutdown();
                    return;
                }
                AppLog.Step("사용 조건에 동의했습니다.");
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }

            var window = new MainWindow(new MainViewModel(scanner, ctx) { UnfinishedRun = unfinished });
            MainWindow = window;
            window.Show();
#if DEBUG
            // Developer check of the crash report paths: PcPrivacyCleaner.exe --crash thread|native|failfast
            int cr = Array.FindIndex(e.Args, a => string.Equals(a, "--crash", StringComparison.OrdinalIgnoreCase));
            if (cr >= 0 && cr + 1 < e.Args.Length) CrashSoon(e.Args[cr + 1]);
#endif
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Normal exit (also after [관리자로 실행] hands over to the elevated copy): no crash to report next time.
            CrashReport.Finish();
            base.OnExit(e);
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

        /// <summary>Crash kind ①: an exception on the UI thread. The window survives, so the user can report it right away.</summary>
        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.StepError("처리하지 못한 오류", e.Exception);
            AppLog.Detail(e.Exception.ToString());
            var r = MessageBox.Show("오류가 발생했습니다.\n\n" + e.Exception.Message + "\n\n" + IssueReport.Question + "\n\n기록 파일: " + AppLog.FilePath,
                "개인정보 정리 도구", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (r == MessageBoxResult.Yes) TryOpenReport(AppLog.StepsText(IssueReport.ProgressBudget));
            // Keep the window alive; the user can retry or close it.
            e.Handled = true;
        }

        /// <summary>
        /// Crash kind ②: an exception on another thread. The process ends after this handler, so the Step line
        /// goes into the run marker first; if the question below can not be shown, the next start asks instead.
        /// </summary>
        private static void OnFatalException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            AppLog.StepError(CrashReport.FatalStep, ex);
            if (ex != null) AppLog.Detail(ex.ToString());
            if (!e.IsTerminating) return;
            try
            {
                var r = MessageBox.Show("오류로 프로그램을 종료합니다.\n\n" + IssueReport.Question + "\n\n기록 파일: " + AppLog.FilePath,
                    "개인정보 정리 도구", MessageBoxButton.YesNo, MessageBoxImage.Error);
                if (r == MessageBoxResult.Yes) TryOpenReport(AppLog.StepsText(IssueReport.ProgressBudget));
                // Asked once already (yes or no): do not ask again on the next start.
                CrashReport.Finish();
            }
            catch
            {
                // No message box possible at this point; the marker stays and the next start offers the report.
            }
        }

        private static void TryOpenReport(string progress)
        {
            if (!IssueReport.TryOpen(progress))
                MessageBox.Show(IssueReport.OpenFailedText, "개인정보 정리 도구", MessageBoxButton.OK, MessageBoxImage.Information);
        }

#if DEBUG
        /// <summary>
        /// thread: unhandled exception on a worker thread (②, caught by OnFatalException).
        /// native: access violation, as a bad P/Invoke would cause (② on .NET Framework, also logged by Windows).
        /// failfast: Environment.FailFast, where no handler runs at all (③, only the next start can report it).
        /// </summary>
        private void CrashSoon(string kind)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, a) =>
            {
                timer.Stop();
                AppLog.Step("테스트용 크래시를 일으킵니다.");
                if (kind == "thread") new System.Threading.Thread(() => { throw new InvalidOperationException("test crash"); }).Start();
                else if (kind == "native") new System.Threading.Thread(() => System.Runtime.InteropServices.Marshal.ReadInt32(IntPtr.Zero)).Start();
                else if (kind == "failfast") Environment.FailFast("test crash");
            };
            timer.Start();
        }
#endif
    }
}
