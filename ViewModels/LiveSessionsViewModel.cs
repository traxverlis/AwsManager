using System.Collections.ObjectModel;
using System.Windows.Input;
using AwsManager.Models;
using AwsManager.Services;
using AwsManager.ViewModels;


namespace AwsManager.ViewModels
{
    public class LiveSessionsViewModel : ViewModelBase, IRefreshableViewModel
    {
        public static string Name => "Live Sessions";

        public ObservableCollection<TrackedSessionModel> ActiveSessions => SessionTrackingService.Instance.ActiveSessions;

        private TrackedSessionModel? _selectedSession;
        public TrackedSessionModel? SelectedSession
        {
            get => _selectedSession;
            set => SetField(ref _selectedSession, value);
        }

        public ICommand KillSessionCommand { get; }
        public ICommand RefreshCommand { get; }

        public LiveSessionsViewModel()
        {
            KillSessionCommand = new RelayCommand(KillSession, _ => SelectedSession != null);
            // The list is an ObservableCollection managed by a singleton service,
            // so it updates automatically. A manual refresh isn't needed.
            RefreshCommand = new RelayCommand(_ => { }, _ => true);
        }

        private void KillSession(object? parameter)
        {
            if (SelectedSession != null && new NotificationService().Confirm($"Fermer cette connexion ?\n{SelectedSession.Description}"))
            {
                SessionTrackingService.Instance.KillSession(SelectedSession);
            }
        }
    }
}
