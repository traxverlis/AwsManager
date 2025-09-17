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
        public ICommand EditRuleCommand { get; }

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
            EditRuleCommand = new RelayCommand(EditRule, _ => true);

            _ = LoadSecurityGroupsAsync();
        }
        private async Task LoadSecurityGroupsAsync()
        {
            IsLoading = true;

            // Sauvegarder l'ID du groupe de sécurité actuellement sélectionné
            var selectedGroupId = SelectedSecurityGroup?.GroupId;

            SecurityGroups.Clear();
            try
            {
                using var ec2Client = new AmazonEC2Client();
                var response = await ec2Client.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest());

                // Créer une liste temporaire pour le tri
                var securityGroupsList = new List<SecurityGroupModel>();

                foreach (var sg in response.SecurityGroups)
                {
                    var sgModel = new SecurityGroupModel
                    {
                        GroupId = sg.GroupId,
                        GroupName = sg.GroupName,
                        Description = sg.Description,
                        VpcId = sg.VpcId,
                    };

                    // Ingress rules - créer une liste temporaire pour le tri
                    var ingressRules = (sg.IpPermissions ?? Enumerable.Empty<IpPermission>())
                        .SelectMany(p =>
                        {
                            var portRange = (p.FromPort.HasValue && p.ToPort.HasValue)
                                ? (p.FromPort == p.ToPort
                                    ? p.FromPort.Value.ToString()
                                    : $"{p.FromPort.Value}-{p.ToPort.Value}")
                                : "All";

                            // Créer une règle pour chaque source/destination
                            var rules = new List<SecurityGroupRuleModel>();

                            // Règles pour les plages IP
                            if (p.Ipv4Ranges != null)
                            {
                                rules.AddRange(p.Ipv4Ranges.Select(r => new SecurityGroupRuleModel
                                {
                                    Type = "Ingress",
                                    Protocol = p.IpProtocol,
                                    PortRange = portRange,
                                    SourceOrDestination = r.CidrIp,
                                    Description = r.Description ?? "",
                                    SortKey = GetPortSortKey(portRange) // Clé pour le tri
                                }));
                            }

                            // Règles pour les groupes de sécurité
                            if (p.UserIdGroupPairs != null)
                            {
                                rules.AddRange(p.UserIdGroupPairs.Select(g => new SecurityGroupRuleModel
                                {
                                    Type = "Ingress",
                                    Protocol = p.IpProtocol,
                                    PortRange = portRange,
                                    SourceOrDestination = g.GroupId,
                                    Description = g.Description ?? "",
                                    SortKey = GetPortSortKey(portRange) // Clé pour le tri
                                }));
                            }

                            // Si ni IP ni groupe, créer une règle vide
                            if (!rules.Any())
                            {
                                rules.Add(new SecurityGroupRuleModel
                                {
                                    Type = "Ingress",
                                    Protocol = p.IpProtocol,
                                    PortRange = portRange,
                                    SourceOrDestination = "",
                                    Description = "",
                                    SortKey = GetPortSortKey(portRange)
                                });
                            }

                            return rules;
                        })
                        .OrderBy(r => r.SortKey) // Tri par port
                        .ThenBy(r => r.Protocol) // Puis par protocole
                        .ThenBy(r => r.SourceOrDestination) // Puis par source/destination
                        .ToList();

                    sgModel.IngressRules.AddRange(ingressRules);

                    // Egress rules - même logique
                    var egressRules = (sg.IpPermissionsEgress ?? Enumerable.Empty<IpPermission>())
                        .SelectMany(p =>
                        {
                            var portRange = (p.FromPort.HasValue && p.ToPort.HasValue)
                                ? (p.FromPort == p.ToPort
                                    ? p.FromPort.Value.ToString()
                                    : $"{p.FromPort.Value}-{p.ToPort.Value}")
                                : "All";

                            var rules = new List<SecurityGroupRuleModel>();

                            if (p.Ipv4Ranges != null)
                            {
                                rules.AddRange(p.Ipv4Ranges.Select(r => new SecurityGroupRuleModel
                                {
                                    Type = "Egress",
                                    Protocol = p.IpProtocol,
                                    PortRange = portRange,
                                    SourceOrDestination = r.CidrIp,
                                    Description = r.Description ?? "",
                                    SortKey = GetPortSortKey(portRange)
                                }));
                            }

                            if (p.UserIdGroupPairs != null)
                            {
                                rules.AddRange(p.UserIdGroupPairs.Select(g => new SecurityGroupRuleModel
                                {
                                    Type = "Egress",
                                    Protocol = p.IpProtocol,
                                    PortRange = portRange,
                                    SourceOrDestination = g.GroupId,
                                    Description = g.Description ?? "",
                                    SortKey = GetPortSortKey(portRange)
                                }));
                            }

                            if (!rules.Any())
                            {
                                rules.Add(new SecurityGroupRuleModel
                                {
                                    Type = "Egress",
                                    Protocol = p.IpProtocol,
                                    PortRange = portRange,
                                    SourceOrDestination = "",
                                    Description = "",
                                    SortKey = GetPortSortKey(portRange)
                                });
                            }

                            return rules;
                        })
                        .OrderBy(r => r.SortKey)
                        .ThenBy(r => r.Protocol)
                        .ThenBy(r => r.SourceOrDestination)
                        .ToList();

                    sgModel.EgressRules.AddRange(egressRules);
                    securityGroupsList.Add(sgModel);
                }

                // Trier la liste des groupes de sécurité par nom
                var sortedSecurityGroups = securityGroupsList
                    .OrderBy(sg => sg.GroupName)
                    .ToList();

                // Ajouter les groupes triés à la collection
                foreach (var sg in sortedSecurityGroups)
                {
                    SecurityGroups.Add(sg);
                }

                // Resélectionner l'ancien groupe de sécurité s'il existe encore
                if (!string.IsNullOrEmpty(selectedGroupId))
                {
                    var previouslySelected = SecurityGroups.FirstOrDefault(sg => sg.GroupId == selectedGroupId);
                    if (previouslySelected != null)
                    {
                        SelectedSecurityGroup = previouslySelected;
                    }
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

        // Méthode helper pour créer une clé de tri numérique pour les ports
        private int GetPortSortKey(string portRange)
        {
            if (portRange.Equals("All", StringComparison.OrdinalIgnoreCase))
                return 0; // "All" en premier

            if (portRange.Contains('-'))
            {
                var parts = portRange.Split('-');
                if (int.TryParse(parts[0], out var fromPort))
                    return fromPort;
            }
            else if (int.TryParse(portRange, out var port))
            {
                return port;
            }

            return int.MaxValue; // Ports non parsables en dernier
        }
        //private async Task LoadSecurityGroupsAsync()
        //{
        //    IsLoading = true;
        //    SecurityGroups.Clear();
        //    try
        //    {
        //        using var ec2Client = new AmazonEC2Client();
        //        var response = await ec2Client.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest());

        //        foreach (var sg in response.SecurityGroups)
        //        {
        //            var sgModel = new SecurityGroupModel
        //            {
        //                GroupId = sg.GroupId,
        //                GroupName = sg.GroupName,
        //                Description = sg.Description,
        //                VpcId = sg.VpcId,
        //            };
        //            sgModel.IngressRules.AddRange(
        //                     (sg.IpPermissions ?? Enumerable.Empty<IpPermission>())
        //                     .Select(p => new SecurityGroupRuleModel
        //                     {
        //                         Type = "Ingress",
        //                         Protocol = p.IpProtocol,
        //                         PortRange = (p.FromPort.HasValue && p.ToPort.HasValue)
        //                            ? (p.FromPort == p.ToPort
        //                                ? p.FromPort.Value.ToString()
        //                                : $"{p.FromPort.Value}-{p.ToPort.Value}")
        //                            : "All",
        //                         SourceOrDestination = string.Join(", ",
        //                             (p.Ipv4Ranges ?? Enumerable.Empty<IpRange>()).Select(r => r.CidrIp)
        //                             .Concat((p.UserIdGroupPairs ?? Enumerable.Empty<UserIdGroupPair>()).Select(g => g.GroupId))
        //                         ),
        //                         Description = (p.Ipv4Ranges != null && p.Ipv4Ranges.Count != 0)
        //                             ? p.Ipv4Ranges.First().Description ?? ""
        //                             : ""
        //                     })
        //                 );

        //            // Egress rules
        //            sgModel.EgressRules.AddRange(
        //                (sg.IpPermissionsEgress ?? Enumerable.Empty<IpPermission>())
        //                .Select(p => new SecurityGroupRuleModel
        //                {
        //                    Type = "Egress",
        //                    Protocol = p.IpProtocol,
        //                    PortRange = (p.FromPort.HasValue && p.ToPort.HasValue)
        //                            ? (p.FromPort == p.ToPort
        //                                ? p.FromPort.Value.ToString()
        //                                : $"{p.FromPort.Value}-{p.ToPort.Value}")
        //                            : "All",
        //                    SourceOrDestination = string.Join(", ",
        //                        (p.Ipv4Ranges ?? Enumerable.Empty<IpRange>()).Select(r => r.CidrIp)
        //                        .Concat((p.UserIdGroupPairs ?? Enumerable.Empty<UserIdGroupPair>()).Select(g => g.GroupId))
        //                    ),
        //                    Description = (p.Ipv4Ranges != null && p.Ipv4Ranges.Count != 0)
        //                        ? p.Ipv4Ranges.First().Description ?? ""
        //                        : ""
        //                })
        //            );

        //            SecurityGroups.Add(sgModel);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show($"Failed to load Security Groups: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        //    }
        //    finally
        //    {
        //        IsLoading = false;
        //    }
        //}

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
        private async void EditRule(object? parameter)
        {
            if (parameter is not SecurityGroupRuleModel rule || SelectedSecurityGroup == null) return;

            var dialog = new AddSecurityGroupRuleWindow
            {
                Owner = Application.Current.MainWindow,
                Title = "Edit Security Group Rule"
            };

            // Pré-remplir le dialog avec les données de la règle existante
            dialog.Loaded += (s, e) =>
            {
                // Définir le type de règle (Ingress/Egress)
                dialog.RuleTypeComboBox.SelectedItem = dialog.RuleTypeComboBox.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(item => (string)item.Content == rule.Type);

                // Pré-remplir les champs
                dialog.ProtocolTextBox.Text = rule.Protocol;
                dialog.PortRangeTextBox.Text = rule.PortRange;
                dialog.CidrTextBox.Text = rule.SourceOrDestination;
                dialog.DescriptionTextBox.Text = rule.Description;
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    using var ec2Client = new AmazonEC2Client();

                    // 1. Supprimer l'ancienne règle
                    var oldIpPermission = new IpPermission
                    {
                        IpProtocol = rule.Protocol
                    };

                    // Détecter si c'est une plage IP ou un groupe de sécurité
                    if (rule.SourceOrDestination.StartsWith("sg-"))
                    {
                        oldIpPermission.UserIdGroupPairs =
                        [
                            new UserIdGroupPair { GroupId = rule.SourceOrDestination }
                        ];
                    }
                    else
                    {
                        oldIpPermission.Ipv4Ranges =
                        [
                            new IpRange { CidrIp = rule.SourceOrDestination, Description = rule.Description }
                        ];
                    }

                    // Configuration des ports pour l'ancienne règle
                    if (rule.PortRange.Equals("All", StringComparison.OrdinalIgnoreCase))
                    {
                        oldIpPermission.FromPort = -1;
                        oldIpPermission.ToPort = -1;
                    }
                    else
                    {
                        var ports = rule.PortRange.Split('-');
                        if (ports.Length == 2 && int.TryParse(ports[0], out var fromPort) && int.TryParse(ports[1], out var toPort))
                        {
                            oldIpPermission.FromPort = fromPort;
                            oldIpPermission.ToPort = toPort;
                        }
                        else if (int.TryParse(rule.PortRange, out var port))
                        {
                            oldIpPermission.FromPort = port;
                            oldIpPermission.ToPort = port;
                        }
                    }

                    // Supprimer l'ancienne règle
                    if (rule.Type == "Ingress")
                    {
                        var revokeRequest = new RevokeSecurityGroupIngressRequest
                        {
                            GroupId = SelectedSecurityGroup.GroupId,
                            IpPermissions = [oldIpPermission]
                        };
                        await ec2Client.RevokeSecurityGroupIngressAsync(revokeRequest);
                    }
                    else // Egress
                    {
                        var revokeRequest = new RevokeSecurityGroupEgressRequest
                        {
                            GroupId = SelectedSecurityGroup.GroupId,
                            IpPermissions = [oldIpPermission]
                        };
                        await ec2Client.RevokeSecurityGroupEgressAsync(revokeRequest);
                    }

                    // 2. Ajouter la nouvelle règle
                    var newIpPermission = new IpPermission
                    {
                        IpProtocol = dialog.Protocol
                    };

                    // Détecter si c'est une plage IP ou un groupe de sécurité pour la nouvelle règle
                    if (dialog.Cidr.StartsWith("sg-"))
                    {
                        newIpPermission.UserIdGroupPairs =
                        [
                            new UserIdGroupPair { GroupId = dialog.Cidr, Description = dialog.Description }
                        ];
                    }
                    else
                    {
                        newIpPermission.Ipv4Ranges =
                        [
                            new IpRange { CidrIp = dialog.Cidr, Description = dialog.Description }
                        ];
                    }

                    // Configuration des ports pour la nouvelle règle
                    if (dialog.PortRange.Equals("All", StringComparison.OrdinalIgnoreCase))
                    {
                        newIpPermission.FromPort = -1;
                        newIpPermission.ToPort = -1;
                    }
                    else
                    {
                        var ports = dialog.PortRange.Split('-');
                        if (ports.Length == 2 && int.TryParse(ports[0], out var fromPort) && int.TryParse(ports[1], out var toPort))
                        {
                            newIpPermission.FromPort = fromPort;
                            newIpPermission.ToPort = toPort;
                        }
                        else if (int.TryParse(dialog.PortRange, out var port))
                        {
                            newIpPermission.FromPort = port;
                            newIpPermission.ToPort = port;
                        }
                        else
                        {
                            MessageBox.Show("Invalid Port Range format. Use a single number (e.g., 22) or a range (e.g., 80-443).", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }

                    // Ajouter la nouvelle règle
                    if (dialog.RuleType == "Ingress")
                    {
                        var addRequest = new AuthorizeSecurityGroupIngressRequest
                        {
                            GroupId = SelectedSecurityGroup.GroupId,
                            IpPermissions = [newIpPermission]
                        };
                        await ec2Client.AuthorizeSecurityGroupIngressAsync(addRequest);
                    }
                    else // Egress
                    {
                        var addRequest = new AuthorizeSecurityGroupEgressRequest
                        {
                            GroupId = SelectedSecurityGroup.GroupId,
                            IpPermissions = [newIpPermission]
                        };
                        await ec2Client.AuthorizeSecurityGroupEgressAsync(addRequest);
                    }

                    MessageBox.Show("Rule updated successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    await LoadSecurityGroupsAsync(); // Refresh
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to edit rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    // En cas d'erreur, recharger pour s'assurer de la cohérence
                    await LoadSecurityGroupsAsync();
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
