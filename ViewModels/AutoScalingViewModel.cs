using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
using AwsManager.Models;


namespace AwsManager.ViewModels
{
    public class AutoScalingViewModel : ViewModelBase, IRefreshableViewModel
    {
        public static string Name => "Auto Scaling";

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        public ObservableCollection<AutoScalingGroupModel> AutoScalingGroups { get; }
        public ICommand RefreshCommand { get; }
        public ICommand UpdateGroupCommand { get; }

        private AutoScalingGroupModel? _selectedGroup;
        public AutoScalingGroupModel? SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (SetField(ref _selectedGroup, value) && value != null)
                {
                    NewMinSize = value.MinSize;
                    NewMaxSize = value.MaxSize;
                    NewDesiredCapacity = value.DesiredCapacity;
                }
            }
        }

        private int _newMinSize;
        public int NewMinSize { get => _newMinSize; set => SetField(ref _newMinSize, value); }

        private int _newMaxSize;
        public int NewMaxSize { get => _newMaxSize; set => SetField(ref _newMaxSize, value); }

        private int _newDesiredCapacity;
        public int NewDesiredCapacity { get => _newDesiredCapacity; set => SetField(ref _newDesiredCapacity, value); }

        public AutoScalingViewModel()
        {
            AutoScalingGroups = [];
            RefreshCommand = new RelayCommand(async _ => await LoadGroupsAsync(), _ => !IsLoading);
            UpdateGroupCommand = new RelayCommand(async _ => await UpdateGroupAsync(), _ => SelectedGroup != null && !IsLoading);

            _ = LoadGroupsAsync();
        }

        private async Task LoadGroupsAsync()
        {
            IsLoading = true;
            AutoScalingGroups.Clear();
            try
            {
                using var asgClient = new AmazonAutoScalingClient();
                var paginator = asgClient.Paginators.DescribeAutoScalingGroups(new DescribeAutoScalingGroupsRequest());

                await foreach (var asg in paginator.AutoScalingGroups)
                {
                    AutoScalingGroups.Add(new AutoScalingGroupModel
                    {
                        AutoScalingGroupName = asg.AutoScalingGroupName ?? "",
                        MinSize = asg.MinSize ?? 0,
                        MaxSize = asg.MaxSize ?? 0,
                        DesiredCapacity = asg.DesiredCapacity ?? 0,
                        AvailabilityZones = asg.AvailabilityZones ?? [],
                        Instances = asg.Instances?.Select(i => i.InstanceId).ToList() ?? [],
                        LaunchConfigurationName = asg.LaunchConfigurationName ?? "",
                        Status = asg.Status ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load Auto Scaling Groups: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task UpdateGroupAsync()
        {
            if (SelectedGroup == null) return;

            var groupName = SelectedGroup.AutoScalingGroupName;
            var request = new UpdateAutoScalingGroupRequest
            {
                AutoScalingGroupName = groupName,
                MinSize = NewMinSize,
                MaxSize = NewMaxSize,
                DesiredCapacity = NewDesiredCapacity
            };

            try
            {
                using var asgClient = new AmazonAutoScalingClient();
                await asgClient.UpdateAutoScalingGroupAsync(request);
                MessageBox.Show($"Successfully updated Auto Scaling Group '{groupName}'.", "Update Successful", MessageBoxButton.OK, MessageBoxImage.Information);

                await LoadGroupsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update Auto Scaling Group '{groupName}': {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
