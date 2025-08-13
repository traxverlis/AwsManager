using System.Collections.Generic;
using System.Linq;

namespace AwsManager.Models
{
    public class ResourceRecordSetModel
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public long TTL { get; set; }
        public List<string> ResourceRecords { get; set; } = [];
        public string Value => string.Join("\n", ResourceRecords.Select(r => r.Replace("\"", "")));
    }
}