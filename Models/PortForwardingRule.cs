namespace AwsManager.Models
{
    public class PortForwardingRule
    {
        public int RemotePort { get; set; } = 8080;
        public int LocalPort { get; set; } = 8080;
    }
}