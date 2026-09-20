using System.Collections.Generic;
using System.Linq;

namespace AwsManager.Models
{
    public class ResourceRecordSetModel
    {
        public Amazon.Route53.Model.ResourceRecordSet? Original { get; set; }
        public bool IsAlias => Original?.AliasTarget != null;
        public bool IsEditable => Original != null && string.IsNullOrEmpty(Original.SetIdentifier) && string.IsNullOrEmpty(Original.TrafficPolicyInstanceId) && Original.MultiValueAnswer != true;
        public string Routing => IsEditable ? (IsAlias ? "Alias" : "Simple") : "Avance (lecture seule)";
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public long TTL { get; set; }
        public List<string> ResourceRecords { get; set; } = [];
        public string Value => string.Join("\n", ResourceRecords);
    }
}