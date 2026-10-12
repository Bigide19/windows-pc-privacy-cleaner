using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PcPrivacyCleaner.ViewModels;

namespace PcPrivacyCleaner
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            // Start scanning as soon as the window is on screen. A crash of the previous run is offered for
            // reporting first, so the question does not pop up over the scan overlay.
            Loaded += async (s, e) =>
            {
                await _vm.OfferCrashReportAsync();
                await _vm.ScanAsync();
            };
        }

        private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            LogBox.ScrollToEnd();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing mid-delete would leave some items half done. Ask first; scanning can just stop.
            if (_vm.IsBusy)
            {
                var r = MessageBox.Show(this, "작업이 아직 끝나지 않았습니다. 중지하고 창을 닫을까요?", "개인정보 정리 도구",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
                if (_vm.CancelCommand.CanExecute(null)) _vm.CancelCommand.Execute(null);
            }
            base.OnClosing(e);
        }
    }
}
