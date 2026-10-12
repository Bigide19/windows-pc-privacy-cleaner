using System.Windows;

namespace PcPrivacyCleaner
{
    /// <summary>
    /// Terms shown before the tool does anything. DialogResult is true only when [동의하고 시작] was pressed;
    /// [종료], Esc, Enter and the close box all count as not agreeing.
    /// </summary>
    public partial class ConsentWindow : Window
    {
        public ConsentWindow()
        {
            InitializeComponent();
        }

        private void Agree_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
