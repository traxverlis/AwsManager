namespace AwsManager.Models
{
    public class SecurityGroupRuleModel
    {
        public string Type { get; set; } = "Ingress"; // or Egress
        public string Protocol { get; set; } = string.Empty;
        public string PortRange { get; set; } = string.Empty;
        public string SourceOrDestination { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
