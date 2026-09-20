using Amazon.EC2.Model;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AwsManager.Services;

public static class SecurityRuleEditor
{
    public static SecurityGroupRuleRequest Parse(string protocol, string ports, string source, string description)
    {
        protocol = protocol.Trim().ToLowerInvariant() switch { "6" => "tcp", "17" => "udp", "1" => "icmp", "58" => "icmpv6", var value => value };
        var rule = new SecurityGroupRuleRequest { IpProtocol = protocol, Description = description.Trim() };
        if (description.Length > 255) throw new ArgumentException("Description : 255 caracteres maximum.");
        if (protocol is not ("tcp" or "udp" or "icmp" or "icmpv6" or "-1") &&
            (!int.TryParse(protocol, out var number) || number < 0 || number > 255))
            throw new ArgumentException("Protocole : tcp, udp, icmp, icmpv6, -1 ou numero de 0 a 255.");
        if (protocol is "tcp" or "udp")
        {
            var parts = ports.Trim().Split('-');
            if (ports.Equals("All", StringComparison.OrdinalIgnoreCase)) { rule.FromPort = 0; rule.ToPort = 65535; }
            else
            {
                if (parts.Length > 2 || !int.TryParse(parts[0], out var from) ||
                    !int.TryParse(parts[^1], out var to) || from < 0 || to > 65535 || from > to)
                    throw new ArgumentException("Ports : nombre entre 0 et 65535 ou plage croissante (80-443).");
                rule.FromPort = from; rule.ToPort = to;
            }
        }
        if (protocol is "icmp" or "icmpv6")
        {
            var parts = ports.Equals("All", StringComparison.OrdinalIgnoreCase) ? new[] { "-1", "-1" } : ports.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var type) || !int.TryParse(parts[1], out var code) ||
                type < -1 || type > 255 || code < -1 || code > 255 || type == -1 && code != -1)
                throw new ArgumentException("ICMP : type/code (8/0) ou All. Valeurs de -1 a 255.");
            rule.FromPort = type; rule.ToPort = code;
        }
        source = source.Trim();
        if (Regex.IsMatch(source, "^sg-[0-9a-f]+$")) rule.ReferencedGroupId = source;
        else if (Regex.IsMatch(source, "^pl-[0-9a-f]+$")) rule.PrefixListId = source;
        else
        {
            var parts = source.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix) ||
                prefix < 0 || prefix > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
                throw new ArgumentException("Source : CIDR IPv4/IPv6, identifiant sg- ou pl- valide.");
            if (address.AddressFamily == AddressFamily.InterNetwork) rule.CidrIpv4 = source;
            else rule.CidrIpv6 = source;
        }
        return rule;
    }

    public static IpPermission ToPermission(SecurityGroupRuleRequest rule) => new()
    {
        IpProtocol = rule.IpProtocol,
        FromPort = rule.FromPort,
        ToPort = rule.ToPort,
        Ipv4Ranges = rule.CidrIpv4 == null ? null : [new IpRange { CidrIp = rule.CidrIpv4, Description = rule.Description }],
        Ipv6Ranges = rule.CidrIpv6 == null ? null : [new Ipv6Range { CidrIpv6 = rule.CidrIpv6, Description = rule.Description }],
        PrefixListIds = rule.PrefixListId == null ? null : [new PrefixListId { Id = rule.PrefixListId, Description = rule.Description }],
        UserIdGroupPairs = rule.ReferencedGroupId == null ? null : [new UserIdGroupPair { GroupId = rule.ReferencedGroupId, Description = rule.Description }]
    };
}