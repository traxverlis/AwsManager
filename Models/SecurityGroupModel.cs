using System.Collections.Generic;

namespace AwsManager.Models
{
    public class SecurityGroupModel
    {
        public string GroupId { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string VpcId { get; set; } = string.Empty;
        public List<SecurityGroupRuleModel> IngressRules { get; set; } = new List<SecurityGroupRuleModel>();
        public List<SecurityGroupRuleModel> EgressRules { get; set; } = new List<SecurityGroupRuleModel>();
    }
}
