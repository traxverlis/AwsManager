namespace AwsManager.Models
{
    public class HostedZoneModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public long ResourceRecordSetCount { get; set; }
        public bool IsPrivateZone { get; set; }
    }
}
