using Amazon.EC2.Model;
using AwsManager.Models;
using AwsManager.Services;

namespace AwsManager.ViewModels;

public sealed class SecurityRuleEditorViewModel : ViewModelBase
{
    public sealed record Choice(string Id, string Name);
    public sealed record Preset(string Name, string Protocol, string From, string To);
    public sealed record GroupChoice(string Id, string Name)
    {
        public string DisplayName => $"{Name} ({Id})";
    }
    public IReadOnlyList<Choice> Protocols { get; } = [new("tcp", "TCP"), new("udp", "UDP"), new("icmp", "ICMP IPv4"), new("icmpv6", "ICMP IPv6"), new("-1", "Tout le trafic"), new("custom", "Autre protocole IP")];
    public IReadOnlyList<Choice> SourceKinds { get; } = [new("ipv4", "CIDR IPv4"), new("ipv6", "CIDR IPv6"), new("group", "Groupe de securite"), new("prefix", "Liste de prefixes"), new("any4", "Toutes les adresses IPv4"), new("any6", "Toutes les adresses IPv6")];
    public IReadOnlyList<Preset> Presets { get; } =
    [
        new("SSH", "tcp", "22", "22"), new("HTTP", "tcp", "80", "80"), new("HTTPS", "tcp", "443", "443"),
        new("RDP", "tcp", "3389", "3389"), new("Oracle", "tcp", "1521", "1521"), new("PostgreSQL", "tcp", "5432", "5432"),
        new("MySQL / MariaDB", "tcp", "3306", "3306"), new("SQL Server", "tcp", "1433", "1433"),
        new("DNS UDP", "udp", "53", "53"), new("DNS TCP", "tcp", "53", "53"),
        new("Ping IPv4", "icmp", "8", "0"), new("Ping IPv6", "icmpv6", "128", "0")
    ];
    public IReadOnlyList<GroupChoice> KnownGroups { get; }
    public string GroupId { get; }
    public string GroupName { get; }
    public string RuleId { get; }
    public bool IsEditing => RuleId.Length > 0;
    private readonly bool _allowDirectionChange;
    public bool CanChangeDirection => !IsEditing && _allowDirectionChange;
    public string Title => IsEditing ? "Modifier la regle" : "Ajouter une regle";
    private bool _isEgress;
    public bool IsEgress { get => _isEgress; set { if (CanChangeDirection && SetField(ref _isEgress, value)) UpdateState(); } }
    public bool IsIngress { get => !IsEgress; set { if (value) IsEgress = false; } }
    public string Direction => IsEgress ? "Egress" : "Ingress";
    public string SourceLabel => IsEgress ? "Destination" : "Source";
    private Preset? _selectedPreset;
    public Preset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetField(ref _selectedPreset, value) || value == null) return;
            _protocol = value.Protocol;
            _from = value.From;
            _to = value.To;
            _allPorts = false;
            OnPropertyChanged(nameof(Protocol));
            OnPropertyChanged(nameof(From));
            OnPropertyChanged(nameof(To));
            OnPropertyChanged(nameof(AllPorts));
            UpdateState();
        }
    }
    private string _protocol = "tcp";
    public string Protocol { get => _protocol; set { if (SetField(ref _protocol, value)) { ClearPreset(); UpdateState(); } } }
    private string _protocolNumber = "";
    public string ProtocolNumber { get => _protocolNumber; set { if (SetField(ref _protocolNumber, value)) UpdateState(); } }
    private string _from = "";
    public string From { get => _from; set { if (SetField(ref _from, value)) { ClearPreset(); UpdateState(); } } }
    private string _to = "";
    public string To { get => _to; set { if (SetField(ref _to, value)) { ClearPreset(); UpdateState(); } } }
    private bool _allPorts;
    public bool AllPorts { get => _allPorts; set { if (SetField(ref _allPorts, value)) { ClearPreset(); UpdateState(); } } }
    private string _sourceKind = "ipv4";
    public string SourceKind { get => _sourceKind; set { if (SetField(ref _sourceKind, value)) UpdateState(); } }
    private string _source = "";
    public string Source { get => _source; set { if (SetField(ref _source, value)) UpdateState(); } }
    private string _description = "";
    public string Description { get => _description; set { if (SetField(ref _description, value)) UpdateState(); } }
    public bool IsCustomProtocol => Protocol == "custom";
    public bool IsIcmp => EffectiveProtocol is "icmp" or "icmpv6" or "1" or "58";
    public bool HasPorts => EffectiveProtocol is "tcp" or "udp" or "6" or "17" || IsIcmp;
    public bool CanEditPorts => HasPorts && !AllPorts;
    public string FromLabel => IsIcmp ? "Type ICMP" : "Port debut";
    public string ToLabel => IsIcmp ? "Code ICMP" : "Port fin (facultatif)";
    public string AllPortsLabel => IsIcmp ? "Tous les types et codes" : "Tous les ports";
    public bool IsGroupSource => SourceKind == "group";
    public bool IsManualSource => SourceKind is "ipv4" or "ipv6" or "prefix";
    public string SourceValueLabel => SourceKind switch { "group" => "Groupe (sg-)", "prefix" => "Liste (pl-)", "ipv6" => "Reseau IPv6", _ => "Reseau IPv4" };
    public bool CanSubmit => ValidationMessage.Length == 0;
    public string ValidationMessage { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public string ExposureWarning { get; private set; } = "";
    private string EffectiveProtocol => IsCustomProtocol ? ProtocolNumber.Trim() : Protocol;

    public SecurityRuleEditorViewModel(SecurityGroupModel? group = null, IEnumerable<SecurityGroupModel>? groups = null, SecurityGroupRuleModel? original = null, bool isEgress = false, bool allowDirectionChange = true)
    {
        _allowDirectionChange = allowDirectionChange;
        GroupId = original?.GroupId ?? group?.GroupId ?? "";
        GroupName = group?.GroupName ?? "";
        RuleId = original?.RuleId ?? "";
        KnownGroups = (groups ?? []).OrderBy(item => item.GroupName).Select(item => new GroupChoice(item.GroupId, item.GroupName)).ToArray();
        _isEgress = original == null ? isEgress : original.Type == "Egress";
        if (original != null)
        {
            var rule = SecurityRuleEditor.Parse(original.Protocol, original.PortRange, original.SourceOrDestination, original.Description);
            _protocol = Protocols.Any(item => item.Id == rule.IpProtocol) ? rule.IpProtocol : "custom";
            _protocolNumber = _protocol == "custom" ? rule.IpProtocol : "";
            _from = rule.FromPort?.ToString() ?? "";
            _to = rule.ToPort?.ToString() ?? "";
            _allPorts = IsIcmp ? rule.FromPort == -1 && rule.ToPort == -1 : rule.FromPort == 0 && rule.ToPort == 65535;
            _sourceKind = rule.ReferencedGroupId != null ? "group" : rule.PrefixListId != null ? "prefix" : rule.CidrIpv6 != null ? "ipv6" : "ipv4";
            _source = original.SourceOrDestination;
            _description = original.Description;
        }
        UpdateState();
    }

    public SecurityGroupRuleRequest BuildRule()
    {
        if (!Protocols.Any(item => item.Id == Protocol) || !SourceKinds.Any(item => item.Id == SourceKind)) throw new ArgumentException("Selectionnez un protocole et un type de source valides.");
        if (IsCustomProtocol && (!int.TryParse(ProtocolNumber, out var number) || number < 0 || number > 255)) throw new ArgumentException("Numero de protocole IP : de 0 a 255.");
        var ports = AllPorts || !HasPorts ? "All" : IsIcmp ? $"{From}/{To}" : string.IsNullOrWhiteSpace(To) ? From : $"{From}-{To}";
        var source = SourceKind switch { "any4" => "0.0.0.0/0", "any6" => "::/0", _ => Source };
        var rule = SecurityRuleEditor.Parse(EffectiveProtocol, ports, source, Description);
        if (SourceKind is "ipv4" or "any4" && rule.CidrIpv4 == null ||
            SourceKind is "ipv6" or "any6" && rule.CidrIpv6 == null ||
            SourceKind == "group" && rule.ReferencedGroupId == null || SourceKind == "prefix" && rule.PrefixListId == null)
            throw new ArgumentException("La valeur ne correspond pas au type de source ou destination selectionne.");
        return rule;
    }

    private void ClearPreset()
    {
        _selectedPreset = null;
        OnPropertyChanged(nameof(SelectedPreset));
    }

    private void UpdateState()
    {
        OnPropertyChanged(nameof(IsIngress));
        ExposureWarning = "";
        Summary = "";
        try
        {
            var rule = BuildRule();
            var source = rule.CidrIpv4 ?? rule.CidrIpv6 ?? rule.ReferencedGroupId ?? rule.PrefixListId;
            var ports = rule.FromPort == null ? "Tous" : IsIcmp ? $"{rule.FromPort}/{rule.ToPort}" : rule.FromPort == rule.ToPort ? $"{rule.FromPort}" : $"{rule.FromPort}-{rule.ToPort}";
            Summary = $"{(IsEgress ? "Sortie" : "Entree")} | {rule.IpProtocol} | {ports} | {source}";
            var cidr = rule.CidrIpv4 ?? rule.CidrIpv6;
            if (cidr != null && int.TryParse(cidr.Split('/')[1], out var prefix) && prefix == 0)
                ExposureWarning = "Attention : cette regle autorise toutes les adresses IP.";
            ValidationMessage = "";
        }
        catch (ArgumentException exception) { ValidationMessage = exception.Message; }
        foreach (var property in new[] { nameof(IsCustomProtocol), nameof(IsIcmp), nameof(HasPorts), nameof(CanEditPorts), nameof(FromLabel), nameof(ToLabel), nameof(AllPortsLabel), nameof(IsGroupSource), nameof(IsManualSource), nameof(SourceValueLabel), nameof(SourceLabel), nameof(Direction), nameof(ValidationMessage), nameof(CanSubmit), nameof(Summary), nameof(ExposureWarning) })
            OnPropertyChanged(property);
    }
}