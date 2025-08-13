namespace AwsManager.Models
{
    public class Ec2InstanceModel
    {
        public string InstanceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string InstanceType { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string PublicIp { get; set; } = string.Empty;
        public string PrivateIp { get; set; } = string.Empty;
        public bool IsSsmManaged { get; set; } 
        public string SecurityGroups { get; set; } = string.Empty;

        // Add the missing 'Platform' property to fix the error  
        public string Platform { get; set; } = string.Empty;
    }
}
