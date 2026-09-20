using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace AwsManager.Services
{
    public class AwsErrorHandler : IAwsErrorHandler
    {
        public async Task<bool> HandleExceptionAsync(Exception ex, string context)
        {
            if (ex.Message.Contains("SSO Token has expired"))
            {
                MessageBox.Show(
                    "Votre session AWS SSO a expiré.\nVous devez vous reconnecter.",
                    "SSO expiré",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );

                var profile = Environment.GetEnvironmentVariable("AWS_PROFILE") ?? "";
                await ReloginSsoAsync(profile);
                return true;
            }

            MessageBox.Show(
                $"{context}: {ex.Message}",
                "Erreur",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            return false;
        }

        private static async Task ReloginSsoAsync(string profile)
        {
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "aws",
                    Arguments = $"sso login --profile {profile}",
                    UseShellExecute = true
                };
                Process.Start(psi);
            });
        }
    }
}