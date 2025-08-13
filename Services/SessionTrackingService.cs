using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using AwsManager;
using AwsManager.Models;

namespace AwsManager.Services
{
    public class SessionTrackingService
    {
        private static readonly Lazy<SessionTrackingService> _instance = new Lazy<SessionTrackingService>(() => new SessionTrackingService());
        public static SessionTrackingService Instance => _instance.Value;

        public ObservableCollection<TrackedSessionModel> ActiveSessions { get; }

        private SessionTrackingService()
        {
            ActiveSessions = new ObservableCollection<TrackedSessionModel>();
        }

        public void AddSession(Process process, string description)
        {
            if (process == null) return;

            var session = new TrackedSessionModel(process, description);

            // Ensure the process can raise events
            process.EnableRaisingEvents = true;

            // Register for the Exited event to remove the session from the list
            process.Exited += (sender, args) =>
            {
                // We need to update the collection on the UI thread
                App.Current.Dispatcher.Invoke(() =>
                {
                    if (ActiveSessions.Contains(session))
                    {
                        ActiveSessions.Remove(session);
                    }
                });
            };

            // Add the session to the collection on the UI thread
            App.Current.Dispatcher.Invoke(() => ActiveSessions.Add(session));
        }

        public void KillSession(TrackedSessionModel session)
        {
            try
            {
                if (!session.Process.HasExited)
                {
                    session.Process.Kill(true); // Kill entire process tree
                }
            }
            catch (Exception)
            {
                // Process may have already exited, which is fine.
                // The Exited event will clean it up.
                App.Current.Dispatcher.Invoke(() => {
                    if (ActiveSessions.Contains(session)) ActiveSessions.Remove(session);
                });
            }
        }
    }
}
