using Amazon.EC2.Model;
using AwsManager.Models;
using AwsManager.Services;
using AwsManager.Views.Dialogs;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace AwsManager.ViewModels;

public class SecurityGroupViewModel : AwsResourceViewModel, IRefreshableViewModel
{
    public static string Name => "Groupes de securite";
    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set { SetField(ref _isLoading, value); CommandManager.InvalidateRequerySuggested(); } }
    public ObservableCollection<SecurityGroupModel> SecurityGroups { get; } = [];
    private SecurityGroupModel? _selectedSecurityGroup;
    public SecurityGroupModel? SelectedSecurityGroup { get => _selectedSecurityGroup; set => SetField(ref _selectedSecurityGroup, value); }
    public ICommand RefreshCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand EditRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    private readonly Func<SecurityRuleEditorViewModel, bool> _showEditor;
    private readonly Func<string, bool> _confirmRule;

    public SecurityGroupViewModel() : this(null) { }
    public SecurityGroupViewModel(IAwsClientFactory? clientFactory, bool load = true, Func<SecurityRuleEditorViewModel, bool>? showEditor = null, Func<string, bool>? confirmRule = null, AwsContext? context = null) : base(clientFactory, context)
    {
        _showEditor = showEditor ?? (editor => new AddSecurityGroupRuleWindow(editor) { Owner = Application.Current.MainWindow }.ShowDialog() == true);
        _confirmRule = confirmRule ?? Confirm;
        ConfigureFilter<SecurityGroupModel>(SecurityGroups, group => $"{group.GroupId} {group.GroupName} {group.VpcId} {group.Description}");
        RefreshCommand = new AsyncRelayCommand(_ => LoadSecurityGroupsAsync(), _ => !IsLoading && Allowed("ec2:DescribeSecurityGroups,ec2:DescribeSecurityGroupRules"));
        AddRuleCommand = new AsyncRelayCommand(parameter => EditRuleAsync(null, parameter as string == "Egress"), parameter => !IsLoading && SelectedSecurityGroup != null && Allowed(parameter as string == "Egress" ? "ec2:AuthorizeSecurityGroupEgress" : "ec2:AuthorizeSecurityGroupIngress", Arn("ec2", $"security-group/{SelectedSecurityGroup.GroupId}"), true));
        EditRuleCommand = new AsyncRelayCommand(parameter => EditRuleAsync((SecurityGroupRuleModel)parameter!), parameter => !IsLoading && parameter is SecurityGroupRuleModel rule && Allowed("ec2:ModifySecurityGroupRules", Arn("ec2", $"security-group/{rule.GroupId}"), true));
        DeleteRuleCommand = new AsyncRelayCommand(DeleteRuleAsync, parameter => !IsLoading && parameter is SecurityGroupRuleModel rule && Allowed(rule.Type == "Ingress" ? "ec2:RevokeSecurityGroupIngress" : "ec2:RevokeSecurityGroupEgress", Arn("ec2", $"security-group/{rule.GroupId}"), true));
        if (load) _ = LoadSecurityGroupsAsync();
    }

    private async Task LoadSecurityGroupsAsync()
    {
        IsLoading = true;
        Status = "";
        var selectedId = SelectedSecurityGroup?.GroupId;
        SelectedSecurityGroup = null;
        SecurityGroups.Clear();
        try
        {
            using var client = ClientFactory.CreateEc2Client();
            var groups = new Dictionary<string, SecurityGroupModel>();
            await foreach (var group in client.Paginators.DescribeSecurityGroups(new DescribeSecurityGroupsRequest()).SecurityGroups)
                groups[group.GroupId] = new SecurityGroupModel { GroupId = group.GroupId, GroupName = group.GroupName, VpcId = group.VpcId, Description = group.Description };
            await foreach (var rule in client.Paginators.DescribeSecurityGroupRules(new DescribeSecurityGroupRulesRequest()).SecurityGroupRules)
            {
                if (!groups.TryGetValue(rule.GroupId, out var group)) continue;
                var model = new SecurityGroupRuleModel
                {
                    RuleId = rule.SecurityGroupRuleId,
                    GroupId = rule.GroupId,
                    Type = rule.IsEgress == true ? "Egress" : "Ingress",
                    Protocol = rule.IpProtocol,
                    PortRange = rule.IpProtocol is "icmp" or "icmpv6" or "1" or "58" ? $"{rule.FromPort}/{rule.ToPort}" :
                        rule.FromPort == null ? "All" : rule.FromPort == rule.ToPort ? $"{rule.FromPort}" : $"{rule.FromPort}-{rule.ToPort}",
                    SourceOrDestination = rule.CidrIpv4 ?? rule.CidrIpv6 ?? rule.PrefixListId ?? rule.ReferencedGroupInfo?.GroupId ?? "",
                    Description = rule.Description ?? "",
                    SortKey = rule.FromPort ?? -1
                };
                (rule.IsEgress == true ? group.EgressRules : group.IngressRules).Add(model);
            }
            foreach (var group in groups.Values.OrderBy(group => group.GroupName)) SecurityGroups.Add(group);
            SelectedSecurityGroup = SecurityGroups.FirstOrDefault(group => group.GroupId == selectedId);
        }
        catch (Exception exception) { ReportError(exception); }
        finally { IsLoading = false; }
    }

    private async Task EditRuleAsync(SecurityGroupRuleModel? rule, bool isEgress = false)
    {
        var groupId = rule?.GroupId ?? SelectedSecurityGroup?.GroupId;
        if (groupId == null) return;
        var group = SecurityGroups.FirstOrDefault(item => item.GroupId == groupId) ?? SelectedSecurityGroup;
        var resource = Arn("ec2", $"security-group/{groupId}");
        var canChangeDirection = rule == null && await CheckAccessAsync([
            new("ec2:AuthorizeSecurityGroupIngress", resource), new("ec2:AuthorizeSecurityGroupEgress", resource)], true);
        var editor = new SecurityRuleEditorViewModel(group, SecurityGroups, rule, isEgress, canChangeDirection);
        if (!_showEditor(editor)) return;
        try
        {
            var input = editor.BuildRule();
            var action = rule != null ? "ec2:ModifySecurityGroupRules" : editor.IsEgress ? "ec2:AuthorizeSecurityGroupEgress" : "ec2:AuthorizeSecurityGroupIngress";
            if (!await CheckAccessAsync([new(action, resource)], true)) { Status = "Modification non autorisee ou non verifiee."; return; }
            var previous = rule == null ? "" : $"\nAvant : {rule.Type} / {rule.Protocol} / {rule.PortRange} / {rule.SourceOrDestination}";
            if (!_confirmRule($"{(rule == null ? "Ajouter" : "Modifier")} la regle de {groupId} ?{previous}\nApres : {editor.Summary}\n{editor.ExposureWarning}")) return;
            IsLoading = true;
            using var client = ClientFactory.CreateEc2Client();
            if (rule != null)
                await client.ModifySecurityGroupRulesAsync(new ModifySecurityGroupRulesRequest
                {
                    GroupId = groupId,
                    SecurityGroupRules = [new SecurityGroupRuleUpdate { SecurityGroupRuleId = rule.RuleId, SecurityGroupRule = input }]
                });
            else if (editor.Direction == "Ingress")
                await client.AuthorizeSecurityGroupIngressAsync(new AuthorizeSecurityGroupIngressRequest { GroupId = groupId, IpPermissions = [SecurityRuleEditor.ToPermission(input)] });
            else
                await client.AuthorizeSecurityGroupEgressAsync(new AuthorizeSecurityGroupEgressRequest { GroupId = groupId, IpPermissions = [SecurityRuleEditor.ToPermission(input)] });
            NotificationService.Publish("Regle enregistree.");
            await LoadSecurityGroupsAsync();
        }
        catch (Exception exception) { ReportError(exception); }
        finally { IsLoading = false; }
    }

    private async Task DeleteRuleAsync(object? parameter)
    {
        if (parameter is not SecurityGroupRuleModel rule || !Confirm($"Supprimer {rule.RuleId} dans {rule.GroupId} ?\n{rule.Type} / {rule.Protocol} / {rule.PortRange} / {rule.SourceOrDestination}")) return;
        try
        {
            IsLoading = true;
            using var client = ClientFactory.CreateEc2Client();
            if (rule.Type == "Ingress")
                await client.RevokeSecurityGroupIngressAsync(new RevokeSecurityGroupIngressRequest { GroupId = rule.GroupId, SecurityGroupRuleIds = [rule.RuleId] });
            else
                await client.RevokeSecurityGroupEgressAsync(new RevokeSecurityGroupEgressRequest { GroupId = rule.GroupId, SecurityGroupRuleIds = [rule.RuleId] });
            NotificationService.Publish("Regle supprimee.");
            await LoadSecurityGroupsAsync();
        }
        catch (Exception exception) { ReportError(exception); }
        finally { IsLoading = false; }
    }
}