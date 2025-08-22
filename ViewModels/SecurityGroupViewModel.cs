using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amazon.EC2;
using Amazon.EC2.Model;
using AwsManager.Models;
using AwsManager.Views.Dialogs;

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
            DeleteRuleCommand = new RelayCommand(DeleteRule, _ => true);

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

        private async void AddRule(object? parameter)
        {
            if (SelectedSecurityGroup == null) return;

            var dialog = new AddSecurityGroupRuleWindow
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    using var ec2Client = new AmazonEC2Client();
                    var ipPermission = new IpPermission
                    {
                        IpProtocol = dialog.Protocol,
                        Ipv4Ranges =
                        [
                            new IpRange { CidrIp = dialog.Cidr, Description = dialog.Description }
                        ]
                    };

                    if (dialog.PortRange.Equals("All", StringComparison.OrdinalIgnoreCase))
                    {
                        ipPermission.FromPort = -1;
                        ipPermission.ToPort = -1;
                    }
                    else
                    {
                        var ports = dialog.PortRange.Split('-');
                        if (ports.Length == 2 && int.TryParse(ports[0], out var fromPort) && int.TryParse(ports[1], out var toPort))
                        {
                            ipPermission.FromPort = fromPort;
                            ipPermission.ToPort = toPort;
                        }
                        else if (int.TryParse(dialog.PortRange, out var port))
                        {
                            ipPermission.FromPort = port;
                            ipPermission.ToPort = port;
                        }
                        else
                        {
                            MessageBox.Show("Invalid Port Range format. Use a single number (e.g., 22) or a range (e.g., 80-443).", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }

                    if (dialog.RuleType == "Ingress")
                    {
                        var request = new AuthorizeSecurityGroupIngressRequest
                        {
                            GroupId = SelectedSecurityGroup.GroupId,
                            IpPermissions = [ipPermission]
                        };
                        await ec2Client.AuthorizeSecurityGroupIngressAsync(request);
                    }
                    else // Egress
                    {
                        var request = new AuthorizeSecurityGroupEgressRequest
                        {
                            GroupId = SelectedSecurityGroup.GroupId,
                            IpPermissions = [ipPermission]
                        };
                        await ec2Client.AuthorizeSecurityGroupEgressAsync(request);
                    }

                    MessageBox.Show("Rule added successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    await LoadSecurityGroupsAsync(); // Refresh
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to add rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void DeleteRule(object? parameter)
        {
            if (parameter is not SecurityGroupRuleModel rule || SelectedSecurityGroup == null) return;

            var result = MessageBox.Show($"Are you sure you want to delete this rule?\n\n{rule.Type}: {rule.Protocol} / {rule.PortRange} / {rule.SourceOrDestination}", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No) return;

            try
            {
                using var ec2Client = new AmazonEC2Client();
                var ipPermission = new IpPermission
                {
                    IpProtocol = rule.Protocol,
                    Ipv4Ranges =
                    [
                        new IpRange { CidrIp = rule.SourceOrDestination }
                    ]
                };

                if (rule.PortRange.Equals("All", StringComparison.OrdinalIgnoreCase))
                {
                    ipPermission.FromPort = -1;
                    ipPermission.ToPort = -1;
                }
                else
                {
                    var ports = rule.PortRange.Split('-');
                    if (ports.Length == 2 && int.TryParse(ports[0], out var fromPort) && int.TryParse(ports[1], out var toPort))
                    {
                        ipPermission.FromPort = fromPort;
                        ipPermission.ToPort = toPort;
                    }
                    else if (int.TryParse(rule.PortRange, out var port))
                    {
                        ipPermission.FromPort = port;
                        ipPermission.ToPort = port;
                    }
                }

                if (rule.Type == "Ingress")
                {
                    var request = new RevokeSecurityGroupIngressRequest
                    {
                        GroupId = SelectedSecurityGroup.GroupId,
                        IpPermissions = [ipPermission]
                    };
                    await ec2Client.RevokeSecurityGroupIngressAsync(request);
                }
                else // Egress
                {
                    var request = new RevokeSecurityGroupEgressRequest
                    {
                        GroupId = SelectedSecurityGroup.GroupId,
                        IpPermissions = [ipPermission]
                    };
                    await ec2Client.RevokeSecurityGroupEgressAsync(request);
                }

                MessageBox.Show("Rule deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadSecurityGroupsAsync(); // Refresh
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuItem)sender;
            var rule = menuItem.CommandParameter as SecurityGroupRuleModel;
            var vm = menuItem.Command;

            MessageBox.Show($"MenuItem clicked!\nRule: {rule?.Type}\nCommand: {vm != null}", "Debug");
        }


    }

}
