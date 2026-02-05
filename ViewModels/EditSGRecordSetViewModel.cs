using System.Collections.Generic;
using System.Linq;
using AwsManager.Models;

namespace AwsManager.ViewModels
{
    public class EditSGRecordSetViewModel(ResourceRecordSetModel? recordToEdit = null) : ViewModelBase
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public long Ttl { get; set; }
        public string Value { get; set; } // A single string for simplicity, for multiple values use newline

        public List<string> RecordTypes { get; } = ["A", "AAAA", "CNAME", "MX", "NS", "PTR", "SOA", "SPF", "SRV", "TXT"];
        public ResourceRecordSetModel OriginalRecord { get; }
    }
}
