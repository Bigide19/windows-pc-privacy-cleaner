using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PcPrivacyCleaner.Infrastructure;
using PcPrivacyCleaner.Models;
using PcPrivacyCleaner.Services;

namespace PcPrivacyCleaner.ViewModels
{
    /// <summary>
    /// Main screen state. Scan and clean run on a background thread (Task.Run) and report
    /// progress through IProgress, so the window keeps responding while the disk is being walked.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly IScanner _scanner;
        private readonly RunContext _ctx;
        private CancellationTokenSource _cts;

        public MainViewModel(IScanner scanner, RunContext ctx)
        {
            _scanner = scanner;
            _ctx = ctx;

            Items = new ObservableCollection<FoundItem>();
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
            view.GroupDescriptions.Add(new PropertyGroupDescription("GroupTitle"));
            // Groups appear in GroupInfo order; inside a group the scan order is kept.
            view.SortDescriptions.Add(new SortDescription("GroupOrder", ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription("Index", ListSortDirection.Ascending));
            ItemsView = view;

            _isDryRun = ctx.StartInDryRun;

            ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsBusy);
            CleanCommand = new RelayCommand(async () => await CleanAsync(), () => !IsBusy && Items.Count > 0);
            CancelCommand = new RelayCommand(Cancel, () => IsBusy && !_cancelRequested);
            ResetCommand = new RelayCommand(() => { foreach (var it in Items) it.ResetCheck(); }, () => !IsBusy);
            CheckAllFilesCommand = new RelayCommand(
                () => { foreach (var it in Items.Where(x => x.Group == ItemGroup.Files)) it.IsChecked = true; },
                () => !IsBusy);
            RelaunchAdminCommand = new RelayCommand(RelaunchAsAdmin, () => !IsBusy);
            // Only opens the browser, so it stays available while a scan or delete is running.
            ReportIssueCommand = new RelayCommand(ReportIssue);

            AppLog.LineAdded += line =>
            {
                // Log lines arrive from the worker thread too.
                var app = Application.Current;
                if (app != null) app.Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
            };
        }

        // ---------- header ----------

        public string AccountText
        {
            get
            {
                return string.Format("지금 계정: {0}     관리자 권한: {1}", _ctx.IdentityName, _ctx.IsAdmin ? "있음" : "없음");
            }
        }

        public string ScopeText
        {
            get
            {
                if (_ctx.IsAdmin)
                    return "이 PC의 모든 사용자를 검사합니다. 다른 사용자는 파일만 정리하고, 인증서, 로그인 정보, 사용 기록은 지금 계정 것만 정리합니다.";
                return "관리자 권한이 없어서 지금 계정만 검사합니다. 다른 사용자까지 정리하려면 [관리자로 실행]을 누르세요.";
            }
        }

        public bool ShowOtherAccountWarning { get { return _ctx.OtherAccount; } }

        public string OtherAccountText
        {
            get
            {
                return string.Format("주의: 지금 로그인한 사용자({0})와 이 도구를 실행한 계정이 다릅니다. 로그인한 사용자의 인증서, 로그인 정보, 사용 기록은 정리되지 않습니다.", _ctx.ConsoleUser);
            }
        }

        public bool CanRelaunchAsAdmin { get { return !_ctx.IsAdmin; } }

        // ---------- footer ----------

        /// <summary>Shown next to [문제 알리기], so a user can tell which version they are reporting about.</summary>
        public string VersionText { get { return "v" + IssueReport.AppVersion; } }

        // ---------- list ----------

        public ObservableCollection<FoundItem> Items { get; private set; }
        public ICollectionView ItemsView { get; private set; }

        private FoundItem _selected;
        public FoundItem Selected
        {
            get { return _selected; }
            set
            {
                if (Set(ref _selected, value)) OnPropertyChanged("HasSelection");
            }
        }

        public bool HasSelection { get { return _selected != null; } }

        private string _selectionSummary = "";
        public string SelectionSummary
        {
            get { return _selectionSummary; }
            private set { Set(ref _selectionSummary, value); }
        }

        private void UpdateSelectionSummary()
        {
            int n = 0;
            long size = 0;
            foreach (var it in Items)
            {
                if (!it.IsChecked) continue;
                n++;
                if (it.Size > 0) size += it.Size;
            }
            SelectionSummary = string.Format("{0}개 선택, 약 {1}", n, Format.Size(size, 0));
        }

        private void OnItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsChecked") UpdateSelectionSummary();
        }

        // ---------- busy overlay ----------

        private bool _isBusy;
        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
            }
        }

        private bool _cancelRequested;

        private string _busyTitle = "";
        public string BusyTitle { get { return _busyTitle; } private set { Set(ref _busyTitle, value); } }

        private string _busyText = "";
        public string BusyText { get { return _busyText; } private set { Set(ref _busyText, value); } }

        private double _progressValue;
        public double ProgressValue { get { return _progressValue; } private set { Set(ref _progressValue, value); } }

        private double _progressMax = 1;
        public double ProgressMax { get { return _progressMax; } private set { Set(ref _progressMax, value); } }

        private string _statusText = "";
        public string StatusText { get { return _statusText; } private set { Set(ref _statusText, value); } }

        // ---------- dry run / clean button ----------

        private bool _isDryRun;
        public bool IsDryRun
        {
            get { return _isDryRun; }
            set
            {
                if (Set(ref _isDryRun, value)) OnPropertyChanged("CleanButtonText");
            }
        }

        public string CleanButtonText { get { return _isDryRun ? "연습 실행 (지우지 않음)" : "선택 항목 삭제"; } }

        // ---------- log ----------

        private readonly System.Text.StringBuilder _log = new System.Text.StringBuilder();
        public string LogText { get { return _log.ToString(); } }

        private void AppendLog(string line)
        {
            _log.AppendLine(line);
            OnPropertyChanged("LogText");
        }

        // ---------- commands ----------

        public ICommand ScanCommand { get; private set; }
        public ICommand CleanCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
        public ICommand ResetCommand { get; private set; }
        public ICommand CheckAllFilesCommand { get; private set; }
        public ICommand RelaunchAdminCommand { get; private set; }
        public ICommand ReportIssueCommand { get; private set; }

        private void Cancel()
        {
            if (_cts == null) return;
            _cancelRequested = true;
            BusyText = "중지하는 중입니다. 지금 하던 단계까지만 마칩니다...";
            _cts.Cancel();
            CommandManager.InvalidateRequerySuggested();
        }

        private void BeginBusy(string title)
        {
            _cts = new CancellationTokenSource();
            _cancelRequested = false;
            BusyTitle = title;
            BusyText = "";
            ProgressValue = 0;
            ProgressMax = 1;
            IsBusy = true;
        }

        private void EndBusy()
        {
            IsBusy = false;
            if (_cts != null) { _cts.Dispose(); _cts = null; }
        }

        private void OnProgress(ScanProgress p)
        {
            ProgressMax = Math.Max(1, p.StepCount);
            ProgressValue = p.Step;
            BusyText = string.Format("({0}/{1}) {2}", Math.Min(p.Step + 1, p.StepCount), p.StepCount, p.Text);
        }

        public async Task ScanAsync()
        {
            if (IsBusy) return;
            BeginBusy("검사 중");
            Selected = null;
            var progress = new Progress<ScanProgress>(OnProgress);
            var token = _cts.Token;
            try
            {
                List<FoundItem> found = await Task.Run(() =>
                {
                    var list = _scanner.Scan(progress, token);
                    // Scope touches the disk (is it a file or a folder?), so it is computed here, off the UI thread.
                    foreach (var it in list) it.Scope = DeleteScope.Build(it);
                    return list;
                });

                foreach (var old in Items) old.PropertyChanged -= OnItemChanged;
                Items.Clear();
                int index = 0;
                foreach (var it in found)
                {
                    it.Index = index++;
                    it.ResetCheck();
                    it.PropertyChanged += OnItemChanged;
                    Items.Add(it);
                }
                UpdateSelectionSummary();
                StatusText = token.IsCancellationRequested
                    ? string.Format("검사를 중지했습니다. 그때까지 찾은 {0}개 항목만 보여 줍니다.", Items.Count)
                    : string.Format("검사를 마쳤습니다. {0}개 항목을 찾았습니다.", Items.Count);
            }
            catch (Exception ex)
            {
                AppLog.Write("검사 중 오류: " + ex.Message);
                ShowMessage("검사 중 오류가 발생했습니다.\n\n" + ex.Message + "\n\n기록 파일: " + AppLog.FilePath, MessageBoxImage.Error);
            }
            finally
            {
                EndBusy();
            }
        }

        public async Task CleanAsync()
        {
            if (IsBusy) return;
            var sel = Items.Where(x => x.IsChecked).ToList();
            if (sel.Count == 0) { ShowMessage("선택한 항목이 없습니다.", MessageBoxImage.Information); return; }
            bool dry = IsDryRun;

            if (!dry)
            {
                string msg = string.Format("선택한 {0}개 항목을 삭제합니다. 삭제한 뒤에는 되돌릴 수 없습니다.", sel.Count);
                int certCount = sel.Count(x => x.Group == ItemGroup.Cert);
                if (certCount > 0)
                    msg += string.Format("\n\n공동인증서 {0}개가 포함되어 있습니다. 휴대폰이나 USB에 백업해 두지 않았다면 나중에 은행에서 다시 발급받아야 합니다.", certCount);
                int fileCount = sel.Count(x => x.Group == ItemGroup.Files);
                if (fileCount > 0)
                    msg += string.Format("\n\n개인 파일 {0}개가 포함되어 있습니다. 업무 파일이 섞여 있지 않은지 한 번 더 확인해 주세요.", fileCount);
                msg += "\n\n계속할까요?";
                if (Ask(msg, "삭제 확인") != MessageBoxResult.Yes) return;

                if (!await CloseRunningProgramsAsync(sel)) return;
            }

            BeginBusy(dry ? "연습 실행 중" : "삭제 중");
            var progress = new Progress<ScanProgress>(OnProgress);
            var token = _cts.Token;
            CleanResult result = null;
            try
            {
                result = await Task.Run(() => Cleaner.Clean(sel, dry, progress, token));
            }
            catch (Exception ex)
            {
                AppLog.Write("삭제 중 오류: " + ex.Message);
            }
            finally
            {
                EndBusy();
            }

            string summary;
            if (result == null) summary = "삭제 중 오류가 발생했습니다.";
            else if (dry) summary = "연습 모드라 실제로는 지우지 않았습니다. 지울 목록은 기록 파일에 남겼습니다.";
            else summary = string.Format("삭제를 마쳤습니다. 성공 {0}개, 실패 {1}개입니다.", result.Ok, result.Failed);
            if (result != null && result.Cancelled) summary += "\n\n중간에 중지해서 일부 항목은 처리하지 않았습니다.";
            summary += "\n\n기록 파일: " + AppLog.FilePath;
            if (result != null && result.Failed > 0) summary += "\n\n실패한 이유는 기록 파일에서 볼 수 있습니다.";
            ShowMessage(summary, MessageBoxImage.Information);

            if (!dry) await ScanAsync();
        }

        /// <summary>
        /// Browsers and messengers lock their files while running. Only processes in this session are
        /// checked; other users' programs can not be closed from here.
        /// </summary>
        private async Task<bool> CloseRunningProgramsAsync(List<FoundItem> sel)
        {
            var names = sel.SelectMany(x => x.Processes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0) return true;
            int mySession = Process.GetCurrentProcess().SessionId;
            var running = new List<Process>();
            foreach (var n in names)
                running.AddRange(Process.GetProcessesByName(n).Where(p => p.SessionId == mySession));
            if (running.Count == 0) return true;

            string list = string.Join(", ", running.Select(p => DeleteScope.ProgramName(p.ProcessName)).Distinct());
            string msg = "다음 프로그램이 켜져 있어서 파일을 지울 수 없습니다.\n\n" + list +
                "\n\n[예]를 누르면 프로그램을 닫고 계속합니다. 작성 중인 내용이 있다면 [아니요]를 누르고, 저장한 뒤 직접 닫아 주세요.";
            if (Ask(msg, "프로그램 종료") != MessageBoxResult.Yes) return false;
            foreach (var p in running)
            {
                try { p.Kill(); } catch { /* already gone */ }
            }
            // Give the killed processes a moment to release their file handles, without freezing the window.
            await Task.Delay(2000);
            return true;
        }

        private void RelaunchAsAdmin()
        {
            try
            {
                // Keep 연습 모드 on across the restart if it was on.
                var psi = new ProcessStartInfo(_ctx.ExePath, IsDryRun ? "--dry-run" : "")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch
            {
                // The UAC prompt was cancelled.
                ShowMessage("관리자 권한으로 다시 실행하지 못했습니다. 지금 계정으로 계속 사용할 수 있습니다.", MessageBoxImage.Information);
            }
        }

        private static void ReportIssue()
        {
            try
            {
                IssueReport.Open();
                AppLog.Write("문제 알리기 양식을 브라우저로 열었습니다.");
            }
            catch (Exception ex)
            {
                // No default browser, or the browser is blocked by company policy. Give the address instead.
                AppLog.Write("문제 알리기 양식을 열지 못했습니다: " + ex.Message);
                ShowMessage("브라우저를 열지 못했습니다. 아래 주소를 브라우저에 직접 입력해 주세요.\n\n" + IssueReport.FormUrl,
                    MessageBoxImage.Information);
            }
        }

        private static void ShowMessage(string text, MessageBoxImage icon)
        {
            MessageBox.Show(Application.Current.MainWindow, text, "개인정보 정리 도구", MessageBoxButton.OK, icon);
        }

        private static MessageBoxResult Ask(string text, string title)
        {
            return MessageBox.Show(Application.Current.MainWindow, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        }
    }
}
