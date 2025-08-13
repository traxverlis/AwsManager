using System.Collections.Generic;

namespace AwsManager.Models
{
    public class AutoScalingGroupModel
    {
        public string AutoScalingGroupName { get; set; } = string.Empty;
        public int MinSize { get; set; }
        public int MaxSize { get; set; }
        public int DesiredCapacity { get; set; }
        public List<string> AvailabilityZones { get; set; } = [];
        public int InstanceCount => Instances.Count;
        public List<string> Instances { get; set; } = [];
        public string LaunchConfigurationName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
