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

        public void StartSshSession(string instanceId)
        {
            var description = $"SSH session to {instanceId}";
            var arguments = $"ssm start-session --target {instanceId} {GetProfileArgument()}";
            LaunchAwsCliProcess(arguments, description);
        }

        public void StartPortForwardingSession(string instanceId, int remotePort, int localPort)
        {
            // The parameters value needs to be properly quoted for the command line.
            var description = $"Port forward {instanceId} ({remotePort}:{localPort})";
            var parameters = $"portNumber={remotePort},localPortNumber={localPort}";
            var arguments = $"ssm start-session --target {instanceId} --document-name AWS-StartPortForwardingSession --parameters \"{parameters}\" {GetProfileArgument()} ";
            LaunchAwsCliProcess(arguments, description);
        }

        private static void LaunchAwsCliProcess(string arguments, string description)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe", // Use cmd.exe to spawn a new window for the session
                        Arguments = $"/C aws {arguments}",
                        UseShellExecute = false,
                        CreateNoWindow = !false,
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
