using AwsManager.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace AwsManager.Views.Dialogs
{
    public partial class HelpWindow : Window
    {
        public HelpWindow(string? message = null)
        {
            InitializeComponent();
            DataContext = new HelpViewModel(message);
        }

        private void TopicSelectionChanged(object sender, SelectionChangedEventArgs args) => TopicScroll?.ScrollToHome();
        private void CloseHelp(object sender, RoutedEventArgs args) => Close();
    }
}
