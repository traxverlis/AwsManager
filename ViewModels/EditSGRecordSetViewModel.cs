using System.Collections.Generic;
using System.Linq;
using AwsManager.Models;

namespace AwsManager.ViewModels
{
    public class EditSGRecordSetViewModel(ResourceRecordSetModel? recordToEdit = null) : ViewModelBase
    {
        public string Name { get; set; } = recordToEdit?.Name ?? "";
        public string Type { get; set; } = recordToEdit?.Type ?? "A";
        public long Ttl { get; set; } = recordToEdit?.TTL ?? 300;
        public string Value { get; set; } = recordToEdit == null ? "" : string.Join("\n", recordToEdit.ResourceRecords); // A single string for simplicity, for multiple values use newline

        public List<string> RecordTypes { get; } = ["A", "AAAA", "CNAME", "MX", "NS", "PTR", "SOA", "SPF", "SRV", "TXT"];
        public ResourceRecordSetModel OriginalRecord { get; } = recordToEdit ?? new ResourceRecordSetModel();
    }
}
