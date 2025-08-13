using System;
using System.Collections.ObjectModel;
using AwsManager.Views.Dialogs;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.RDS;
using Amazon.RDS.Model;
using AwsManager.Models;

namespace AwsManager.ViewModels
{
    public class RdsViewModel : ViewModelBase, IRefreshableViewModel
    {
        public string Name => "RDS Instances";

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

        private RdsInstanceModel? _selectedInstance;
        public RdsInstanceModel? SelectedInstance
        {
            get => _selectedInstance;
            set => SetField(ref _selectedInstance, value);
        }

        public RdsViewModel()
        {
            Instances = new ObservableCollection<RdsInstanceModel>();
            RefreshCommand = new RelayCommand(async _ => await LoadInstancesAsync(), _ => !IsLoading);
            StartDbInstanceCommand = new RelayCommand(StartDbInstance, _ => SelectedInstance != null);
            StopDbInstanceCommand = new RelayCommand(StopDbInstance, _ => SelectedInstance != null);
            CreateDbSnapshotCommand = new RelayCommand(CreateDbSnapshot, _ => SelectedInstance != null);
            ViewDetailsCommand = new RelayCommand(ViewDetails, _ => SelectedInstance != null);
            EditTagsCommand = new RelayCommand(EditTags, _ => SelectedInstance != null);

            _ = LoadInstancesAsync();
        }

        private async Task LoadInstancesAsync()
        {
            IsLoading = true;
            Instances.Clear();
            try
            {
                using var rdsClient = new AmazonRDSClient();
                var response = await rdsClient.DescribeDBInstancesAsync(new DescribeDBInstancesRequest());

                foreach (var dbInstance in response.DBInstances ?? Enumerable.Empty<DBInstance>())
                {
                    Instances.Add(new RdsInstanceModel
                    {
                        DbInstanceIdentifier = dbInstance.DBInstanceIdentifier,
                        DbInstanceClass = dbInstance.DBInstanceClass,
                        Engine = dbInstance.Engine,
                        DbInstanceStatus = dbInstance.DBInstanceStatus,
                        EndpointAddress = dbInstance.Endpoint?.Address ?? "N/A",
                        AllocatedStorage = dbInstance.AllocatedStorage ?? 0,
                        MultiAZ = dbInstance.MultiAZ ?? false,
                        SecurityGroups = string.Join(", ", dbInstance.VpcSecurityGroups.Select(sg => sg.VpcSecurityGroupId))
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load RDS instances: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
        private async void StartDbInstance(object? parameter)
        {
            if (SelectedInstance == null) return;

            try
            {
                IsLoading = true;
                using var rdsClient = new AmazonRDSClient();

                var request = new StartDBInstanceRequest
                {
                    DBInstanceIdentifier = SelectedInstance.DbInstanceIdentifier
                };

                await rdsClient.StartDBInstanceAsync(request);

                MessageBox.Show($"DB instance {SelectedInstance.DbInstanceIdentifier} is starting...", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Refresh the instances list to show updated status
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start DB instance {SelectedInstance.DbInstanceIdentifier}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }


        private async void StopDbInstance(object? parameter)
        {
            if (SelectedInstance == null) return;

            try
            {
                IsLoading = true;
                using var rdsClient = new AmazonRDSClient();

                var request = new StopDBInstanceRequest
                {
                    DBInstanceIdentifier = SelectedInstance.DbInstanceIdentifier
                };

                await rdsClient.StopDBInstanceAsync(request);

                MessageBox.Show($"DB instance {SelectedInstance.DbInstanceIdentifier} is Stopping...", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Refresh the instances list to show updated status
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to stop DB instance {SelectedInstance.DbInstanceIdentifier}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
            
        }

        private void CreateDbSnapshot(object? parameter)
        {
            var snapshotId = $"{SelectedInstance?.DbInstanceIdentifier}-{DateTime.Now:yyyy-MM-dd-HH-mm}";
            MessageBox.Show($"This action would create a snapshot for {SelectedInstance?.DbInstanceIdentifier} with ID: {snapshotId}", "Action: Create Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
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

            var tagEditorViewModel = new RdsTagEditorViewModel(SelectedInstance.DbInstanceIdentifier);
            var tagEditorWindow = new TagEditorWindow
            {
                DataContext = tagEditorViewModel,
                Owner = Application.Current.MainWindow
            };

            tagEditorWindow.ShowDialog();

        }
    }
}
