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
            ActiveSessions = [];
        }

        public TrackedSessionModel AddSession(Process process, string description)
        {
            ArgumentNullException.ThrowIfNull(process);

            var session = new TrackedSessionModel(process, description);

            // Ensure the process can raise events
            // Register for the Exited event to remove the session from the list
            process.Exited += (sender, args) =>
            {
                // We need to update the collection on the UI thread
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ActiveSessions.Contains(session))
                    {
                        ActiveSessions.Remove(session);
                    }
                });
            };

            // Add the session to the collection on the UI thread
            App.Current.Dispatcher.Invoke(() => ActiveSessions.Add(session));
            process.EnableRaisingEvents = true;
            if (process.HasExited) App.Current.Dispatcher.Invoke(() => ActiveSessions.Remove(session));
            return session;
        }

        public void KillSession(TrackedSessionModel session)
        {
            if (!ActiveSessions.Contains(session)) return;
            try
            {
                if (!session.Process.HasExited)
                {
                    session.Process.Kill(true); // Kill entire process tree
                }
            }
            catch (Exception exception)
            {
                NotificationService.Publish($"Arret du processus {session.ProcessId} impossible : {exception.GetType().Name}.");
            }
        }
    }
}
