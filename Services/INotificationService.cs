
namespace AwsManager.Services
{
    public interface INotificationService
    {
        void ShowInfo(string message, string title = "Information");
        void ShowWarning(string message, string title = "Attention");
        void ShowError(string message, string title = "Erreur");
        bool Confirm(string message, string title = "Confirmation");
    }
}