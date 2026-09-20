using System.Windows;


namespace AwsManager.Services
{
    public class NotificationService : INotificationService
    {
        public static event Action<string>? Published;
        public static void Publish(string message) => Published?.Invoke(message);
        public void ShowInfo(string message, string title = "Information")
            => Publish(message);

        public void ShowWarning(string message, string title = "Attention")
            => Publish(message);

        public void ShowError(string message, string title = "Erreur")
            => Publish(message);

        public bool Confirm(string message, string title = "Confirmation")
            => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

}