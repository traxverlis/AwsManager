using System;
using System.Collections.ObjectModel;
using AwsManager.Views.Dialogs;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.RDS;
using Amazon.RDS.Model;
using AwsManager.Models;
using AwsManager.Services;

namespace AwsManager.ViewModels
{
    public class RdsViewModel : AwsResourceViewModel, IRefreshableViewModel
    {
        public static string Name => "RDS Instances";

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        public ObservableCollection<RdsInstanceModel> Instances { get; }
        public ICommand RefreshCommand { get; }
        public ICommand StartDbInstanceCommand { get; }
        public ICommand StopDbInstanceCommand { get; }
        public ICommand CreateDbSnapshotCommand { get; }
        public ICommand ViewDetailsCommand { get; }
        public ICommand EditTagsCommand { get; }
        public ICommand ConnectCommand { get; }

        private RdsInstanceModel? _selectedInstance;
        public RdsInstanceModel? SelectedInstance
        {
            get => _selectedInstance;
            set => SetField(ref _selectedInstance, value);
        }

        public RdsViewModel() : this(null) { }
        public RdsViewModel(IAwsClientFactory? clientFactory, bool load = true, AwsContext? context = null) : base(clientFactory, context)
        {
            Instances = [];
            ConfigureFilter<RdsInstanceModel>(Instances, instance => $"{instance.DbInstanceIdentifier} {instance.Engine} {instance.DbInstanceStatus} {instance.EndpointAddress}");
            RefreshCommand = new AsyncRelayCommand(async _ => await LoadInstancesAsync(), _ => !IsLoading && Allowed("rds:DescribeDBInstances"));
            StartDbInstanceCommand = new AsyncRelayCommand(StartDbInstance, _ => !IsLoading && SelectedInstance?.DbInstanceStatus == "stopped" && Allowed("rds:StartDBInstance", SelectedInstance.ResourceArn, true));
            StopDbInstanceCommand = new AsyncRelayCommand(StopDbInstance, _ => !IsLoading && SelectedInstance?.DbInstanceStatus == "available" && Allowed("rds:StopDBInstance", SelectedInstance.ResourceArn, true));
            CreateDbSnapshotCommand = new AsyncRelayCommand(CreateDbSnapshot, _ => !IsLoading && SelectedInstance?.DbInstanceStatus == "available" && Allowed([new("rds:CreateDBSnapshot", SelectedInstance.ResourceArn), new("rds:CreateDBSnapshot", Arn("rds", "snapshot:*"))], true));
            ViewDetailsCommand = new RelayCommand(ViewDetails, _ => SelectedInstance != null);
            EditTagsCommand = new RelayCommand(EditTags, _ => SelectedInstance != null && Allowed("rds:ListTagsForResource", SelectedInstance.ResourceArn) &&
                (Allowed("rds:AddTagsToResource", SelectedInstance.ResourceArn, true) | Allowed("rds:RemoveTagsFromResource", SelectedInstance.ResourceArn, true)));
            ConnectCommand = new RelayCommand(_ =>
            {
                var model = new RdsTunnelViewModel(SelectedInstance!, ClientFactory, context: Context);
                new RdsTunnelWindow { DataContext = model, Owner = Application.Current.MainWindow }.ShowDialog();
            }, _ => !IsLoading && SelectedInstance != null && Allowed("rds:DescribeDBInstances,ec2:DescribeInstances,ssm:DescribeInstanceInformation"));

            if (load) _ = LoadInstancesAsync();
        }

        private async Task LoadInstancesAsync()
        {
            IsLoading = true;
            Status = "";
            SelectedInstance = null;
            Instances.Clear();
            try
            {
                using var rdsClient = ClientFactory.CreateRdsClient();
                await foreach (var dbInstance in rdsClient.Paginators.DescribeDBInstances(new DescribeDBInstancesRequest()).DBInstances)
                {
                    Instances.Add(new RdsInstanceModel
                    {
                        DbInstanceIdentifier = dbInstance.DBInstanceIdentifier,
                        ResourceArn = dbInstance.DBInstanceArn,
                        DbInstanceClass = dbInstance.DBInstanceClass,
                        Engine = dbInstance.Engine,
                        DbInstanceStatus = dbInstance.DBInstanceStatus,
                        EndpointAddress = dbInstance.Endpoint?.Address ?? "N/A",
                        AllocatedStorage = dbInstance.AllocatedStorage ?? 0,
                        MultiAZ = dbInstance.MultiAZ ?? false,
                        SecurityGroups = string.Join(", ", (dbInstance.VpcSecurityGroups ?? []).Select(sg => sg.VpcSecurityGroupId))
                    });
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
        private async Task StartDbInstance(object? parameter)
        {
            if (SelectedInstance == null) return;
            var target = SelectedInstance.DbInstanceIdentifier;

            try
            {
                IsLoading = true;
                using var rdsClient = ClientFactory.CreateRdsClient();

                var request = new StartDBInstanceRequest
                {
                    DBInstanceIdentifier = target
                };

                await rdsClient.StartDBInstanceAsync(request);

                NotificationService.Publish($"Demarrage demande : {target}.");

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


        private async Task StopDbInstance(object? parameter)
        {
            if (SelectedInstance == null) return;
            var target = SelectedInstance.DbInstanceIdentifier;
            if (!Confirm($"Arreter la base {target} ? Les connexions seront interrompues.")) return;

            try
            {
                IsLoading = true;
                using var rdsClient = ClientFactory.CreateRdsClient();

                var request = new StopDBInstanceRequest
                {
                    DBInstanceIdentifier = target
                };

                await rdsClient.StopDBInstanceAsync(request);

                NotificationService.Publish($"Arret demande : {target}.");

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

        private async Task CreateDbSnapshot(object? parameter)
        {
            if (SelectedInstance == null) return;
            var target = SelectedInstance.DbInstanceIdentifier;
            var snapshotId = $"{target}-{DateTime.UtcNow:yyyy-MM-dd-HH-mm-ss}";
            if (!Confirm($"Creer le snapshot {snapshotId} ?\nLe stockage peut entrainer des frais.")) return;
            IsLoading = true;
            try
            {
                using var client = ClientFactory.CreateRdsClient();
                var response = await client.CreateDBSnapshotAsync(new CreateDBSnapshotRequest { DBInstanceIdentifier = target, DBSnapshotIdentifier = snapshotId });
                NotificationService.Publish($"Snapshot {snapshotId} : {response.DBSnapshot?.Status ?? "demande acceptee"}. La sauvegarde n'est pas encore disponible.");
            }
            catch (Exception exception) { ReportError(exception); }
            finally { IsLoading = false; }
        }

        private void ViewDetails(object? parameter)
        {
            if (SelectedInstance == null) return;

            var detailsViewModel = new RdsDetailsViewModel(SelectedInstance);
            var detailsWindow = new InstanceDetailsWindow
            {
                DataContext = detailsViewModel,
                Owner = Application.Current.MainWindow
            };

            detailsWindow.Show();
        }

        private void EditTags(object? parameter)
        {
            if (SelectedInstance == null) return;

            var tagEditorViewModel = new RdsTagEditorViewModel(SelectedInstance.DbInstanceIdentifier, SelectedInstance.ResourceArn);
            var tagEditorWindow = new TagEditorWindow
            {
                DataContext = tagEditorViewModel,
                Owner = Application.Current.MainWindow
            };

            tagEditorWindow.ShowDialog();

        }
    }
}
