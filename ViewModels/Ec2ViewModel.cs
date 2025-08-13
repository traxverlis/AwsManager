using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using AwsManager.Models;
using AwsManager.Views.Dialogs;


namespace AwsManager.ViewModels
{
    public class Ec2ViewModel : ViewModelBase, IRefreshableViewModel
    {
        public static string Name => "EC2 Instances";
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        public ObservableCollection<Ec2InstanceModel> Instances { get; }
        public ICommand RefreshCommand { get; }
        public ICommand StartInstanceCommand { get; }
        public ICommand StopInstanceCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand ViewDetailsCommand { get; }
        public ICommand EditTagsCommand { get; }


        private Ec2InstanceModel? _selectedInstance;
        public Ec2InstanceModel? SelectedInstance
        {
            get => _selectedInstance;
            set => SetField(ref _selectedInstance, value);
        }

        public Ec2ViewModel()
        {
            Instances = [];
            RefreshCommand = new RelayCommand(async _ => await LoadInstancesAsync(), _ => !IsLoading);
            StartInstanceCommand = new RelayCommand(StartInstance, _ => SelectedInstance != null);
            StopInstanceCommand = new RelayCommand(StopInstance, _ => SelectedInstance != null);
            ConnectCommand = new RelayCommand(Connect, _ => SelectedInstance != null && SelectedInstance.IsSsmManaged);
            DisconnectCommand = new RelayCommand(Disconnect, _ => !IsLoading);
            ViewDetailsCommand = new RelayCommand(ViewDetails, _ => SelectedInstance != null);
            EditTagsCommand = new RelayCommand(EditTags, _ => SelectedInstance != null);




            // Load instances on startup
            _ = LoadInstancesAsync();
        }

        private async Task LoadInstancesAsync()
        {
            IsLoading = true;
            Instances.Clear();
            try
            {
                using var ec2Client = new AmazonEC2Client();
                using var ssmClient = new AmazonSimpleSystemsManagementClient();

                var instancesResponse = await ec2Client.DescribeInstancesAsync(new DescribeInstancesRequest());
                var ssmResponse = await ssmClient.DescribeInstanceInformationAsync(new DescribeInstanceInformationRequest());

                foreach (var reservation in instancesResponse.Reservations)
                {
                    foreach (var instance in reservation.Instances)
                    {
                        var ssmInfo = ssmResponse.InstanceInformationList.FirstOrDefault(i => i.InstanceId == instance.InstanceId);
                        Instances.Add(new Ec2InstanceModel
                        {
                            InstanceId = instance.InstanceId,
                            Name = instance.Tags.FirstOrDefault(t => t.Key == "Name")?.Value ?? "N/A",
                            InstanceType = instance.InstanceType,
                            State = instance.State.Name,
                            PublicIp = instance.PublicIpAddress ?? "N/A",
                            PrivateIp = instance.PrivateIpAddress ?? "N/A",
                            IsSsmManaged = ssmInfo?.PingStatus == PingStatus.Online,
                            SecurityGroups = string.Join(", ", instance.SecurityGroups.Select(sg => sg.GroupName)),
                            Platform = instance.PlatformDetails?.ToString() ?? "N/A"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("SSO Token has expired"))
                {
                    MessageBox.Show(
                        "Votre session AWS SSO a expiré.\nVous devez vous reconnecter.",
                        "SSO expiré",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );

                    // Tentative de relancer la connexion SSO


                    await ReloginSsoAsync("");
                }
                else
                {
                    MessageBox.Show($"Failed to load EC2 instances: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void StartInstance(object? parameter)
        {
            MessageBox.Show($"This action would start instance: {SelectedInstance?.InstanceId}", "Action: Start", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void StopInstance(object? parameter)
        {
            MessageBox.Show($"This action would stop instance: {SelectedInstance?.InstanceId}", "Action: Stop", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Disconnect(object? parameter)
        {
            MessageBox.Show($"This action would stop All RDP Session", "Action: Stop", MessageBoxButton.OK, MessageBoxImage.Information);
            foreach (Process process in Process.GetProcessesByName("session-manager-plugin"))
                process.Kill();


        }
        private void ViewDetails(object? parameter)
        {
            if (SelectedInstance == null) return;

            var detailsViewModel = new InstanceDetailsViewModel(SelectedInstance);
            var detailsWindow = new InstanceDetailsWindow
            {
                DataContext = detailsViewModel,
                Owner = Application.Current.MainWindow
            };

            detailsWindow.Show(); // Use Show() for non-modal so user can have multiple open
        }

        private void EditTags(object? parameter)
        {
            if (SelectedInstance == null) return;

            var tagEditorViewModel = new TagEditorViewModel(SelectedInstance.InstanceId);
            var tagEditorWindow = new TagEditorWindow
            {
                DataContext = tagEditorViewModel,
                Owner = Application.Current.MainWindow
            };

            tagEditorWindow.Show();
            // After closing the dialog, refresh the main instance list in case the name tag was changed
            if (RefreshCommand.CanExecute(null))
            {
                RefreshCommand.Execute(null);
            }
        }
        private void Connect(object? parameter)
        {
            if (SelectedInstance == null) return;

            var connectionViewModel = new SsmConnectionViewModel(SelectedInstance);
            var connectionWindow = new SsmConnectionWindow
            {
                DataContext = connectionViewModel,
                Owner = Application.Current.MainWindow
            };

            connectionWindow.ShowDialog();
        }
                private static async Task ReloginSsoAsync(string profileName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "aws",
                    Arguments = $"sso login --profile egf-devops", // ⚠ c'est bien "sso login", pas "sso-login"
                    UseShellExecute = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    MessageBox.Show(
                        $"Connexion SSO réussie pour le profil '{profileName}'.",
                        "Succès",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Impossible de relancer la connexion SSO : {ex.Message}",
                    "Erreur",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

    }
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
            => (_execute, _canExecute) = (execute ?? throw new ArgumentNullException(nameof(execute)), canExecute);

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke(parameter) ?? true;
        }

        public void Execute(object? parameter)
        {
            _execute(parameter);
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

}
