using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using AwsManager.ViewModels;

namespace AwsManager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow() : this(new MainViewModel()) { }
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Closed += (_, _) => viewModel.Dispose();
            Closing += (_, args) =>
            {
                var sessions = Services.SessionTrackingService.Instance.ActiveSessions.ToArray();
                if (sessions.Length == 0) return;
                if (!new Services.NotificationService().Confirm($"Fermer AWS Manager et ses {sessions.Length} connexion(s) SSM ?"))
                {
                    args.Cancel = true;
                    return;
                }
                foreach (var session in sessions) Services.SessionTrackingService.Instance.KillSession(session);
            };
        }
    }
}