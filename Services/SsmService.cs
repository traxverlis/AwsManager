using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace AwsManager.Services
{
    public class SsmService
    {
        private static string GetProfileArgument()
        {
            var profile = Environment.GetEnvironmentVariable("AWS_PROFILE");
            return string.IsNullOrEmpty(profile) ? "" : $"--profile {profile}";
        }

        public static void StartSshSession(string instanceId)
        {
            //var description = $"SSH session to {instanceId}";
            var arguments = $"ssm start-session --target {instanceId} {GetProfileArgument()}";
            LaunchAwsCliProcess(arguments);
        }

        public static void StartPortForwardingSession(string instanceId, int remotePort, int localPort)
        {
            // The parameters value needs to be properly quoted for the command line.
            //var description = $"Port forward {instanceId} ({remotePort}:{localPort})";
            var parameters = $"portNumber={remotePort},localPortNumber={localPort}";
            var arguments = $"ssm start-session --target {instanceId} --document-name AWS-StartPortForwardingSession --parameters \"{parameters}\" {GetProfileArgument()} ";
            LaunchAwsCliProcess(arguments,false);
        }

        private static void LaunchAwsCliProcess(string arguments, bool windows =true)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/K aws {arguments}", // /K pour garder la fenêtre ouverte
                        UseShellExecute = windows, // true pour ouvrir une nouvelle fenêtre
                        CreateNoWindow = !windows, // false pour afficher la fenêtre
                        WindowStyle = ProcessWindowStyle.Normal
                        
                    }
                };
                process.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start AWS CLI process. Is it installed and in your PATH?\n\nError: {ex.Message}", "Process Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
