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
using AwsManager.Services;


namespace AwsManager.ViewModels
{
    public class Ec2ViewModel : AwsResourceViewModel, IRefreshableViewModel
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
        public ICommand ChangeProfileCommand { get; }


        private Ec2InstanceModel? _selectedInstance;
        public Ec2InstanceModel? SelectedInstance
        {
            get => _selectedInstance;
            set => SetField(ref _selectedInstance, value);
        }

        public Ec2ViewModel() : this(null) { }
        public Ec2ViewModel(IAwsClientFactory? clientFactory, bool load = true, AwsContext? context = null) : base(clientFactory, context)
        {
            Instances = [];
            ConfigureFilter<Ec2InstanceModel>(Instances, instance => $"{instance.Name} {instance.InstanceId} {instance.State} {instance.PrivateIp} {instance.PublicIp}");
            RefreshCommand = new AsyncRelayCommand(async _ => await LoadInstancesAsync(), _ => !IsLoading && Allowed("ec2:DescribeInstances"));
            StartInstanceCommand = new AsyncRelayCommand(StartInstance, parameter => !IsLoading && (parameter as Ec2InstanceModel ?? SelectedInstance)?.State == "stopped" && CanChange("ec2:StartInstances", parameter));
            StopInstanceCommand = new AsyncRelayCommand(StopInstance, parameter => !IsLoading && (parameter as Ec2InstanceModel ?? SelectedInstance)?.State == "running" && CanChange("ec2:StopInstances", parameter));
            TerminateInstanceCommand = new AsyncRelayCommand(TerminateInstance, parameter => !IsLoading && (parameter as Ec2InstanceModel ?? SelectedInstance) is { State: not "terminated" and not "shutting-down" } && CanChange("ec2:TerminateInstances", parameter));
            ConnectCommand = new RelayCommand(Connect, _ => SelectedInstance != null && SelectedInstance.IsSsmManaged && CanChange("ssm:StartSession", null));
            DisconnectCommand = new RelayCommand(Disconnect, _ => !IsLoading);
            ViewDetailsCommand = new RelayCommand(ViewDetails, _ => SelectedInstance != null);
            EditTagsCommand = new RelayCommand(EditTags, _ => SelectedInstance != null && Allowed("ec2:DescribeTags") &&
                (CanChange("ec2:CreateTags", null) | CanChange("ec2:DeleteTags", null)));
            ChangeProfileCommand = new RelayCommand(parameter =>
            {
                var target = parameter as Ec2InstanceModel ?? SelectedInstance;
                if (target == null) return;
                new Ec2ProfileWindow(new(target.InstanceId, ClientFactory, Context)) { Owner = Application.Current.MainWindow }.ShowDialog();
            }, parameter => !IsLoading && (parameter as Ec2InstanceModel ?? SelectedInstance) is { State: "running" or "stopped" } &&
                Allowed("ec2:DescribeInstances,ec2:DescribeIamInstanceProfileAssociations,iam:ListInstanceProfiles") &&
                (CanChange("ec2:AssociateIamInstanceProfile", parameter) | CanChange("ec2:ReplaceIamInstanceProfileAssociation", parameter)));




            // Load instances on startup
            if (load) _ = LoadInstancesAsync();
        }

        private bool CanChange(string actions, object? parameter) => Allowed(actions,
            Arn("ec2", $"instance/{(parameter as Ec2InstanceModel ?? SelectedInstance)?.InstanceId}"), true);

        private async Task LoadInstancesAsync()
        {
            IsLoading = true;
            Status = "";
            Instances.Clear();
            try
            {
                using var ec2Client = ClientFactory.CreateEc2Client();
                using var ssmClient = ClientFactory.CreateSsmClient();

                var reservations = new List<Reservation>();
                await foreach (var reservation in ec2Client.Paginators.DescribeInstances(new DescribeInstancesRequest()).Reservations)
                    reservations.Add(reservation);

                var allSsmInstances = new List<InstanceInformation>();
                string? nextToken = null;

                try
                {
                    if (await CheckAccessAsync([new("ssm:DescribeInstanceInformation")])) do
                    {
                        var response = await ssmClient.DescribeInstanceInformationAsync(new DescribeInstanceInformationRequest { MaxResults = 50, NextToken = nextToken });
                        allSsmInstances.AddRange(response.InstanceInformationList ?? []);
                        nextToken = response.NextToken;
                    } while (!string.IsNullOrEmpty(nextToken));
                }
                catch (AmazonServiceException exception) when (exception.ErrorCode is "AccessDeniedException" or "AccessDenied")
                {
                    NotificationService.Publish("Inventaire SSM non autorise : disponibilite des connexions inconnue.");
                }


                foreach (var reservation in reservations)
                {
                    foreach (var instance in reservation.Instances ?? [])
                    {
                        var ssmInfo = allSsmInstances.FirstOrDefault(i => i.InstanceId == instance.InstanceId);
                        Instances.Add(new Ec2InstanceModel
                        {
                            InstanceId = instance.InstanceId,
                            Name = instance.Tags?.FirstOrDefault(t => t.Key == "Name")?.Value ?? "N/A",
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
                ReportError(ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task StartInstance(object? parameter)
        {
            var target = parameter as Ec2InstanceModel ?? SelectedInstance;
            if (target == null) return;

            try
            {
                IsLoading = true;
                using var EC2Client = ClientFactory.CreateEc2Client();

                var request = new StartInstancesRequest
                {
                    InstanceIds = [target.InstanceId]
                };


                await EC2Client.StartInstancesAsync(request);

                NotificationService.Publish($"Demarrage demande : {target.InstanceId}.");

                // Refresh the instances list to show updated status
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally
            {
                IsLoading = false;
            }

        }

        private async Task StopInstance(object? parameter)
        {
            var target = parameter as Ec2InstanceModel ?? SelectedInstance;
            if (target == null || !Confirm($"Arreter l'instance {target.Name} ({target.InstanceId}) ?")) return;

            try
            {
                IsLoading = true;
                using var EC2Client = ClientFactory.CreateEc2Client();

                var request = new StopInstancesRequest
                {
                    InstanceIds = [target.InstanceId]
                };


                await EC2Client.StopInstancesAsync(request);

                NotificationService.Publish($"Arret demande : {target.InstanceId}.");

                // Refresh the instances list to show updated status
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally
            {
                IsLoading = false;
            }

        }

        private async Task TerminateInstance(object? parameter)
        {
            var target = parameter as Ec2InstanceModel ?? SelectedInstance;
            if (target == null || !Confirm($"SUPPRESSION DEFINITIVE de {target.Name} ({target.InstanceId}).\nLes volumes configures pour etre supprimes seront perdus. Continuer ?")) return;

            try
            {
                IsLoading = true;
                using var EC2Client = ClientFactory.CreateEc2Client();

                var request = new TerminateInstancesRequest
                {
                    InstanceIds = [target.InstanceId]
                };


                await EC2Client.TerminateInstancesAsync(request);

                NotificationService.Publish($"Suppression demandee : {target.InstanceId}.");

                // Refresh the instances list to show updated status
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally
            {
                IsLoading = false;
            }

        }

        private void Disconnect(object? parameter)
        {
            NotificationService.Publish("Les connexions se ferment individuellement depuis Sessions.");
        }
        private void ViewDetails(object? parameter)
        {
            SelectedInstance = parameter as Ec2InstanceModel ?? SelectedInstance;
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
            SelectedInstance = parameter as Ec2InstanceModel ?? SelectedInstance;
            if (SelectedInstance == null) return;

            var tagEditorViewModel = new TagEditorViewModel(SelectedInstance.InstanceId);
            var tagEditorWindow = new TagEditorWindow
            {
                DataContext = tagEditorViewModel,
                Owner = Application.Current.MainWindow
            };

            tagEditorWindow.ShowDialog();
            // After closing the dialog, refresh the main instance list in case the name tag was changed
            if (RefreshCommand.CanExecute(null))
            {
                RefreshCommand.Execute(null);
            }
        }
        private void Connect(object? parameter)
        {
            SelectedInstance = parameter as Ec2InstanceModel ?? SelectedInstance;
            if (SelectedInstance == null) return;

            var connectionViewModel = new SsmConnectionViewModel(SelectedInstance);
            var connectionWindow = new SsmConnectionWindow
            {
                DataContext = connectionViewModel,
                Owner = Application.Current.MainWindow
            };

            connectionWindow.ShowDialog();
        }

    }


}
