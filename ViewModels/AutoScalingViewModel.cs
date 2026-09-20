using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
using AwsManager.Models;
using AwsManager.Views.Dialogs;
using AwsManager.Services;


namespace AwsManager.ViewModels
{
    public class AutoScalingViewModel : AwsResourceViewModel, IRefreshableViewModel
    {
        public static string Name => "Auto Scaling";

        private bool _isLoading;
        private readonly Dictionary<string, string> _scheduleErrors = [];
        public bool IsLoading
        {
            get => _isLoading;
            set { if (SetField(ref _isLoading, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        public ObservableCollection<AutoScalingGroupModel> AutoScalingGroups { get; }
        public ICommand RefreshCommand { get; }
        public ICommand UpdateGroupCommand { get; }
        public ICommand EditTagsCommand { get; }
        public ICommand DescribeScheduleCommand { get; }

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

        public AutoScalingViewModel() : this(null) { }
        public AutoScalingViewModel(IAwsClientFactory? clientFactory, bool load = true, AwsContext? context = null) : base(clientFactory, context)
        {
            AutoScalingGroups = [];
            ConfigureFilter<AutoScalingGroupModel>(AutoScalingGroups, group => $"{group.AutoScalingGroupName} {group.Status}");
            RefreshCommand = new AsyncRelayCommand(async _ => await LoadGroupsAsync(), _ => !IsLoading && Allowed("autoscaling:DescribeAutoScalingGroups"));
            UpdateGroupCommand = new AsyncRelayCommand(async _ => await UpdateGroupAsync(), _ => SelectedGroup != null && !IsLoading && Allowed("autoscaling:UpdateAutoScalingGroup", SelectedGroup.ResourceArn, true));
            EditTagsCommand = new RelayCommand(EditTags, _ => SelectedGroup != null && !IsLoading &&
                (Allowed("autoscaling:CreateOrUpdateTags", SelectedGroup.ResourceArn, true) | Allowed("autoscaling:DeleteTags", SelectedGroup.ResourceArn, true)));
            DescribeScheduleCommand = new RelayCommand(DescribeSchedule, _ => SelectedGroup != null && !IsLoading && Allowed("autoscaling:DescribeScheduledActions"));

            if (load) _ = LoadGroupsAsync();
        }

        private async Task LoadGroupsAsync()
        {
            IsLoading = true;
            Status = "";
            SelectedGroup = null;
            _scheduleErrors.Clear();
            AutoScalingGroups.Clear();
            try
            {
                using var asgClient = ClientFactory.CreateAutoScalingClient();
                var paginator = asgClient.Paginators.DescribeAutoScalingGroups(new DescribeAutoScalingGroupsRequest());



                await foreach (var asg in paginator.AutoScalingGroups)
                {
                    if (string.IsNullOrWhiteSpace(asg.AutoScalingGroupName))
                    {
                        continue;
                    }

                    var scheduledActions = await GetScheduledActionsForGroupAsync(asgClient, asg.AutoScalingGroupName);


                    AutoScalingGroups.Add(new AutoScalingGroupModel
                    {
                        AutoScalingGroupName = asg.AutoScalingGroupName ?? "",
                        ResourceArn = asg.AutoScalingGroupARN ?? "",
                        MinSize = asg.MinSize ?? 0,
                        MaxSize = asg.MaxSize ?? 0,
                        DesiredCapacity = asg.DesiredCapacity ?? 0,
                        AvailabilityZones = asg.AvailabilityZones ?? [],
                        Instances = asg.Instances?.Select(i => i.InstanceId).ToList() ?? [],
                        LaunchConfigurationName = asg.LaunchConfigurationName ?? "",
                        Status = asg.Status ?? "",
                        ScheduledActions = scheduledActions
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

        private async Task<List<ScheduledActionModel>> GetScheduledActionsForGroupAsync(IAmazonAutoScaling asgClient, string groupName)
        {
            try
            {
                var request = new DescribeScheduledActionsRequest
                {
                    AutoScalingGroupName = groupName
                };

                var actions = new List<ScheduledUpdateGroupAction>();
                await foreach (var action in asgClient.Paginators.DescribeScheduledActions(request).ScheduledUpdateGroupActions)
                    actions.Add(action);
                return [.. actions.Select(action => new ScheduledActionModel
                {
                    ScheduledActionName = action.ScheduledActionName ?? "",
                    AutoScalingGroupName = action.AutoScalingGroupName ?? "",
                    StartTime = action.StartTime,
                    EndTime = action.EndTime,
                    Recurrence = action.Recurrence ?? "",
                    MinSize = action.MinSize,
                    MaxSize = action.MaxSize,
                    DesiredCapacity = action.DesiredCapacity,
                    Time = action.Time
                })];
            }
            catch (Exception ex)
            {
                _scheduleErrors[groupName] = AwsSessionService.DescribeError(ex);
                ReportError(ex);
                return [];
            }
        }

        private async Task UpdateGroupAsync()
        {
            if (SelectedGroup == null) return;

            var groupName = SelectedGroup.AutoScalingGroupName;
            try { ResourceValidation.Capacity(NewMinSize, NewDesiredCapacity, NewMaxSize); }
            catch (ArgumentException exception) { ReportError(exception); return; }
            if (!Confirm($"Modifier {groupName} ?\nMinimum : {SelectedGroup.MinSize} -> {NewMinSize}\nSouhaite : {SelectedGroup.DesiredCapacity} -> {NewDesiredCapacity}\nMaximum : {SelectedGroup.MaxSize} -> {NewMaxSize}")) return;
            var request = new UpdateAutoScalingGroupRequest
            {
                AutoScalingGroupName = groupName,
                MinSize = NewMinSize,
                MaxSize = NewMaxSize,
                DesiredCapacity = NewDesiredCapacity
            };

            try
            {
                IsLoading = true;
                using var asgClient = ClientFactory.CreateAutoScalingClient();
                await asgClient.UpdateAutoScalingGroupAsync(request);
                NotificationService.Publish($"Modification demandee : {groupName}.");

                await LoadGroupsAsync();
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally { IsLoading = false; }
        }

        private void EditTags(object? parameter)
        {
            SelectedGroup = parameter as AutoScalingGroupModel ?? SelectedGroup;
            if (SelectedGroup == null) return;

            var tagEditorViewModel = new AutoScalingTagEditorViewModel(SelectedGroup.AutoScalingGroupName);
            var tagEditorWindow = new ASGTagEditorWindow
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

        private void DescribeSchedule(object? parameter)
        {
            if (SelectedGroup == null) return;
            if (_scheduleErrors.TryGetValue(SelectedGroup.AutoScalingGroupName, out var error))
            {
                new HelpWindow { Owner = Application.Current.MainWindow, Title = "Actions planifiees indisponibles", DataContext = error }.ShowDialog();
                return;
            }
            var text = string.Join("\n", SelectedGroup.ScheduledActions.Select(action => $"{action.ScheduledActionName} | {action.Recurrence} | {action.StartTime} | min={action.MinSize} / max={action.MaxSize} / souhaite={action.DesiredCapacity}"));
            new HelpWindow { Owner = Application.Current.MainWindow, Title = "Actions planifiees", DataContext = string.IsNullOrEmpty(text) ? "Aucune action planifiee." : text }.ShowDialog();
            //if (SelectedGroup == null) return;

            //var scheduledActions = await GetScheduledActionsForGroupAsync(SelectedGroup.AutoScalingGroupName);

            //var scheduleViewModel = new ScheduleDetailsViewModel(SelectedGroup.ScheduledActions);
            //var scheduleWindow = new ScheduleDetailsWindow
            //{
            //    DataContext = scheduleViewModel,
            //    Owner = Application.Current.MainWindow
            //};

            //scheduleWindow.Show();
        }
    }
}
