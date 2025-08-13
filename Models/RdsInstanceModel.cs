namespace AwsManager.Models
{
    public class RdsInstanceModel
    {
        public string DbInstanceIdentifier { get; set; } = string.Empty;
        public string DbInstanceClass { get; set; } = string.Empty;
        public string Engine { get; set; } = string.Empty;
        public string DbInstanceStatus { get; set; } = string.Empty;
        public string EndpointAddress { get; set; } = "N/A";
        public int AllocatedStorage { get; set; }
        public bool MultiAZ { get; set; }
        public string SecurityGroups { get; set; } = string.Empty;
    }
}
