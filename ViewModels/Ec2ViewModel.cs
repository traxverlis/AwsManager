using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.RDS.Model;
using Amazon.RDS;
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
        public bool IsNotLoading => !IsLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (SetField(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(IsNotLoading));
                }
            }
        }

        public ObservableCollection<Ec2InstanceModel> Instances { get; }
        public ICommand RefreshCommand { get; }
        public ICommand StartInstanceCommand { get; }
        public ICommand StopInstanceCommand { get; }
        public ICommand TerminateInstanceCommand { get; }
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
            TerminateInstanceCommand = new RelayCommand(TerminateInstance, _ => SelectedInstance != null);
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

                var allSsmInstances = new List<InstanceInformation>();
                string? nextToken = null;

                do
                {
                    var request = new DescribeInstanceInformationRequest
                    {
                        MaxResults = 50, // valeur max autorisée
                        NextToken = nextToken
                    };

                    var response = await ssmClient.DescribeInstanceInformationAsync(request);

                    if (response.InstanceInformationList != null)
                        allSsmInstances.AddRange(response.InstanceInformationList);

                    nextToken = response.NextToken;
                }
                while (!string.IsNullOrEmpty(nextToken));


                foreach (var reservation in instancesResponse.Reservations)
                {
                    foreach (var instance in reservation.Instances)
                    {
                        var ssmInfo = allSsmInstances.FirstOrDefault(i => i.InstanceId == instance.InstanceId);
                        Instances.Add(new Ec2InstanceModel
                        {
                            InstanceId = instance.InstanceId,
                            Name = instance.Tags.FirstOrDefault(t => t.Key == "Name")?.Value ?? "N/A",
                            InstanceType = instance.InstanceType,
                            State = instance.State.Name,
                            PublicIp = instance.PublicIpAddress ?? "N/A",
                            PrivateIp = instance.PrivateIpAddress ?? "N/A",
                            IsSsmManaged = ssmInfo?.PingStatus == PingStatus.Online,
                            SecurityGroups = instance.SecurityGroups != null && instance.SecurityGroups.Count != 0
                                ? string.Join(", ", instance.SecurityGroups.Select(sg => sg.GroupName))
                                : "N/A",
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


                    var profile = Environment.GetEnvironmentVariable("AWS_PROFILE") ?? "";
                    await ReloginSsoAsync(profile);
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

        private async void StartInstance(object? parameter)
        {
            if (SelectedInstance == null) return;

            try
            {
                IsLoading = true;
                using var EC2Client = new AmazonEC2Client();

                var request = new StartInstancesRequest
                {
                    InstanceIds = [SelectedInstance.InstanceId]
                };


                await EC2Client.StartInstancesAsync(request);

                MessageBox.Show($"DB instance {SelectedInstance.InstanceId} is starting...", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Refresh the instances list to show updated status
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start DB instance {SelectedInstance.InstanceId}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }

        }

        private async void StopInstance(object? parameter)
        {
            if (SelectedInstance == null) return;

            try
            {
                IsLoading = true;
                using var EC2Client = new AmazonEC2Client();

                var request = new StopInstancesRequest
                {
                    InstanceIds = [SelectedInstance.InstanceId]
                };


                await EC2Client.StopInstancesAsync(request);

                MessageBox.Show($"DB instance {SelectedInstance.InstanceId} is stopping...", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Refresh the instances list to show updated status
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to stop DB instance {SelectedInstance.InstanceId}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }

        }

        private async void TerminateInstance(object? parameter)
        {
            if (SelectedInstance == null) return;

            try
            {
                IsLoading = true;
                using var EC2Client = new AmazonEC2Client();

                var request = new TerminateInstancesRequest
                {
                    InstanceIds = [SelectedInstance.InstanceId]
                };


                await EC2Client.TerminateInstancesAsync(request);

                MessageBox.Show($"DB instance {SelectedInstance.InstanceId} is Terminating...", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Refresh the instances list to show updated status
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to terminate DB instance {SelectedInstance.InstanceId}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }

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
                    Arguments = $"sso login --profile {profileName}", 
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
