using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.EC2;
using Amazon.EC2.Model;
using AwsManager.Models;

namespace AwsManager.ViewModels
{
    public class SecurityGroupViewModel : ViewModelBase, IRefreshableViewModel
    {
        public static string Name => "Security Groups";

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        public ObservableCollection<SecurityGroupModel> SecurityGroups { get; }
        public ICommand RefreshCommand { get; }
        public ICommand AddRuleCommand { get; }
        public ICommand DeleteRuleCommand { get; }

        private SecurityGroupModel? _selectedSecurityGroup;
        public SecurityGroupModel? SelectedSecurityGroup
        {
            get => _selectedSecurityGroup;
            set => SetField(ref _selectedSecurityGroup, value);
        }

        public SecurityGroupViewModel()
        {
            SecurityGroups = [];
            RefreshCommand = new RelayCommand(async _ => await LoadSecurityGroupsAsync(), _ => !IsLoading);
            AddRuleCommand = new RelayCommand(AddRule, _ => SelectedSecurityGroup != null);
            DeleteRuleCommand = new RelayCommand(DeleteRule, _ => SelectedSecurityGroup != null);

            _ = LoadSecurityGroupsAsync();
        }

        private async Task LoadSecurityGroupsAsync()
        {
            IsLoading = true;
            SecurityGroups.Clear();
            try
            {
                using var ec2Client = new AmazonEC2Client();
                var response = await ec2Client.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest());

                foreach (var sg in response.SecurityGroups)
                {
                    var sgModel = new SecurityGroupModel
                    {
                        GroupId = sg.GroupId,
                        GroupName = sg.GroupName,
                        Description = sg.Description,
                        VpcId = sg.VpcId,
                    };
                    sgModel.IngressRules.AddRange(
                             (sg.IpPermissions ?? Enumerable.Empty<IpPermission>())
                             .Select(p => new SecurityGroupRuleModel
                             {
                                 Type = "Ingress",
                                 Protocol = p.IpProtocol,
                                 PortRange = (p.FromPort.HasValue && p.ToPort.HasValue)
                                    ? (p.FromPort == p.ToPort
                                        ? p.FromPort.Value.ToString()
                                        : $"{p.FromPort.Value}-{p.ToPort.Value}")
                                    : "All",
                                 SourceOrDestination = string.Join(", ",
                                     (p.Ipv4Ranges ?? Enumerable.Empty<IpRange>()).Select(r => r.CidrIp)
                                     .Concat((p.UserIdGroupPairs ?? Enumerable.Empty<UserIdGroupPair>()).Select(g => g.GroupId))
                                 ),
                                 Description = (p.Ipv4Ranges != null && p.Ipv4Ranges.Count != 0)
                                     ? p.Ipv4Ranges.First().Description ?? ""
                                     : ""
                             })
                         );

                    // Egress rules
                    sgModel.EgressRules.AddRange(
                        (sg.IpPermissionsEgress ?? Enumerable.Empty<IpPermission>())
                        .Select(p => new SecurityGroupRuleModel
                        {
                            Type = "Egress",
                            Protocol = p.IpProtocol,
                            PortRange = (p.FromPort.HasValue && p.ToPort.HasValue)
                                    ? (p.FromPort == p.ToPort
                                        ? p.FromPort.Value.ToString()
                                        : $"{p.FromPort.Value}-{p.ToPort.Value}")
                                    : "All",
                            SourceOrDestination = string.Join(", ",
                                (p.Ipv4Ranges ?? Enumerable.Empty<IpRange>()).Select(r => r.CidrIp)
                                .Concat((p.UserIdGroupPairs ?? Enumerable.Empty<UserIdGroupPair>()).Select(g => g.GroupId))
                            ),
                            Description = (p.Ipv4Ranges != null && p.Ipv4Ranges.Count != 0)
                                ? p.Ipv4Ranges.First().Description ?? ""
                                : ""
                        })
                    );

                    SecurityGroups.Add(sgModel);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load Security Groups: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void AddRule(object? parameter)
        {
            MessageBox.Show($"This would open a dialog to add a rule to {SelectedSecurityGroup?.GroupName}", "Action: Add Rule", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DeleteRule(object? parameter)
        {
            if (parameter is SecurityGroupRuleModel rule)
            {
                MessageBox.Show($"This would delete the rule '{rule.Protocol} / {rule.PortRange}' from {SelectedSecurityGroup?.GroupName}", "Action: Delete Rule", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
